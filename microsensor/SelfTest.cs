using System;
using System.Collections.Generic;
using System.Linq;

namespace microsensor
{
    /// <summary>命令行自检：验证协议/数据库/设置存储，供 --selftest 启动参数调用。</summary>
    public static class SelfTest
    {
        public static bool Run()
        {
            var checks = new List<(string Name, bool Ok)>
            {
                ("Protocol.Voltage", TestVoltage()),
                ("Protocol.Config", TestConfig()),
                ("Protocol.SensorId", TestSensorId()),
                ("Protocol.Dac", TestDac()),
                ("SettingsStore", TestSettings()),
                ("SampleDatabase", TestDatabase()),
            };

            bool allOk = true;
            foreach (var (name, ok) in checks)
            {
                Console.WriteLine($"{(ok ? "[PASS]" : "[FAIL]")} {name}");
                allOk &= ok;
            }
            Console.WriteLine(allOk ? "SELF TEST: ALL OK" : "SELF TEST: FAILED");
            return allOk;
        }

        private static bool TestVoltage()
        {
            try
            {
                // Python 示例帧（校验和 31 为最后一字节）
                byte[] frame =
                {
                    0x02, 0x0D, 0x20, 0x03, 0x2F, 0x0A, 0x2F, 0x0A, 0x2F, 0x17,
                    0x2F, 0x13, 0x2F, 0x1A, 0x2F, 0x08, 0x2F, 0xFD, 0x2E, 0x0D,
                    0x2F, 0x07, 0x2F, 0x18, 0x2F, 0x27, 0x2F, 0x22, 0x2F, 0x06,
                    0x2F, 0x1F, 0x2F, 0x19, 0x2F, 0x31,
                };
                var values = Protocol.ParseVoltageResponse(frame);
                if (values.Length != 16)
                    return false;
                // 第 1 通道：0x2F03 小端 = 12035
                if (values[0] != 0x2F03)
                    return false;
                // 校验和错误帧应抛异常
                var bad = (byte[])frame.Clone();
                bad[35] = (byte)(bad[35] + 1);
                try
                {
                    Protocol.ParseVoltageResponse(bad);
                    return false;
                }
                catch (ProtocolException)
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TestConfig()
        {
            try
            {
                // 02 03 03 32 32 01 6D -> (50, 50, 1)
                var (vref, bias, res) = Protocol.ParseConfigResponse(new byte[] { 0x02, 0x03, 0x03, 0x32, 0x32, 0x01, 0x6D });
                if (vref != 50 || bias != 50 || res != 1)
                    return false;

                // DAC 关闭：档位索引写入
                var req = Protocol.BuildWriteConfigRequest(3, 1, 2);
                if (!req.SequenceEqual(new byte[] { 0x02, 0x03, 0x03, 0x03, 0x01, 0x02, 0x0E }))
                    return false;

                // DAC 开启：电压*100 写入
                var req2 = Protocol.BuildWriteConfigRequest(120, 90, 0);
                if (!req2.SequenceEqual(new byte[] { 0x02, 0x03, 0x03, 0x78, 0x5A, 0x00, 0xDA }))
                    return false;

                if (!Protocol.IsWriteConfigAck(new byte[] { 0x02, 0x03, 0x00, 0x05 }))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TestSensorId()
        {
            try
            {
                // 02 0E 01 31 42 -> 49 (CO)
                if (Protocol.ParseSensorIdResponse(new byte[] { 0x02, 0x0E, 0x01, 0x31, 0x42 }) != 49)
                    return false;

                var req = Protocol.BuildWriteSensorIdRequest(49);
                if (!req.SequenceEqual(new byte[] { 0x02, 0x0E, 0x01, 0x31, 0x42 }))
                    return false;

                if (!Protocol.IsWriteSensorIdAck(new byte[] { 0x02, 0x0E, 0x00, 0x10 }))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TestDac()
        {
            try
            {
                // 02 0F 02 00 00 13 -> (0, 0)
                var (v, b) = Protocol.ParseDacResponse(new byte[] { 0x02, 0x0F, 0x02, 0x00, 0x00, 0x13 });
                if (v != 0 || b != 0)
                    return false;

                var req = Protocol.BuildWriteDacRequest(1, 1);
                if (!req.SequenceEqual(new byte[] { 0x02, 0x0F, 0x02, 0x01, 0x01, 0x15 }))
                    return false;

                if (!Protocol.IsWriteDacAck(new byte[] { 0x02, 0x0F, 0x00, 0x11 }))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TestSettings()
        {
            try
            {
                var path = "selftest_settings.ini";
                var store = new SettingsStore(path);
                store.SetPort("COM7");
                store.SetBaud(115200);
                store.SetConvertMv(true);
                store.SetDisplaySeconds(3600);
                store.SetChannelChecks(new List<bool>
                {
                    true, false, true, false, true, false, true, false,
                    true, false, true, false, true, false, true, false,
                });
                store.SetDacChecks(true, false);
                store.SetThemeMode("dark");

                var store2 = new SettingsStore(path);
                bool ok = store2.GetPort() == "COM7"
                    && store2.GetBaud() == 115200
                    && store2.GetConvertMv()
                    && store2.GetDisplaySeconds() == 3600
                    && store2.GetChannelChecks().Count == 16
                    && store2.GetChannelChecks()[1] == false
                    && store2.GetDacChecks() == (true, false)
                    && store2.GetThemeMode() == "dark";

                System.IO.File.Delete(path);
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static bool TestDatabase()
        {
            try
            {
                var path = "selftest.db";
                using (var db = new SampleDatabase(path))
                {
                    var values = new int[16];
                    for (int i = 0; i < 16; i++)
                        values[i] = 1000 + i;

                    db.InsertSample(1000, values, null);
                    db.InsertSample(1001, values, null);

                    var recent = db.FetchRecentByLatest(1); // 锚点 1001 → start 1000
                    if (recent.Count != 2)
                        return false;
                    if (recent[0].Item1 != 1000 || recent[0].Item2[0] != 1000)
                        return false;

                    var all = db.FetchAll();
                    if (all.Count != 2)
                        return false;

                    db.ClearAll();
                    if (db.FetchAll().Count != 0)
                        return false;
                }
                System.IO.File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
