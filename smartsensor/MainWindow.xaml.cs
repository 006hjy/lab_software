using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace smartsensor
{
    public partial class MainWindow : Window
    {
        private const string DefaultTaskManagerPipe = "task_manager";

        private SampleDatabase _db;
        private AcquisitionController _acq;
        private TaskManagerStatusPoller _poller;
        private DispatcherTimer _plotTimer;

        private string _taskManagerPipeName = DefaultTaskManagerPipe;
        private int _duration = 1800;
        private string _themeMode = "light";

        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _db = new SampleDatabase("smartsensor.db");
            _acq = new AcquisitionController(_db, onError: msg => Console.WriteLine(msg),
                statusProvider: () => _poller != null ? _poller.Status : null);
            _poller = new TaskManagerStatusPoller(() => _taskManagerPipeName);

            ChannelPanel1.Initialize(Enumerable.Repeat(true, 16).ToList(), Palette.ChannelColors);
            ChannelPanel1.Changed += OnChannelChecksChanged;

            _plotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _plotTimer.Tick += (s, ev) => RefreshPlot();

            RefreshBoards();
            RefreshPlot();
            ApplyTheme("light");
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _plotTimer?.Stop();
            _acq?.Dispose();
            _poller?.Dispose();
            _db?.Dispose();
        }

        // ---------------- 采集板发现 ----------------

        private void RefreshBoards()
        {
            var boards = SmartsensorDiscovery.DiscoverBoards();
            BoardCombo.Items.Clear();
            foreach (var b in boards)
            {
                BoardCombo.Items.Add(new BoardOption(b));
            }

            // 只连接一块板时自动选择。
            if (boards.Count == 1)
            {
                BoardCombo.SelectedIndex = 0;
            }
        }

        private void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            RefreshBoards();
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
            {
                return;
            }

            _db?.ClearAll();
            RefreshPlot();
            MessageBox.Show(this, "Database has been cleared.", "Clear Database",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------------- 开始 / 停止 ----------------

        private void OnStartClicked(object sender, RoutedEventArgs e)
        {
            if (_acq.IsRunning)
            {
                StopAcquisition();
                return;
            }

            var option = BoardCombo.SelectedItem as BoardOption;
            if (option == null)
            {
                MessageBox.Show(this, "Please select a board first.", "SmartSensor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> ports;
            try
            {
                ports = CollectPorts(option.Board);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "SmartSensor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _acq.OpenPorts(ports);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "SmartSensor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _poller.Start();
            _acq.Start();
            _plotTimer.Start();
            RefreshPlot();
            StartBtn.Content = "Stop";
            StatusText.Text = $"Acquiring {ports.Count} channels...";
        }

        private void StopAcquisition()
        {
            _acq.Stop();
            _acq.ClosePorts();
            _poller.Stop();
            _plotTimer.Stop();
            StartBtn.Content = "Start";
            StatusText.Text = "Stopped";
        }

        /// <summary>按通道 1~16 顺序收集串口名（组1 Ch A~H → 1~8，组2 Ch A~H → 9~16）。</summary>
        private static List<string> CollectPorts(SmartsensorBoard board)
        {
            var ports = new List<string>(16);
            foreach (var group in board.Groups.OrderBy(g => g.GroupIndex))
            {
                foreach (var port in group.Ports.OrderBy(p => p.Channel, StringComparer.Ordinal))
                {
                    if (string.IsNullOrEmpty(port.PortName))
                    {
                        throw new InvalidOperationException(
                            $"Board {board.BoardIndex}: channel {port.Channel} has no COM port assigned.");
                    }
                    ports.Add(port.PortName);
                }
            }
            if (ports.Count != 16)
            {
                throw new InvalidOperationException(
                    $"Board {board.BoardIndex}: only {ports.Count} of 16 ports found.");
            }
            return ports;
        }

        // ---------------- 图表 ----------------

        private void OnFieldChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_db != null)
            {
                RefreshPlot();
            }
        }

        private void OnDurationSetClicked(object sender, RoutedEventArgs e)
        {
            int seconds;
            if (!int.TryParse(DurationEdit.Text.Trim(), out seconds) || seconds <= 0)
            {
                MessageBox.Show(this, "Duration must be a positive integer (seconds).", "SmartSensor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _duration = seconds;
            PlotPanel1.ResetAxes(_duration);
            RefreshPlot();
        }

        private void OnChannelChecksChanged()
        {
            RefreshPlot();
        }

        private void OnTaskManagerPipeTextChanged(object sender, TextChangedEventArgs e)
        {
            _taskManagerPipeName = TaskManagerPipeEdit.Text.Trim();
        }

        private void RefreshPlot()
        {
            if (_db == null)
            {
                return;
            }

            var rows = _db.FetchRecent(_duration);
            string field = (FieldCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "reading";

            // 各通道最新值显示（按所选字段）。
            var latest = new double?[16];
            foreach (var row in rows)
            {
                if (row.Channel >= 1 && row.Channel <= 16)
                {
                    latest[row.Channel - 1] = SelectFieldValue(row, field);
                }
            }
            ChannelPanel1.SetValues(latest, field);

            var selected = ChannelPanel1.GetStates();
            PlotPanel1.Render(rows, field, selected, _duration);
        }

        private static double SelectFieldValue(SampleRow row, string field)
        {
            switch (field)
            {
                case "raw":
                    return row.Raw;
                case "raw_ref":
                    return row.RawRef;
                case "temperature":
                    return row.Temperature;
                default:
                    return row.Reading;
            }
        }

        // ---------------- Theme ----------------

        private void OnThemeButtonClicked(object sender, RoutedEventArgs e)
        {
            var next = _themeMode == "light" ? "dark" : "light";
            ApplyTheme(next);
        }

        private void ApplyTheme(string mode)
        {
            _themeMode = mode == "dark" ? "dark" : "light";
            bool dark = _themeMode == "dark";

            var fg = dark
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xEF))
                : new SolidColorBrush(Colors.Black);

            Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
                : new SolidColorBrush(Colors.White);
            Foreground = fg;

            PlotPanel1.ApplyTheme(_themeMode);
            ThemeButton.Content = dark ? "Light" : "Dark";
        }
    }

    /// <summary>采集板下拉框项。</summary>
    public sealed class BoardOption
    {
        public SmartsensorBoard Board { get; }

        public BoardOption(SmartsensorBoard board)
        {
            Board = board;
        }

        public override string ToString()
        {
            int portCount = Board.Groups.Sum(g => g.Ports.Count);
            return $"Board {Board.BoardIndex} ({portCount} ports)";
        }
    }
}
