using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace microsensor
{
    /// <summary>CSV 导出工具：表头 timestamp + ch1..ch16 + status，可选 mV 缩放。</summary>
    public static class Exporter
    {
        public static void ExportRowsToCsv(string path, IReadOnlyList<Tuple<long, int[], string>> rows, bool convertMv = false)
        {
            var sb = new StringBuilder();
            sb.Append("timestamp");
            for (int i = 1; i <= 16; i++)
                sb.Append(",ch").Append(i);
            sb.Append(",status");
            sb.AppendLine();

            foreach (var row in rows)
            {
                sb.Append(row.Item1.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < 16; i++)
                {
                    sb.Append(',');
                    if (convertMv)
                        sb.Append((row.Item2[i] * Constants.MvScale).ToString("0.000", CultureInfo.InvariantCulture));
                    else
                        sb.Append(row.Item2[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(',').Append(EscapeCsv(row.Item3));
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>status 为原始 JSON（含逗号/引号），按需加引号并转义双引号。</summary>
        private static string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return string.Empty;
            }

            bool needsQuote = field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!needsQuote)
            {
                return field;
            }

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
    }
}
