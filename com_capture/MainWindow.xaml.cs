using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace com_capture
{
    public partial class MainWindow : Window
    {
        // 连续收到的字节若在 80ms 内没有后续数据，则视为一条完整报文并记入日志。
        private const long IdleGapTicks = 80 * TimeSpan.TicksPerMillisecond;
        private const int MaxLogEntries = 3000;
        private const int MaxDisplayBytes = 512;

        private sealed class PortState
        {
            public SerialPort Port;
            public readonly List<byte> Pending = new List<byte>();
            public long LastTicks;
            public long RxBytes;
            public long TxBytes;
        }

        private sealed class LogEntry
        {
            public string TimeText { get; set; }
            public string DirectionText { get; set; }
            public string DirectionColor { get; set; }
            public string LenText { get; set; }
            public string HexText { get; set; }
            public string AsciiText { get; set; }
        }

        private readonly object dataLock = new object();
        private readonly ObservableCollection<LogEntry> logEntries = new ObservableCollection<LogEntry>();
        private readonly DispatcherTimer flushTimer;
        private PortState state1;
        private PortState state2;

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = logEntries;

            flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            flushTimer.Tick += FlushTimer_Tick;

            RefreshPorts();
        }

        private void RefreshPorts()
        {
            string prev1 = Port1Combo.Text;
            string prev2 = Port2Combo.Text;
            string[] names;
            try
            {
                names = SerialPort.GetPortNames();
            }
            catch (Exception ex)
            {
                MessageBox.Show("扫描串口失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Array.Sort(names, (a, b) =>
            {
                int na, nb;
                bool oka = int.TryParse(a.Replace("COM", ""), out na);
                bool okb = int.TryParse(b.Replace("COM", ""), out nb);
                if (oka && okb) return na.CompareTo(nb);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            Port1Combo.ItemsSource = names;
            Port2Combo.ItemsSource = names;

            bool has1 = SetSelected(Port1Combo, names, prev1);
            bool has2 = SetSelected(Port2Combo, names, prev2);
            if (!has1 && names.Length > 0) Port1Combo.SelectedIndex = 0;
            if (!has2 && names.Length > 1) Port2Combo.SelectedIndex = 1;
        }

        private static bool SetSelected(System.Windows.Controls.ComboBox combo, string[] names, string prev)
        {
            if (string.IsNullOrEmpty(prev)) return false;
            foreach (string n in names)
            {
                if (string.Equals(n, prev, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = n;
                    return true;
                }
            }
            return false;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        private void StartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (StartToggle.IsChecked == true)
                TryStart();
            else
                Stop();
        }

        private void TryStart()
        {
            string name1 = (Port1Combo.Text ?? "").Trim();
            string name2 = (Port2Combo.Text ?? "").Trim();

            if (name1.Length == 0 || name2.Length == 0)
            {
                MessageBox.Show("请先选择串口 1 和串口 2。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                StartToggle.IsChecked = false;
                return;
            }
            if (string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("串口 1 和串口 2 不能相同。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                StartToggle.IsChecked = false;
                return;
            }

            string baudText = (BaudTextBox.Text ?? "").Trim();
            if (baudText.Length == 0) baudText = "9600";
            int baud;
            if (!int.TryParse(baudText, out baud) || baud <= 0)
            {
                MessageBox.Show("波特率必须是正整数，例如 9600。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                StartToggle.IsChecked = false;
                return;
            }

            SerialPort sp1 = null;
            SerialPort sp2 = null;
            try
            {
                sp1 = OpenPort(name1, baud);
                sp2 = OpenPort(name2, baud);
            }
            catch (Exception ex)
            {
                ClosePort(sp1);
                ClosePort(sp2);
                MessageBox.Show("打开串口失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StartToggle.IsChecked = false;
                return;
            }

            lock (dataLock)
            {
                state1 = new PortState { Port = sp1 };
                state2 = new PortState { Port = sp2 };
            }
            sp1.DataReceived += Port_DataReceived;
            sp2.DataReceived += Port_DataReceived;

            StatusText.Text = string.Format("已连接：{0} ↔ {1}  @  {2} 波特", name1, name2, baud);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
            StatsText.Text = "";
            SetInputsEnabled(false);
            flushTimer.Start();
        }

        private static SerialPort OpenPort(string name, int baud)
        {
            var sp = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500
            };
            sp.Open();
            return sp;
        }

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var sp = (SerialPort)sender;
            PortState from = null;
            PortState to = null;
            byte[] chunk = null;

            lock (dataLock)
            {
                if (state1 == null || state2 == null) return;
                if (ReferenceEquals(sp, state1.Port)) { from = state1; to = state2; }
                else if (ReferenceEquals(sp, state2.Port)) { from = state2; to = state1; }
                else return;

                try
                {
                    int n = sp.BytesToRead;
                    if (n <= 0) return;
                    var buf = new byte[n];
                    int read = sp.Read(buf, 0, n);
                    if (read <= 0) return;

                    chunk = new byte[read];
                    Array.Copy(buf, chunk, read);

                    from.Pending.AddRange(chunk);
                    from.LastTicks = DateTime.UtcNow.Ticks;
                    from.RxBytes += read;

                    if (to.Port != null && to.Port.IsOpen)
                    {
                        to.Port.Write(chunk, 0, chunk.Length);
                        to.TxBytes += chunk.Length;
                    }
                }
                catch (Exception ex)
                {
                    LogSystemMessage("串口错误：" + ex.Message);
                    Dispatcher.BeginInvoke(new Action(Stop));
                }
            }
        }

        private void FlushTimer_Tick(object sender, EventArgs e)
        {
            long now = DateTime.UtcNow.Ticks;
            FlushState(state1, now, true);
            FlushState(state2, now, false);
        }

        private void FlushState(PortState state, long now, bool fromPort1)
        {
            byte[] data;
            lock (dataLock)
            {
                if (state == null || state.Pending.Count == 0) return;
                if (now - state.LastTicks < IdleGapTicks) return;
                data = state.Pending.ToArray();
                state.Pending.Clear();
            }

            string name1 = state1.Port.PortName;
            string name2 = state2.Port.PortName;
            string dir = fromPort1 ? name1 + " → " + name2 : name2 + " → " + name1;
            string color = fromPort1 ? "#1565C0" : "#2E7D32";

            AppendLog(new LogEntry
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss.fff"),
                DirectionText = dir,
                DirectionColor = color,
                LenText = data.Length + " B",
                HexText = ToHex(data),
                AsciiText = ToAscii(data)
            });
        }

        private void AppendLog(LogEntry entry)
        {
            logEntries.Add(entry);
            while (logEntries.Count > MaxLogEntries)
                logEntries.RemoveAt(0);

            if (AutoScrollCheck.IsChecked == true && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);

            UpdateStats();
        }

        private void UpdateStats()
        {
            string name1 = "";
            string name2 = "";
            long p1to2 = 0;
            long p2to1 = 0;
            lock (dataLock)
            {
                if (state1 != null)
                {
                    name1 = state1.Port.PortName;
                    p1to2 = state1.RxBytes;
                    p2to1 = state1.TxBytes;
                }
                if (state2 != null)
                {
                    name2 = state2.Port.PortName;
                }
            }
            if (state1 == null) return;
            StatsText.Text = string.Format("{0}→{1}: {2} B    {1}→{0}: {3} B",
                name1, name2, p1to2, p2to1);
        }

        private void LogSystemMessage(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog(new LogEntry
                {
                    TimeText = DateTime.Now.ToString("HH:mm:ss.fff"),
                    DirectionText = "系统",
                    DirectionColor = "#E67E22",
                    LenText = "",
                    HexText = text,
                    AsciiText = ""
                });
            }));
        }

        private void Stop()
        {
            PortState s1;
            PortState s2;
            lock (dataLock)
            {
                s1 = state1;
                s2 = state2;
                state1 = null;
                state2 = null;
            }

            if (s1 != null)
            {
                s1.Port.DataReceived -= Port_DataReceived;
                ClosePort(s1.Port);
            }
            if (s2 != null)
            {
                s2.Port.DataReceived -= Port_DataReceived;
                ClosePort(s2.Port);
            }

            flushTimer.Stop();
            StatusText.Text = "未连接";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            StartToggle.IsChecked = false;
            SetInputsEnabled(true);
        }

        private static void ClosePort(SerialPort sp)
        {
            if (sp == null) return;
            try
            {
                if (sp.IsOpen) sp.Close();
            }
            catch
            {
                // 关闭失败时端口已被释放或不可用，忽略。
            }
            sp.Dispose();
        }

        private void SetInputsEnabled(bool enabled)
        {
            Port1Combo.IsEnabled = enabled;
            Port2Combo.IsEnabled = enabled;
            BaudTextBox.IsEnabled = enabled;
            RefreshButton.IsEnabled = enabled;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            logEntries.Clear();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            flushTimer.Stop();
            Stop();
        }

        private static string ToHex(byte[] data)
        {
            int shown = Math.Min(data.Length, MaxDisplayBytes);
            var sb = new System.Text.StringBuilder(shown * 3);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            if (data.Length > shown)
                sb.Append("  …");
            return sb.ToString();
        }

        private static string ToAscii(byte[] data)
        {
            int shown = Math.Min(data.Length, MaxDisplayBytes);
            var sb = new System.Text.StringBuilder(shown);
            for (int i = 0; i < shown; i++)
            {
                byte b = data[i];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            if (data.Length > shown)
                sb.Append("  …");
            return sb.ToString();
        }

        private void BaudTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void BaudTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            string s = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrEmpty(s))
            {
                e.CancelCommand();
                return;
            }
            foreach (char c in s.Trim())
            {
                if (!char.IsDigit(c))
                {
                    e.CancelCommand();
                    return;
                }
            }
        }
    }
}
