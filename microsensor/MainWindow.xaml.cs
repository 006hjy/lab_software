using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace microsensor
{
    /// <summary>
    /// 主窗口：三行工具栏 + 16 通道复选框面板 + 实时曲线图。
    /// 功能对齐 Python 版：串口扫描、参数读写（DAC/配置/SensorID）、每秒采集、CSV 导出、主题与设置持久化。
    /// </summary>
    public partial class MainWindow : Window
    {
        private SettingsStore _settings;
        private SerialClient _serial;
        private SampleDatabase _db;
        private AcquisitionController _acq;
        private PortScanner _portScanner;
        private DispatcherTimer _plotTimer;

        private string _themeMode = "light";
        private bool _suppressPortEvents;
        private string _taskManagerPipeName = "task_manager";

        public MainWindow()
        {
            InitializeComponent();

            // 填充静态下拉列表
            for (int i = 0; i < Constants.DacLabels.Length; i++)
            {
                VrefCombo.Items.Add(Constants.DacLabels[i]);
                BiasCombo.Items.Add(Constants.DacLabels[i]);
            }
            VrefCombo.SelectedIndex = 0;
            BiasCombo.SelectedIndex = 0;

            foreach (var label in Constants.ResistorLabels)
                ResistorCombo.Items.Add(label);
            ResistorCombo.SelectedIndex = 0;

            foreach (var (id, name) in Constants.SensorIds)
                SensorIdCombo.Items.Add($"{id} : {name}");

            // 初始化 16 通道复选框面板
            ChannelPanel1.Initialize(Enumerable.Repeat(true, 16).ToList(), Constants.ChannelColors);

            // 通道面板状态变化 → 立即重绘
            ChannelPanel1.Changed += OnChannelChecksChanged;

            // 扫描器
            _portScanner = new PortScanner();
            _portScanner.ScanStarted += OnScanStarted;
            _portScanner.ScanFinished += OnScanFinished;
        }

        // ---------------- 生命周期 ----------------

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 延迟初始化：窗口显示后再加载设置与硬件
            _settings = new SettingsStore();
            LoadSettingsToUi();
            ApplyTheme(_settings.GetThemeMode(), persist: false);

            _serial = new SerialClient();
            _db = new SampleDatabase("microsensor.db");
            _acq = new AcquisitionController(
                _serial, _db,
                onError: msg => Console.WriteLine(msg),
                pipeNameProvider: () => _taskManagerPipeName);

            // 扫描串口（完成后自动读取设备参数）
            RefreshPorts();

            // 绘图定时器：每秒刷新
            _plotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _plotTimer.Tick += (s, ev) => RefreshPlot();

            RefreshPlot();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _plotTimer?.Stop();
            _acq?.Stop();
            _serial?.Close();
            _db?.Dispose();
        }

        // ---------------- 设置加载 ----------------

        private void LoadSettingsToUi()
        {
            BaudEdit.Text = _settings.GetBaud().ToString();
            DisplaySecondsEdit.Text = _settings.GetDisplaySeconds().ToString();
            ConvertMvCheck.IsChecked = _settings.GetConvertMv();
            ChannelPanel1.SetStates(_settings.GetChannelChecks());

            var (vref, bias) = _settings.GetDacChecks();
            VrefDacCheck.IsChecked = vref;
            BiasDacCheck.IsChecked = bias;
            UpdateDacUi();

            TaskManagerPipeEdit.Text = _settings.GetTaskManagerPipe();
            _taskManagerPipeName = TaskManagerPipeEdit.Text.Trim();
        }

        // ---------------- DAC UI 切换 ----------------

        private void UpdateDacUi()
        {
            bool vrefDac = VrefDacCheck.IsChecked == true;
            bool biasDac = BiasDacCheck.IsChecked == true;
            VrefEdit.Visibility = vrefDac ? Visibility.Visible : Visibility.Collapsed;
            VrefCombo.Visibility = vrefDac ? Visibility.Collapsed : Visibility.Visible;
            BiasEdit.Visibility = biasDac ? Visibility.Visible : Visibility.Collapsed;
            BiasCombo.Visibility = biasDac ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnDacToggled(object sender, RoutedEventArgs e) => UpdateDacUi();

        // ---------------- 端口扫描 ----------------

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            int baud = GetBaudFromUi();
            string skipPort = "";
            if (_serial != null && !string.IsNullOrEmpty(_serial.Port) && _serial.IsOpen)
                skipPort = _serial.Port;

            _portScanner.Start(ports, baud, skipPort);
        }

        private void OnScanStarted()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshBtn.IsEnabled = false;
                _suppressPortEvents = true;
                PortCombo.Items.Clear();
                PortCombo.Items.Add("Scanning...");
                PortCombo.SelectedIndex = 0;
                _suppressPortEvents = false;
            });
        }

        private void OnScanFinished(IReadOnlyList<string> validPorts)
        {
            Dispatcher.Invoke(() =>
            {
                var current = PortCombo.Text?.Trim() ?? "";
                var saved = _settings.GetPort().Trim();
                var ports = validPorts.ToList();

                Logger.Log($"OnScanFinished: valid ports=[{string.Join(",", ports)}] saved='{saved}' current='{current}'");

                _suppressPortEvents = true;
                PortCombo.Items.Clear();
                foreach (var p in ports)
                    PortCombo.Items.Add(p);

                var preferred = !string.IsNullOrEmpty(saved) ? saved : current;
                if (ports.Contains(preferred))
                    PortCombo.Text = preferred;
                _suppressPortEvents = false;

                RefreshBtn.IsEnabled = true;

                if (ports.Count == 0)
                {
                    Logger.Log("OnScanFinished: no ports, skip auto read");
                    return;
                }

                if (string.IsNullOrEmpty(PortCombo.Text?.Trim()))
                {
                    // 触发 OnPortChanged → 自动读取设备参数
                    Logger.Log($"OnScanFinished: no selection, setting index 0 -> '{ports[0]}'");
                    PortCombo.SelectedIndex = 0;
                    return;
                }

                // 端口在屏蔽信号期间被选中 → 手动触发首次自动读取
                Logger.Log($"OnScanFinished: auto-read for '{GetSelectedPort()}'");
                ReadDac(auto: true);
                ReadConfig(auto: true);
                ReadSensorId(auto: true);
            });
        }

        private void OnPortChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressPortEvents)
                return;

            var port = GetSelectedPort();
            Logger.Log($"OnPortChanged: '{port}' -> auto read");
            _settings.SetPort(port);
            _serial.Configure(port, GetBaudFromUi());

            ReadDac(auto: true);
            ReadConfig(auto: true);
            ReadSensorId(auto: true);
        }

        /// <summary>获取下拉框当前选中的端口名（用 SelectedItem，SelectionChanged 触发时同步可用）。</summary>
        private string GetSelectedPort()
        {
            return (PortCombo.SelectedItem as string)?.Trim() ?? "";
        }

        private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshPorts();

        private void OnBaudEdited(object sender, RoutedEventArgs e)
        {
            var text = BaudEdit.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                BaudEdit.Text = _settings.GetBaud().ToString();
                return;
            }
            if (!int.TryParse(text, out var baud))
            {
                BaudEdit.Text = _settings.GetBaud().ToString();
                return;
            }

            _settings.SetBaud(baud);
            _serial.Configure(GetSelectedPort(), baud);

            ReadDac(auto: true);
            ReadConfig(auto: true);
            ReadSensorId(auto: true);
        }

        /// <summary>保存 task_manager 管道名（失焦时），并同步给采集线程。</summary>
        private void OnTaskManagerPipeEdited(object sender, RoutedEventArgs e)
        {
            var text = TaskManagerPipeEdit.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                TaskManagerPipeEdit.Text = _taskManagerPipeName;
                return;
            }

            _taskManagerPipeName = text;
            _settings?.SetTaskManagerPipe(text);
        }

        private int GetBaudFromUi()
        {
            // 波特率固定 115200，不再从 UI 读取
            return Constants.DefaultBaud;
        }

        // ---------------- 参数读取/写入 ----------------

        private void ReadDac(bool auto)
        {
            _serial.Configure(GetSelectedPort(), GetBaudFromUi());
            Logger.Log($"ReadDac(auto={auto}): start for '{_serial.Port}'");
            (int VrefDac, int BiasDac)? status;
            try
            {
                status = _serial.ReadDacStatus();
            }
            catch (Exception ex)
            {
                Logger.Log($"ReadDac(auto={auto}): exception {ex.GetType().Name}: {ex.Message}");
                if (auto)
                    Console.WriteLine($"Auto read dac failed: {ex.Message}");
                else
                    MessageBox.Show(this, $"Read dac failed: {ex.Message}", "Read Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (status == null)
            {
                Logger.Log($"ReadDac(auto={auto}): got null");
                if (auto)
                    Console.WriteLine("Auto read dac failed: timeout/checksum/header mismatch");
                else
                    MessageBox.Show(this, "Read dac failed.", "Read Failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Log($"ReadDac(auto={auto}): vref={status.Value.VrefDac} bias={status.Value.BiasDac}");
            VrefDacCheck.IsChecked = status.Value.VrefDac != 0;
            BiasDacCheck.IsChecked = status.Value.BiasDac != 0;
            _settings.SetDacChecks(status.Value.VrefDac != 0, status.Value.BiasDac != 0);
        }

        private void ReadConfig(bool auto)
        {
            _serial.Configure(GetSelectedPort(), GetBaudFromUi());
            Logger.Log($"ReadConfig(auto={auto}): start for '{_serial.Port}'");
            (int VrefByte, int BiasByte, int Resistor)? config;
            try
            {
                config = _serial.ReadConfig();
            }
            catch (Exception ex)
            {
                Logger.Log($"ReadConfig(auto={auto}): exception {ex.GetType().Name}: {ex.Message}");
                if (auto)
                    Console.WriteLine($"Auto read config failed: {ex.Message}");
                else
                    MessageBox.Show(this, $"Read config failed: {ex.Message}", "Read Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (config == null)
            {
                Logger.Log($"ReadConfig(auto={auto}): got null");
                if (auto)
                    Console.WriteLine("Auto read config failed: timeout/checksum/header mismatch");
                else
                    MessageBox.Show(this, "Read config failed.", "Read Failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Log($"ReadConfig(auto={auto}): vref_byte={config.Value.VrefByte} bias_byte={config.Value.BiasByte} resistor={config.Value.Resistor}");
            ApplyConfigToUi(config.Value.VrefByte, config.Value.BiasByte, config.Value.Resistor);
        }

        private void ApplyConfigToUi(int vrefByte, int biasByte, int resistor)
        {
            if (VrefDacCheck.IsChecked == true)
                VrefEdit.Text = (vrefByte / 100.0).ToString("0.00");
            else
                VrefCombo.SelectedIndex = Math.Max(0, Math.Min(Constants.DacLabels.Length - 1, vrefByte));

            if (BiasDacCheck.IsChecked == true)
                BiasEdit.Text = (biasByte / 100.0).ToString("0.00");
            else
                BiasCombo.SelectedIndex = Math.Max(0, Math.Min(Constants.DacLabels.Length - 1, biasByte));

            ResistorCombo.SelectedIndex = Math.Max(0, Math.Min(7, resistor));
        }

        private void ReadSensorId(bool auto)
        {
            _serial.Configure(GetSelectedPort(), GetBaudFromUi());
            Logger.Log($"ReadSensorId(auto={auto}): start for '{_serial.Port}'");
            int? sensorId;
            try
            {
                sensorId = _serial.ReadSensorId();
            }
            catch (Exception ex)
            {
                Logger.Log($"ReadSensorId(auto={auto}): exception {ex.GetType().Name}: {ex.Message}");
                if (auto)
                    Console.WriteLine($"Auto read sensor id failed: {ex.Message}");
                else
                    MessageBox.Show(this, $"Read sensor id failed: {ex.Message}", "Read Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (sensorId == null)
            {
                Logger.Log($"ReadSensorId(auto={auto}): got null");
                if (auto)
                    Console.WriteLine("Auto read sensor id failed: timeout/checksum/header mismatch");
                else
                    MessageBox.Show(this, "Read sensor id failed.", "Read Failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Log($"ReadSensorId(auto={auto}): id={sensorId.Value}");
            var index = SensorIdCombo.Items.Cast<string>()
                .Select((item, i) => new { item, i })
                .FirstOrDefault(x => x.item.StartsWith($"{sensorId.Value} :"));
            if (index != null)
                SensorIdCombo.SelectedIndex = index.i;
        }

        private void OnReadConfigClicked(object sender, RoutedEventArgs e) => ReadConfig(auto: false);

        private void OnReadSensorIdClicked(object sender, RoutedEventArgs e) => ReadSensorId(auto: false);

        private void OnSetConfigClicked(object sender, RoutedEventArgs e)
        {
            int vrefByte, biasByte, resistor;
            try
            {
                vrefByte = GetVrefByte();
                biasByte = GetBiasByte();
                resistor = ResistorCombo.SelectedIndex;
            }
            catch
            {
                MessageBox.Show(this, "Please check Vref/Bias/Resistor values.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _serial.Configure(GetSelectedPort(), GetBaudFromUi());

            // 先写 DAC 状态
            int vrefDac = VrefDacCheck.IsChecked == true ? 1 : 0;
            int biasDac = BiasDacCheck.IsChecked == true ? 1 : 0;
            bool dacOk;
            try
            {
                dacOk = _serial.WriteDacStatus(vrefDac, biasDac);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Write DAC failed: {ex.Message}", "Serial Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!dacOk)
            {
                MessageBox.Show(this, "Write DAC failed: timeout > 0.5s.", "Write Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 再写配置
            bool ok;
            try
            {
                ok = _serial.WriteConfig(vrefByte, biasByte, resistor);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Write config failed: {ex.Message}", "Serial Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (ok)
            {
                _settings.SetDacChecks(VrefDacCheck.IsChecked == true, BiasDacCheck.IsChecked == true);
                MessageBox.Show(this, "Configuration has been written successfully.", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, "Write config failed: timeout > 0.5s.", "Write Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnWriteSensorIdClicked(object sender, RoutedEventArgs e)
        {
            var text = SensorIdCombo.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show(this, "Please select a sensor type.", "Invalid Selection",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var sensorId = int.Parse(text.Split(':')[0].Trim());

            _serial.Configure(GetSelectedPort(), GetBaudFromUi());

            bool ok;
            try
            {
                ok = _serial.WriteSensorId(sensorId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Write sensor id failed: {ex.Message}", "Serial Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (ok)
            {
                MessageBox.Show(this, "Sensor id has been written successfully.", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                // 传感器类型已改变 → 重新读取 DAC 状态与配置
                ReadDac(auto: true);
                ReadConfig(auto: true);
            }
            else
            {
                MessageBox.Show(this, "Write sensor id failed: timeout > 0.5s.", "Write Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Vref 发送字节：DAC 开启=电压*100；DAC 关闭=档位索引 0~6。</summary>
        private int GetVrefByte()
        {
            if (VrefDacCheck.IsChecked == true)
                return (int)Math.Round(Validators.ParseVoltageText(VrefEdit.Text) * 100);
            return VrefCombo.SelectedIndex;
        }

        /// <summary>Bias 发送字节：DAC 开启=电压*100；DAC 关闭=档位索引 0~6。</summary>
        private int GetBiasByte()
        {
            if (BiasDacCheck.IsChecked == true)
                return (int)Math.Round(Validators.ParseVoltageText(BiasEdit.Text) * 100);
            return BiasCombo.SelectedIndex;
        }

        // ---------------- 采集 ----------------

        private void OnStartClicked(object sender, RoutedEventArgs e)
        {
            var port = GetSelectedPort();
            _serial.Configure(port, GetBaudFromUi());

            if (_acq.IsRunning)
            {
                _acq.Stop();
                _plotTimer.Stop();
                StartBtn.Content = "Start";
                return;
            }

            bool opened;
            try
            {
                opened = _serial.EnsureOpen();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to open serial port: {ex.Message}", "Serial Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!opened)
            {
                MessageBox.Show(this, "Please select a serial port first.", "Serial",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _acq.Start();
            _plotTimer.Start();
            RefreshPlot();
            StartBtn.Content = "Stop";
        }

        // ---------------- 图表刷新 ----------------

        private void RefreshPlot()
        {
            if (_db == null)
                return;

            int seconds;
            try
            {
                seconds = Validators.ParsePositiveIntText(DisplaySecondsEdit.Text);
            }
            catch
            {
                seconds = _settings.GetDisplaySeconds();
            }

            var rows = _db.FetchRecentByLatest(seconds);
            if (rows.Count > 0)
                ChannelPanel1.SetValues(rows[rows.Count - 1].Item2, ConvertMvCheck.IsChecked == true);
            else
                ChannelPanel1.SetValues(null, false);

            PlotPanel1.Render(rows, ChannelPanel1.GetStates(), ConvertMvCheck.IsChecked == true, seconds);
        }

        private void OnConvertMvChanged(object sender, RoutedEventArgs e) => RefreshPlot();

        private void OnChannelChecksChanged()
        {
            _settings?.SetChannelChecks(ChannelPanel1.GetStates());
            RefreshPlot();
        }

        /// <summary>应用 Duration(s) 设置：仅在点击 Set 按钮或按回车时调用。</summary>
        private void ApplyDisplaySeconds()
        {
            string text = DisplaySecondsEdit.Text?.Trim() ?? "";
            int seconds;
            try
            {
                seconds = Validators.ParsePositiveIntText(text);
            }
            catch
            {
                seconds = _settings.GetDisplaySeconds();
                DisplaySecondsEdit.Text = seconds.ToString();
                return;
            }

            _settings.SetDisplaySeconds(seconds);
            PlotPanel1.ResetXAxis(seconds); // 重置图表位置到新范围（清除用户缩放状态）
            PlotPanel1.SetYAxisRange(0, 65535); // y 轴固定为 0~65535
            RefreshPlot();
        }

        private void OnDisplaySecondsSetClicked(object sender, RoutedEventArgs e) => ApplyDisplaySeconds();

        private void OnDisplaySecondsKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyDisplaySeconds();
                e.Handled = true;
            }
        }

        // ---------------- 导出 / 清空 ----------------

        private void OnExportClicked(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = $"microsensor_export_{DateTime.Now:yyyyMMddHHmm}.csv",
                Filter = "CSV Files (*.csv)|*.csv",
            };
            if (dlg.ShowDialog(this) != true)
                return;

            var rows = _db.FetchAll();
            Exporter.ExportRowsToCsv(dlg.FileName, rows, ConvertMvCheck.IsChecked == true);
            MessageBox.Show(this, $"Exported {rows.Count} rows.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnClearClicked(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Are you sure you want to clear all records in the database?",
                "Clear Database",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
                return;

            _db.ClearAll();
            RefreshPlot();
            MessageBox.Show(this, "Database has been cleared.", "Clear Database",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------------- 主题 ----------------

        private void OnThemeButtonClicked(object sender, RoutedEventArgs e)
        {
            var next = _themeMode == "light" ? "dark" : "light";
            ApplyTheme(next, persist: true);
        }

        private void ApplyTheme(string mode, bool persist)
        {
            _themeMode = mode == "dark" ? "dark" : "light";
            bool dark = _themeMode == "dark";

            var fg = dark
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED))
                : new SolidColorBrush(Colors.Black);

            Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
                : new SolidColorBrush(Colors.White);
            Foreground = fg;

            // WPF CheckBox 默认主题样式不继承 Window.Foreground，需显式设置
            ConvertMvCheck.Foreground = fg;
            VrefDacCheck.Foreground = fg;
            BiasDacCheck.Foreground = fg;

            PlotPanel1.ApplyTheme(_themeMode);
            ThemeButton.Content = dark ? "Light" : "Dark";

            if (persist && _settings != null)
                _settings.SetThemeMode(_themeMode);
        }
    }
}
