using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace yixing_chamber
{
    public partial class MainWindow : Window
    {
        /// <summary>默认管道名。</summary>
        private const string DefaultPipeName = "yixing_chamber";

        /// <summary>串口通讯模块（温度专用，自动重连）。</summary>
        private readonly YixingSerial _serial = new YixingSerial();

        /// <summary>1 秒定时器，推进温度 ramp（不涉及串口 I/O）。</summary>
        private DispatcherTimer _rampTimer;

        // ---- Named pipe server state ----
        private Thread _pipeAcceptThread;
        private volatile bool _pipeRunning;

        // ---- Latest status, shared with the pipe server ----
        private double _curTemp, _tgtTemp;
        private volatile byte _runState;
        private volatile bool _hasStatus;

        /// <summary>是否已连接（镜像 <see cref="YixingSerial.Connected"/>）。</summary>
        private volatile bool _connected;

        /// <summary>允许 "-"、数字和最多一位小数（温度输入）。</summary>
        private static readonly Regex TempInputRegex = new Regex(@"^-?\d*\.?\d{0,1}$");

        /// <summary>只允许数字（ramp 时长，整秒）。</summary>
        private static readonly Regex DurationInputRegex = new Regex(@"^\d*$");

        // ---- 温度 ramp 状态 ----
        private readonly RampTask _tempRamp = new RampTask();
        private readonly object _rampLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            _serial.StatusReceived += OnSerialStatus;
            _serial.StateChanged += OnSerialStateChanged;

            _rampTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _rampTimer.Tick += (s, e) => TickRamp();
            _rampTimer.Start();

            Loaded += (s, e) =>
            {
                RefreshPorts();
                UpdateRampUi();
            };
            // Pipe server is started by PipeToggleButton (default ON) during InitializeComponent.
        }

        protected override void OnClosed(EventArgs e)
        {
            StopPipeServer();
            _serial.Stop();
            base.OnClosed(e);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serial.Connected)
            {
                _serial.Stop();
                return;
            }

            // 重连中点击按钮 = 取消自动重连。
            if (_serial.Reconnecting)
            {
                _serial.Stop();
                return;
            }

            string port = PortComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(port))
            {
                MessageBox.Show("Please select a serial port first.", "Yixing Chamber",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _serial.Start(port);
        }

        /// <summary>I/O 线程每轮状态更新。</summary>
        private void OnSerialStatus(YixingStatus s)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)(() => UpdateDisplay(s.CurTemp, s.TgtTemp, s.RunState)));
            }
            catch
            {
                // 窗口关闭中。
            }
        }

        /// <summary>I/O 线程连接状态变化。</summary>
        private void OnSerialStateChanged(YixingConnectionState state)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (state.Connected)
                    {
                        ConnectionStatusText.Text = "";
                        UpdateConnectedUi(true);
                    }
                    else
                    {
                        // 任何断开（重连或最终）都停止 ramp。
                        StopTempRamp();

                        if (state.Reconnecting)
                        {
                            UpdateConnectedUi(false);
                            ConnectionStatusText.Text = state.Message ?? "Reconnecting…";
                            ConnectButton.Content = "Reconnecting...";
                        }
                        else
                        {
                            _hasStatus = false;
                            ConnectionStatusText.Text = "";
                            UpdateConnectedUi(false);
                            ResetDisplay();
                        }
                    }
                }));
            }
            catch
            {
                // 窗口关闭中。
            }
        }

        private async void SetTempButton_Click(object sender, RoutedEventArgs e)
        {
            double temp;
            if (!TryParseDouble(SetTempTextBox.Text, out temp))
            {
                ShowInputError("Invalid temperature value.");
                return;
            }
            if (temp < -40.0 || temp > 150.0)
            {
                ShowInputError("Temperature must be between -40.0 and 150.0 °C.");
                return;
            }

            // 手动设定温度会取消正在运行的温度 ramp。
            StopTempRamp();

            // 后台执行"写入+回读验证+不符重发"，避免阻塞 UI。
            SetTempButton.IsEnabled = false;
            bool verified;
            try
            {
                verified = await Task.Run(() => _serial.WriteSetTempVerified(temp));
            }
            finally
            {
                SetTempButton.IsEnabled = _connected;
            }
            if (!verified)
            {
                ShowInputError("Temperature set failed: device did not confirm the setpoint.");
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            await SetRunStateVerified(true, StartButton);
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await SetRunStateVerified(false, StopButton);
        }

        /// <summary>后台执行"写入启停+回读验证+不符重发"，避免阻塞 UI。</summary>
        private async Task SetRunStateVerified(bool run, Button button)
        {
            button.IsEnabled = false;
            bool verified;
            try
            {
                verified = await Task.Run(() => _serial.WriteRunStateVerified(run));
            }
            finally
            {
                button.IsEnabled = _connected;
            }
            if (!verified)
            {
                ShowInputError((run ? "Start" : "Stop") + " failed: device did not confirm the run state.");
            }
        }

        // ================= 温度 ramp =================

        private void StartTempRampButton_Click(object sender, RoutedEventArgs e)
        {
            double start, end;
            int duration;
            if (!TryParseDouble(StartTempTextBox.Text, out start) ||
                !TryParseDouble(EndTempTextBox.Text, out end) ||
                !TryParsePositiveInt(DurationTempTextBox.Text, out duration))
            {
                ShowInputError("Please enter valid start, end and duration values.");
                return;
            }

            string error = TryStartTempRamp(start, end, duration);
            if (error != null)
            {
                ShowInputError(error);
            }
        }

        private void StopTempRampButton_Click(object sender, RoutedEventArgs e)
        {
            StopTempRamp();
        }

        private static bool TryParsePositiveInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0;
        }

        /// <summary>
        /// 校验并启动温度 ramp：先写开始值，之后每秒线性推进。
        /// </summary>
        private string TryStartTempRamp(double start, double end, int duration)
        {
            if (!_serial.Connected)
            {
                return "not connected";
            }
            if (start < -40.0 || start > 150.0 || end < -40.0 || end > 150.0)
            {
                return "Temperature must be between -40.0 and 150.0 °C.";
            }
            if (duration <= 0)
            {
                return "Duration must be a positive integer (seconds).";
            }

            lock (_rampLock)
            {
                _tempRamp.StartValue = start;
                _tempRamp.EndValue = end;
                _tempRamp.DurationSeconds = duration;
                _tempRamp.StartTimeUtc = DateTime.UtcNow;
                _tempRamp.LastSetpoint = start;
                _tempRamp.Running = true;
                _tempRamp.HasRun = true;
            }

            _serial.WriteSetTemp(start);
            UpdateRampUi();
            return null;
        }

        /// <summary>停止温度 ramp。目标温度保持最后写入的值。</summary>
        private void StopTempRamp()
        {
            bool changed = false;
            lock (_rampLock)
            {
                if (_tempRamp.Running)
                {
                    _tempRamp.Running = false;
                    changed = true;
                }
            }
            if (changed)
            {
                UpdateRampUi();
            }
        }

        /// <summary>每秒推进温度 ramp（由 UI 定时器驱动）。</summary>
        private void TickRamp()
        {
            lock (_rampLock)
            {
                if (!_tempRamp.Running)
                {
                    return;
                }

                double elapsed = (DateTime.UtcNow - _tempRamp.StartTimeUtc).TotalSeconds;
                if (elapsed >= _tempRamp.DurationSeconds)
                {
                    // 时间到：写入结束值并停止。
                    _serial.WriteSetTemp(_tempRamp.EndValue);
                    _tempRamp.Running = false;
                }
                else
                {
                    double frac = _tempRamp.DurationSeconds > 0
                        ? elapsed / _tempRamp.DurationSeconds
                        : 0;
                    double value = _tempRamp.StartValue
                        + (_tempRamp.EndValue - _tempRamp.StartValue) * frac;
                    // 设定温度按 0.1 粒度写入；变化时再写。
                    double rounded = Math.Round(value * 10.0) / 10.0;
                    if (Math.Abs(rounded - _tempRamp.LastSetpoint) > 0.0001)
                    {
                        _tempRamp.LastSetpoint = rounded;
                        _serial.WriteSetTemp(rounded);
                    }
                }
            }

            UpdateRampUi();
        }

        /// <summary>刷新 ramp 状态文本与控件使能（跨线程安全）。</summary>
        private void UpdateRampUi()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)UpdateRampUi);
                return;
            }

            bool tempRunning = _tempRamp.Running;

            if (_tempRamp.Running)
            {
                double elapsed = (DateTime.UtcNow - _tempRamp.StartTimeUtc).TotalSeconds;
                if (elapsed < 0) elapsed = 0;
                if (elapsed > _tempRamp.DurationSeconds) elapsed = _tempRamp.DurationSeconds;
                TempRampStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                    "Running {0:0}/{1} s", elapsed, _tempRamp.DurationSeconds);
            }
            else if (_tempRamp.HasRun)
            {
                TempRampStatusText.Text = "Stopped";
            }
            else
            {
                TempRampStatusText.Text = "Idle";
            }

            StartTempTextBox.IsEnabled = _connected && !tempRunning;
            EndTempTextBox.IsEnabled = _connected && !tempRunning;
            DurationTempTextBox.IsEnabled = _connected && !tempRunning;
            StartTempRampButton.IsEnabled = _connected && !tempRunning;
            StopTempRampButton.IsEnabled = _connected && tempRunning;
        }

        /// <summary>处理管道 start_temp_ramp 请求。</summary>
        private PipeResponse StartTempRampFromPipe(PipeRequest req)
        {
            int duration;
            if (!IsPositiveInt(req.Duration, out duration))
            {
                return new PipeResponse { Ok = false, Error = "duration must be a positive integer (seconds)" };
            }
            string error = TryStartTempRamp(req.Value, req.End, duration);
            return error != null
                ? new PipeResponse { Ok = false, Error = error }
                : new PipeResponse { Ok = true };
        }

        private static bool IsPositiveInt(double value, out int result)
        {
            result = (int)Math.Round(value);
            return result > 0 && Math.Abs(value - result) < 0.0001;
        }

        // ================= 数值输入校验 =================

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void ShowInputError(string message)
        {
            MessageBox.Show(message, "Yixing Chamber",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = (TextBox)sender;
            string candidate = textBox.Text
                .Remove(textBox.SelectionStart, textBox.SelectionLength)
                .Insert(textBox.SelectionStart, e.Text);

            if (!IsValidNumericInput(textBox, candidate))
            {
                e.Handled = true;
            }
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = (TextBox)sender;
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pasted = (string)e.DataObject.GetData(typeof(string));
                string candidate = textBox.Text
                    .Remove(textBox.SelectionStart, textBox.SelectionLength)
                    .Insert(textBox.SelectionStart, pasted);

                if (!IsValidNumericInput(textBox, candidate))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private bool IsValidNumericInput(TextBox textBox, string candidate)
        {
            // 温度输入框允许负号与一位小数。
            if (ReferenceEquals(textBox, SetTempTextBox) ||
                ReferenceEquals(textBox, StartTempTextBox) ||
                ReferenceEquals(textBox, EndTempTextBox))
            {
                return TempInputRegex.IsMatch(candidate);
            }

            // ramp 时长只接受整秒。
            if (ReferenceEquals(textBox, DurationTempTextBox))
            {
                return DurationInputRegex.IsMatch(candidate);
            }

            return TempInputRegex.IsMatch(candidate);
        }

        // ================= Named pipe server =================

        private void ApplyPipeButton_Click(object sender, RoutedEventArgs e)
        {
            if (PipeToggleButton.IsChecked == true)
            {
                RestartPipeServer();
            }
        }

        private void PipeToggle_Checked(object sender, RoutedEventArgs e)
        {
            PipeToggleButton.Content = "Pipe: ON";
            if (!StartPipeServer())
            {
                PipeToggleButton.IsChecked = false;
            }
        }

        private void PipeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            PipeToggleButton.Content = "Pipe: OFF";
            StopPipeServer();
        }

        private void RestartPipeServer()
        {
            StopPipeServer();
            if (!StartPipeServer())
            {
                PipeToggleButton.IsChecked = false;
            }
        }

        private bool StartPipeServer()
        {
            string pipeName = PipeNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(pipeName))
            {
                MessageBox.Show("Pipe name cannot be empty.", "Yixing Chamber",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (IsPipeNameInUse(pipeName))
            {
                MessageBox.Show("The named pipe '" + pipeName + "' is already in use by another process.\n" +
                                "Close the other instance or use a different pipe name.",
                                "Yixing Chamber", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                using (var probe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    10,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous))
                {
                    // Created OK → the name is available.
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start the named pipe server '" + pipeName + "': " + ex.Message,
                                "Yixing Chamber", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            _pipeRunning = true;
            _pipeAcceptThread = new Thread(() => PipeAcceptLoop(pipeName))
            {
                IsBackground = true,
                Name = "PipeAccept"
            };
            _pipeAcceptThread.Start();
            return true;
        }

        private static bool IsPipeNameInUse(string pipeName)
        {
            try
            {
                using (var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    probe.Connect(100);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StopPipeServer()
        {
            _pipeRunning = false;
            try
            {
                if (_pipeAcceptThread != null && _pipeAcceptThread.IsAlive)
                {
                    using (var wake = new NamedPipeClientStream(".", PipeNameTextBox.Text.Trim(), PipeDirection.In))
                    {
                        wake.Connect(100);
                    }
                    _pipeAcceptThread.Join(200);
                }
            }
            catch
            {
                // No client-side wake-up possible.
            }
            _pipeAcceptThread = null;
        }

        private void PipeAcceptLoop(string pipeName)
        {
            while (_pipeRunning)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        10,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    server.WaitForConnection();

                    var handler = new Thread(() => HandlePipeClient(server))
                    {
                        IsBackground = true,
                        Name = "PipeClient"
                    };
                    handler.Start();
                }
                catch
                {
                    if (server != null)
                    {
                        server.Dispose();
                    }
                    if (!_pipeRunning)
                    {
                        break;
                    }
                }
            }
        }

        private void HandlePipeClient(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                using (var reader = new StreamReader(server))
                using (var writer = new StreamWriter(server) { AutoFlush = true })
                {
                    string line;
                    while (_pipeRunning && (line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0)
                        {
                            continue;
                        }
                        string response = ProcessPipeRequest(line.Trim());
                        writer.WriteLine(response);
                    }
                }
            }
            catch
            {
                // Client disconnected or pipe error → drop this connection.
            }
        }

        /// <summary>解析管道请求并返回响应（仅温度命令）。</summary>
        private string ProcessPipeRequest(string json)
        {
            var ser = new JavaScriptSerializer();
            try
            {
                var req = ser.Deserialize<PipeRequest>(json);
                if (req == null || string.IsNullOrEmpty(req.Cmd))
                {
                    return ser.Serialize(new PipeResponse { Ok = false, Error = "invalid request" });
                }

                switch (req.Cmd.ToLowerInvariant())
                {
                    case "get_status":
                        return ser.Serialize(BuildStatusResponse());

                    case "set_temp":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        if (req.Value < -40.0 || req.Value > 150.0)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "Temperature must be between -40.0 and 150.0 °C." });
                        }
                        StopTempRamp();
                        // 写入并回读验证，不符自动重发，直到一致或超时。
                        if (!_serial.WriteSetTempVerified(req.Value))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "verify failed: setpoint readback mismatch" });
                        }
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "start":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        // 写入并回读验证运行状态，不符自动重发，直到一致或超时。
                        if (!_serial.WriteRunStateVerified(true))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "verify failed: run state readback mismatch" });
                        }
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "stop":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        if (!_serial.WriteRunStateVerified(false))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "verify failed: run state readback mismatch" });
                        }
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "start_temp_ramp":
                        return ser.Serialize(StartTempRampFromPipe(req));

                    case "stop_temp_ramp":
                        StopTempRamp();
                        return ser.Serialize(new PipeResponse { Ok = true });

                    default:
                        return ser.Serialize(new PipeResponse { Ok = false, Error = "unknown command: " + req.Cmd });
                }
            }
            catch (Exception ex)
            {
                return ser.Serialize(new PipeResponse { Ok = false, Error = "bad request: " + ex.Message });
            }
        }

        private PipeResponse BuildStatusResponse()
        {
            var resp = new PipeResponse
            {
                Ok = true,
                HasStatus = _hasStatus,
                CurTemp = _curTemp,
                TgtTemp = _tgtTemp,
                RunState = _runState
            };

            lock (_rampLock)
            {
                bool running = _tempRamp.Running;
                double start = _tempRamp.StartValue;
                double end = _tempRamp.EndValue;
                double duration = _tempRamp.DurationSeconds;
                double elapsed = 0, target = 0;

                if (running)
                {
                    elapsed = (DateTime.UtcNow - _tempRamp.StartTimeUtc).TotalSeconds;
                    if (elapsed < 0) elapsed = 0;
                    if (elapsed > duration) elapsed = duration;
                    target = duration > 0 ? start + (end - start) * (elapsed / duration) : end;
                }

                double remaining = duration - elapsed;
                if (remaining < 0) remaining = 0;

                resp.TempRampRunning = running;
                resp.TempRampStart = start;
                resp.TempRampEnd = end;
                resp.TempRampDuration = duration;
                resp.TempRampElapsed = elapsed;
                resp.TempRampRemaining = remaining;
                resp.TempRampTarget = target;
            }
            return resp;
        }

        // ================= 状态显示 =================

        private void UpdateDisplay(double curTemp, double tgtTemp, byte runState)
        {
            if (!_connected)
            {
                return; // 断开后的过期帧
            }
            _curTemp = curTemp;
            _tgtTemp = tgtTemp;
            _runState = runState;
            _hasStatus = true;

            CurrentTempText.Text = curTemp.ToString("0.0") + " °C";
            TargetTempText.Text = tgtTemp.ToString("0.0") + " °C";
            RunStateText.Text = runState == 1 ? "Running" : "Stopped";
        }

        private void ResetDisplay()
        {
            CurrentTempText.Text = "--";
            TargetTempText.Text = "--";
            RunStateText.Text = "--";
        }

        private void UpdateConnectedUi(bool connected)
        {
            _connected = connected;
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            PortComboBox.IsEnabled = !connected;
            RefreshButton.IsEnabled = !connected;

            SetTempTextBox.IsEnabled = connected;
            SetTempButton.IsEnabled = connected;
            StartButton.IsEnabled = connected;
            StopButton.IsEnabled = connected;

            UpdateRampUi();
        }

        // ================= 端口扫描 =================

        private async void RefreshPorts()
        {
            RefreshButton.IsEnabled = false;
            PortComboBox.ItemsSource = null;

            try
            {
                List<string> ports = await Task.Run(() => YixingSerial.DetectPorts());
                PortComboBox.ItemsSource = ports;
                // 只探测到一个串口时自动选中。
                if (ports.Count == 1)
                {
                    PortComboBox.SelectedIndex = 0;
                }
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    // ================= Helper types =================

    /// <summary>温度 ramp 任务状态。</summary>
    public sealed class RampTask
    {
        public volatile bool Running;
        public volatile bool HasRun;
        public double StartValue;
        public double EndValue;
        public int DurationSeconds;
        public DateTime StartTimeUtc;
        public double LastSetpoint;
    }

    /// <summary>
    /// Named pipe 请求：
    ///   {"cmd":"get_status"}
    ///   {"cmd":"set_temp","value":27.5}
    ///   {"cmd":"start"} / {"cmd":"stop"}
    ///   {"cmd":"start_temp_ramp","value":25.0,"end":60.0,"duration":600}
    ///   {"cmd":"stop_temp_ramp"}
    /// </summary>
    public class PipeRequest
    {
        public string Cmd { get; set; }
        public double Value { get; set; }
        public double End { get; set; }
        public double Duration { get; set; }
    }

    /// <summary>Named pipe 响应（PascalCase，仅温度字段）。</summary>
    public class PipeResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool HasStatus { get; set; }
        public double CurTemp { get; set; }
        public double TgtTemp { get; set; }
        public int RunState { get; set; }

        // Temperature ramp state.
        public bool TempRampRunning { get; set; }
        public double TempRampStart { get; set; }
        public double TempRampEnd { get; set; }
        public double TempRampDuration { get; set; }
        public double TempRampElapsed { get; set; }
        public double TempRampRemaining { get; set; }
        public double TempRampTarget { get; set; }
    }
}
