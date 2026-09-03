namespace microsensor
{
    /// <summary>全局常量：窗口尺寸、协议数据表、颜色等。</summary>
    public static class Constants
    {
        public const int DefaultWindowWidth = 900;
        public const int DefaultWindowHeight = 600;

        /// <summary>原始 ADC 值转 mV 的缩放系数。</summary>
        public const double MvScale = 2500.0 / 65535.0;

        /// <summary>图表线宽（像素）。</summary>
        public const int PlotLineWidth = 2;

        /// <summary>串口命令超时（秒）。</summary>
        public const double SerialTimeoutSeconds = 0.5;

        /// <summary>默认波特率。</summary>
        public const int DefaultBaud = 115200;

        /// <summary>SensorID 列表：ID 与名称。</summary>
        public static readonly (int Id, string Name)[] SensorIds =
        {
            (49, "CO"),
            (47, "H2S"),
            (102, "H2-20000"),
            (101, "H2-1000"),
            (76, "NH3"),
            (73, "SO2"),
            (79, "NO2"),
            (78, "Cl2"),
            (44, "O2"),
        };

        /// <summary>DAC 关闭时档位索引对应的电压值（0~6）。</summary>
        public static readonly double[] DacVoltages = { 0.5, 0.9, 1.0, 1.2, 1.5, 1.8, 2.1 };

        /// <summary>DAC 关闭时档位下拉框显示文本。</summary>
        public static readonly string[] DacLabels =
        {
            "0 : 0.5V",
            "1 : 0.9V",
            "2 : 1.0V",
            "3 : 1.2V",
            "4 : 1.5V",
            "5 : 1.8V",
            "6 : 2.1V",
        };

        /// <summary>电阻档位下拉框显示文本（索引 0~7）。</summary>
        public static readonly string[] ResistorLabels =
        {
            "0 : 14.7K",
            "1 : 22K",
            "2 : 33K",
            "3 : 78.7K",
            "4 : 140K",
            "5 : 150K",
            "6 : 300K",
            "7 : 499K",
        };

        /// <summary>16 个通道的线条/复选框颜色。</summary>
        public static readonly string[] ChannelColors =
        {
            "#e63946",
            "#f4a261",
            "#2a9d8f",
            "#1d3557",
            "#457b9d",
            "#8ac926",
            "#ff006e",
            "#8338ec",
            "#3a86ff",
            "#06d6a0",
            "#ef476f",
            "#118ab2",
            "#ff9f1c",
            "#2ec4b6",
            "#bc6c25",
            "#6a4c93",
        };
    }
}
