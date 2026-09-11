using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace smartsensor
{
    /// <summary>一行采样数据（一个通道一次采集）。</summary>
    public sealed class SampleRow
    {
        public long Ts;          // 秒时间戳（UTC）
        public int Channel;      // 1~16
        public double Reading;   // 大端 4 字节 ÷100
        public int Raw;
        public int RawRef;
        public int Temperature;
        public string Status;    // task_manager get_status 原始 JSON
    }

    /// <summary>
    /// 采样数据库：samples 表，每行 = 一个通道一秒的采样。
    /// </summary>
    public sealed class SampleDatabase : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private readonly object _lock = new object();

        public SampleDatabase(string dbPath)
        {
            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();
            InitSchema();
        }

        private void InitSchema()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts INTEGER NOT NULL,
                    channel INTEGER NOT NULL,
                    reading REAL,
                    raw INTEGER,
                    raw_ref INTEGER,
                    temperature INTEGER,
                    status TEXT)";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts)";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_samples_ts_ch ON samples(ts, channel)";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>插入一条采样（多线程调用，内部加锁串行化）。</summary>
        public void InsertSample(long ts, int channel, double reading, int raw, int rawRef,
            int temperature, string status)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO samples(ts, channel, reading, raw, raw_ref, temperature, status)
                                        VALUES (@ts, @channel, @reading, @raw, @raw_ref, @temperature, @status)";
                    cmd.Parameters.AddWithValue("@ts", ts);
                    cmd.Parameters.AddWithValue("@channel", channel);
                    cmd.Parameters.AddWithValue("@reading", reading);
                    cmd.Parameters.AddWithValue("@raw", raw);
                    cmd.Parameters.AddWithValue("@raw_ref", rawRef);
                    cmd.Parameters.AddWithValue("@temperature", temperature);
                    cmd.Parameters.AddWithValue("@status", (object)status ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>查询最近 n 秒（以最新时间戳为锚点）的全部通道数据，按 ts 升序。</summary>
        public List<SampleRow> FetchRecent(int seconds)
        {
            var result = new List<SampleRow>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT MAX(ts) FROM samples";
                    var raw = cmd.ExecuteScalar();
                    if (raw == null || Convert.IsDBNull(raw))
                    {
                        return result;
                    }
                    long latest = Convert.ToInt64(raw);
                    long start = latest - seconds;

                    cmd.CommandText = @"SELECT ts, channel, reading, raw, raw_ref, temperature
                                        FROM samples WHERE ts >= @start AND ts <= @latest ORDER BY ts ASC";
                    cmd.Parameters.AddWithValue("@start", start);
                    cmd.Parameters.AddWithValue("@latest", latest);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new SampleRow
                            {
                                Ts = reader.GetInt64(0),
                                Channel = reader.GetInt32(1),
                                Reading = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                                Raw = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                RawRef = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                                Temperature = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                            });
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>查询全部数据。</summary>
        public List<SampleRow> FetchAll()
        {
            var result = new List<SampleRow>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT ts, channel, reading, raw, raw_ref, temperature, status
                                        FROM samples ORDER BY ts ASC";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new SampleRow
                            {
                                Ts = reader.GetInt64(0),
                                Channel = reader.GetInt32(1),
                                Reading = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                                Raw = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                RawRef = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                                Temperature = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                                Status = reader.IsDBNull(6) ? null : reader.GetString(6)
                            });
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>清空全部数据。</summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM samples";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            try { _conn.Close(); } catch { }
            _conn.Dispose();
        }
    }
}
