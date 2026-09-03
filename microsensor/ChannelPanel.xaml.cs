using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace microsensor
{
    /// <summary>16 通道复选框面板：彩色标签 + 当前值，窄列布局。</summary>
    public partial class ChannelPanel : UserControl
    {
        private readonly List<CheckBox> _checks = new List<CheckBox>();
        private readonly List<string> _prefixes = new List<string>();

        /// <summary>任一复选框状态变化时触发。</summary>
        public event Action Changed;

        public ChannelPanel()
        {
            InitializeComponent();
        }

        /// <summary>初始化 16 个通道复选框（XAML 无参实例化后调用）。</summary>
        public void Initialize(IReadOnlyList<bool> states, IReadOnlyList<string> colors)
        {
            for (int i = 0; i < 16; i++)
            {
                var prefix = $"█ {i + 1:00}:";
                _prefixes.Add(prefix);

                var check = new CheckBox
                {
                    Content = prefix,
                    IsChecked = i < states.Count && states[i],
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                check.Checked += OnAnyToggled;
                check.Unchecked += OnAnyToggled;
                _checks.Add(check);
                Panel.Children.Add(check);
            }
        }

        private void OnAnyToggled(object sender, RoutedEventArgs e) => Changed?.Invoke();

        public List<bool> GetStates() => _checks.Select(c => c.IsChecked == true).ToList();

        public void SetStates(IReadOnlyList<bool> states)
        {
            for (int i = 0; i < _checks.Count && i < states.Count; i++)
                _checks[i].IsChecked = states[i];
        }

        /// <summary>更新复选框文本：prefix + 当前值（可选 mV 缩放）。</summary>
        public void SetValues(int[] values, bool convertMv)
        {
            for (int i = 0; i < _checks.Count; i++)
            {
                var text = _prefixes[i];
                if (values != null && values.Length >= 16)
                {
                    text = convertMv
                        ? $"{_prefixes[i]} {values[i] * Constants.MvScale:0.0}"
                        : $"{_prefixes[i]} {values[i]}";
                }
                _checks[i].Content = text;
            }
        }
    }
}
