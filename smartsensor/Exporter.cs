using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace smartsensor
{
    /// <summary>CSV 导出工具：表头 ts,channel,reading,raw,raw_ref,temperature,status。</summary>
    public static class Exporter
    {
        public static void ExportRowsToCsv(string path, IReadOnlyList<SampleRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("ts,channel,reading,raw,raw_ref,temperature,status");
            sb.AppendLine();

            foreach (var row in rows)
            {
                sb.Append(row.Ts.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.Channel.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.Reading.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.Raw.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.RawRef.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.Temperature.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(EscapeCsv(row.Status));
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
