using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace start
{
    /// <summary>列表中的一项：一个可执行程序。</summary>
    public sealed class ExeItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public string FullPath { get; }
        public string Display { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        public ExeItem(string fullPath, string root)
        {
            FullPath = fullPath;
            Display = ToRelative(root, fullPath);
        }

        private static string ToRelative(string root, string path)
        {
            string r = Path.GetFullPath(root);
            if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                string rel = path.Substring(r.Length).TrimStart('\\', '/');
                if (rel.Length > 0)
                {
                    return rel;
                }
            }
            return path;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private bool _loading;
        private string _extraStatus = "";

        public MainWindow()
        {
            InitializeComponent();

            RootText.Text = "Scan root: " + AppDomain.CurrentDomain.BaseDirectory;
            ReloadList();
        }

        private void ReloadList()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            var previouslyChecked = new HashSet<string>(
                (ExeList.ItemsSource as IEnumerable<ExeItem> ?? Enumerable.Empty<ExeItem>())
                    .Where(i => i.IsChecked)
                    .Select(i => i.FullPath),
                StringComparer.OrdinalIgnoreCase);

            var items = FindExeFiles(root)
                .Select(p => new ExeItem(p, root))
                .OrderBy(i => i.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _loading = true;
            try
            {
                foreach (var item in items)
                {
                    item.IsChecked = previouslyChecked.Contains(item.FullPath);
                }
            }
            finally
            {
                _loading = false;
            }

            ExeList.ItemsSource = items;
            UpdateStatus();
        }

        private static List<string> FindExeFiles(string root)
        {
            var result = new List<string>();
            CollectExeFiles(root, result);
            return result;
        }

        private static void CollectExeFiles(string dir, List<string> result)
        {
            List<string> files;
            List<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.exe").ToList();
                subdirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Cannot access directory " + dir + ": " + ex.Message);
                return;
            }

            foreach (string f in files)
            {
                if (!IsExcluded(f))
                {
                    result.Add(f);
                }
            }
            foreach (string sub in subdirs)
            {
                CollectExeFiles(sub, result);
            }
        }

        private static bool IsExcluded(string path)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".vshost.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return string.Equals(path, CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }

        private static string CurrentExePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                return Assembly.GetEntryAssembly()?.Location ?? "";
            }
        }

        private void SetAllChecked(bool value)
        {
            if (!(ExeList.ItemsSource is IEnumerable<ExeItem> items))
            {
                return;
            }
            _loading = true;
            try
            {
                foreach (var item in items)
                {
                    item.IsChecked = value;
                }
            }
            finally
            {
                _loading = false;
            }
            UpdateStatus();
        }

        private void OnSelectAllClicked(object sender, RoutedEventArgs e) => SetAllChecked(true);

        private void OnSelectNoneClicked(object sender, RoutedEventArgs e) => SetAllChecked(false);

        private void OnRefreshClicked(object sender, RoutedEventArgs e) => ReloadList();

        private void OnItemToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                UpdateStatus();
            }
        }

        private void OnStartClicked(object sender, RoutedEventArgs e)
        {
            var targets = CheckedItems().ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "Please check the programs to launch first.", "Start",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var failures = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    string workDir = Path.GetDirectoryName(target.FullPath)
                        ?? AppDomain.CurrentDomain.BaseDirectory;
                    Process.Start(new ProcessStartInfo(target.FullPath)
                    {
                        WorkingDirectory = workDir,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    failures.Add(target.FullPath + ": " + ex.Message);
                }
            }

            if (failures.Count == 0)
            {
                SetExtraStatus("  | Launched " + targets.Count + " program(s)");
            }
            else
            {
                SetExtraStatus(string.Format("  | Launched {0}, failed {1}",
                    targets.Count - failures.Count, failures.Count));
                MessageBox.Show(this,
                    string.Format("Launched {0}, failed {1}:\n{2}",
                        targets.Count - failures.Count, failures.Count,
                        string.Join("\n", failures)),
                    "Start Completed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            var targets = CheckedItems().ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "Please check the programs to close first.", "Close",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(this,
                string.Format("Close all processes of these {0} program(s)?", targets.Count),
                "Confirm Close", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            int killed = 0;
            var failures = new List<string>();
            foreach (var target in targets)
            {
                string processName = Path.GetFileNameWithoutExtension(target.FullPath);
                try
                {
                    foreach (Process p in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            if (IsSameProcess(p, target.FullPath))
                            {
                                p.Kill();
                                p.WaitForExit(2000);
                                killed++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add(target.Display + ": " + ex.Message);
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(target.Display + ": " + ex.Message);
                }
            }

            if (failures.Count == 0)
            {
                SetExtraStatus("  | Closed " + killed + " process(es)");
            }
            else
            {
                SetExtraStatus(string.Format("  | Closed {0} process(es), failed {1}",
                    killed, failures.Count));
                MessageBox.Show(this,
                    string.Format("Closed {0} process(es), failed {1}:\n{2}",
                        killed, failures.Count, string.Join("\n", failures)),
                    "Close Completed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private IEnumerable<ExeItem> CheckedItems()
        {
            return (ExeList.ItemsSource as IEnumerable<ExeItem> ?? Enumerable.Empty<ExeItem>())
                .Where(i => i.IsChecked);
        }

        private static bool IsSameProcess(Process p, string exePath)
        {
            try
            {
                return string.Equals(p.MainModule.FileName, exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Cannot read the process path (possibly insufficient rights):
                // treat the process name as matching.
                return true;
            }
        }

        private void SetExtraStatus(string extra)
        {
            _extraStatus = extra;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            int selected = (ExeList.ItemsSource as IEnumerable<ExeItem> ?? Enumerable.Empty<ExeItem>())
                .Count(i => i.IsChecked);
            int total = (ExeList.ItemsSource as IEnumerable<ExeItem> ?? Enumerable.Empty<ExeItem>())
                .Count();
            StatusText.Text = string.Format("Found {0} executable(s), {1} selected{2}",
                total, selected, _extraStatus);
        }
    }
}
