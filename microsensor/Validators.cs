using System;

namespace microsensor
{
    /// <summary>输入解析与校验工具。</summary>
    public static class Validators
    {
        /// <summary>解析电压文本，范围 0~2.50，返回两位小数。</summary>
        public static double ParseVoltageText(string text)
        {
            if (!double.TryParse((text ?? "").Trim(), out var value))
                throw new FormatException("Invalid voltage format");
            if (value < 0.0 || value > 2.5)
                throw new ArgumentOutOfRangeException("Voltage out of range [0, 2.5]");
            return Math.Round(value, 2);
        }

        /// <summary>解析正整数文本。</summary>
        public static int ParsePositiveIntText(string text)
        {
            if (!int.TryParse((text ?? "").Trim(), out var value) || value <= 0)
                throw new FormatException("Value must be a positive integer");
            return value;
        }
    }
}
