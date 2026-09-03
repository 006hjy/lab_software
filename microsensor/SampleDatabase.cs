using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace microsensor
{
    /// <summary>采样数据库：samples 表（ts + ch1..ch16 + status），提供插入/查询/清空。</summary>
    public class SampleDatabase : IDisposable
    {
        private readonly SQLiteConnection _conn;

        public SampleDatabase(string dbPath)
        {
            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();
            InitSchema();
        }

        private static string ChannelColumns => string.Join(",", Enumerable.Range(1, 16).Select(i => $"ch{i}"));
        private static string ChannelColumnDefs => string.Join(",", Enumerable.Range(1, 16).Select(i => $"ch{i} INTEGER NOT NULL"));

        private void InitSchema()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TABLE IF NOT EXISTS samples (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, {ChannelColumnDefs}, status TEXT)";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts)";
                cmd.ExecuteNonQuery();

                // 已有数据库迁移：老表没有 status 列时补充。
                if (!ColumnExists("samples", "status"))
                {
                    cmd.CommandText = "ALTER TABLE samples ADD COLUMN status TEXT";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private bool ColumnExists(string table, string column)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table})";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>插入一条 16 通道采样（status 为 task_manager get_status 的原始 JSON，可为 null）。</summary>
        public void InsertSample(long ts, int[] channels, string status)
        {
            if (channels == null || channels.Length != 16)
                throw new ArgumentException("Expected 16 channel values");

            var placeholders = string.Join(",", Enumerable.Repeat("?", 18));
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"INSERT INTO samples(ts, {ChannelColumns}, status) VALUES ({placeholders})";
                cmd.Parameters.AddWithValue("@p0", ts);
                for (int i = 0; i < 16; i++)
                    cmd.Parameters.AddWithValue($"@p{i + 1}", channels[i]);
                cmd.Parameters.AddWithValue("@p17", (object)status ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>查询最近 n 秒（以最新时间戳为锚点）的数据，按 ts 升序。</summary>
        public List<Tuple<long, int[]>> FetchRecentByLatest(int seconds)
        {
            var result = new List<Tuple<long, int[]>>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MAX(ts) FROM samples";
                var raw = cmd.ExecuteScalar();
                if (raw == null || Convert.IsDBNull(raw))
                    return result;
                long latest = Convert.ToInt64(raw);
                long start = latest - seconds;

                cmd.CommandText = $"SELECT ts, {ChannelColumns} FROM samples WHERE ts >= @start AND ts <= @latest ORDER BY ts ASC";
                cmd.Parameters.AddWithValue("@start", start);
                cmd.Parameters.AddWithValue("@latest", latest);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var values = new int[16];
                        for (int i = 0; i < 16; i++)
                            values[i] = reader.GetInt32(i + 1);
                        result.Add(Tuple.Create(reader.GetInt64(0), values));
                    }
                }
            }
            return result;
        }

        /// <summary>查询全部数据，按 ts 升序。</summary>
        public List<Tuple<long, int[]>> FetchAll()
        {
            var result = new List<Tuple<long, int[]>>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT ts, {ChannelColumns} FROM samples ORDER BY ts ASC";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var values = new int[16];
                        for (int i = 0; i < 16; i++)
                            values[i] = reader.GetInt32(i + 1);
                        result.Add(Tuple.Create(reader.GetInt64(0), values));
                    }
                }
            }
            return result;
        }

        /// <summary>清空全部数据。</summary>
        public void ClearAll()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM samples";
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            try
            {
                _conn.Close();
            }
            catch
            {
                // 忽略
            }
            _conn.Dispose();
        }
    }
}
