using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace smartsensor
{
    /// <summary>16 通道复选框面板：彩色标签 + 当前值显示。</summary>
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

        /// <summary>初始化 16 个通道复选框。</summary>
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
            {
                _checks[i].IsChecked = states[i];
            }
        }

        /// <summary>
        /// 更新每个通道的当前值显示（null = 该通道暂无数据）。
        /// reading 显示小数，raw / raw_ref / temperature 显示整数。
        /// </summary>
        public void SetValues(double?[] values, string field)
        {
            bool decimals = field == "reading";
            for (int i = 0; i < _checks.Count; i++)
            {
                string text = _prefixes[i];
                if (values != null && values[i].HasValue)
                {
                    double v = values[i].Value;
                    text = decimals
                        ? $"{_prefixes[i]} {v:0.0#}"
                        : $"{_prefixes[i]} {v:0}";
                }
                _checks[i].Content = text;
            }
        }
    }
}
