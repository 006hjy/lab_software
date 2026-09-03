using System;
using System.Collections.Generic;
using System.Windows.Controls;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace smartsensor
{
    /// <summary>16 通道实时曲线图（OxyPlot），支持选择展示字段。</summary>
    public partial class PlotPanel : UserControl
    {
        private readonly PlotModel _model;
        private readonly LineSeries[] _series = new LineSeries[16];
        private readonly LinearAxis _leftAxis;
        private readonly LinearAxis _bottomAxis;

        public PlotPanel()
        {
            InitializeComponent();

            _model = new PlotModel { PlotAreaBorderThickness = new OxyThickness(0) };

            _bottomAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Time Offset (s)",
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(40, 0, 0, 0),
                Minimum = 0,
                Maximum = 1800
            };
            _model.Axes.Add(_bottomAxis);

            _leftAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Value",
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(40, 0, 0, 0)
            };
            _model.Axes.Add(_leftAxis);

            for (int i = 0; i < 16; i++)
            {
                var series = new LineSeries
                {
                    StrokeThickness = 1.5,
                    Color = OxyColor.Parse(Palette.ChannelColors[i]),
                    Title = $"CH{i + 1}"
                };
                _series[i] = series;
                _model.Series.Add(series);
            }

            PlotView.Model = _model;

            // 禁用鼠标中键框选缩放，滚轮缩放仅作用于时间轴。
            var controller = new PlotController();
            controller.Unbind(new OxyMouseDownGesture(OxyMouseButton.Middle));
            PlotView.Controller = controller;

            PlotView.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        /// <summary>
        /// 滚轮缩放：默认仅缩放 x 轴（时间轴）；按住 Shift 时仅缩放 y 轴。
        /// 均围绕鼠标位置。
        /// </summary>
        private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var position = e.GetPosition(PlotView);
            bool zoomY = (System.Windows.Input.Keyboard.Modifiers
                & System.Windows.Input.ModifierKeys.Shift) != 0;

            double factor = e.Delta > 0 ? 1.0 / 1.15 : 1.15;

            if (zoomY)
            {
                var axis = _leftAxis;
                double anchorY = axis.InverseTransform(position.Y);
                if (double.IsNaN(anchorY))
                {
                    anchorY = (axis.ActualMinimum + axis.ActualMaximum) / 2.0;
                }

                double min = axis.ActualMinimum;
                double max = axis.ActualMaximum;
                axis.Zoom(anchorY + (min - anchorY) * factor, anchorY + (max - anchorY) * factor);
            }
            else
            {
                double anchorX = _bottomAxis.InverseTransform(position.X);
                if (double.IsNaN(anchorX))
                {
                    anchorX = (_bottomAxis.ActualMinimum + _bottomAxis.ActualMaximum) / 2.0;
                }

                double min = _bottomAxis.ActualMinimum;
                double max = _bottomAxis.ActualMaximum;
                _bottomAxis.Zoom(anchorX + (min - anchorX) * factor, anchorX + (max - anchorX) * factor);
            }

            PlotView.InvalidatePlot(true);
            e.Handled = true;
        }

        /// <summary>重置时间轴范围 0~duration。</summary>
        public void ResetXAxis(int duration)
        {
            _bottomAxis.Minimum = 0;
            _bottomAxis.Maximum = duration;
            _bottomAxis.Reset();
            PlotView.InvalidatePlot(true);
        }

        /// <summary>重置 X 轴（0~duration）与 Y 轴（恢复自动缩放），清除用户缩放/平移状态。</summary>
        public void ResetAxes(int duration)
        {
            _bottomAxis.Minimum = 0;
            _bottomAxis.Maximum = duration;
            _bottomAxis.Reset();  // 清除 ViewMinimum/ViewMaximum
            _leftAxis.Reset();    // 清除 Y 轴 View 状态，恢复自动范围
            PlotView.InvalidatePlot(true);
        }

        /// <summary>应用深浅主题。</summary>
        public void ApplyTheme(string mode)
        {
            bool dark = mode == "dark";
            var background = dark ? OxyColor.Parse("#17181a") : OxyColors.White;
            var axisColor = dark ? OxyColor.Parse("#d8dde6") : OxyColor.Parse("#222222");
            var labelColor = dark ? OxyColor.Parse("#e8ecf1") : OxyColor.Parse("#1f2937");
            var gridColor = dark
                ? OxyColor.FromArgb(40, 0xD8, 0xDD, 0xE6)
                : OxyColor.FromArgb(40, 0, 0, 0);

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

        /// <summary>
        /// 渲染数据：x 为相对首条记录的秒偏移，y 为所选字段值；x 轴范围 0~duration。
        /// <paramref name="selectedChannels"/> 为 false 的通道不绘制。
        /// </summary>
        public void Render(List<SampleRow> rows, string field,
            IReadOnlyList<bool> selectedChannels, int duration = 1800)
        {
            for (int i = 0; i < 16; i++)
            {
                _series[i].Points.Clear();
            }

            _bottomAxis.Minimum = 0;
            _bottomAxis.Maximum = duration;

            if (rows != null && rows.Count > 0)
            {
                long anchor = rows[0].Ts;
                foreach (var row in rows)
                {
                    if (row.Channel < 1 || row.Channel > 16)
                    {
                        continue;
                    }
                    if (selectedChannels == null
                        || row.Channel > selectedChannels.Count
                        || !selectedChannels[row.Channel - 1])
                    {
                        continue;
                    }
                    double y = SelectField(row, field);
                    _series[row.Channel - 1].Points.Add(new DataPoint(row.Ts - anchor, y));
                }
            }

            PlotView.InvalidatePlot(true);
        }

        private static double SelectField(SampleRow row, string field)
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
    }
}
