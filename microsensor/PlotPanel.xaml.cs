using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace microsensor
{
    /// <summary>16 通道实时曲线图（OxyPlot），支持 mV 缩放与深浅主题。</summary>
    public partial class PlotPanel : UserControl
    {
        private readonly PlotModel _model;
        private readonly LineSeries[] _series = new LineSeries[16];
        private readonly LinearAxis _leftAxis;
        private readonly LinearAxis _bottomAxis;

        public PlotPanel()
        {
            InitializeComponent();

            _model = new PlotModel
            {
                PlotAreaBorderThickness = new OxyThickness(0),
            };

            _leftAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Signal",
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(40, 0, 0, 0),
            };
            _bottomAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Time Offset (s)",
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(40, 0, 0, 0),
                Minimum = 0,
                Maximum = 1800,
            };
            _model.Axes.Add(_leftAxis);
            _model.Axes.Add(_bottomAxis);

            for (int i = 0; i < 16; i++)
            {
                var series = new LineSeries
                {
                    StrokeThickness = Constants.PlotLineWidth,
                    Color = OxyColor.Parse(Constants.ChannelColors[i]),
                    Title = $"CH{i + 1}",
                };
                _series[i] = series;
                _model.Series.Add(series);
            }

            PlotView.Model = _model;

            // 禁用鼠标中键框选缩放：OxyPlot 默认把中键按下绑定为
            // ZoomRectangle（框选显示范围），这里仅解绑中键手势，
            // 其余默认操作（左键平移、滚轮缩放等）保持不变。
            var controller = new PlotController();
            controller.Unbind(new OxyMouseDownGesture(OxyMouseButton.Middle));
            PlotView.Controller = controller;

            // 滚轮仅缩放 x 轴：隧道阶段拦截，阻止 OxyPlot 默认的双轴缩放
            PlotView.PreviewMouseWheel += OnPreviewMouseWheel;

            ApplyTheme("light");
        }

        /// <summary>滚轮缩放：默认仅缩放 x 轴（时间轴）；按住 Shift 时仅缩放 y 轴。均围绕鼠标位置。</summary>
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var position = e.GetPosition(PlotView);
            bool zoomY = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // 滚轮向上放大（范围缩小），向下缩小（范围扩大）
            double factor = e.Delta > 0 ? 1.0 / 1.15 : 1.15;

            if (zoomY)
            {
                var axis = _leftAxis;
                double anchorY = axis.InverseTransform(position.Y);
                if (double.IsNaN(anchorY))
                    anchorY = (axis.ActualMinimum + axis.ActualMaximum) / 2.0;

                double min = axis.ActualMinimum;
                double max = axis.ActualMaximum;
                double newMin = anchorY + (min - anchorY) * factor;
                double newMax = anchorY + (max - anchorY) * factor;

                axis.Zoom(newMin, newMax);
            }
            else
            {
                // 鼠标在绘图区的 x 数据坐标（作为缩放锚点）
                double anchorX = _bottomAxis.InverseTransform(position.X);
                if (double.IsNaN(anchorX))
                    anchorX = (_bottomAxis.ActualMinimum + _bottomAxis.ActualMaximum) / 2.0;

                double min = _bottomAxis.ActualMinimum;
                double max = _bottomAxis.ActualMaximum;
                double newMin = anchorX + (min - anchorX) * factor;
                double newMax = anchorX + (max - anchorX) * factor;

                _bottomAxis.Zoom(newMin, newMax);
            }

            PlotView.InvalidatePlot(true);

            // 标记已处理，阻止 OxyPlot 默认同时缩放另一轴
            e.Handled = true;
        }

        /// <summary>重置 x 轴为默认范围 0~duration：清除用户缩放/平移留下的 View 状态，使 Minimum/Maximum 重新生效。</summary>
        public void ResetXAxis(int duration)
        {
            _bottomAxis.Minimum = 0;
            _bottomAxis.Maximum = duration;
            _bottomAxis.Reset(); // 清除 ViewMinimum/ViewMaximum，恢复用 Minimum/Maximum 计算
            PlotView.InvalidatePlot(true);
        }

        /// <summary>将 y 轴范围设置为固定值 [min, max]：清除用户缩放/平移留下的 View 状态，使 Minimum/Maximum 重新生效。</summary>
        public void SetYAxisRange(double min, double max)
        {
            _leftAxis.Minimum = min;
            _leftAxis.Maximum = max;
            _leftAxis.Reset(); // 清除 ViewMinimum/ViewMaximum，恢复用 Minimum/Maximum 计算
            PlotView.InvalidatePlot(true);
        }

        /// <summary>渲染数据：x 为相对首条记录的秒偏移（0 起），y 为原始 ADC 值；x 轴范围 0~duration。
        /// mV 模式下仅改变 y 轴刻度标签显示（数据点不变，线条形状与位置保持不变）。</summary>
        public void Render(List<Tuple<long, int[]>> rows, IReadOnlyList<bool> selectedChannels, bool convertMv,
            int duration = 1800)
        {
            for (int i = 0; i < 16; i++)
                _series[i].Points.Clear();

            // x 轴范围：0 ~ duration
            _bottomAxis.Minimum = 0;
            _bottomAxis.Maximum = duration;

            // y 轴刻度标签：mV 模式下刻度值 ×MvScale 显示；数据点始终用原始 ADC 值，保证线条形状/位置不变
            _leftAxis.LabelFormatter = convertMv
                ? (double v) => (v * Constants.MvScale).ToString("0.0")
                : null; // null = OxyPlot 默认格式

            if (rows != null && rows.Count > 0)
            {
                var anchor = rows[0].Item1;
                for (int i = 0; i < 16; i++)
                {
                    if (selectedChannels == null || i >= selectedChannels.Count || !selectedChannels[i])
                        continue;
                    var points = _series[i].Points;
                    foreach (var row in rows)
                        points.Add(new DataPoint(row.Item1 - anchor, row.Item2[i]));
                }
            }

            PlotView.InvalidatePlot(true);
        }

        /// <summary>应用深浅主题。</summary>
        public void ApplyTheme(string mode)
        {
            bool dark = mode == "dark";
            var background = dark ? OxyColor.Parse("#17181a") : OxyColors.White;
            var axisColor = dark ? OxyColor.Parse("#d8dde6") : OxyColor.Parse("#222222");
            var labelColor = dark ? OxyColor.Parse("#e8ecf1") : OxyColor.Parse("#1f2937");
            var gridColor = dark ? OxyColor.FromArgb(40, 0xD8, 0xDD, 0xE6) : OxyColor.FromArgb(40, 0, 0, 0);

            _model.Background = background;
            _leftAxis.TextColor = axisColor;
            _leftAxis.TitleColor = labelColor;
            _leftAxis.MajorGridlineColor = gridColor;
            _leftAxis.TicklineColor = axisColor;
            _leftAxis.MajorTickSize = 0;
            _bottomAxis.TextColor = axisColor;
            _bottomAxis.TitleColor = labelColor;
            _bottomAxis.MajorGridlineColor = gridColor;
            _bottomAxis.TicklineColor = axisColor;
            _bottomAxis.MajorTickSize = 0;

            PlotView.InvalidatePlot(true);
        }
    }
}
