using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace task_manager
{
    /// <summary>
    /// Supported task types.
    /// </summary>
    public enum TaskType
    {
        /// <summary>Wait for a fixed number of seconds.</summary>
        WaitSeconds,

        /// <summary>Configure MFC gas mixing: total flow + source conc + target conc, then apply.</summary>
        MfcMix,

        /// <summary>Set both MFC devices' flow setpoints directly (target &amp; background).</summary>
        MfcSetpoints,

        /// <summary>Set the chamber target temperature (in °C).</summary>
        ChamberSetTemp,

        /// <summary>Set the chamber target humidity (in %RH).</summary>
        ChamberSetHumidity,

        /// <summary>Turn the chamber on.</summary>
        ChamberStart,

        /// <summary>Turn the chamber off.</summary>
        ChamberStop,

        /// <summary>Wait until the chamber temperature reaches target ± tolerance.</summary>
        ChamberWaitTemp,

        /// <summary>Wait until the chamber humidity reaches target ± tolerance.</summary>
        ChamberWaitHumidity,

        /// <summary>Start a linear temperature ramp (start → end over duration seconds).</summary>
        ChamberStartTempRamp,

        /// <summary>Start a linear humidity ramp (start → end over duration seconds).</summary>
        ChamberStartHumRamp,

        /// <summary>Set the chamber fan speed (in %, 30..100).</summary>
        ChamberSetFan
    }

    /// <summary>
    /// A single task in the task list. All fields are serialized to/from the
    /// INI file by <see cref="TaskListCodec"/>.
    /// </summary>
    public class TaskItem
    {
        public TaskType Type { get; set; }

        // WaitSeconds / waits
        public double Seconds { get; set; }

        /// <summary>For WaitSeconds: beep during the last N seconds (0 = no beep).</summary>
        public double BeepSeconds { get; set; }

        // MfcMix
        public double TotalFlow { get; set; }
        public double SourceConc { get; set; }
        public double TargetConc { get; set; }

        // MfcSetpoints
        public double TargetFlow { get; set; }
        public double BackgroundFlow { get; set; }

        // ChamberSetTemp / SetHumidity / WaitTemp / WaitHumidity
        public double Value { get; set; }          // target temp/humidity
        public double Tolerance { get; set; }      // ± tolerance for wait tasks
        public double TimeoutSeconds { get; set; } // timeout for wait tasks (0 = none)

        // ChamberStartTempRamp / ChamberStartHumRamp
        public double EndValue { get; set; }       // ramp end temp/humidity
        public double DurationSeconds { get; set; } // ramp duration in whole seconds
    }

    /// <summary>
    /// Serializes / deserializes the task list to and from the INI file.
    /// Each task occupies one line: "index|type|field=value;field=value;..."
    /// </summary>
    public static class TaskListCodec
    {
        private const string Section = "Tasks";

        public static void Save(List<TaskItem> tasks)
        {
            Save(tasks, IniConfig.FilePath);
        }

        /// <summary>把任务列表保存到指定 INI 文件。</summary>
        public static void Save(List<TaskItem> tasks, string path)
        {
            IniConfig.Write(path, Section, "Count",
                tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < tasks.Count; i++)
            {
                IniConfig.Write(path, Section, "Task" + i, Serialize(tasks[i]));
            }
        }

        public static List<TaskItem> Load()
        {
            return Load(IniConfig.FilePath);
        }

        /// <summary>从指定 INI 文件载入任务列表。</summary>
        public static List<TaskItem> Load(string path)
        {
            var result = new List<TaskItem>();
            string countStr = IniConfig.Read(path, Section, "Count", "0");
            int count;
            if (!int.TryParse(countStr, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out count) || count < 0 || count > 1000)
            {
                return result;
            }
            for (int i = 0; i < count; i++)
            {
                string line = IniConfig.Read(path, Section, "Task" + i, "");
                TaskItem t = Deserialize(line);
                if (t != null) result.Add(t);
            }
            return result;
        }

        private static string Serialize(TaskItem t)
        {
            var sb = new StringBuilder();
            sb.Append((int)t.Type);
            Append(sb, "seconds", t.Seconds);
            Append(sb, "beep_seconds", t.BeepSeconds);
            Append(sb, "total_flow", t.TotalFlow);
            Append(sb, "source_conc", t.SourceConc);
            Append(sb, "target_conc", t.TargetConc);
            Append(sb, "target_flow", t.TargetFlow);
            Append(sb, "background_flow", t.BackgroundFlow);
            Append(sb, "value", t.Value);
            Append(sb, "tolerance", t.Tolerance);
            Append(sb, "timeout", t.TimeoutSeconds);
            Append(sb, "end_value", t.EndValue);
            Append(sb, "duration", t.DurationSeconds);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string key, double value)
        {
            string s = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            sb.Append('|').Append(key).Append('=').Append(s);
        }

        private static TaskItem Deserialize(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string[] parts = line.Split('|');
            int type;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out type))
            {
                return null;
            }
            var t = new TaskItem { Type = (TaskType)type };
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = parts[i].Substring(0, eq);
                string val = parts[i].Substring(eq + 1);
                double d;
                if (!double.TryParse(val, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                {
                    continue;
                }
                switch (key)
                {
                    case "seconds": t.Seconds = d; break;
                    case "beep_seconds": t.BeepSeconds = d; break;
                    case "total_flow": t.TotalFlow = d; break;
                    case "source_conc": t.SourceConc = d; break;
                    case "target_conc": t.TargetConc = d; break;
                    case "target_flow": t.TargetFlow = d; break;
                    case "background_flow": t.BackgroundFlow = d; break;
                    case "value": t.Value = d; break;
                    case "tolerance": t.Tolerance = d; break;
                    case "timeout": t.TimeoutSeconds = d; break;
                    case "end_value": t.EndValue = d; break;
                    case "duration": t.DurationSeconds = d; break;
                }
            }
            return t;
        }
    }
}
