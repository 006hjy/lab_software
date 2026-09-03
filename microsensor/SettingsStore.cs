using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace microsensor
{
    /// <summary>
    /// 设置存储：INI 格式文件（兼容 Python QSettings IniFormat），
    /// 默认保存在程序目录 settings.ini。
    /// </summary>
    public class SettingsStore
    {
        private readonly string _filePath;
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public SettingsStore(string filePath = "settings.ini")
        {
            _filePath = filePath;
            Load();
        }

        private void Load()
        {
            _sections.Clear();
            if (!File.Exists(_filePath))
                return;

            string currentSection = "";
            foreach (var rawLine in File.ReadAllLines(_filePath, Encoding.UTF8))
            {
                var line = (rawLine ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                SetRaw(currentSection, key, value);
            }
        }

        private void SetRaw(string section, string key, string value)
        {
            if (!_sections.TryGetValue(section, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _sections[section] = dict;
            }
            dict[key] = value;
        }

        private void Save()
        {
            var sb = new StringBuilder();
            foreach (var section in _sections)
            {
                if (section.Value.Count == 0)
                    continue;
                sb.Append('[').Append(section.Key).AppendLine("]");
                foreach (var kv in section.Value)
                    sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
                sb.AppendLine();
            }
            File.WriteAllText(_filePath, sb.ToString(), Encoding.UTF8);
        }

        public string Get(string section, string key, string def = "")
        {
            if (_sections.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var v))
                return v;
            return def;
        }

        public void Set(string section, string key, string value)
        {
            SetRaw(section, key, value ?? "");
            Save();
        }

        // ---------- 序列化/反序列化辅助 ----------

        private static string BoolToStr(bool v) => v ? "true" : "false";
        private static bool StrToBool(string v) =>
            v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

        // ---------- 业务字段 ----------

        public string GetPort() => Get("serial", "port", "");
        public void SetPort(string value) => Set("serial", "port", value ?? "");

        public int GetBaud() => int.TryParse(Get("serial", "baud", "115200"), out var b) ? b : 115200;
        public void SetBaud(int value) => Set("serial", "baud", value.ToString(CultureInfo.InvariantCulture));

        public bool GetConvertMv() => StrToBool(Get("plot", "convert_mv", "false"));
        public void SetConvertMv(bool value) => Set("plot", "convert_mv", BoolToStr(value));

        public int GetDisplaySeconds() => int.TryParse(Get("plot", "display_seconds", "1800"), out var s) ? s : 1800;
        public void SetDisplaySeconds(int value) => Set("plot", "display_seconds", value.ToString(CultureInfo.InvariantCulture));

        public List<bool> GetChannelChecks()
        {
            var raw = Get("plot", "channels", "");
            var result = new List<bool>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var part in raw.Split(','))
                {
                    result.Add(StrToBool(part.Trim()));
                    if (result.Count >= 16)
                        break;
                }
            }
            while (result.Count < 16)
                result.Add(true);
            return result;
        }

        public void SetChannelChecks(IReadOnlyList<bool> values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 16 && i < values.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(values[i] ? "1" : "0");
            }
            Set("plot", "channels", sb.ToString());
        }

        public string GetThemeMode()
        {
            var v = Get("ui", "theme_mode", "light").ToLowerInvariant();
            return v == "dark" ? "dark" : "light";
        }

        public void SetThemeMode(string value)
        {
            Set("ui", "theme_mode", value == "dark" ? "dark" : "light");
        }

        public (bool Vref, bool Bias) GetDacChecks()
        {
            var vref = StrToBool(Get("dac", "vref", "false"));
            var bias = StrToBool(Get("dac", "bias", "false"));
            return (vref, bias);
        }

        public void SetDacChecks(bool vref, bool bias)
        {
            Set("dac", "vref", BoolToStr(vref));
            Set("dac", "bias", BoolToStr(bias));
        }

        public string GetTaskManagerPipe() => Get("taskmanager", "pipe", "task_manager");

        public void SetTaskManagerPipe(string value) => Set("taskmanager", "pipe", value ?? "");
    }
}
