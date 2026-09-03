using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace binder_chamber
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        // ---- BINDER MB2 Modbus registers (BINDER Interface Technical Specifications 10/2017) ----
        private const ushort RegTempAct = 0x1004;       // Temperature actual value (32-bit float)
        private const ushort RegHumAct = 0x100A;        // Humidity actual value (32-bit float)
        private const ushort RegTempSpRead = 0x10B2;    // Temperature set-point (read, float)
        private const ushort RegHumSpRead = 0x10B4;     // Humidity set-point (read, float)
        private const ushort RegTempSpManual = 0x114C;  // Temperature set-point (manual write, float)
        private const ushort RegHumSpManual = 0x114E;   // Humidity set-point (manual write, float)
        private const ushort RegFanSpRead = 0x10B6;     // Fan speed set-point (read, float)
        private const ushort RegFanSpManual = 0x1150;   // Fan speed set-point (manual write, float)

        /// <summary>Modbus slave address.</summary>
        private const byte SlaveId = 0x01;

        /// <summary>UDP discovery port and broadcast address.</summary>
        private const int UdpPort = 30718;
        private const string UdpBroadcast = "255.255.255.255";

        /// <summary>TCP control port (raw BINDER Modbus tunnel).</summary>
        private const int TcpPort = 10001;

        /// <summary>Discovery timeout in milliseconds.</summary>
        private const int DiscoveryTimeoutMs = 3000;

        /// <summary>Read/write timeout in milliseconds for TCP commands.</summary>
        private const int TcpTimeoutMs = 1000;

        /// <summary>Connected TCP client and its stream.</summary>
        private TcpClient _tcpClient;
        private NetworkStream _stream;

        /// <summary>Background thread that polls temperature/humidity once per second.</summary>
        private Thread _pollThread;
        private volatile bool _polling;

        /// <summary>Guards request/response transactions on the TCP stream.</summary>
        private readonly object _tcpLock = new object();

        // ---- Named pipe server state ----
        private Thread _pipeAcceptThread;
        private volatile bool _pipeRunning;

        // ---- Latest status, shared with the pipe server ----
        // Note: double fields cannot be volatile; writes happen only on the UI
        // thread, reads from pipe threads see a slightly stale but coherent value.
        private double _curTemp, _tgtTemp, _curHum, _tgtHum, _fanSp;
        private volatile bool _hasStatus;

        /// <summary>Allows "-", digits and at most one decimal digit (for temperature).</summary>
        private static readonly Regex TempInputRegex = new Regex(@"^-?\d*\.?\d{0,1}$");

        /// <summary>Allows digits and at most one decimal digit (for humidity, no sign).</summary>
        private static readonly Regex HumidityInputRegex = new Regex(@"^\d*\.?\d{0,1}$");

        /// <summary>Allows only digits (for ramp duration in whole seconds).</summary>
        private static readonly Regex DurationInputRegex = new Regex(@"^\d*$");

        // ---- Linear ramp (temperature / humidity) task state ----
        private readonly RampTask _tempRamp = new RampTask();
        private readonly RampTask _humRamp = new RampTask();
        private readonly object _rampLock = new object();

        /// <summary>True while the TCP connection is established.</summary>
        private volatile bool _connected;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                DiscoverChambers();
                UpdateRampUi();
            };
            // Pipe server is started by PipeToggleButton (default ON) during InitializeComponent.
        }

        protected override void OnClosed(EventArgs e)
        {
            StopPipeServer();
            Disconnect();
            base.OnClosed(e);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            DiscoverChambers();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connected)
            {
                Disconnect();
                return;
            }

            string ip = (ChamberComboBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show("Please enter or select a chamber IP address.", "BINDER Chamber",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!IsValidIpv4(ip))
            {
                MessageBox.Show("Invalid IP address: " + ip, "BINDER Chamber",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(ip, TcpPort);
                _tcpClient.ReceiveTimeout = TcpTimeoutMs;
                _tcpClient.SendTimeout = TcpTimeoutMs;
                _stream = _tcpClient.GetStream();

                _polling = true;
                _pollThread = new Thread(PollLoop)
                {
                    IsBackground = true,
                    Name = "PollLoop"
                };
                _pollThread.Start();

                UpdateConnectedUi(true);
            }
            catch (Exception ex)
            {
                Disconnect();
                MessageBox.Show("Failed to connect to " + ip + ": " + ex.Message, "BINDER Chamber",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Stop the poll thread, close the TCP connection and reset the UI.
        /// </summary>
        private void Disconnect()
        {
            _polling = false;
            if (_pollThread != null)
            {
                try { _pollThread.Join(1500); } catch { }
                _pollThread = null;
            }

            if (_stream != null)
            {
                try { _stream.Dispose(); } catch { }
                _stream = null;
            }
            if (_tcpClient != null)
            {
                try { _tcpClient.Close(); } catch { }
                _tcpClient = null;
            }

            StopTempRamp();
            StopHumRamp();

            UpdateConnectedUi(false);
            ResetDisplay();
        }

        private void SetTempButton_Click(object sender, RoutedEventArgs e)
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

            // A manual temperature set cancels any running temperature ramp.
            StopTempRamp();

            WriteFloat(RegTempSpManual, temp);
        }

        private void SetHumidityButton_Click(object sender, RoutedEventArgs e)
        {
            double humidity;
            if (!TryParseDouble(SetHumidityTextBox.Text, out humidity))
            {
                ShowInputError("Invalid humidity value.");
                return;
            }
            if (humidity < 0.0 || humidity > 100.0)
            {
                ShowInputError("Humidity must be between 0.0 and 100.0 %RH.");
                return;
            }

            // A manual humidity set cancels any running humidity ramp.
            StopHumRamp();

            WriteFloat(RegHumSpManual, humidity);
        }

        private void SetFanButton_Click(object sender, RoutedEventArgs e)
        {
            double fan;
            if (!TryParseDouble(SetFanTextBox.Text, out fan))
            {
                ShowInputError("Invalid fan speed value.");
                return;
            }
            if (fan < 30.0 || fan > 100.0)
            {
                ShowInputError("Fan speed must be between 30 and 100 %.");
                return;
            }

            WriteFloat(RegFanSpManual, fan);
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // Start/stop is not supported on this BINDER device; the buttons are disabled.
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            // Start/stop is not supported on this BINDER device; the buttons are disabled.
        }

        // ================= Linear ramp (temperature / humidity) =================

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

        private void StartHumRampButton_Click(object sender, RoutedEventArgs e)
        {
            double start, end;
            int duration;
            if (!TryParseDouble(StartHumTextBox.Text, out start) ||
                !TryParseDouble(EndHumTextBox.Text, out end) ||
                !TryParsePositiveInt(DurationHumTextBox.Text, out duration))
            {
                ShowInputError("Please enter valid start, end and duration values.");
                return;
            }

            string error = TryStartHumRamp(start, end, duration);
            if (error != null)
            {
                ShowInputError(error);
            }
        }

        private void StopHumRampButton_Click(object sender, RoutedEventArgs e)
        {
            StopHumRamp();
        }

        private static bool TryParsePositiveInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0;
        }

        /// <summary>
        /// Validate and start the temperature ramp. Writes the start value
        /// immediately, then advances linearly once per second. Returns an
        /// error message, or null on success.
        /// </summary>
        private string TryStartTempRamp(double start, double end, int duration)
        {
            if (!_connected)
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
                _tempRamp.LastSetpoint = Math.Round(start * 10.0) / 10.0;
                _tempRamp.Running = true;
                _tempRamp.HasRun = true;
            }

            WriteFloat(RegTempSpManual, start);
            UpdateRampUi();
            return null;
        }

        private string TryStartHumRamp(double start, double end, int duration)
        {
            if (!_connected)
            {
                return "not connected";
            }
            if (start < 0.0 || start > 100.0 || end < 0.0 || end > 100.0)
            {
                return "Humidity must be between 0.0 and 100.0 %RH.";
            }
            if (duration <= 0)
            {
                return "Duration must be a positive integer (seconds).";
            }

            lock (_rampLock)
            {
                _humRamp.StartValue = start;
                _humRamp.EndValue = end;
                _humRamp.DurationSeconds = duration;
                _humRamp.StartTimeUtc = DateTime.UtcNow;
                _humRamp.LastSetpoint = Math.Round(start * 10.0) / 10.0;
                _humRamp.Running = true;
                _humRamp.HasRun = true;
            }

            WriteFloat(RegHumSpManual, start);
            UpdateRampUi();
            return null;
        }

        /// <summary>Stop the temperature ramp. The target keeps its last value.</summary>
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

        /// <summary>Stop the humidity ramp. The target keeps its last value.</summary>
        private void StopHumRamp()
        {
            bool changed = false;
            lock (_rampLock)
            {
                if (_humRamp.Running)
                {
                    _humRamp.Running = false;
                    changed = true;
                }
            }
            if (changed)
            {
                UpdateRampUi();
            }
        }

        /// <summary>Called once per second by the poll loop to advance both ramps.</summary>
        private void TickRamps()
        {
            TickRamp(_tempRamp, RegTempSpManual);
            TickRamp(_humRamp, RegHumSpManual);
        }

        /// <summary>
        /// Advance one ramp by one step: write the linear value when the
        /// register-level value changed, and finalize when the duration elapses.
        /// </summary>
        private void TickRamp(RampTask ramp, ushort register)
        {
            lock (_rampLock)
            {
                if (!ramp.Running)
                {
                    return;
                }

                double elapsed = (DateTime.UtcNow - ramp.StartTimeUtc).TotalSeconds;
                if (elapsed >= ramp.DurationSeconds)
                {
                    // Time is up: write the end value once and mark the task stopped.
                    WriteFloat(register, ramp.EndValue);
                    ramp.Running = false;
                }
                else
                {
                    double frac = ramp.DurationSeconds > 0 ? elapsed / ramp.DurationSeconds : 0;
                    double value = ramp.StartValue + (ramp.EndValue - ramp.StartValue) * frac;
                    double rounded = Math.Round(value * 10.0) / 10.0;
                    if (Math.Abs(rounded - ramp.LastSetpoint) > 0.0001)
                    {
                        ramp.LastSetpoint = rounded;
                        WriteFloat(register, value);
                    }
                }
            }

            UpdateRampUi();
        }

        /// <summary>
        /// Refresh the ramp status texts and control enablement. Safe to call
        /// from any thread; marshals to the UI thread when needed.
        /// </summary>
        private void UpdateRampUi()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)UpdateRampUi);
                return;
            }

            UpdateRampStatusText(TempRampStatusText, _tempRamp);
            UpdateRampStatusText(HumRampStatusText, _humRamp);

            bool tempRunning = _tempRamp.Running;
            bool humRunning = _humRamp.Running;

            StartTempTextBox.IsEnabled = _connected && !tempRunning;
            EndTempTextBox.IsEnabled = _connected && !tempRunning;
            DurationTempTextBox.IsEnabled = _connected && !tempRunning;
            StartTempRampButton.IsEnabled = _connected && !tempRunning;
            StopTempRampButton.IsEnabled = _connected && tempRunning;

            StartHumTextBox.IsEnabled = _connected && !humRunning;
            EndHumTextBox.IsEnabled = _connected && !humRunning;
            DurationHumTextBox.IsEnabled = _connected && !humRunning;
            StartHumRampButton.IsEnabled = _connected && !humRunning;
            StopHumRampButton.IsEnabled = _connected && humRunning;
        }

        private void UpdateRampStatusText(TextBlock status, RampTask ramp)
        {
            if (ramp.Running)
            {
                double elapsed = (DateTime.UtcNow - ramp.StartTimeUtc).TotalSeconds;
                if (elapsed < 0) elapsed = 0;
                if (elapsed > ramp.DurationSeconds) elapsed = ramp.DurationSeconds;
                status.Text = string.Format(CultureInfo.InvariantCulture,
                    "Running {0:0}/{1} s", elapsed, ramp.DurationSeconds);
            }
            else if (ramp.HasRun)
            {
                status.Text = "Stopped";
            }
            else
            {
                status.Text = "Idle";
            }
        }

        /// <summary>Handle "start_temp_ramp" from the pipe: validate, start, return a response.</summary>
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

        /// <summary>Handle "start_hum_ramp" from the pipe: validate, start, return a response.</summary>
        private PipeResponse StartHumRampFromPipe(PipeRequest req)
        {
            int duration;
            if (!IsPositiveInt(req.Duration, out duration))
            {
                return new PipeResponse { Ok = false, Error = "duration must be a positive integer (seconds)" };
            }
            string error = TryStartHumRamp(req.Value, req.End, duration);
            return error != null
                ? new PipeResponse { Ok = false, Error = error }
                : new PipeResponse { Ok = true };
        }

        private static bool IsPositiveInt(double value, out int result)
        {
            result = (int)Math.Round(value);
            return result > 0 && Math.Abs(value - result) < 0.0001;
        }

        // ================= BINDER MB2 Modbus tunnel (TCP 10001) =================

        /// <summary>向设备写入一个 32 位 BINDER 浮点数（两个寄存器，字交换）。</summary>
        private bool WriteFloat(ushort address, double value)
        {
            try
            {
                lock (_tcpLock)
                {
                    if (_stream == null)
                    {
                        return false;
                    }

                    byte[] data = EncodeBinderFloat(value);
                    // 7 字节请求头 + 4 字节浮点数据 = 11 字节（与 Python write_float 一致）。
                    var payload = new byte[11];
                    payload[0] = SlaveId;
                    payload[1] = 0x10; // write multiple registers
                    payload[2] = (byte)(address >> 8);
                    payload[3] = (byte)(address & 0xFF);
                    payload[4] = 0x00;
                    payload[5] = 0x02; // two registers
                    payload[6] = 0x04; // four data bytes
                    Array.Copy(data, 0, payload, 7, 4);

                    SendFrame(payload);
                    byte[] resp = RecvExact(8);
                    VerifyCrc(resp);

                    if (resp[0] != SlaveId)
                    {
                        throw new IOException("Unexpected slave address in write response.");
                    }
                    if ((resp[1] & 0x80) != 0)
                    {
                        throw new IOException("Modbus exception code " + resp[2]);
                    }
                    if (resp[1] != 0x06 && resp[1] != 0x10)
                    {
                        throw new IOException("Unexpected write function 0x" + resp[1].ToString("X2"));
                    }
                    return true;
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)HandleConnectionError);
                return false;
            }
        }

        /// <summary>读取一个 32 位 BINDER 浮点数；失败返回 null。</summary>
        private double? ReadFloat(ushort address)
        {
            try
            {
                lock (_tcpLock)
                {
                    if (_stream == null)
                    {
                        return null;
                    }
                    byte[] data = ReadWords(address, 2);
                    return DecodeBinderFloat(data);
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)HandleConnectionError);
                return null;
            }
        }

        /// <summary>读保持寄存器（功能码 0x03），返回数据字节。</summary>
        private byte[] ReadWords(ushort address, ushort words)
        {
            var payload = new byte[6];
            payload[0] = SlaveId;
            payload[1] = 0x03;
            payload[2] = (byte)(address >> 8);
            payload[3] = (byte)(address & 0xFF);
            payload[4] = (byte)(words >> 8);
            payload[5] = (byte)(words & 0xFF);

            SendFrame(payload);

            byte[] header = RecvExact(3);
            byte func = header[1];
            if ((func & 0x80) != 0)
            {
                byte[] exception = Concat(header, RecvExact(2));
                VerifyCrc(exception);
                throw new IOException("Modbus exception code " + header[2]);
            }

            int byteCount = header[2];
            byte[] full = Concat(header, RecvExact(byteCount + 2));
            VerifyCrc(full);

            if (header[0] != SlaveId || func != 0x03)
            {
                throw new IOException("Unexpected Modbus response.");
            }

            var data = new byte[byteCount];
            Array.Copy(full, 3, data, 0, byteCount);
            return data;
        }

        private void SendFrame(byte[] payload)
        {
            byte[] frame = AppendCrc(payload);
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();
        }

        /// <summary>严格读取 n 字节；连接关闭时抛异常。</summary>
        private byte[] RecvExact(int n)
        {
            var buffer = new byte[n];
            int offset = 0;
            while (offset < n)
            {
                int read = _stream.Read(buffer, offset, n - offset);
                if (read <= 0)
                {
                    throw new IOException("Connection closed by chamber.");
                }
                offset += read;
            }
            return buffer;
        }

        /// <summary>CRC16-Modbus 校验，低字节在前。</summary>
        private static ushort Crc16Modbus(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0
                        ? (ushort)((crc >> 1) ^ 0xA001)
                        : (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        private static byte[] AppendCrc(byte[] payload)
        {
            ushort crc = Crc16Modbus(payload, 0, payload.Length);
            var frame = new byte[payload.Length + 2];
            Array.Copy(payload, frame, payload.Length);
            frame[payload.Length] = (byte)(crc & 0xFF);
            frame[payload.Length + 1] = (byte)(crc >> 8);
            return frame;
        }

        private static void VerifyCrc(byte[] frame)
        {
            if (frame.Length < 4)
            {
                throw new IOException("Short Modbus response.");
            }
            ushort calc = Crc16Modbus(frame, 0, frame.Length - 2);
            ushort got = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            if (calc != got)
            {
                throw new IOException("CRC error in Modbus response.");
            }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }

        /// <summary>
        /// BINDER 32 位浮点数按两个 16 位寄存器存储，字序与 IEEE-754 相反：
        /// 寄存器字节 b = [r1hi, r1lo, r2hi, r2lo]，IEEE 顺序为 [b2, b3, b0, b1]。
        /// </summary>
        private static double DecodeBinderFloat(byte[] b)
        {
            if (b.Length != 4)
            {
                throw new IOException("Float requires 4 bytes.");
            }
            uint bits = ((uint)b[2] << 24) | ((uint)b[3] << 16) | ((uint)b[0] << 8) | b[1];
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        private static byte[] EncodeBinderFloat(double value)
        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
            byte[] ieee =
            {
                (byte)(bits >> 24),
                (byte)(bits >> 16),
                (byte)(bits >> 8),
                (byte)bits
            };
            return new[] { ieee[2], ieee[3], ieee[0], ieee[1] };
        }

        /// <summary>Background poll loop: advance ramps and read status once per second.</summary>
        private void PollLoop()
        {
            while (_polling)
            {
                TickRamps();
                ReadStatus();
                Thread.Sleep(1000);
            }
        }

        /// <summary>
        /// Read the temperature/humidity actual and set-point values, then
        /// refresh the status display on the UI thread.
        /// </summary>
        private void ReadStatus()
        {
            double? curTemp = ReadFloat(RegTempAct);
            double? tgtTemp = ReadFloat(RegTempSpRead);
            double? curHum = ReadFloat(RegHumAct);
            double? tgtHum = ReadFloat(RegHumSpRead);
            double? fanSp = ReadFloat(RegFanSpRead);

            double dCurTemp = curTemp ?? _curTemp;
            double dTgtTemp = tgtTemp ?? _tgtTemp;
            double dCurHum = curHum ?? _curHum;
            double dTgtHum = tgtHum ?? _tgtHum;
            double dFanSp = fanSp ?? _fanSp;

            Dispatcher.BeginInvoke((Action)(() =>
                UpdateDisplay(dCurTemp, dTgtTemp, dCurHum, dTgtHum, dFanSp)));
        }

        /// <summary>Called on the UI thread when the TCP connection is lost.</summary>
        private void HandleConnectionError()
        {
            Disconnect();
            MessageBox.Show("TCP connection lost.", "BINDER Chamber",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void ShowInputError(string message)
        {
            MessageBox.Show(message, "Lab Software",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// Allow only legal numeric characters while typing:
        /// temperature box accepts "-", digits and one decimal point;
        /// humidity box accepts digits and one decimal point (no sign).
        /// </summary>
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

        /// <summary>Reject pasted text that does not form a legal numeric value.</summary>
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
            // Temperature boxes (including ramp start/end) allow a minus sign.
            if (ReferenceEquals(textBox, SetTempTextBox) ||
                ReferenceEquals(textBox, StartTempTextBox) ||
                ReferenceEquals(textBox, EndTempTextBox))
            {
                return TempInputRegex.IsMatch(candidate);
            }

            // Ramp duration boxes accept whole seconds only.
            if (ReferenceEquals(textBox, DurationTempTextBox) ||
                ReferenceEquals(textBox, DurationHumTextBox))
            {
                return DurationInputRegex.IsMatch(candidate);
            }

            return HumidityInputRegex.IsMatch(candidate);
        }

        private void UpdateDisplay(double curTemp, double tgtTemp, double curHum, double tgtHum, double fanSp)
        {
            _curTemp = curTemp;
            _tgtTemp = tgtTemp;
            _curHum = curHum;
            _tgtHum = tgtHum;
            _fanSp = fanSp;
            _hasStatus = true;

            CurrentTempText.Text = curTemp.ToString("0.0") + " °C";
            TargetTempText.Text = tgtTemp.ToString("0.0") + " °C";
            CurrentHumidityText.Text = curHum.ToString("0.0") + " %RH";
            TargetHumidityText.Text = tgtHum.ToString("0.0") + " %RH";
            FanSpText.Text = fanSp.ToString("0.0") + " %";
        }

        private void ResetDisplay()
        {
            CurrentTempText.Text = "--";
            TargetTempText.Text = "--";
            CurrentHumidityText.Text = "--";
            TargetHumidityText.Text = "--";
            FanSpText.Text = "--";
        }

        private void UpdateConnectedUi(bool connected)
        {
            _connected = connected;
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            ChamberComboBox.IsEnabled = !connected;
            RefreshButton.IsEnabled = !connected;

            SetTempTextBox.IsEnabled = connected;
            SetTempButton.IsEnabled = connected;
            SetHumidityTextBox.IsEnabled = connected;
            SetHumidityButton.IsEnabled = connected;
            SetFanTextBox.IsEnabled = connected;
            SetFanButton.IsEnabled = connected;

            UpdateRampUi();
        }

        // ================= Named pipe server =================

        private void ApplyPipeButton_Click(object sender, RoutedEventArgs e)
        {
            // Only meaningful while the pipe server is enabled.
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
                // Creation failed → revert the toggle so the UI matches reality.
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

        /// <summary>
        /// Start the pipe server. Returns false (and shows a message box)
        /// if the pipe name is already served by another process or a pipe
        /// instance cannot be created (e.g. access denied).
        /// </summary>
        private bool StartPipeServer()
        {
            string pipeName = PipeNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(pipeName))
            {
                MessageBox.Show("Pipe name cannot be empty.", "Chamber",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Windows allows several processes to create servers for the same
            // pipe name (they share it and steal connections from each other).
            // Detect the conflict up front by connecting as a client: if a
            // server is already listening, the pipe name is taken.
            if (IsPipeNameInUse(pipeName))
            {
                MessageBox.Show("The named pipe '" + pipeName + "' is already in use by another process.\n" +
                                "Close the other instance or use a different pipe name.",
                                "Chamber", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Probe: try to create one instance of the pipe. This fails fast
            // when access is denied or the instance limit is exhausted.
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
                                "Chamber", MessageBoxButton.OK, MessageBoxImage.Error);
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

        /// <summary>
        /// Return true if another process is currently serving the pipe name.
        /// A client connection that succeeds within a short timeout proves a
        /// server is listening; a timeout means the name is free.
        /// </summary>
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
            catch (TimeoutException)
            {
                return false; // No server listening → name is free.
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (Exception)
            {
                return false; // Unexpected error → treat as free; server probe will catch real failures.
            }
        }

        /// <summary>Stop the accept loop and free the pipe resources.</summary>
        private void StopPipeServer()
        {
            _pipeRunning = false;

            // A blocking WaitForConnection cannot be aborted from another
            // thread; wake it up by connecting to the pipe once.
            try
            {
                if (_pipeAcceptThread != null && _pipeAcceptThread.IsAlive)
                {
                    using (var wake = new NamedPipeClientStream(".", PipeNameTextBox.Text.Trim(), PipeDirection.In))
                    {
                        wake.Connect(100);
                    }
                    // Wait for the accept loop to exit so the pipe is fully
                    // released before a restart probes for it again.
                    _pipeAcceptThread.Join(200);
                }
            }
            catch
            {
                // No client-side wake-up possible → thread exits on next loop check.
            }

            _pipeAcceptThread = null;
        }

        /// <summary>
        /// Accept loop: creates a new server instance, waits for one client,
        /// hands it to a handler thread and loops.
        /// </summary>
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
                        10, // max server instances (concurrent clients)
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

        /// <summary>Serve one connected client: read JSON lines, reply with JSON lines.</summary>
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

        /// <summary>Parse a JSON request and produce a JSON response.</summary>
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
                        // A manual temperature set cancels any running temperature ramp.
                        StopTempRamp();
                        WriteFloat(RegTempSpManual, req.Value);
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "set_humidity":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        if (req.Value < 0.0 || req.Value > 100.0)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "Humidity must be between 0.0 and 100.0 %RH." });
                        }
                        // A manual humidity set cancels any running humidity ramp.
                        StopHumRamp();
                        WriteFloat(RegHumSpManual, req.Value);
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "set_fan":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        if (req.Value < 30.0 || req.Value > 100.0)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "Fan speed must be between 30 and 100 %." });
                        }
                        WriteFloat(RegFanSpManual, req.Value);
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "start":
                    case "stop":
                        // Start/stop is not supported on this BINDER device.
                        return ser.Serialize(new PipeResponse { Ok = false, Error = "unsupported" });

                    case "start_temp_ramp":
                        return ser.Serialize(StartTempRampFromPipe(req));

                    case "stop_temp_ramp":
                        StopTempRamp();
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "start_hum_ramp":
                        return ser.Serialize(StartHumRampFromPipe(req));

                    case "stop_hum_ramp":
                        StopHumRamp();
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
                CurHum = _curHum,
                TgtHum = _tgtHum,
                FanSp = _fanSp,
                RunState = 0 // Run/stop is not supported on this device.
            };

            lock (_rampLock)
            {
                FillRampResponse(resp, _tempRamp, true);
                FillRampResponse(resp, _humRamp, false);
            }
            return resp;
        }

        /// <summary>Copy a ramp task's state into the pipe response fields.</summary>
        private static void FillRampResponse(PipeResponse resp, RampTask ramp, bool isTemp)
        {
            bool running = ramp.Running;
            double start = ramp.StartValue;
            double end = ramp.EndValue;
            double duration = ramp.DurationSeconds;
            double elapsed = 0, target = 0;

            if (running)
            {
                elapsed = (DateTime.UtcNow - ramp.StartTimeUtc).TotalSeconds;
                if (elapsed < 0) elapsed = 0;
                if (elapsed > duration) elapsed = duration;
                target = duration > 0 ? start + (end - start) * (elapsed / duration) : end;
            }

            double remaining = duration - elapsed;
            if (remaining < 0) remaining = 0;

            if (isTemp)
            {
                resp.TempRampRunning = running;
                resp.TempRampStart = start;
                resp.TempRampEnd = end;
                resp.TempRampDuration = duration;
                resp.TempRampElapsed = elapsed;
                resp.TempRampRemaining = remaining;
                resp.TempRampTarget = target;
            }
            else
            {
                resp.HumRampRunning = running;
                resp.HumRampStart = start;
                resp.HumRampEnd = end;
                resp.HumRampDuration = duration;
                resp.HumRampElapsed = elapsed;
                resp.HumRampRemaining = remaining;
                resp.HumRampTarget = target;
            }
        }

        /// <summary>
        /// Discover BINDER MB2 chambers by broadcasting a probe datagram to
        /// 255.255.255.255:30718 and collecting the source IP of each answer.
        /// </summary>
        private async void DiscoverChambers()
        {
            RefreshButton.IsEnabled = false;
            ChamberComboBox.ItemsSource = null;

            try
            {
                List<string> ips = await Task.Run(() => DiscoverChambersCore());
                ChamberComboBox.ItemsSource = ips;
                if (ips.Count == 0)
                {
                    MessageBox.Show("No BINDER chamber found on the network.", "BINDER Chamber",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Broadcast a probe and collect the IP address of every chamber that
        /// answers within the discovery timeout.
        /// </summary>
        private List<string> DiscoverChambersCore()
        {
            var result = new List<string>();
            var seen = new HashSet<string>();

            try
            {
                using (var udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    udp.Client.ReceiveTimeout = 500;

                    byte[] payload = { 0x00 };
                    var ep = new IPEndPoint(IPAddress.Parse(UdpBroadcast), UdpPort);
                    udp.Send(payload, payload.Length, ep);

                    var deadline = DateTime.UtcNow.AddMilliseconds(DiscoveryTimeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            var remote = new IPEndPoint(IPAddress.Any, 0);
                            udp.Receive(ref remote);
                            string ip = remote.Address.ToString();
                            if (seen.Add(ip))
                            {
                                result.Add(ip);
                            }
                        }
                        catch (SocketException)
                        {
                            // Receive timeout → keep waiting until the deadline.
                        }
                    }
                }
            }
            catch
            {
                // Discovery failure → return whatever was collected (possibly empty).
            }

            result.Sort(CompareIp);
            return result;
        }

        /// <summary>
        /// Return true if the string is a well-formed IPv4 address
        /// (four dot-separated octets, each in the range 0..255).
        /// </summary>
        private static bool IsValidIpv4(string ip)
        {
            if (string.IsNullOrEmpty(ip))
            {
                return false;
            }
            if (!Regex.IsMatch(ip, @"^\d{1,3}(\.\d{1,3}){3}$"))
            {
                return false;
            }
            foreach (string part in ip.Split('.'))
            {
                int value;
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                    || value < 0 || value > 255)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Compare two IPv4 addresses numerically.</summary>
        private static int CompareIp(string a, string b)
        {
            return IpToUint(a).CompareTo(IpToUint(b));
        }

        private static uint IpToUint(string ip)
        {
            try
            {
                byte[] bytes = IPAddress.Parse(ip).GetAddressBytes();
                if (bytes.Length != 4)
                {
                    return 0;
                }
                return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// State of one linear-ramp task (temperature or humidity).
        /// </summary>
        private sealed class RampTask
        {
            /// <summary>True while the ramp is advancing.</summary>
            public volatile bool Running;

            /// <summary>True once a ramp has been started (for the "Stopped" status text).</summary>
            public volatile bool HasRun;

            /// <summary>Ramp start value (physical units).</summary>
            public double StartValue;

            /// <summary>Ramp end value (physical units).</summary>
            public double EndValue;

            /// <summary>Ramp duration in whole seconds.</summary>
            public int DurationSeconds;

            /// <summary>UTC time the ramp was started.</summary>
            public DateTime StartTimeUtc;

            /// <summary>Last setpoint value written (0.1 granularity) to avoid redundant writes.</summary>
            public double LastSetpoint;
        }
    }

    /// <summary>
    /// Named pipe request: one JSON line, UTF-8 encoded.
    /// Field names are matched case-insensitively ("cmd" == "Cmd").
    ///   {"cmd":"get_status"}
    ///   {"cmd":"set_temp","value":27.5}
    ///   {"cmd":"set_humidity","value":50.0}
    ///   {"cmd":"start"} / {"cmd":"stop"}
    ///   {"cmd":"start_temp_ramp","value":25.0,"end":60.0,"duration":600}
    ///   {"cmd":"stop_temp_ramp"}
    ///   {"cmd":"start_hum_ramp","value":30.0,"end":80.0,"duration":600}
    ///   {"cmd":"stop_hum_ramp"}
    /// </summary>
    public class PipeRequest
    {
        public string Cmd { get; set; }
        public double Value { get; set; }
        public double End { get; set; }
        public double Duration { get; set; }
    }

    /// <summary>
    /// Named pipe response: one JSON line. Field names are PascalCase
    /// (JavaScriptSerializer serializes C# property names as-is).
    ///   {"Ok":true,"HasStatus":true,"CurTemp":32.7,"TgtTemp":27.0,
    ///    "CurHum":42.5,"TgtHum":50.0,"RunState":0,
    ///    "TempRampRunning":true,"TempRampStart":25.0,"TempRampEnd":60.0,
    ///    "TempRampDuration":600,"TempRampElapsed":12,"TempRampRemaining":588,
    ///    "TempRampTarget":25.7, ...HumRamp*...}
    ///   {"Ok":false,"Error":"..."}
    /// </summary>
    public class PipeResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool HasStatus { get; set; }
        public double CurTemp { get; set; }
        public double TgtTemp { get; set; }
        public double CurHum { get; set; }
        public double TgtHum { get; set; }
        public double FanSp { get; set; }
        public int RunState { get; set; }

        // Temperature ramp state.
        public bool TempRampRunning { get; set; }
        public double TempRampStart { get; set; }
        public double TempRampEnd { get; set; }
        public double TempRampDuration { get; set; }
        public double TempRampElapsed { get; set; }
        public double TempRampRemaining { get; set; }
        public double TempRampTarget { get; set; }

        // Humidity ramp state.
        public bool HumRampRunning { get; set; }
        public double HumRampStart { get; set; }
        public double HumRampEnd { get; set; }
        public double HumRampDuration { get; set; }
        public double HumRampElapsed { get; set; }
        public double HumRampRemaining { get; set; }
        public double HumRampTarget { get; set; }
    }
}
