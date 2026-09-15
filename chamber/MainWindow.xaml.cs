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

namespace lab_software
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Clean serial communication with the chamber.</summary>
        private readonly ChamberSerial _serial = new ChamberSerial();

        /// <summary>1-second timer that advances the ramp tasks (no serial I/O).</summary>
        private DispatcherTimer _rampTimer;

        // ---- Named pipe server state ----
        private Thread _pipeAcceptThread;
        private volatile bool _pipeRunning;

        // ---- Latest status, shared with the pipe server ----
        // Note: double fields cannot be volatile; writes happen only on the UI
        // thread, reads from pipe threads see a slightly stale but coherent value.
        private double _curTemp, _tgtTemp, _curHum, _tgtHum;
        private volatile byte _runState;
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

        /// <summary>True while the serial port is connected (mirror of <see cref="ChamberSerial.Connected"/>).</summary>
        private volatile bool _connected;

        public MainWindow()
        {
            InitializeComponent();

            _serial.StatusReceived += OnSerialStatus;
            _serial.StateChanged += OnSerialStateChanged;

            _rampTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _rampTimer.Tick += (s, e) => TickRamps();
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

            // While reconnecting, clicking the button cancels the retry loop.
            if (_serial.Reconnecting)
            {
                _serial.Stop();
                return;
            }

            string port = PortComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(port))
            {
                MessageBox.Show("Please select a serial port first.", "Lab Software",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _serial.Start(port);
        }

        /// <summary>Raised by the serial I/O thread for every valid status frame.</summary>
        private void OnSerialStatus(ChamberStatus s)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)(() =>
                    UpdateDisplay(s.CurTemp, s.TgtTemp, s.CurHum, s.TgtHum, s.RunState)));
            }
            catch
            {
                // Window / application shutting down.
            }
        }

        /// <summary>Raised by the serial I/O thread when the connection state changes.</summary>
        private void OnSerialStateChanged(ChamberConnectionState state)
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
                        // Any disconnect (reconnecting or final) stops the ramps.
                        StopTempRamp();
                        StopHumRamp();

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
                // Window / application shutting down.
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

            // A manual temperature set cancels any running temperature ramp.
            StopTempRamp();

            // 后台执行"写入+回读验证+不符重发"，避免阻塞 UI。
            SetTempButton.IsEnabled = false;
            bool verified;
            try
            {
                verified = await Task.Run(() => _serial.WriteSetpointVerified(ChamberSerial.AddrSetTemp, temp));
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

        private async void SetHumidityButton_Click(object sender, RoutedEventArgs e)
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

            SetHumidityButton.IsEnabled = false;
            bool verified;
            try
            {
                verified = await Task.Run(() => _serial.WriteSetpointVerified(ChamberSerial.AddrSetHumidity, humidity));
            }
            finally
            {
                SetHumidityButton.IsEnabled = _connected;
            }
            if (!verified)
            {
                ShowInputError("Humidity set failed: device did not confirm the setpoint.");
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            _serial.WriteRegister(ChamberSerial.AddrRunStop, 1);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _serial.WriteRegister(ChamberSerial.AddrRunStop, 0);
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

        /// <summary>Round a physical value to the register granularity (0.1).</summary>
        private static short RoundReg(double value)
        {
            return (short)Math.Round(value * 10.0);
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

            short startReg = RoundReg(start);
            lock (_rampLock)
            {
                _tempRamp.StartValue = start;
                _tempRamp.EndValue = end;
                _tempRamp.DurationSeconds = duration;
                _tempRamp.StartTimeUtc = DateTime.UtcNow;
                _tempRamp.LastReg = startReg;
                _tempRamp.Running = true;
                _tempRamp.HasRun = true;
            }

            _serial.WriteRegister(ChamberSerial.AddrSetTemp, startReg);
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

            short startReg = RoundReg(start);
            lock (_rampLock)
            {
                _humRamp.StartValue = start;
                _humRamp.EndValue = end;
                _humRamp.DurationSeconds = duration;
                _humRamp.StartTimeUtc = DateTime.UtcNow;
                _humRamp.LastReg = startReg;
                _humRamp.Running = true;
                _humRamp.HasRun = true;
            }

            _serial.WriteRegister(ChamberSerial.AddrSetHumidity, startReg);
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

        /// <summary>Called once per second by the poll timer to advance both ramps.</summary>
        private void TickRamps()
        {
            TickRamp(_tempRamp, ChamberSerial.AddrSetTemp);
            TickRamp(_humRamp, ChamberSerial.AddrSetHumidity);
        }

        /// <summary>
        /// Advance one ramp by one step: write the linear value when the
        /// register-level value changed, and finalize when the duration elapses.
        /// Runs on the polling thread.
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
                    _serial.WriteRegister(register, RoundReg(ramp.EndValue));
                    ramp.Running = false;
                }
                else
                {
                    double frac = ramp.DurationSeconds > 0 ? elapsed / ramp.DurationSeconds : 0;
                    double value = ramp.StartValue + (ramp.EndValue - ramp.StartValue) * frac;
                    short reg = RoundReg(value);
                    if (reg != ramp.LastReg)
                    {
                        ramp.LastReg = reg;
                        _serial.WriteRegister(register, reg);
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

        private void UpdateDisplay(double curTemp, double tgtTemp, double curHum, double tgtHum, byte runState)
        {
            if (!_connected)
            {
                return; // stale frame delivered during/after teardown
            }
            _curTemp = curTemp;
            _tgtTemp = tgtTemp;
            _curHum = curHum;
            _tgtHum = tgtHum;
            _runState = runState;
            _hasStatus = true;

            CurrentTempText.Text = curTemp.ToString("0.0") + " °C";
            TargetTempText.Text = tgtTemp.ToString("0.0") + " °C";
            CurrentHumidityText.Text = curHum.ToString("0.0") + " %RH";
            TargetHumidityText.Text = tgtHum.ToString("0.0") + " %RH";
            RunStateText.Text = runState == 1 ? "Running" : "Stopped";
        }

        private void ResetDisplay()
        {
            CurrentTempText.Text = "--";
            TargetTempText.Text = "--";
            CurrentHumidityText.Text = "--";
            TargetHumidityText.Text = "--";
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
            SetHumidityTextBox.IsEnabled = connected;
            SetHumidityButton.IsEnabled = connected;
            StartButton.IsEnabled = connected;
            StopButton.IsEnabled = connected;

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
                        // 写入并回读验证，不符自动重发，直到一致或超时。
                        if (!_serial.WriteSetpointVerified(ChamberSerial.AddrSetTemp, req.Value))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "verify failed: setpoint readback mismatch" });
                        }
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
                        if (!_serial.WriteSetpointVerified(ChamberSerial.AddrSetHumidity, req.Value))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "verify failed: setpoint readback mismatch" });
                        }
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "start":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        _serial.WriteRegister(ChamberSerial.AddrRunStop, 1);
                        return ser.Serialize(new PipeResponse { Ok = true });

                    case "stop":
                        if (!_connected)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "not connected" });
                        }
                        _serial.WriteRegister(ChamberSerial.AddrRunStop, 0);
                        return ser.Serialize(new PipeResponse { Ok = true });

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
                RunState = _runState
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
        /// Scan all serial ports at 9600 baud and keep only those that
        /// respond to the chamber read-status command within 500 ms.
        /// Runs off the UI thread so the window stays responsive.
        /// </summary>
        private async void RefreshPorts()
        {
            RefreshButton.IsEnabled = false;
            PortComboBox.ItemsSource = null;

            try
            {
                List<string> ports = await Task.Run(() => ChamberSerial.DetectPorts());
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

            /// <summary>Last register value written (granularity 0.1) to avoid redundant writes.</summary>
            public short LastReg;
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
