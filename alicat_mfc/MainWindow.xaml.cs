using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace alicat_mfc
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Default Alicat baud rate.</summary>
        private const int DefaultBaudRate = 19200;

        /// <summary>Probe response timeout in milliseconds.</summary>
        private const int ProbeTimeoutMs = 500;

        /// <summary>Poll interval in milliseconds.</summary>
        private const int PollIntervalMs = 1000;

        // ---- 设定流量回读验证（写入 → 等待轮询回读 Setpoint → 不符重发）----
        private const double SetVerifyTolerance = 0.02;       // SCCM；写入量化 1/128≈0.008，覆盖回读显示误差
        private const int SetVerifyRetryWindowMs = 1500;      // 单次写入后等待 1s 周期状态轮询回读的窗口
        private const int SetVerifyTimeoutMs = 10000;         // 验证总超时，超时仍未符则报失败

        /// <summary>Gap between commands to different addresses on the same port.</summary>
        private const int InterCommandGapMs = 30;

        /// <summary>Default pipe server name.</summary>
        private const string DefaultPipeName = "alicat_mfc";

        /// <summary>Maximum per-device flow setpoint in SCCM.</summary>
        private const double MaxFlowSccm = 500.0;

        /// <summary>Maximum concentration value (unitless integer).</summary>
        private const double MaxConcentration = 1000000.0;

        /// <summary>Maximum total flow in SCCM.</summary>
        private const double MaxTotalFlowSccm = 1000.0;

        // ---- Gas mixing parameters (null = not set) ----
        private double? _sourceConc;
        private double? _totalFlow;
        private double? _targetConc;

        // ---- Persisted config (used during startup to re-apply port selection) ----
        // Starts true so role-radio Checked events fired while InitializeComponent
        // parses the XAML (IsChecked="True") do not trigger SaveConfig and wipe
        // the persisted ports before LoadConfig runs.
        private bool _loadingConfig = true;

        // ---- Device/channel state ----
        private readonly MfcDevice[] _devices = new MfcDevice[2];
        private readonly Dictionary<string, MfcChannel> _channels = new Dictionary<string, MfcChannel>();

        /// <summary>Background poll thread.</summary>
        private Thread _pollThread;
        private volatile bool _pollRunning;

        /// <summary>Per-connection stop flag for the poll thread.</summary>
        private PollSession _pollSession;

        /// <summary>True while the background thread is retrying to reconnect.</summary>
        private volatile bool _autoReconnect;

        /// <summary>Background thread that retries the serial connection after an unexpected loss.</summary>
        private Thread _reconnectThread;

        /// <summary>Delay between auto-reconnect attempts.</summary>
        private const int ReconnectDelayMs = 2000;

        /// <summary>
        /// If a channel receives no valid response within this time while
        /// connected, it is treated as lost and auto-reconnect starts.
        /// </summary>
        private const int ResponseWatchdogMs = 5000;

        // ---- Last successful connection profile (used for auto-reconnect) ----
        private string _connPort1, _connPort2;
        private string _connAddr1, _connAddr2;
        private int _connBaud1 = DefaultBaudRate, _connBaud2 = DefaultBaudRate;
        private bool _connTarget1, _connTarget2;

        // ---- Named pipe server state ----
        private Thread _pipeAcceptThread;
        private volatile bool _pipeRunning;

        /// <summary>Allows a single uppercase letter A-Z (device address).</summary>
        private static readonly Regex AddressRegex = new Regex(@"^[A-Za-z]$");

        /// <summary>Allows digits and up to two decimal digits (flow in SCCM).</summary>
        private static readonly Regex FlowInputRegex = new Regex(@"^\d*\.?\d{0,2}$");

        /// <summary>Allows a non-negative integer (gas number).</summary>
        private static readonly Regex GasInputRegex = new Regex(@"^\d*$");

        /// <summary>Allows a positive integer (baud rate).</summary>
        private static readonly Regex BaudInputRegex = new Regex(@"^\d*$");

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                LoadConfig();
                RefreshPorts();
            };
            // Pipe server is started by PipeToggleButton (default ON) during InitializeComponent.
        }

        protected override void OnClosed(EventArgs e)
        {
            IniConfig.Write("Window", "Width", Width.ToString(CultureInfo.InvariantCulture));
            IniConfig.Write("Window", "Height", Height.ToString(CultureInfo.InvariantCulture));

            StopPipeServer();
            Disconnect();
            base.OnClosed(e);
        }

        // ================= Serial port probing =================

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        /// <summary>
        /// Scan all serial ports in parallel; a port is kept if any configured
        /// device (address + baud rate) answers the Alicat read command with a
        /// line that starts with that address.
        /// </summary>
        private async void RefreshPorts()
        {
            RefreshButton.IsEnabled = false;

            var probes = new List<ProbeSpec>();
            AddProbe(probes, AddressTextBox1.Text, BaudTextBox1.Text);
            AddProbe(probes, AddressTextBox2.Text, BaudTextBox2.Text);
            if (probes.Count == 0) probes.Add(new ProbeSpec("A", DefaultBaudRate));

            try
            {
                List<string> ports = await Task.Run(() => DetectRespondingPorts(probes));

                // Assigning ItemsSource resets the editable ComboBox text; preserve
                // the current port values so the scan only refreshes the dropdown.
                string port1 = PortComboBox1.Text;
                string port2 = PortComboBox2.Text;
                PortComboBox1.ItemsSource = ports;
                PortComboBox2.ItemsSource = ports;
                PortComboBox1.Text = port1;
                PortComboBox2.Text = port2;
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        /// <summary>Add a (address, baud rate) probe, skipping invalid or duplicate entries.</summary>
        private void AddProbe(List<ProbeSpec> probes, string addrText, string baudText)
        {
            string addr = (addrText ?? "").Trim().ToUpperInvariant();
            if (!IsValidAddress(addr)) return;
            int baud = ParseBaud(baudText);
            if (!probes.Any(p => p.Address == addr && p.BaudRate == baud))
            {
                probes.Add(new ProbeSpec(addr, baud));
            }
        }

        private List<string> DetectRespondingPorts(List<ProbeSpec> probes)
        {
            string[] allPorts = SerialPort.GetPortNames();

            var responding = new ConcurrentBag<string>();
            Parallel.ForEach(allPorts, portName =>
            {
                if (ProbePort(portName, probes))
                {
                    responding.Add(portName);
                }
            });

            var result = new List<string>(responding);
            result.Sort((a, b) => PortNumber(a).CompareTo(PortNumber(b)));
            return result;
        }

        /// <summary>
        /// Open the port and try each (address, baud rate) probe. A port is
        /// considered responding if any probe receives a line starting with
        /// that address.
        /// </summary>
        private bool ProbePort(string portName, List<ProbeSpec> probes)
        {
            foreach (ProbeSpec probe in probes)
            {
                try
                {
                    using (var sp = new SerialPort(portName, probe.BaudRate, Parity.None, 8, StopBits.One))
                    {
                        sp.ReadTimeout = ProbeTimeoutMs;
                        sp.WriteTimeout = ProbeTimeoutMs;
                        sp.Open();

                        byte[] cmd = Encoding.ASCII.GetBytes(probe.Address + "\r\r");
                        sp.Write(cmd, 0, cmd.Length);
                        try
                        {
                            string line = ReadLineCr(sp);
                            if (!string.IsNullOrEmpty(line) && line[0] == probe.Address[0])
                            {
                                return true;
                            }
                        }
                        catch (TimeoutException)
                        {
                            // Try the next probe on the same port.
                        }
                    }
                }
                catch (Exception)
                {
                    // Port busy or error → skip this probe.
                }
            }
            return false;
        }

        /// <summary>Read bytes until CR (0x0D), which terminates an Alicat response line.</summary>
        private static string ReadLineCr(SerialPort sp)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = sp.ReadByte();
                if (b < 0) break;
                if (b == '\r') break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        // ================= Connect / Disconnect =================

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_channels.Count > 0)
            {
                Disconnect();
                return;
            }

            // While auto-reconnect is active, clicking the button cancels it.
            if (_autoReconnect)
            {
                _autoReconnect = false;
                ConnectButton.Content = "Connect";
                ConnectionStatusText.Text = "";
                return;
            }

            string port1 = (PortComboBox1.Text ?? "").Trim();
            string port2 = (PortComboBox2.Text ?? "").Trim();
            string addr1 = AddressTextBox1.Text.Trim().ToUpperInvariant();
            string addr2 = AddressTextBox2.Text.Trim().ToUpperInvariant();
            int baud1 = ParseBaud(BaudTextBox1.Text);
            int baud2 = ParseBaud(BaudTextBox2.Text);

            if (string.IsNullOrEmpty(port1) && string.IsNullOrEmpty(port2))
            {
                MessageBox.Show("Please select at least one serial port.", "Alicat MFC",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Only validate the address of a device that actually has a port selected.
            if (!string.IsNullOrEmpty(port1) && !IsValidAddress(addr1))
            {
                MessageBox.Show("Device 1 address must be a single letter A-Z.", "Alicat MFC",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(port2) && !IsValidAddress(addr2))
            {
                MessageBox.Show("Device 2 address must be a single letter A-Z.", "Alicat MFC",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Devices sharing the same port must use different addresses and
            // the same baud rate. On different ports they may use the same
            // address (e.g. both A) and different baud rates.
            if (!string.IsNullOrEmpty(port1) && port1 == port2)
            {
                if (addr1 == addr2)
                {
                    MessageBox.Show("Devices on the same port must use different addresses.", "Alicat MFC",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (baud1 != baud2)
                {
                    MessageBox.Show("Devices on the same port must use the same baud rate.", "Alicat MFC",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Snapshot the connection profile so auto-reconnect can re-open the
            // same ports even if the user changes the UI while disconnected.
            _connPort1 = port1;
            _connPort2 = port2;
            _connAddr1 = addr1;
            _connAddr2 = addr2;
            _connBaud1 = baud1;
            _connBaud2 = baud2;
            _connTarget1 = TargetRadio1.IsChecked == true;
            _connTarget2 = TargetRadio2.IsChecked == true;

            string error;
            if (!OpenConnection(out error))
            {
                MessageBox.Show("Failed to open serial port: " + error, "Alicat MFC",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Open the serial channels from the current connection profile and
        /// start polling. Must run on the UI thread. Returns false and fills
        /// <paramref name="error"/> when a port cannot be opened.
        /// </summary>
        private bool OpenConnection(out string error)
        {
            error = null;
            try
            {
                // Create a device only for each selected port.
                _devices[0] = null;
                _devices[1] = null;
                if (!string.IsNullOrEmpty(_connPort1))
                {
                    _devices[0] = new MfcDevice
                    {
                        Index = 0,
                        Address = _connAddr1,
                        BaudRate = _connBaud1,
                        IsTarget = _connTarget1
                    };
                }
                if (!string.IsNullOrEmpty(_connPort2))
                {
                    _devices[1] = new MfcDevice
                    {
                        Index = 1,
                        Address = _connAddr2,
                        BaudRate = _connBaud2,
                        IsTarget = _connTarget2
                    };
                }

                // Same port for both devices → one channel with two addresses.
                if (!string.IsNullOrEmpty(_connPort1) && _connPort1 == _connPort2)
                {
                    var ch = OpenChannel(_connPort1, _connBaud1);
                    _devices[0].Channel = ch;
                    _devices[1].Channel = ch;
                    ch.Devices.Add(_devices[0]);
                    ch.Devices.Add(_devices[1]);
                }
                else
                {
                    if (!string.IsNullOrEmpty(_connPort1))
                    {
                        var ch1 = OpenChannel(_connPort1, _connBaud1);
                        _devices[0].Channel = ch1;
                        ch1.Devices.Add(_devices[0]);
                    }
                    if (!string.IsNullOrEmpty(_connPort2))
                    {
                        var ch2 = OpenChannel(_connPort2, _connBaud2);
                        _devices[1].Channel = ch2;
                        ch2.Devices.Add(_devices[1]);
                    }
                }

                // A (re)connection succeeded: leave the reconnect state so the
                // response watchdog stays active.
                _autoReconnect = false;
                ConnectionStatusText.Text = "";
                StartPolling();
                UpdateConnectedUi(true);
                SaveConfig();
                return true;
            }
            catch (Exception ex)
            {
                TeardownChannels();
                error = ex.Message;
                return false;
            }
        }

        private MfcChannel OpenChannel(string portName, int baudRate)
        {
            if (_channels.TryGetValue(portName, out var existing))
            {
                return existing;
            }

            var sp = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            sp.ReadTimeout = 1000;
            sp.WriteTimeout = 1000;
            var ch = new MfcChannel { PortName = portName, BaudRate = baudRate, Port = sp };
            sp.DataReceived += (s, e) => Channel_DataReceived(ch);
            sp.Open();
            ch.LastRxUtc = DateTime.UtcNow;
            _channels[portName] = ch;
            return ch;
        }

        private void Disconnect()
        {
            _autoReconnect = false;
            ConnectionStatusText.Text = "";
            TeardownChannels();
            UpdateConnectedUi(false);
            ResetDisplays();
        }

        /// <summary>
        /// Stop polling, close all channels and clear devices. Does not touch
        /// the auto-reconnect state (so a failed reconnect attempt can retry).
        /// </summary>
        private void TeardownChannels()
        {
            StopPolling();

            foreach (var ch in _channels.Values)
            {
                try
                {
                    ch.Port.DataReceived -= (s, e) => Channel_DataReceived(ch);
                }
                catch { }
                try { ch.Port.Close(); } catch { }
                ch.Port.Dispose();
            }
            _channels.Clear();

            for (int i = 0; i < _devices.Length; i++)
            {
                _devices[i] = null;
            }
        }

        // ================= Polling =================

        private void StartPolling()
        {
            _pollSession = new PollSession();
            var session = _pollSession;
            _pollRunning = true;
            _pollThread = new Thread(() => PollLoop(session)) { IsBackground = true, Name = "AlicatPoll" };
            _pollThread.Start();
        }

        private void StopPolling()
        {
            _pollRunning = false;
            if (_pollSession != null)
            {
                _pollSession.Stop = true;
                _pollSession = null;
            }
            _pollThread = null;
        }

        /// <summary>
        /// Every second, for each channel (serial port), send the read command
        /// to every device address on that channel with a small gap in between.
        /// A channel that stops responding for <see cref="ResponseWatchdogMs"/>
        /// is treated as lost and triggers auto-reconnect.
        /// </summary>
        private void PollLoop(PollSession session)
        {
            while (_pollRunning && !session.Stop)
            {
                try
                {
                    foreach (var ch in _channels.Values)
                    {
                        if (ch.Port == null || !ch.Port.IsOpen)
                        {
                            RaiseSerialError("Serial port disconnected");
                            return;
                        }
                        lock (ch.WriteLock)
                        {
                            foreach (var dev in ch.Devices)
                            {
                                byte[] cmd = Encoding.ASCII.GetBytes(dev.Address + "\r\r");
                                ch.Port.Write(cmd, 0, cmd.Length);
                                Thread.Sleep(InterCommandGapMs);
                            }
                        }

                        // 看门狗：连接后长时间收不到任何有效响应 → 判定串口失效。
                        if ((DateTime.UtcNow - ch.LastRxUtc).TotalMilliseconds > ResponseWatchdogMs)
                        {
                            RaiseSerialError("No response from device");
                            return;
                        }
                    }
                }
                catch
                {
                    RaiseSerialError("Serial connection lost");
                    return;
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        /// <summary>Queue a serial-error notification on the UI thread.</summary>
        private void RaiseSerialError(string reason)
        {
            Dispatcher.BeginInvoke((Action)(() => HandleSerialError(reason)));
        }

        /// <summary>
        /// Called when the connection is lost (write error, unplug or watchdog
        /// timeout). Tears the connection down and starts a background thread
        /// that keeps retrying the saved connection profile until it connects
        /// again (or the user cancels).
        /// </summary>
        private void HandleSerialError(string reason)
        {
            // Already reconnecting → keep the state, just refresh the message.
            if (_autoReconnect)
            {
                ConnectionStatusText.Text = reason + " — auto-reconnecting…";
                return;
            }

            // A user-initiated disconnect / window close already tore down the
            // poll session → do not start auto-reconnect for a stale error.
            if (_pollSession == null)
            {
                return;
            }

            Disconnect();

            if (string.IsNullOrEmpty(_connPort1) && string.IsNullOrEmpty(_connPort2))
            {
                return;
            }

            ConnectionStatusText.Text = reason + " — auto-reconnecting…";
            ConnectButton.Content = "Reconnecting...";
            _autoReconnect = true;

            if (_reconnectThread == null || !_reconnectThread.IsAlive)
            {
                _reconnectThread = new Thread(AutoReconnectLoop)
                {
                    IsBackground = true,
                    Name = "AlicatReconnect"
                };
                _reconnectThread.Start();
            }
        }

        /// <summary>
        /// Retry the serial connection every <see cref="ReconnectDelayMs"/>
        /// until it succeeds or auto-reconnect is cancelled. Connection
        /// attempts are marshaled to the UI thread.
        /// </summary>
        private void AutoReconnectLoop()
        {
            while (_autoReconnect)
            {
                bool ok = false;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!_autoReconnect)
                        {
                            return;
                        }
                        string error;
                        ok = OpenConnection(out error);
                        if (!ok)
                        {
                            // Keep the "reconnecting" state visible and show
                            // the reason the latest attempt failed.
                            ConnectionStatusText.Text = "Auto-reconnecting… (failed: " + error + ")";
                            ConnectButton.Content = "Reconnecting...";
                        }
                    });
                }
                catch
                {
                    break; // window closing
                }

                if (ok)
                {
                    return;
                }

                // Wait in small steps so a click on "Reconnecting..." cancels promptly.
                for (int i = 0; i < ReconnectDelayMs / 100 && _autoReconnect; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        // ================= Receive & parse =================

        private void Channel_DataReceived(MfcChannel ch)
        {
            try
            {
                int available = ch.Port.BytesToRead;
                if (available <= 0) return;

                byte[] chunk = new byte[available];
                int read = ch.Port.Read(chunk, 0, available);

                lock (ch.RxLock)
                {
                    ch.RxBuffer.Append(Encoding.ASCII.GetString(chunk, 0, read));
                    ProcessLines(ch);
                }
            }
            catch
            {
                // Transient read error; the poll loop will detect a dead port.
            }
        }

        /// <summary>Split buffered text on CR and dispatch complete lines. Must hold ch.RxLock.</summary>
        private void ProcessLines(MfcChannel ch)
        {
            string text = ch.RxBuffer.ToString();
            int cr = text.IndexOf('\r');
            while (cr >= 0)
            {
                string line = text.Substring(0, cr);
                ch.RxBuffer.Remove(0, cr + 1);
                ParseResponseLine(line, ch);

                text = ch.RxBuffer.ToString();
                cr = text.IndexOf('\r');
            }

            // Safety cap against garbage data.
            if (ch.RxBuffer.Length > 4096)
            {
                ch.RxBuffer.Clear();
            }
        }

        /// <summary>Parse one Alicat response line and route it to the matching device.</summary>
        private void ParseResponseLine(string line, MfcChannel ch)
        {
            line = line.Trim();
            if (line.Length == 0) return;

            char id = line[0];
            MfcDevice dev = ch.Devices.FirstOrDefault(d => d.Address.Length > 0 && d.Address[0] == id);
            if (dev == null) return;

            // 收到本通道设备的有效响应 → 刷新看门狗时间戳。
            ch.LastRxUtc = DateTime.UtcNow;

            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) return;

            // parts: [0]=ID [1]=Abs.Pressure [2]=Temperature [3]=Vol.Flow
            //        [4]=Mass.Flow [5]=Setpoint [6..]=Gas name
            double value;
            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                dev.AbsPressure = value;
            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                dev.Temperature = value;
            if (double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                dev.VolFlow = value;
            if (double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                dev.MassFlow = value;
            if (double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                dev.Setpoint = value;

            dev.GasName = parts.Length > 6
                ? string.Join(" ", parts, 6, parts.Length - 6)
                : "";
            dev.HasStatus = true;
            // 时间戳最后写入（Interlocked 兼作内存屏障）：读方看到新 tick 即可见完整 Setpoint。
            Interlocked.Exchange(ref dev.LastStatusTick, Environment.TickCount);

            Dispatcher.BeginInvoke((Action)(() => UpdateDeviceUi(dev.Index)));
        }

        private void UpdateDeviceUi(int index)
        {
            MfcDevice dev = _devices[index];
            if (dev == null) return;

            string fmt(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

            if (index == 0)
            {
                Pressure1Text.Text = dev.HasStatus ? fmt(dev.AbsPressure) : "--";
                Temperature1Text.Text = dev.HasStatus ? fmt(dev.Temperature) : "--";
                VolFlow1Text.Text = dev.HasStatus ? fmt(dev.VolFlow) : "--";
                MassFlow1Text.Text = dev.HasStatus ? fmt(dev.MassFlow) : "--";
                Setpoint1Text.Text = dev.HasStatus ? fmt(dev.Setpoint) : "--";
                GasName1Text.Text = dev.HasStatus ? dev.GasName : "--";
            }
            else
            {
                Pressure2Text.Text = dev.HasStatus ? fmt(dev.AbsPressure) : "--";
                Temperature2Text.Text = dev.HasStatus ? fmt(dev.Temperature) : "--";
                VolFlow2Text.Text = dev.HasStatus ? fmt(dev.VolFlow) : "--";
                MassFlow2Text.Text = dev.HasStatus ? fmt(dev.MassFlow) : "--";
                Setpoint2Text.Text = dev.HasStatus ? fmt(dev.Setpoint) : "--";
                GasName2Text.Text = dev.HasStatus ? dev.GasName : "--";
            }
        }

        private void ResetDisplays()
        {
            Pressure1Text.Text = Temperature1Text.Text = VolFlow1Text.Text = "--";
            MassFlow1Text.Text = Setpoint1Text.Text = GasName1Text.Text = "--";
            Pressure2Text.Text = Temperature2Text.Text = VolFlow2Text.Text = "--";
            MassFlow2Text.Text = Setpoint2Text.Text = GasName2Text.Text = "--";
        }

        // ================= Control commands =================

        private async void SetFlowButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ReferenceEquals(sender, SetFlowButton1) ? 0 : 1;
            TextBox tb = index == 0 ? FlowTextBox1 : FlowTextBox2;
            double value;
            if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                ShowInputError("Invalid flow value.");
                return;
            }
            if (value < 0 || value > 500.0)
            {
                ShowInputError("Flow must be between 0 and 500 SCCM.");
                return;
            }

            // 后台执行"写入+回读验证+不符重发"，避免阻塞 UI。
            Button btn = (Button)sender;
            btn.IsEnabled = false;
            bool verified;
            try
            {
                verified = await Task.Run(() => SendSetFlowVerified(_devices[index], value));
            }
            finally
            {
                btn.IsEnabled = _uiConnected;
            }
            if (!verified)
            {
                ShowInputError("Flow set failed: device did not confirm the setpoint.");
            }
        }

        private void SetGasButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ReferenceEquals(sender, SetGasButton1) ? 0 : 1;
            TextBox tb = index == 0 ? GasTextBox1 : GasTextBox2;
            int gas;
            if (!int.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out gas) || gas < 0)
            {
                ShowInputError("Invalid gas number.");
                return;
            }
            SendSetGas(_devices[index], gas);
        }

        private async void ZeroButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ReferenceEquals(sender, ZeroButton1) ? 0 : 1;

            Button btn = (Button)sender;
            btn.IsEnabled = false;
            bool verified;
            try
            {
                verified = await Task.Run(() => SendSetFlowVerified(_devices[index], 0.0));
            }
            finally
            {
                btn.IsEnabled = _uiConnected;
            }
            if (!verified)
            {
                ShowInputError("Zero failed: device did not confirm the setpoint.");
            }
        }

        // ================= Gas mixing =================

        private async void SetGasMixingButton_Click(object sender, RoutedEventArgs e)
        {
            double sourceConc, totalFlow, targetConc;
            if (!double.TryParse(SourceConcTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out sourceConc))
            {
                ShowInputError("Invalid source concentration.");
                return;
            }
            if (!double.TryParse(TotalFlowTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out totalFlow))
            {
                ShowInputError("Invalid total flow.");
                return;
            }
            if (!double.TryParse(TargetConcTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out targetConc))
            {
                ShowInputError("Invalid target concentration.");
                return;
            }

            string error;
            if (!TrySetSourceConc(sourceConc, out error))
            {
                ShowInputError(error);
                return;
            }
            if (!TrySetTotalFlow(totalFlow, out error))
            {
                ShowInputError(error);
                return;
            }
            if (!TrySetTargetConc(targetConc, out error))
            {
                ShowInputError(error);
                return;
            }

            SaveConfig();

            // 后台执行两路"写入+回读验证+不符重发"，避免阻塞 UI。
            SetGasMixingButton.IsEnabled = false;
            bool ok;
            string applyError = null;
            try
            {
                ok = await Task.Run(() => ApplyMixIfReady(out applyError));
            }
            finally
            {
                SetGasMixingButton.IsEnabled = _uiConnected;
            }
            if (!ok)
            {
                ShowInputError(applyError ?? "Gas mixing failed: device did not confirm the setpoints.");
            }
        }

        /// <summary>Validate and store the source gas concentration (1..1000000).</summary>
        private bool TrySetSourceConc(double v, out string error)
        {
            error = null;
            if (v < 1 || v > MaxConcentration)
            {
                error = "source concentration must be between 1 and 1000000";
                return false;
            }
            _sourceConc = v;
            return true;
        }

        /// <summary>Validate and store the total flow in SCCM (0..1000).</summary>
        private bool TrySetTotalFlow(double v, out string error)
        {
            error = null;
            if (v <= 0 || v > MaxTotalFlowSccm)
            {
                error = "total flow must be between 0 and 1000 SCCM";
                return false;
            }
            _totalFlow = v;
            return true;
        }

        /// <summary>Validate and store the target gas concentration (0..1000000).</summary>
        private bool TrySetTargetConc(double v, out string error)
        {
            error = null;
            if (v < 0 || v > MaxConcentration)
            {
                error = "target concentration must be between 0 and 1000000";
                return false;
            }
            _targetConc = v;
            return true;
        }

        /// <summary>
        /// Compute target/background gas flows from the three mix parameters.
        /// </summary>
        private static bool TryComputeMix(double totalFlow, double sourceConc, double targetConc,
                                          out double targetFlow, out double backgroundFlow, out string error)
        {
            targetFlow = 0;
            backgroundFlow = 0;
            error = null;

            if (sourceConc < 1 || sourceConc > MaxConcentration)
            {
                error = "source concentration must be between 1 and 1000000";
                return false;
            }
            if (targetConc < 0 || targetConc > MaxConcentration)
            {
                error = "target concentration must be between 0 and 1000000";
                return false;
            }
            if (targetConc > sourceConc)
            {
                error = "target concentration cannot exceed source concentration";
                return false;
            }
            if (totalFlow <= 0 || totalFlow > MaxTotalFlowSccm)
            {
                error = "total flow must be between 0 and 1000 SCCM";
                return false;
            }

            targetFlow = Math.Round(totalFlow * targetConc / sourceConc, 2);
            backgroundFlow = Math.Round(totalFlow - targetFlow, 2);

            if (targetFlow < 0 || targetFlow > MaxFlowSccm || backgroundFlow < 0 || backgroundFlow > MaxFlowSccm)
            {
                error = "computed flow is outside 0 - 500 SCCM";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Compute and write the two device setpoints from the mix parameters.
        /// All three parameters must be set; returns false with an error if any
        /// is missing or the mix cannot be applied.
        /// </summary>
        private bool ApplyMixIfReady(out string error)
        {
            error = null;
            if (_sourceConc == null)
            {
                error = "source_conc not set";
                return false;
            }
            if (_totalFlow == null)
            {
                error = "total_flow not set";
                return false;
            }
            if (_targetConc == null)
            {
                error = "target_conc not set";
                return false;
            }

            double targetFlow, backgroundFlow;
            if (!TryComputeMix(_totalFlow.Value, _sourceConc.Value, _targetConc.Value,
                               out targetFlow, out backgroundFlow, out error))
            {
                return false;
            }

            MfcDevice targetDev = FindDeviceByRole("target");
            MfcDevice bgDev = FindDeviceByRole("background");
            if (targetDev == null || bgDev == null)
            {
                error = "device not found (not connected)";
                return false;
            }

            if (!SendSetFlowVerified(targetDev, targetFlow))
            {
                error = "target setpoint not confirmed by device";
                return false;
            }
            if (!SendSetFlowVerified(bgDev, backgroundFlow))
            {
                error = "background setpoint not confirmed by device";
                return false;
            }
            return true;
        }

        /// <summary>Flow setpoint: value × 128, rounded, sent as decimal ASCII.</summary>
        private bool SendSetFlow(MfcDevice dev, double sccm)
        {
            if (dev == null || dev.Channel == null) return false;
            long scaled = (long)Math.Round(sccm * 128.0);
            return WriteToDevice(dev, dev.Address + " " + scaled.ToString(CultureInfo.InvariantCulture) + "\r\r");
        }

        /// <summary>
        /// 写入设定流量并回读验证：每次写入后等待晚于写入时刻的状态帧轮询回读，
        /// 比较设备 Setpoint 与目标值；不符则重发，直到一致或总超时（最坏约 10s）。
        /// 会阻塞调用线程；UI 线程请经 Task.Run 调用。
        /// </summary>
        private bool SendSetFlowVerified(MfcDevice dev, double sccm)
        {
            if (dev == null || dev.Channel == null) return false;

            int start = Environment.TickCount;
            while (Environment.TickCount - start < SetVerifyTimeoutMs)
            {
                int writeTick = Environment.TickCount;
                if (!SendSetFlow(dev, sccm))
                {
                    return false;
                }

                while (Environment.TickCount - writeTick < SetVerifyRetryWindowMs)
                {
                    if (Interlocked.Read(ref dev.LastStatusTick) - writeTick >= 0
                        && dev.HasStatus
                        && Math.Abs(dev.Setpoint - sccm) <= SetVerifyTolerance)
                    {
                        return true;
                    }
                    Thread.Sleep(50);
                }
                // 本窗口内回读仍不符 → 重发
            }
            return false;
        }

        /// <summary>Gas number: {addr}G {number}\r</summary>
        private void SendSetGas(MfcDevice dev, int gas)
        {
            if (dev == null || dev.Channel == null) return;
            WriteToDevice(dev, dev.Address + "G " + gas.ToString(CultureInfo.InvariantCulture) + "\r");
        }

        private bool WriteToDevice(MfcDevice dev, string command)
        {
            MfcChannel ch = dev.Channel;
            if (ch == null || ch.Port == null || !ch.Port.IsOpen) return false;
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(command);
                lock (ch.WriteLock)
                {
                    ch.Port.Write(bytes, 0, bytes.Length);
                }
                return true;
            }
            catch
            {
                RaiseSerialError("Serial connection lost");
                return false;
            }
        }

        // ================= Device roles =================

        /// <summary>Prevents recursive UI syncing while a radio change propagates to the other device.</summary>
        private bool _updatingRoles;

        /// <summary>UI 连接状态镜像，供异步验证写操作完成后恢复按钮可用状态。</summary>
        private volatile bool _uiConnected;

        /// <summary>
        /// The two devices always have complementary roles: exactly one is
        /// "target gas" and the other is "background gas". Selecting a role on
        /// one device automatically updates the other device's radio buttons.
        /// </summary>
        private void RoleRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingRoles) return;

            // During XAML load the radios are created one by one; the checked
            // event can fire before the sibling radios exist. Skip linking then
            // (the XAML-declared defaults already set device1=target, device2=background).
            if (TargetRadio1 == null || TargetRadio2 == null ||
                BackgroundRadio1 == null || BackgroundRadio2 == null)
            {
                return;
            }

            _updatingRoles = true;
            try
            {
                var radio = (RadioButton)sender;
                bool dev1Target;

                if (radio == TargetRadio1 || radio == BackgroundRadio1)
                {
                    // Device 1 selection drives Device 2.
                    dev1Target = radio == TargetRadio1;
                    TargetRadio2.IsChecked = !dev1Target;
                    BackgroundRadio2.IsChecked = dev1Target;
                }
                else
                {
                    // Device 2 selection drives Device 1.
                    dev1Target = !(radio == TargetRadio2);
                    TargetRadio1.IsChecked = dev1Target;
                    BackgroundRadio1.IsChecked = !dev1Target;
                }

                SetDeviceRole(0, dev1Target);
                SetDeviceRole(1, !dev1Target);

                if (!_loadingConfig)
                {
                    SaveConfig();
                }
            }
            finally
            {
                _updatingRoles = false;
            }
        }

        private void SetDeviceRole(int index, bool isTarget)
        {
            if (_devices[index] != null)
            {
                _devices[index].IsTarget = isTarget;
            }
        }

        private void ApplyRolesFromUi()
        {
            if (_devices[0] != null) _devices[0].IsTarget = TargetRadio1.IsChecked == true;
            if (_devices[1] != null) _devices[1].IsTarget = TargetRadio2.IsChecked == true;
        }

        // ================= Persistence =================

        /// <summary>
        /// Load persisted settings from alicat_mfc.ini and apply them to the UI.
        /// Runs on the UI thread during window load.
        /// </summary>
        private void LoadConfig()
        {
            _loadingConfig = true;
            try
            {
                // Pipe name
                string pipeName = IniConfig.Read("General", "PipeName", DefaultPipeName);
                if (!string.IsNullOrEmpty(pipeName))
                {
                    PipeNameTextBox.Text = pipeName;
                }

                // Window size
                double width, height;
                if (double.TryParse(IniConfig.Read("Window", "Width", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out width) && width >= 200)
                {
                    Width = width;
                }
                if (double.TryParse(IniConfig.Read("Window", "Height", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out height) && height >= 150)
                {
                    Height = height;
                }

                // Device 1
                string port1 = IniConfig.Read("Device1", "Port", "");
                string addr1 = IniConfig.Read("Device1", "Addr", "A");
                string baud1 = IniConfig.Read("Device1", "Baud", DefaultBaudRate.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(port1))
                {
                    PortComboBox1.Text = port1;
                }
                AddressTextBox1.Text = NormalizeAddr(addr1);
                BaudTextBox1.Text = NormalizeBaud(baud1);

                // Device 2
                string port2 = IniConfig.Read("Device2", "Port", "");
                string addr2 = IniConfig.Read("Device2", "Addr", "A");
                string baud2 = IniConfig.Read("Device2", "Baud", DefaultBaudRate.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(port2))
                {
                    PortComboBox2.Text = port2;
                }
                AddressTextBox2.Text = NormalizeAddr(addr2);
                BaudTextBox2.Text = NormalizeBaud(baud2);

                // Roles
                string target = IniConfig.Read("Role", "Target", "1");
                SetRoleUi(target != "2");

                // Mix parameters (target concentration intentionally not persisted).
                string sourceConc = IniConfig.Read("Mix", "SourceConc", "");
                string totalFlow = IniConfig.Read("Mix", "TotalFlow", "");
                if (!string.IsNullOrEmpty(sourceConc))
                {
                    SourceConcTextBox.Text = sourceConc;
                    double v;
                    if (double.TryParse(sourceConc, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    {
                        _sourceConc = v;
                    }
                }
                if (!string.IsNullOrEmpty(totalFlow))
                {
                    TotalFlowTextBox.Text = totalFlow;
                    double v;
                    if (double.TryParse(totalFlow, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    {
                        _totalFlow = v;
                    }
                }
                TargetConcTextBox.Text = "";
                _targetConc = null;
            }
            finally
            {
                _loadingConfig = false;
            }

            // The pipe server started during InitializeComponent using the
            // default name; restart it with the loaded name.
            if (PipeToggleButton.IsChecked == true)
            {
                RestartPipeServer();
            }
        }

        /// <summary>Persist the current settings to alicat_mfc.ini (thread-safe).</summary>
        private void SaveConfig()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke((Action)SaveConfig);
                return;
            }

            IniConfig.Write("General", "PipeName", PipeNameTextBox.Text.Trim());

            string port1 = (PortComboBox1.Text ?? "").Trim();
            string port2 = (PortComboBox2.Text ?? "").Trim();

            IniConfig.Write("Device1", "Port", port1);
            IniConfig.Write("Device1", "Addr", AddressTextBox1.Text.Trim());
            IniConfig.Write("Device1", "Baud", BaudTextBox1.Text.Trim());

            IniConfig.Write("Device2", "Port", port2);
            IniConfig.Write("Device2", "Addr", AddressTextBox2.Text.Trim());
            IniConfig.Write("Device2", "Baud", BaudTextBox2.Text.Trim());

            string target = TargetRadio1.IsChecked == true ? "1" : "2";
            IniConfig.Write("Role", "Target", target);

            IniConfig.Write("Mix", "SourceConc",
                _sourceConc.HasValue ? _sourceConc.Value.ToString(CultureInfo.InvariantCulture) : "");
            IniConfig.Write("Mix", "TotalFlow",
                _totalFlow.HasValue ? _totalFlow.Value.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>Set the complementary role radio buttons for both devices.</summary>
        private void SetRoleUi(bool dev1Target)
        {
            TargetRadio1.IsChecked = dev1Target;
            BackgroundRadio1.IsChecked = !dev1Target;
            TargetRadio2.IsChecked = !dev1Target;
            BackgroundRadio2.IsChecked = dev1Target;
        }

        private static string NormalizeAddr(string addr)
        {
            string a = (addr ?? "").Trim().ToUpperInvariant();
            return a.Length == 1 && a[0] >= 'A' && a[0] <= 'Z' ? a : "A";
        }

        private static string NormalizeBaud(string baud)
        {
            int b;
            return int.TryParse(baud, NumberStyles.Integer, CultureInfo.InvariantCulture, out b) && b > 0
                ? b.ToString(CultureInfo.InvariantCulture)
                : DefaultBaudRate.ToString(CultureInfo.InvariantCulture);
        }

        // ================= Input validation =================

        private void AddressTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!AddressRegex.IsMatch(e.Text))
            {
                e.Handled = true;
            }
        }

        private void AddressTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pasted = (string)e.DataObject.GetData(typeof(string));
                if (!AddressRegex.IsMatch(pasted))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void AddressTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = (TextBox)sender;
            string upper = tb.Text.ToUpperInvariant();
            if (tb.Text != upper)
            {
                tb.Text = upper;
                tb.CaretIndex = tb.Text.Length;
            }
        }

        private void FlowTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = (TextBox)sender;
            string candidate = tb.Text
                .Remove(tb.SelectionStart, tb.SelectionLength)
                .Insert(tb.SelectionStart, e.Text);
            if (!FlowInputRegex.IsMatch(candidate))
            {
                e.Handled = true;
            }
        }

        private void FlowTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var tb = (TextBox)sender;
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pasted = (string)e.DataObject.GetData(typeof(string));
                string candidate = tb.Text
                    .Remove(tb.SelectionStart, tb.SelectionLength)
                    .Insert(tb.SelectionStart, pasted);
                if (!FlowInputRegex.IsMatch(candidate))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void GasTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = (TextBox)sender;
            string candidate = tb.Text
                .Remove(tb.SelectionStart, tb.SelectionLength)
                .Insert(tb.SelectionStart, e.Text);
            if (!GasInputRegex.IsMatch(candidate))
            {
                e.Handled = true;
            }
        }

        private void GasTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var tb = (TextBox)sender;
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pasted = (string)e.DataObject.GetData(typeof(string));
                string candidate = tb.Text
                    .Remove(tb.SelectionStart, tb.SelectionLength)
                    .Insert(tb.SelectionStart, pasted);
                if (!GasInputRegex.IsMatch(candidate))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void BaudTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = (TextBox)sender;
            string candidate = tb.Text
                .Remove(tb.SelectionStart, tb.SelectionLength)
                .Insert(tb.SelectionStart, e.Text);
            if (!BaudInputRegex.IsMatch(candidate))
            {
                e.Handled = true;
            }
        }

        private void BaudTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var tb = (TextBox)sender;
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pasted = (string)e.DataObject.GetData(typeof(string));
                string candidate = tb.Text
                    .Remove(tb.SelectionStart, tb.SelectionLength)
                    .Insert(tb.SelectionStart, pasted);
                if (!BaudInputRegex.IsMatch(candidate))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private static int ParseBaud(string text)
        {
            int baud;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out baud) && baud > 0
                ? baud
                : DefaultBaudRate;
        }

        private static bool IsValidAddress(string addr)
        {
            return addr.Length == 1 && addr[0] >= 'A' && addr[0] <= 'Z';
        }

        private static void ShowInputError(string message)
        {
            MessageBox.Show(message, "Alicat MFC",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void UpdateConnectedUi(bool connected)
        {
            _uiConnected = connected;
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            PortComboBox1.IsEnabled = !connected;
            PortComboBox2.IsEnabled = !connected;
            RefreshButton.IsEnabled = !connected;
            AddressTextBox1.IsEnabled = !connected;
            AddressTextBox2.IsEnabled = !connected;
            BaudTextBox1.IsEnabled = !connected;
            BaudTextBox2.IsEnabled = !connected;

            // Pipe controls stay usable while connected (pipe is independent of serial).
            PipeToggleButton.IsEnabled = true;
            PipeNameTextBox.IsEnabled = true;
            ApplyPipeButton.IsEnabled = true;

            SetFlowButton1.IsEnabled = connected;
            SetFlowButton2.IsEnabled = connected;
            SetGasButton1.IsEnabled = connected;
            SetGasButton2.IsEnabled = connected;
            ZeroButton1.IsEnabled = connected;
            ZeroButton2.IsEnabled = connected;
            FlowTextBox1.IsEnabled = connected;
            FlowTextBox2.IsEnabled = connected;
            GasTextBox1.IsEnabled = connected;
            GasTextBox2.IsEnabled = connected;

            // Mix parameter inputs stay usable regardless of connection state
            // (they are persisted configuration; applying requires a connection).
            SourceConcTextBox.IsEnabled = true;
            TotalFlowTextBox.IsEnabled = true;
            TargetConcTextBox.IsEnabled = true;
            SetGasMixingButton.IsEnabled = true;
        }

        // ================= Named pipe server =================

        private void ApplyPipeButton_Click(object sender, RoutedEventArgs e)
        {
            // Only meaningful while the pipe server is enabled.
            if (PipeToggleButton.IsChecked == true)
            {
                RestartPipeServer();
            }
            SaveConfig();
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
                MessageBox.Show("Pipe name cannot be empty.", "Alicat MFC",
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
                                "Alicat MFC", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                                "Alicat MFC", MessageBoxButton.OK, MessageBoxImage.Error);
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

                    case "set_setpoint":
                    {
                        if (req.Value < 0 || req.Value > 500.0)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "setpoint must be between 0 and 500 SCCM" });
                        }
                        MfcDevice dev = FindDeviceByRole(req.Role);
                        if (dev == null)
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "unknown role: " + req.Role });
                        }
                        // 写入并回读验证，不符自动重发，直到一致或超时。
                        if (!SendSetFlowVerified(dev, req.Value))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = "verify failed: setpoint readback mismatch" });
                        }
                        return ser.Serialize(new PipeResponse { Ok = true });
                    }

                    case "set_source_conc":
                    {
                        string error;
                        if (!TrySetSourceConc(req.Value, out error))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = error });
                        }
                        SaveConfig();
                        return ser.Serialize(new PipeResponse { Ok = true });
                    }

                    case "set_total_flow":
                    {
                        string error;
                        if (!TrySetTotalFlow(req.Value, out error))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = error });
                        }
                        SaveConfig();
                        return ser.Serialize(new PipeResponse { Ok = true });
                    }

                    case "set_target_conc":
                    {
                        string error;
                        if (!TrySetTargetConc(req.Value, out error))
                        {
                            return ser.Serialize(new PipeResponse { Ok = false, Error = error });
                        }
                        return ser.Serialize(new PipeResponse { Ok = true });
                    }

                    case "set_gas_mixing":
                    {
                        string error;
                        return MixPipeResult(out error);
                    }

                    default:
                        return ser.Serialize(new PipeResponse { Ok = false, Error = "unknown command: " + req.Cmd });
                }
            }
            catch (Exception ex)
            {
                return ser.Serialize(new PipeResponse { Ok = false, Error = "bad request: " + ex.Message });
            }
        }

        /// <summary>
        /// Apply the gas mixing (set_gas_mixing command): compute and write both
        /// device setpoints, or report the error.
        /// </summary>
        private string MixPipeResult(out string error)
        {
            var ser = new JavaScriptSerializer();
            if (!ApplyMixIfReady(out error))
            {
                return ser.Serialize(new PipeResponse { Ok = false, Error = error });
            }
            return ser.Serialize(new PipeResponse { Ok = true });
        }

        /// <summary>Find a device by its role ("target" or "background"). Roles are unique across devices.</summary>
        private MfcDevice FindDeviceByRole(string role)
        {
            if (string.IsNullOrEmpty(role)) return null;
            role = role.Trim().ToLowerInvariant();
            bool wantTarget = role == "target";
            return _devices.FirstOrDefault(d => d != null && d.IsTarget == wantTarget);
        }

        /// <summary>
        /// Pipe status exposes only what remote processes are allowed to read:
        /// current mass flow and setpoint for each device, identified by role.
        /// </summary>
        private PipeResponse BuildStatusResponse()
        {
            var devices = new List<DeviceStatus>();
            for (int i = 0; i < _devices.Length; i++)
            {
                MfcDevice d = _devices[i];
                if (d == null) continue;
                devices.Add(new DeviceStatus
                {
                    Role = d.IsTarget ? "target" : "background",
                    HasStatus = d.HasStatus,
                    MassFlow = d.MassFlow,
                    Setpoint = d.Setpoint
                });
            }
            return new PipeResponse
            {
                Ok = true,
                Devices = devices,
                SourceConc = _sourceConc,
                TotalFlow = _totalFlow,
                TargetConc = _targetConc
            };
        }

        /// <summary>Extract the numeric part of a port name (e.g. "COM10" → 10).</summary>
        private static int PortNumber(string portName)
        {
            int idx = portName.IndexOf("COM", StringComparison.OrdinalIgnoreCase);
            int number;
            return idx >= 0 && int.TryParse(portName.Substring(idx + 3), out number)
                ? number
                : int.MaxValue;
        }

        /// <summary>
        /// Per-connection stop flag for the poll thread. A new connection
        /// creates a fresh session, so an old poll thread can never be
        /// accidentally re-enabled by a later reconnect.
        /// </summary>
        private sealed class PollSession
        {
            public volatile bool Stop;
        }
    }

    /// <summary>An (address, baud rate) pair used when probing a serial port.</summary>
    public class ProbeSpec
    {
        public ProbeSpec(string address, int baudRate)
        {
            Address = address;
            BaudRate = baudRate;
        }

        public string Address { get; }
        public int BaudRate { get; }
    }

    /// <summary>One physical serial port; may host several devices (addresses).</summary>
    public class MfcChannel
    {
        public string PortName { get; set; }
        public int BaudRate { get; set; }
        public SerialPort Port { get; set; }
        public object WriteLock { get; } = new object();
        public object RxLock { get; } = new object();
        public StringBuilder RxBuffer { get; } = new StringBuilder();
        public List<MfcDevice> Devices { get; } = new List<MfcDevice>();

        /// <summary>UTC time the last valid response line was received on this channel.</summary>
        public DateTime LastRxUtc { get; set; }
    }

    /// <summary>One Alicat mass flow controller identified by its address.</summary>
    public class MfcDevice
    {
        public int Index { get; set; }
        public string Address { get; set; }
        public int BaudRate { get; set; }
        public bool IsTarget { get; set; }
        public MfcChannel Channel { get; set; }

        public double AbsPressure { get; set; }
        public double Temperature { get; set; }
        public double VolFlow { get; set; }
        public double MassFlow { get; set; }
        public double Setpoint { get; set; }
        public string GasName { get; set; }
        public bool HasStatus { get; set; }

        /// <summary>最近一次从设备状态帧刷新 Setpoint 的时刻（Environment.TickCount），经 Interlocked 读写。</summary>
        public long LastStatusTick;
    }

    /// <summary>
    /// Named pipe request. Only these commands are exposed:
    ///   {"cmd":"get_status"}
    ///   {"cmd":"set_setpoint","role":"target","value":100.0}
    ///   {"cmd":"set_setpoint","role":"background","value":50.0}
    ///   {"cmd":"set_source_conc","value":100}
    ///   {"cmd":"set_total_flow","value":200.0}
    ///   {"cmd":"set_target_conc","value":50}
    ///   {"cmd":"set_gas_mixing"}
    /// All other read/write operations are intentionally not exposed.
    /// </summary>
    public class PipeRequest
    {
        public string Cmd { get; set; }
        public string Role { get; set; }
        public double Value { get; set; }
    }

    /// <summary>Named pipe response: {"ok":true,"devices":[...]} or {"ok":false,"error":"..."}</summary>
    public class PipeResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public List<DeviceStatus> Devices { get; set; }

        /// <summary>Source gas concentration (null = not set).</summary>
        public double? SourceConc { get; set; }

        /// <summary>Total flow in SCCM (null = not set).</summary>
        public double? TotalFlow { get; set; }

        /// <summary>Target gas concentration (null = not set).</summary>
        public double? TargetConc { get; set; }
    }

    /// <summary>Pipe-exposed device data: role plus current mass flow and setpoint only.</summary>
    public class DeviceStatus
    {
        public string Role { get; set; }
        public bool HasStatus { get; set; }
        public double MassFlow { get; set; }
        public double Setpoint { get; set; }
    }
}
