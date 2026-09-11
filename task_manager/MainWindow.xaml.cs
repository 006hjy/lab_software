using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace task_manager
{
    public partial class MainWindow : Window
    {
        // ---- Win32 Beep API ----
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Beep(uint dwFreq, uint dwDuration);

        // ---- Default pipe names ----
        private const string DefaultMfcPipe = "alicat_mfc";
        private const string DefaultChamberPipe = "chamber";
        private const string DefaultSelfPipe = "task_manager";

        /// <summary>Timeout for a single pipe request/response in ms.</summary>
        private const int PipeTimeoutMs = 5000;

        /// <summary>Poll interval used by wait-for-condition tasks, ms.</summary>
        private const int WaitPollMs = 1000;

        /// <summary>Tolerance for comparing set temp/humidity against the 0.1-quantized register readback.</summary>
        private const double SetVerifyTolerance = 0.051;

        // ---- Task list ----
        private readonly List<TaskItem> _tasks = new List<TaskItem>();

        // ---- Pipe name snapshot (captured on the UI thread at Start, read by the runner thread) ----
        private string _runMfcPipe;
        private string _runChamberPipe;

        /// <summary>重复执行次数快照（Start 时从界面捕获）。</summary>
        private int _runRepeatCount = 1;

        /// <summary>Lock guarding writes to the log file.</summary>
        private static readonly object LogLock = new object();

        // ---- Execution state ----
        private Thread _runnerThread;
        private volatile bool _stopRequested;
        private volatile bool _running;
        private int _currentIndex = -1;
        private readonly object _statusLock = new object();

        /// <summary>Stop 按钮右侧的状态文字（get_status 的 Running 字段）。</summary>
        private volatile string _statusText = "";

        /// <summary>当前 MFC 管道名（实时跟随文本框）。</summary>
        private volatile string _mfcPipeName = DefaultMfcPipe;

        /// <summary>当前 chamber 管道名（实时跟随文本框）。</summary>
        private volatile string _chamberPipeName = DefaultChamberPipe;

        /// <summary>每秒轮询 chamber 得到的缓存快照（不可变对象，跨线程安全）。</summary>
        private volatile ChamberSnapshot _chamberSnapshot;

        /// <summary>每秒查询 chamber 状态的后台定时器。</summary>
        private Timer _chamberStatusTimer;

        /// <summary>Content of the last task that operated on the MFC.</summary>
        private volatile string _lastMfcTask;

        // ---- Self pipe server ----
        private Thread _pipeAcceptThread;
        private volatile bool _pipeRunning;

        // ---- Serialization ----
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static readonly Regex NumericRegex = new Regex(@"^-?\d*\.?\d{0,3}$");

        public MainWindow()
        {
            InitializeComponent();

            LoadConfig();
            BuildTypeCombo();
            RefreshTaskList();
            UpdateEditorVisibility();
            StartSelfPipeServer();

            // 每秒后台轮询 chamber 状态并缓存。
            _chamberStatusTimer = new Timer(OnChamberStatusTick, null, 0, 1000);
        }

        protected override void OnClosed(EventArgs e)
        {
            IniConfig.Write("Window", "Width", Width.ToString(CultureInfo.InvariantCulture));
            IniConfig.Write("Window", "Height", Height.ToString(CultureInfo.InvariantCulture));

            _chamberStatusTimer?.Dispose();
            _chamberStatusTimer = null;
            StopSelfPipeServer();
            _stopRequested = true;
            base.OnClosed(e);
        }

        // ================= Task list management =================

        /// <summary>Build the type combo with all task types.</summary>
        private void BuildTypeCombo()
        {
            TypeComboBox.Items.Clear();
            TypeComboBox.Items.Add(new TypeOption(TaskType.WaitSeconds, "Wait seconds"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.MfcMix, "MFC: set mixing and apply"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.MfcSetpoints, "MFC: set two flows"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberSetTemp, "Chamber: set temperature"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberSetHumidity, "Chamber: set humidity"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberStart, "Chamber: start"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberStop, "Chamber: stop"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberWaitTemp, "Chamber: wait temperature"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberWaitHumidity, "Chamber: wait humidity"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberStartTempRamp, "Chamber: start temperature ramp"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberStartHumRamp, "Chamber: start humidity ramp"));
            TypeComboBox.Items.Add(new TypeOption(TaskType.ChamberSetFan, "Chamber: set fan speed"));
            TypeComboBox.SelectedIndex = 0;
        }

        /// <summary>Description of a task, shown in the list.</summary>
        private static string Describe(TaskItem t)
        {
            string d = "";
            switch (t.Type)
            {
                case TaskType.WaitSeconds:
                    d = "Wait " + F(t.Seconds) + " s"
                        + (t.BeepSeconds > 0 ? " (beep last " + F(t.BeepSeconds) + " s)" : "");
                    break;
                case TaskType.MfcMix:
                    d = "MFC mix: total " + F(t.TotalFlow) + ", source " + F(t.SourceConc)
                        + ", target " + F(t.TargetConc);
                    break;
                case TaskType.MfcSetpoints:
                    d = "MFC flows: target " + F(t.TargetFlow) + ", background " + F(t.BackgroundFlow);
                    break;
                case TaskType.ChamberSetTemp:
                    d = "Chamber set temp " + F(t.Value) + " °C";
                    break;
                case TaskType.ChamberSetHumidity:
                    d = "Chamber set humidity " + F(t.Value) + " %RH";
                    break;
                case TaskType.ChamberStart:
                    d = "Chamber start";
                    break;
                case TaskType.ChamberStop:
                    d = "Chamber stop";
                    break;
                case TaskType.ChamberWaitTemp:
                    d = "Chamber wait temp " + F(t.Value) + " ± " + F(t.Tolerance) + " °C"
                        + (t.TimeoutSeconds > 0 ? " (timeout " + F(t.TimeoutSeconds) + " s)" : "");
                    break;
                case TaskType.ChamberWaitHumidity:
                    d = "Chamber wait humidity " + F(t.Value) + " ± " + F(t.Tolerance) + " %RH"
                        + (t.TimeoutSeconds > 0 ? " (timeout " + F(t.TimeoutSeconds) + " s)" : "");
                    break;
                case TaskType.ChamberStartTempRamp:
                    d = "Chamber temp ramp " + F(t.Value) + " → " + F(t.EndValue) + " °C over "
                        + F(t.DurationSeconds) + " s";
                    break;
                case TaskType.ChamberStartHumRamp:
                    d = "Chamber hum ramp " + F(t.Value) + " → " + F(t.EndValue) + " %RH over "
                        + F(t.DurationSeconds) + " s";
                    break;
                case TaskType.ChamberSetFan:
                    d = "Chamber set fan " + F(t.Value) + " %";
                    break;
            }
            return d;
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private void RefreshTaskList()
        {
            TaskListBox.Items.Clear();
            foreach (TaskItem t in _tasks)
            {
                TaskListBox.Items.Add(new TaskListEntry(t, Describe(t)));
            }
            UpdateListButtons();
        }

        // ================= Task editor =================

        private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateEditorVisibility();
        }

        /// <summary>Show/hide editor fields based on the selected task type.</summary>
        private void UpdateEditorVisibility()
        {
            TypeOption opt = TypeComboBox.SelectedItem as TypeOption;
            TaskType type = opt != null ? opt.Type : TaskType.WaitSeconds;

            bool showSeconds = type == TaskType.WaitSeconds;
            bool showBeepSeconds = type == TaskType.WaitSeconds;

            bool showTotalFlow = type == TaskType.MfcMix;
            bool showSourceConc = type == TaskType.MfcMix;
            bool showTargetConc = type == TaskType.MfcMix;

            bool showTargetFlow = type == TaskType.MfcSetpoints;
            bool showBackgroundFlow = type == TaskType.MfcSetpoints;

            bool showValue = type == TaskType.ChamberSetTemp
                          || type == TaskType.ChamberSetHumidity
                          || type == TaskType.ChamberWaitTemp
                          || type == TaskType.ChamberWaitHumidity
                          || type == TaskType.ChamberStartTempRamp
                          || type == TaskType.ChamberStartHumRamp
                          || type == TaskType.ChamberSetFan;

            bool showEndValue = type == TaskType.ChamberStartTempRamp
                             || type == TaskType.ChamberStartHumRamp;

            bool showDuration = type == TaskType.ChamberStartTempRamp
                             || type == TaskType.ChamberStartHumRamp;

            bool showTolerance = type == TaskType.ChamberWaitTemp
                              || type == TaskType.ChamberWaitHumidity;

            bool showTimeout = type == TaskType.ChamberWaitTemp
                            || type == TaskType.ChamberWaitHumidity;

            SecondsLabel.Visibility = showSeconds ? Visibility.Visible : Visibility.Collapsed;
            SecondsTextBox.Visibility = showSeconds ? Visibility.Visible : Visibility.Collapsed;
            BeepSecondsLabel.Visibility = showBeepSeconds ? Visibility.Visible : Visibility.Collapsed;
            BeepSecondsTextBox.Visibility = showBeepSeconds ? Visibility.Visible : Visibility.Collapsed;

            TotalFlowLabel.Visibility = showTotalFlow ? Visibility.Visible : Visibility.Collapsed;
            TotalFlowTextBox.Visibility = showTotalFlow ? Visibility.Visible : Visibility.Collapsed;
            SourceConcLabel.Visibility = showSourceConc ? Visibility.Visible : Visibility.Collapsed;
            SourceConcTextBox.Visibility = showSourceConc ? Visibility.Visible : Visibility.Collapsed;
            TargetConcLabel.Visibility = showTargetConc ? Visibility.Visible : Visibility.Collapsed;
            TargetConcTextBox.Visibility = showTargetConc ? Visibility.Visible : Visibility.Collapsed;

            TargetFlowLabel.Visibility = showTargetFlow ? Visibility.Visible : Visibility.Collapsed;
            TargetFlowTextBox.Visibility = showTargetFlow ? Visibility.Visible : Visibility.Collapsed;
            BackgroundFlowLabel.Visibility = showBackgroundFlow ? Visibility.Visible : Visibility.Collapsed;
            BackgroundFlowTextBox.Visibility = showBackgroundFlow ? Visibility.Visible : Visibility.Collapsed;

            ValueLabel.Text = showEndValue ? "Start value:" : "Target value:";
            ValueLabel.Visibility = showValue ? Visibility.Visible : Visibility.Collapsed;
            ValueTextBox.Visibility = showValue ? Visibility.Visible : Visibility.Collapsed;

            EndValueLabel.Visibility = showEndValue ? Visibility.Visible : Visibility.Collapsed;
            EndValueTextBox.Visibility = showEndValue ? Visibility.Visible : Visibility.Collapsed;
            DurationLabel.Visibility = showDuration ? Visibility.Visible : Visibility.Collapsed;
            DurationTextBox.Visibility = showDuration ? Visibility.Visible : Visibility.Collapsed;

            ToleranceLabel.Visibility = showTolerance ? Visibility.Visible : Visibility.Collapsed;
            ToleranceTextBox.Visibility = showTolerance ? Visibility.Visible : Visibility.Collapsed;
            TimeoutLabel.Visibility = showTimeout ? Visibility.Visible : Visibility.Collapsed;
            TimeoutTextBox.Visibility = showTimeout ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= Editor buttons =================

        private void AddTaskButton_Click(object sender, RoutedEventArgs e)
        {
            TaskItem t;
            if (!ReadEditor(out t))
            {
                return;
            }
            _tasks.Add(t);
            SaveTasks();
            RefreshTaskList();
        }

        private void EditTaskButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = TaskListBox.SelectedIndex;
            if (idx < 0 || idx >= _tasks.Count)
            {
                ShowError("Select a task to edit.");
                return;
            }
            TaskItem t;
            if (!ReadEditor(out t))
            {
                return;
            }
            _tasks[idx] = t;
            SaveTasks();
            RefreshTaskList();
            TaskListBox.SelectedIndex = idx;
        }

        private void DeleteTaskButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = TaskListBox.SelectedIndex;
            if (idx < 0 || idx >= _tasks.Count)
            {
                ShowError("Select a task to delete.");
                return;
            }
            _tasks.RemoveAt(idx);
            SaveTasks();
            RefreshTaskList();
        }

        private void DuplicateTaskButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = TaskListBox.SelectedIndex;
            if (idx < 0 || idx >= _tasks.Count)
            {
                ShowError("Select a task to copy.");
                return;
            }
            _tasks.Insert(idx + 1, CloneTask(_tasks[idx]));
            SaveTasks();
            RefreshTaskList();
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = TaskListBox.SelectedIndex;
            if (idx <= 0 || idx >= _tasks.Count)
            {
                return;
            }
            TaskItem tmp = _tasks[idx - 1];
            _tasks[idx - 1] = _tasks[idx];
            _tasks[idx] = tmp;
            SaveTasks();
            RefreshTaskList();
            TaskListBox.SelectedIndex = idx - 1;
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = TaskListBox.SelectedIndex;
            if (idx < 0 || idx >= _tasks.Count - 1)
            {
                return;
            }
            TaskItem tmp = _tasks[idx + 1];
            _tasks[idx + 1] = _tasks[idx];
            _tasks[idx] = tmp;
            SaveTasks();
            RefreshTaskList();
            TaskListBox.SelectedIndex = idx + 1;
        }

        private void UpdateListButtons()
        {
            bool canEdit = _tasks.Count > 0;
            EditTaskButton.IsEnabled = canEdit;
            DeleteTaskButton.IsEnabled = canEdit;
            DuplicateTaskButton.IsEnabled = canEdit;
            MoveUpButton.IsEnabled = canEdit;
            MoveDownButton.IsEnabled = canEdit;
        }

        private void TaskListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = TaskListBox.SelectedIndex;
            if (idx >= 0 && idx < _tasks.Count)
            {
                LoadEditor(_tasks[idx]);
            }
        }

        private static TaskItem CloneTask(TaskItem src)
        {
            var t = new TaskItem
            {
                Type = src.Type,
                Seconds = src.Seconds,
                BeepSeconds = src.BeepSeconds,
                TotalFlow = src.TotalFlow,
                SourceConc = src.SourceConc,
                TargetConc = src.TargetConc,
                TargetFlow = src.TargetFlow,
                BackgroundFlow = src.BackgroundFlow,
                Value = src.Value,
                Tolerance = src.Tolerance,
                TimeoutSeconds = src.TimeoutSeconds,
                EndValue = src.EndValue,
                DurationSeconds = src.DurationSeconds
            };
            return t;
        }

        /// <summary>Load a task into the editor controls.</summary>
        private void LoadEditor(TaskItem t)
        {
            for (int i = 0; i < TypeComboBox.Items.Count; i++)
            {
                TypeOption opt = (TypeOption)TypeComboBox.Items[i];
                if (opt.Type == t.Type)
                {
                    TypeComboBox.SelectedIndex = i;
                    break;
                }
            }

            SecondsTextBox.Text = F(t.Seconds);
            BeepSecondsTextBox.Text = t.BeepSeconds > 0 ? F(t.BeepSeconds) : "";
            TotalFlowTextBox.Text = F(t.TotalFlow);
            SourceConcTextBox.Text = F(t.SourceConc);
            TargetConcTextBox.Text = F(t.TargetConc);
            TargetFlowTextBox.Text = F(t.TargetFlow);
            BackgroundFlowTextBox.Text = F(t.BackgroundFlow);
            ValueTextBox.Text = F(t.Value);
            ToleranceTextBox.Text = F(t.Tolerance);
            TimeoutTextBox.Text = t.TimeoutSeconds > 0 ? F(t.TimeoutSeconds) : "";
            EndValueTextBox.Text = F(t.EndValue);
            DurationTextBox.Text = t.DurationSeconds > 0 ? F(t.DurationSeconds) : "";
        }

        /// <summary>Read the editor controls into a new task. Shows an error and returns false if invalid.</summary>
        private bool ReadEditor(out TaskItem t)
        {
            t = new TaskItem();
            TypeOption opt = TypeComboBox.SelectedItem as TypeOption;
            TaskType type = opt != null ? opt.Type : TaskType.WaitSeconds;
            t.Type = type;

            double seconds, beepSeconds, totalFlow, sourceConc, targetConc;
            double targetFlow, backgroundFlow, value, tolerance, timeout;

            switch (type)
            {
                case TaskType.WaitSeconds:
                    if (!ReadDouble(SecondsTextBox, 0, 1e9, out seconds, "Seconds must be >= 0")) return false;
                    if (!ReadOptionalDouble(BeepSecondsTextBox, out beepSeconds, "Beep seconds must be a number")) return false;
                    if (beepSeconds > seconds)
                    {
                        ShowError("Beep seconds cannot exceed total seconds.");
                        return false;
                    }
                    t.Seconds = seconds;
                    t.BeepSeconds = beepSeconds;
                    break;

                case TaskType.MfcMix:
                    if (!ReadDouble(TotalFlowTextBox, 0.0001, 1000, out totalFlow, "Total flow must be > 0 and <= 1000 SCCM")) return false;
                    if (!ReadDouble(SourceConcTextBox, 0.0001, 1000000, out sourceConc, "Source conc must be > 0 and <= 1000000")) return false;
                    if (!ReadDouble(TargetConcTextBox, 0, 1000000, out targetConc, "Target conc must be >= 0 and <= 1000000")) return false;
                    if (targetConc > sourceConc)
                    {
                        ShowError("Target conc cannot exceed source conc.");
                        return false;
                    }
                    t.TotalFlow = totalFlow;
                    t.SourceConc = sourceConc;
                    t.TargetConc = targetConc;
                    break;

                case TaskType.MfcSetpoints:
                    if (!ReadDouble(TargetFlowTextBox, 0, 500, out targetFlow, "Target flow must be between 0 and 500 SCCM")) return false;
                    if (!ReadDouble(BackgroundFlowTextBox, 0, 500, out backgroundFlow, "Background flow must be between 0 and 500 SCCM")) return false;
                    t.TargetFlow = targetFlow;
                    t.BackgroundFlow = backgroundFlow;
                    break;

                case TaskType.ChamberSetTemp:
                    if (!ReadDouble(ValueTextBox, -40, 150, out value, "Temperature must be between -40 and 150 °C")) return false;
                    t.Value = value;
                    break;

                case TaskType.ChamberSetHumidity:
                    if (!ReadDouble(ValueTextBox, 0, 100, out value, "Humidity must be between 0 and 100 %RH")) return false;
                    t.Value = value;
                    break;

                case TaskType.ChamberWaitTemp:
                    if (!ReadDouble(ValueTextBox, -40, 150, out value, "Temperature must be between -40 and 150 °C")) return false;
                    if (!ReadDouble(ToleranceTextBox, 0, 100, out tolerance, "Tolerance must be >= 0")) return false;
                    if (!ReadOptionalDouble(TimeoutTextBox, out timeout, "Timeout must be a number")) return false;
                    t.Value = value;
                    t.Tolerance = tolerance;
                    t.TimeoutSeconds = timeout;
                    break;

                case TaskType.ChamberWaitHumidity:
                    if (!ReadDouble(ValueTextBox, 0, 100, out value, "Humidity must be between 0 and 100 %RH")) return false;
                    if (!ReadDouble(ToleranceTextBox, 0, 100, out tolerance, "Tolerance must be >= 0")) return false;
                    if (!ReadOptionalDouble(TimeoutTextBox, out timeout, "Timeout must be a number")) return false;
                    t.Value = value;
                    t.Tolerance = tolerance;
                    t.TimeoutSeconds = timeout;
                    break;

                case TaskType.ChamberStartTempRamp:
                    if (!ReadDouble(ValueTextBox, -40, 150, out value, "Temperature must be between -40 and 150 °C")) return false;
                    if (!ReadDouble(EndValueTextBox, -40, 150, out double endValue, "Temperature must be between -40 and 150 °C")) return false;
                    if (!ReadPositiveInt(DurationTextBox, out double duration, "Duration must be a positive integer (seconds)")) return false;
                    t.Value = value;
                    t.EndValue = endValue;
                    t.DurationSeconds = duration;
                    break;

                case TaskType.ChamberStartHumRamp:
                    if (!ReadDouble(ValueTextBox, 0, 100, out value, "Humidity must be between 0 and 100 %RH")) return false;
                    if (!ReadDouble(EndValueTextBox, 0, 100, out double endHum, "Humidity must be between 0 and 100 %RH")) return false;
                    if (!ReadPositiveInt(DurationTextBox, out double humDuration, "Duration must be a positive integer (seconds)")) return false;
                    t.Value = value;
                    t.EndValue = endHum;
                    t.DurationSeconds = humDuration;
                    break;

                case TaskType.ChamberSetFan:
                    if (!ReadDouble(ValueTextBox, 30, 100, out value, "Fan speed must be between 30 and 100 %")) return false;
                    t.Value = value;
                    break;
            }
            return true;
        }

        private static bool ReadDouble(TextBox tb, double min, double max, out double v, string error)
        {
            if (!double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < min || v > max)
            {
                ShowError(error);
                return false;
            }
            return true;
        }

        /// <summary>Read an optional timeout (empty string = 0 = no timeout).</summary>
        private static bool ReadOptionalDouble(TextBox tb, out double v, string error)
        {
            v = 0;
            string s = tb.Text.Trim();
            if (string.IsNullOrEmpty(s)) return true;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0)
            {
                ShowError(error);
                return false;
            }
            return true;
        }

        /// <summary>Read a whole positive number (ramp duration in seconds).</summary>
        private static bool ReadPositiveInt(TextBox tb, out double v, string error)
        {
            v = 0;
            if (!double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                || v < 1 || v > 1e9 || Math.Abs(v - Math.Round(v)) > 0.0001)
            {
                ShowError(error);
                return false;
            }
            return true;
        }

        private static void ShowError(string msg)
        {
            MessageBox.Show(msg, "Task Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ================= Numeric input validation =================

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = (TextBox)sender;
            string candidate = tb.Text
                .Remove(tb.SelectionStart, tb.SelectionLength)
                .Insert(tb.SelectionStart, e.Text);
            if (!NumericRegex.IsMatch(candidate))
            {
                e.Handled = true;
            }
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var tb = (TextBox)sender;
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pasted = (string)e.DataObject.GetData(typeof(string));
                string candidate = tb.Text
                    .Remove(tb.SelectionStart, tb.SelectionLength)
                    .Insert(tb.SelectionStart, pasted);
                if (!NumericRegex.IsMatch(candidate))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        // ================= Persistence =================

        private void LoadConfig()
        {
            // Window size
            double width, height;
            if (double.TryParse(IniConfig.Read("Window", "Width", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out width) && width >= 400)
            {
                Width = width;
            }
            if (double.TryParse(IniConfig.Read("Window", "Height", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out height) && height >= 300)
            {
                Height = height;
            }

            string mfc = IniConfig.Read("Pipes", "MfcPipe", DefaultMfcPipe);
            string chamber = IniConfig.Read("Pipes", "ChamberPipe", DefaultChamberPipe);
            string self = IniConfig.Read("Pipes", "SelfPipe", DefaultSelfPipe);
            if (!string.IsNullOrEmpty(mfc)) MfcPipeTextBox.Text = mfc;
            if (!string.IsNullOrEmpty(chamber)) ChamberPipeTextBox.Text = chamber;
            if (!string.IsNullOrEmpty(self)) SelfPipeTextBox.Text = self;
            _mfcPipeName = MfcPipeTextBox.Text.Trim();
            _chamberPipeName = ChamberPipeTextBox.Text.Trim();

            string repeat = IniConfig.Read("Run", "Repeat", "1");
            int repeatCount;
            if (int.TryParse(repeat, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeatCount)
                && repeatCount >= 1)
            {
                RepeatEdit.Text = repeatCount.ToString(CultureInfo.InvariantCulture);
            }

            _tasks.Clear();
            _tasks.AddRange(TaskListCodec.Load());
        }

        /// <summary>MFC 管道名文本框变化 → 立即更新 <see cref="_mfcPipeName"/>。</summary>
        private void OnMfcPipeTextChanged(object sender, TextChangedEventArgs e)
        {
            _mfcPipeName = MfcPipeTextBox.Text.Trim();
        }

        /// <summary>Chamber 管道名文本框变化 → 立即更新 <see cref="_chamberPipeName"/>。</summary>
        private void OnChamberPipeTextChanged(object sender, TextChangedEventArgs e)
        {
            _chamberPipeName = ChamberPipeTextBox.Text.Trim();
        }

        private void SaveConfig()
        {
            IniConfig.Write("Pipes", "MfcPipe", MfcPipeTextBox.Text.Trim());
            IniConfig.Write("Pipes", "ChamberPipe", ChamberPipeTextBox.Text.Trim());
            IniConfig.Write("Pipes", "SelfPipe", SelfPipeTextBox.Text.Trim());
            IniConfig.Write("Run", "Repeat", RepeatEdit.Text.Trim());
        }

        private void SaveTasks()
        {
            SaveConfig();
            TaskListCodec.Save(_tasks);
        }

        /// <summary>把当前管道名和任务列表导出到用户选择的 INI 文件。</summary>
        private void OnExportConfigClicked(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = "task_manager.ini",
                DefaultExt = ".ini",
                Filter = "INI files (*.ini)|*.ini|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                IniConfig.Write(dlg.FileName, "Pipes", "MfcPipe", MfcPipeTextBox.Text.Trim());
                IniConfig.Write(dlg.FileName, "Pipes", "ChamberPipe", ChamberPipeTextBox.Text.Trim());
                IniConfig.Write(dlg.FileName, "Pipes", "SelfPipe", SelfPipeTextBox.Text.Trim());
                IniConfig.Write(dlg.FileName, "Run", "Repeat", RepeatEdit.Text.Trim());
                TaskListCodec.Save(_tasks, dlg.FileName);
                UpdateStatus("Config exported to " + dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to export config: " + ex.Message, "Task Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>从用户选择的 INI 文件载入管道名和任务列表并应用。</summary>
        private void OnLoadConfigClicked(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                DefaultExt = ".ini",
                Filter = "INI files (*.ini)|*.ini|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                MfcPipeTextBox.Text = IniConfig.Read(dlg.FileName, "Pipes", "MfcPipe", DefaultMfcPipe);
                ChamberPipeTextBox.Text = IniConfig.Read(dlg.FileName, "Pipes", "ChamberPipe", DefaultChamberPipe);
                SelfPipeTextBox.Text = IniConfig.Read(dlg.FileName, "Pipes", "SelfPipe", DefaultSelfPipe);
                string repeat = IniConfig.Read(dlg.FileName, "Run", "Repeat", "1");
                int repeatCount;
                if (int.TryParse(repeat, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeatCount)
                    && repeatCount >= 1)
                {
                    RepeatEdit.Text = repeatCount.ToString(CultureInfo.InvariantCulture);
                }

                _tasks.Clear();
                _tasks.AddRange(TaskListCodec.Load(dlg.FileName));
                RefreshTaskList();

                // 持久化到默认 ini，重启后仍生效。
                SaveTasks();
                UpdateStatus("Config loaded from " + dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load config: " + ex.Message, "Task Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ================= Pipe client (MFC / chamber) =================

        /// <summary>
        /// Send one JSON request to a named pipe and return the response line.
        /// Throws on timeout / failure.
        /// </summary>
        private static string PipeRequest(string pipeName, string jsonRequest, int timeoutMs = PipeTimeoutMs)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                pipe.Connect(timeoutMs);

                StreamReader reader = null;
                StreamWriter writer = null;
                try
                {
                    reader = new StreamReader(pipe);
                    // Writer must be disposed BEFORE the reader: disposing the
                    // reader first closes the pipe, and the writer's final
                    // flush would then throw "Cannot access a closed pipe" and
                    // mask the real result.
                    writer = new StreamWriter(pipe) { AutoFlush = true };

                    writer.WriteLine(jsonRequest);
                    string resp = reader.ReadLine();
                    if (resp == null)
                    {
                        throw new IOException("Empty response from pipe '" + pipeName
                            + "' (is the control program running?)");
                    }
                    return resp;
                }
                finally
                {
                    // Dispose in a safe order and never let a dispose-time
                    // exception overwrite the real error.
                    try { if (writer != null) writer.Dispose(); } catch { }
                    try { if (reader != null) reader.Dispose(); } catch { }
                }
            }
        }

        /// <summary>Send a request and check the Ok flag. Returns the error string when not Ok.</summary>
        private static string PipeAction(string pipeName, string jsonRequest)
        {
            string resp = PipeRequest(pipeName, jsonRequest);
            var obj = Json.Deserialize<Dictionary<string, object>>(resp);
            if (obj != null && obj.ContainsKey("Ok") && obj["Ok"] is bool && (bool)obj["Ok"])
            {
                return null;
            }
            string err = "unknown response";
            if (obj != null && obj.ContainsKey("Error"))
            {
                err = obj["Error"] as string ?? "unknown error";
            }
            return err;
        }

        private static string JsonNum(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ================= Task runner =================

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            int repeat;
            if (!int.TryParse(RepeatEdit.Text.Trim(), out repeat) || repeat < 1)
            {
                MessageBox.Show("Repeat count must be a positive integer.", "Task Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveConfig(); // persist pipe names before running

            // Snapshot the pipe names on the UI thread so the background runner
            // never touches UI controls (which would throw a cross-thread error).
            _runMfcPipe = MfcPipeTextBox.Text.Trim();
            _runChamberPipe = ChamberPipeTextBox.Text.Trim();
            _chamberPipeName = _runChamberPipe;
            _runRepeatCount = repeat;

            _stopRequested = false;
            _running = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            // 任务执行期间禁止修改管道名称。
            MfcPipeTextBox.IsEnabled = false;
            ChamberPipeTextBox.IsEnabled = false;
            LoadConfigBtn.IsEnabled = false;
            ExportConfigBtn.IsEnabled = false;
            RepeatEdit.IsEnabled = false;
            UpdateStatus(repeat > 1 ? "Running... (repeat " + repeat + ")" : "Running...");

            _runnerThread = new Thread(RunTaskList) { IsBackground = true, Name = "TaskRunner" };
            _runnerThread.Start();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _stopRequested = true;
            UpdateStatus("Stopping...");
        }

        private void RunTaskList()
        {
            int repeat = _runRepeatCount;
            for (int pass = 1; pass <= repeat; pass++)
            {
                if (_stopRequested) break;

                for (int i = 0; i < _tasks.Count; i++)
                {
                    if (_stopRequested) break;

                    _currentIndex = i;
                    string prefix = repeat > 1 ? "Pass " + pass + "/" + repeat + " - " : "";
                    UpdateStatus(prefix + "Task " + (i + 1) + "/" + _tasks.Count + ": " + Describe(_tasks[i]));

                    string result = RunOneTask(_tasks[i]);
                    if (result != null)
                    {
                        // Log failure but continue with the next task.
                        UpdateStatus(prefix + "Task " + (i + 1) + " failed: " + result);
                        LogTaskFailure(i + 1, _tasks[i], result);
                    }

                    if (_stopRequested) break;
                }

                if (_stopRequested) break;
            }

            _currentIndex = -1;
            _running = false;

            Dispatcher.BeginInvoke((Action)(() =>
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                MfcPipeTextBox.IsEnabled = true;
                ChamberPipeTextBox.IsEnabled = true;
                LoadConfigBtn.IsEnabled = true;
                ExportConfigBtn.IsEnabled = true;
                RepeatEdit.IsEnabled = true;
                UpdateStatus(_stopRequested ? "Stopped." : "Finished.");
            }));
        }

        /// <summary>
        /// Run a single task. Returns an error string on failure, or null on success.
        /// Wait tasks time out and return an error (but execution continues).
        /// </summary>
        private string RunOneTask(TaskItem t)
        {
            try
            {
                switch (t.Type)
                {
                    case TaskType.WaitSeconds:
                    {
                        int totalMs = (int)(t.Seconds * 1000);
                        long startTick = Environment.TickCount;
                        int lastBeepSecond = -1;
                        WaitWithStop(totalMs, remainingMs =>
                        {
                            double elapsedSeconds = Math.Floor((Environment.TickCount - startTick) / 1000.0);
                            UpdateStatus("Task " + (_currentIndex + 1) + "/" + _tasks.Count
                                + ": waiting... " + F(elapsedSeconds) + "s");

                            // Beep once per second during the last N seconds.
                            if (t.BeepSeconds > 0)
                            {
                                double elapsedSec = (Environment.TickCount - startTick) / 1000.0;
                                if (elapsedSec >= t.Seconds - t.BeepSeconds)
                                {
                                    int sec = (int)Math.Floor(elapsedSec);
                                    if (sec != lastBeepSecond)
                                    {
                                        lastBeepSecond = sec;
                                        try
                                        {
                                            Beep(1000, 200); // 1 kHz, 200 ms
                                        }
                                        catch { }
                                    }
                                }
                            }
                        });
                        return null;
                    }

                    case TaskType.MfcMix:
                    {
                        _lastMfcTask = Describe(t);
                        string mfcPipe = _runMfcPipe;
                        string e1 = PipeAction(mfcPipe, "{\"cmd\":\"set_total_flow\",\"value\":" + JsonNum(t.TotalFlow) + "}");
                        if (e1 != null) return "set_total_flow: " + e1;
                        string e2 = PipeAction(mfcPipe, "{\"cmd\":\"set_source_conc\",\"value\":" + JsonNum(t.SourceConc) + "}");
                        if (e2 != null) return "set_source_conc: " + e2;
                        string e3 = PipeAction(mfcPipe, "{\"cmd\":\"set_target_conc\",\"value\":" + JsonNum(t.TargetConc) + "}");
                        if (e3 != null) return "set_target_conc: " + e3;
                        string e4 = PipeAction(mfcPipe, "{\"cmd\":\"set_gas_mixing\"}");
                        if (e4 != null) return "set_gas_mixing: " + e4;
                        return null;
                    }

                    case TaskType.MfcSetpoints:
                    {
                        _lastMfcTask = Describe(t);
                        string mfcPipe = _runMfcPipe;
                        string e1 = PipeAction(mfcPipe, "{\"cmd\":\"set_setpoint\",\"role\":\"target\",\"value\":" + JsonNum(t.TargetFlow) + "}");
                        if (e1 != null) return "set target setpoint: " + e1;
                        string e2 = PipeAction(mfcPipe, "{\"cmd\":\"set_setpoint\",\"role\":\"background\",\"value\":" + JsonNum(t.BackgroundFlow) + "}");
                        if (e2 != null) return "set background setpoint: " + e2;
                        return null;
                    }

                    case TaskType.ChamberSetTemp:
                        return SetChamberTargetAndVerify(true, t.Value);

                    case TaskType.ChamberSetHumidity:
                        return SetChamberTargetAndVerify(false, t.Value);

                    case TaskType.ChamberStart:
                    {
                        string ch = _runChamberPipe;
                        string e1 = PipeAction(ch, "{\"cmd\":\"start\"}");
                        return e1 != null ? "start: " + e1 : null;
                    }

                    case TaskType.ChamberStop:
                    {
                        string ch = _runChamberPipe;
                        string e1 = PipeAction(ch, "{\"cmd\":\"stop\"}");
                        return e1 != null ? "stop: " + e1 : null;
                    }

                    case TaskType.ChamberWaitTemp:
                        return WaitForCondition("temp", t, t.Value, t.Tolerance);

                    case TaskType.ChamberWaitHumidity:
                        return WaitForCondition("hum", t, t.Value, t.Tolerance);

                    case TaskType.ChamberStartTempRamp:
                    {
                        string ch = _runChamberPipe;
                        string e1 = PipeAction(ch, "{\"cmd\":\"start_temp_ramp\",\"value\":"
                            + JsonNum(t.Value) + ",\"end\":" + JsonNum(t.EndValue)
                            + ",\"duration\":" + JsonNum(t.DurationSeconds) + "}");
                        return e1 != null ? "start_temp_ramp: " + e1 : null;
                    }

                    case TaskType.ChamberStartHumRamp:
                    {
                        string ch = _runChamberPipe;
                        string e1 = PipeAction(ch, "{\"cmd\":\"start_hum_ramp\",\"value\":"
                            + JsonNum(t.Value) + ",\"end\":" + JsonNum(t.EndValue)
                            + ",\"duration\":" + JsonNum(t.DurationSeconds) + "}");
                        return e1 != null ? "start_hum_ramp: " + e1 : null;
                    }

                    case TaskType.ChamberSetFan:
                    {
                        string ch = _runChamberPipe;
                        string e1 = PipeAction(ch, "{\"cmd\":\"set_fan\",\"value\":"
                            + JsonNum(t.Value) + "}");
                        return e1 != null ? "set_fan: " + e1 : null;
                    }

                    default:
                        return "unknown task type";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Send set_temp / set_humidity, then verify via get_status that the chamber's target
        /// temperature/humidity (TgtTemp / TgtHum, read back from the device at 0.1 resolution)
        /// equals the set value. Resend the set command until it matches, or the task is stopped.
        /// </summary>
        private string SetChamberTargetAndVerify(bool isTemp, double value)
        {
            string ch = _runChamberPipe;
            string cmd = isTemp ? "set_temp" : "set_humidity";
            string key = isTemp ? "TgtTemp" : "TgtHum";

            while (!_stopRequested)
            {
                string err = PipeAction(ch, "{\"cmd\":\"" + cmd + "\",\"value\":" + JsonNum(value) + "}");
                if (err != null)
                {
                    return cmd + ": " + err;
                }

                WaitWithStop(WaitPollMs);
                if (_stopRequested)
                {
                    return null;
                }

                double tgt;
                string rerr = ReadChamberValue(ch, key, out tgt);
                if (rerr != null)
                {
                    return rerr;
                }

                if (Math.Abs(tgt - value) <= SetVerifyTolerance)
                {
                    return null;
                }

                UpdateStatus("Task " + (_currentIndex + 1) + "/" + _tasks.Count
                    + ": " + cmd + " retry (readback " + F(tgt) + " != " + F(value) + ")");
            }
            return null; // stopped
        }

        /// <summary>Wait for the chamber temperature/humidity condition, polling get_status.</summary>
        private string WaitForCondition(string what, TaskItem t, double target, double tol)
        {
            string ch = _runChamberPipe;
            string key = what == "temp" ? "CurTemp" : "CurHum";

            long timeoutMs = t.TimeoutSeconds > 0 ? (long)(t.TimeoutSeconds * 1000) : -1;
            long deadline = timeoutMs > 0 ? Environment.TickCount + timeoutMs : long.MaxValue;

            while (!_stopRequested)
            {
                if (Environment.TickCount > deadline)
                {
                    return "timeout waiting for chamber " + what;
                }

                double val;
                string err = ReadChamberValue(ch, key, out val);
                if (err != null)
                {
                    return err;
                }

                if (Math.Abs(val - target) <= tol)
                {
                    return null;
                }

                WaitWithStop(WaitPollMs);
            }
            return null; // stopped
        }

        /// <summary>Read one numeric status field (CurTemp/CurHum/TgtTemp/TgtHum) via get_status.</summary>
        private static string ReadChamberValue(string pipeName, string key, out double value)
        {
            value = 0;
            string resp = PipeRequest(pipeName, "{\"cmd\":\"get_status\"}");
            var obj = Json.Deserialize<Dictionary<string, object>>(resp);
            if (obj == null || !obj.ContainsKey("Ok") || !(bool)obj["Ok"])
            {
                string err = obj != null && obj.ContainsKey("Error") ? (obj["Error"] as string ?? "unknown") : "unknown error";
                return "get_status: " + err;
            }
            if (!obj.ContainsKey(key))
            {
                return "get_status: missing field " + key;
            }
            object raw = obj[key];
            if (!TryGetJsonNumber(raw, out value))
            {
                return "get_status: bad " + key + " value";
            }
            return null;
        }

        /// <summary>
        /// 把 JavaScriptSerializer 反序列化出的 JSON 数值转换为 double。
        /// 整数会变成 Int32，小数会变成 Decimal，必须全部兼容。
        /// </summary>
        private static bool TryGetJsonNumber(object raw, out double value)
        {
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            if (raw is long)
            {
                value = (long)raw;
                return true;
            }
            if (raw is double)
            {
                value = (double)raw;
                return true;
            }
            if (raw is decimal)
            {
                value = (double)(decimal)raw;
                return true;
            }
            if (raw is float)
            {
                value = (float)raw;
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>
        /// 查询一次 chamber 管道并读取当前温度与湿度。
        /// 返回 false 表示 chamber 不可达或没有状态。
        /// 温度/湿度各自可选：只支持温度的设备（如 yixing_chamber）没有 CurHum 时，
        /// 湿度保持 null，温度仍正常返回。
        /// </summary>
        private bool TryReadChamberStatus(out double? temp, out double? hum)
        {
            temp = null;
            hum = null;

            string pipeName = _chamberPipeName;
            if (string.IsNullOrEmpty(pipeName))
            {
                return false;
            }

            try
            {
                string resp = PipeRequest(pipeName, "{\"cmd\":\"get_status\"}", 1500);
                var obj = Json.Deserialize<Dictionary<string, object>>(resp);
                if (obj == null || !obj.ContainsKey("Ok") || !(bool)obj["Ok"])
                {
                    return false;
                }

                bool hasAny = false;
                if (obj.ContainsKey("CurTemp"))
                {
                    double v;
                    if (TryGetJsonNumber(obj["CurTemp"], out v))
                    {
                        temp = v;
                        hasAny = true;
                    }
                }
                if (obj.ContainsKey("CurHum"))
                {
                    double v;
                    if (TryGetJsonNumber(obj["CurHum"], out v))
                    {
                        hum = v;
                        hasAny = true;
                    }
                }
                return hasAny;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 每秒由后台定时器调用：查询 chamber 状态并缓存；
        /// 查询失败时缓存置为 null。
        /// </summary>
        private void OnChamberStatusTick(object state)
        {
            double? temp, hum;
            if (TryReadChamberStatus(out temp, out hum))
            {
                _chamberSnapshot = new ChamberSnapshot
                {
                    HasStatus = true,
                    Temp = temp,
                    Hum = hum
                };
            }
            else
            {
                _chamberSnapshot = null;
            }
        }

        /// <summary>
        /// Sleep in small increments so the Stop button can interrupt promptly.
        /// Informs <paramref name="onProgress"/> with the remaining milliseconds
        /// after each step (used to display the remaining seconds).
        /// </summary>
        private void WaitWithStop(int durationMs, Action<int> onProgress = null)
        {
            int remaining = durationMs;
            while (remaining > 0 && !_stopRequested)
            {
                int step = Math.Min(remaining, 200);
                Thread.Sleep(step);
                remaining -= step;
                if (onProgress != null)
                {
                    onProgress(remaining);
                }
            }
        }

        private void UpdateStatus(string status)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                StatusText.Text = status ?? "";
                _statusText = status ?? "";
            }));
        }

        /// <summary>
        /// Append a task-failure line to log.txt next to the executable.
        /// </summary>
        private static void LogTaskFailure(int taskNumber, TaskItem t, string error)
        {
            try
            {
                lock (LogLock)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  Task " + taskNumber + " failed: " + error
                        + "  |  " + Describe(t);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the task runner.
            }
        }

        // ================= Self named pipe server =================

        private void StartSelfPipeServer()
        {
            string pipeName = SelfPipeTextBox.Text.Trim();
            if (string.IsNullOrEmpty(pipeName)) return;

            _pipeRunning = true;
            _pipeAcceptThread = new Thread(() => SelfPipeAcceptLoop(pipeName))
            {
                IsBackground = true,
                Name = "TaskManagerPipeAccept"
            };
            _pipeAcceptThread.Start();
        }

        private void StopSelfPipeServer()
        {
            _pipeRunning = false;
            try
            {
                if (_pipeAcceptThread != null && _pipeAcceptThread.IsAlive)
                {
                    using (var wake = new NamedPipeClientStream(".", SelfPipeTextBox.Text.Trim(), PipeDirection.In))
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

        private void SelfPipeAcceptLoop(string pipeName)
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

                    var handler = new Thread(() => HandleSelfClient(server))
                    {
                        IsBackground = true,
                        Name = "TaskManagerPipeClient"
                    };
                    handler.Start();
                }
                catch
                {
                    if (server != null) server.Dispose();
                    if (!_pipeRunning) break;
                }
            }
        }

        private void HandleSelfClient(NamedPipeServerStream server)
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
                        if (line.Trim().Length == 0) continue;
                        string response = ProcessSelfRequest(line.Trim());
                        writer.WriteLine(response);
                    }
                }
            }
            catch
            {
                // Client disconnected.
            }
        }

        private string ProcessSelfRequest(string json)
        {
            var ser = new JavaScriptSerializer();
            try
            {
                var req = ser.Deserialize<SelfPipeRequest>(json);
                if (req == null || string.IsNullOrEmpty(req.Cmd))
                {
                    return ser.Serialize(new SelfPipeResponse { Ok = "invalid request" });
                }

                switch (req.Cmd.ToLowerInvariant())
                {
                    case "get_status":
                        return ser.Serialize(BuildSelfStatus());

                    case "start":
                        Dispatcher.BeginInvoke((Action)(() => StartButton_Click(StartButton, new RoutedEventArgs())));
                        return ser.Serialize(new SelfPipeResponse { Ok = "true" });

                    case "stop":
                        _stopRequested = true;
                        return ser.Serialize(new SelfPipeResponse { Ok = "true" });

                    default:
                        return ser.Serialize(new SelfPipeResponse { Ok = "unknown command: " + req.Cmd });
                }
            }
            catch (Exception ex)
            {
                return ser.Serialize(new SelfPipeResponse { Ok = "bad request: " + ex.Message });
            }
        }

        private SelfPipeResponse BuildSelfStatus()
        {
            // 直接使用每秒缓存的状态，避免 get_status 阻塞。
            ChamberSnapshot snap = _chamberSnapshot;

            return new SelfPipeResponse
            {
                Ok = "true",
                Running = _statusText,
                ChamberTemp = snap != null ? snap.Temp : null,
                ChamberHum = snap != null ? snap.Hum : null,
                LastMfcTask = _lastMfcTask
            };
        }

        /// <summary>每秒轮询 chamber 得到的不可变状态快照。</summary>
        private sealed class ChamberSnapshot
        {
            public bool HasStatus;
            public double? Temp;
            public double? Hum;
        }
    }

    // ================= Helper types =================

    public class TypeOption
    {
        public TaskType Type { get; }
        public string Label { get; }

        public TypeOption(TaskType type, string label)
        {
            Type = type;
            Label = label;
        }

        public override string ToString() => Label;
    }

    public class TaskListEntry
    {
        public TaskItem Task { get; }
        public string Description { get; }

        public TaskListEntry(TaskItem task, string description)
        {
            Task = task;
            Description = description;
        }

        public override string ToString() => Description;
    }

    public class SelfPipeRequest
    {
        public string Cmd { get; set; }
    }

    public class SelfPipeResponse
    {
        /// <summary>成功为 "true"；出错时存放错误信息。</summary>
        public string Ok { get; set; }
        public string Running { get; set; }

        /// <summary>Current chamber temperature (°C), null when no cached status.</summary>
        public double? ChamberTemp { get; set; }

        /// <summary>Current chamber humidity (%RH), null when no cached status.</summary>
        public double? ChamberHum { get; set; }

        /// <summary>Content of the last task that operated on the MFC.</summary>
        public string LastMfcTask { get; set; }
    }
}
