using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Windows.Threading;

namespace smartsensor
{
    /// <summary>
    /// 16 路串口采集：每秒向全部串口发送读取指令，事件驱动接收 23 字节数据帧，
    /// 解析后按通道写入数据库。status 使用每秒缓存一次的 task_manager 状态 JSON。
    /// </summary>
    public sealed class AcquisitionController : IDisposable
    {
        /// <summary>读取指令（16 字节）。</summary>
        private static readonly byte[] ReadCommand =
        {
            0x02, 0x31, 0x0c, 0x7b, 0x59, 0x09, 0x00, 0x01,
            0x30, 0x00, 0x00, 0x38, 0xd3, 0x1f, 0x7d, 0xf4
        };

        private const int FrameLength = 23;
        private const int MaxBuffer = 4096;

        private readonly SampleDatabase _db;
        private readonly Action<string> _onError;
        private readonly Func<string> _statusProvider;
        private readonly List<ChannelPort> _channels = new List<ChannelPort>();
        private readonly DispatcherTimer _timer;
        private readonly object _channelsLock = new object();

        private volatile bool _running;

        public bool IsRunning => _running;

        public AcquisitionController(SampleDatabase db, Action<string> onError, Func<string> statusProvider)
        {
            _db = db;
            _onError = onError;
            _statusProvider = statusProvider ?? (() => null);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
        }

        private sealed class ChannelPort
        {
            public int Channel;                 // 1~16
            public SerialPort Port;
            public readonly List<byte> Buffer = new List<byte>();
        }

        /// <summary>
        /// 打开全部串口（115200、8N1）。<paramref name="ports"/> 为按通道 1~16 顺序的端口名。
        /// 任一端口打开失败则关闭已打开的端口并返回 false。
        /// </summary>
        public bool OpenPorts(IReadOnlyList<string> ports)
        {
            ClosePorts();

            var opened = new List<ChannelPort>();
            try
            {
                for (int i = 0; i < ports.Count; i++)
                {
                    var sp = new SerialPort(ports[i], 115200, Parity.None, 8, StopBits.One);
                    sp.ReadTimeout = 500;
                    sp.WriteTimeout = 500;
                    var ch = new ChannelPort { Channel = i + 1, Port = sp };
                    sp.DataReceived += (s, e) => Channel_DataReceived(ch);
                    sp.Open();
                    opened.Add(ch);
                }
            }
            catch (Exception ex)
            {
                foreach (var ch in opened)
                {
                    try { ch.Port.DataReceived -= (s, e) => Channel_DataReceived(ch); } catch { }
                    try { ch.Port.Close(); } catch { }
                    ch.Port.Dispose();
                }
                throw new InvalidOperationException("Failed to open serial ports: " + ex.Message, ex);
            }

            lock (_channelsLock)
            {
                _channels.Clear();
                _channels.AddRange(opened);
            }
            return true;
        }

        public void Start()
        {
            if (_channels.Count == 0)
            {
                return;
            }
            _running = true;
            _timer.Start();
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
        }

        public void ClosePorts()
        {
            Stop();
            lock (_channelsLock)
            {
                foreach (var ch in _channels)
                {
                    try { ch.Port.DataReceived -= (s, e) => Channel_DataReceived(ch); } catch { }
                    try { ch.Port.Close(); } catch { }
                    ch.Port.Dispose();
                }
                _channels.Clear();
            }
        }

        /// <summary>
        /// 每秒触发：向全部串口发送读取指令。发送前丢弃每个端口残留的不完整帧
        /// （设备每指令回一帧 23 字节，残留 <23 字节说明上一帧已丢失，清掉防错位）。
        /// </summary>
        private void OnTick(object sender, EventArgs e)
        {
            if (!_running)
            {
                return;
            }

            List<ChannelPort> channels;
            lock (_channelsLock)
            {
                channels = new List<ChannelPort>(_channels);
            }

            foreach (var ch in channels)
            {
                lock (ch.Buffer)
                {
                    // 丢弃尾部残留的不完整帧（<23 字节），防止下一帧错位。
                    int leftover = ch.Buffer.Count % FrameLength;
                    if (leftover > 0)
                    {
                        ch.Buffer.RemoveRange(ch.Buffer.Count - leftover, leftover);
                    }
                }

                try
                {
                    ch.Port.Write(ReadCommand, 0, ReadCommand.Length);
                }
                catch
                {
                    // 串口异常：忽略，下一轮继续；如需断线检测可后续扩展。
                }
            }
        }

        private void Channel_DataReceived(ChannelPort ch)
        {
            try
            {
                int available = ch.Port.BytesToRead;
                if (available <= 0)
                {
                    return;
                }

                byte[] chunk = new byte[available];
                int read = ch.Port.Read(chunk, 0, available);
                if (read <= 0)
                {
                    return;
                }

                byte[] frame = null;
                lock (ch.Buffer)
                {
                    for (int i = 0; i < read; i++)
                    {
                        ch.Buffer.Add(chunk[i]);
                    }

                    if (ch.Buffer.Count >= FrameLength)
                    {
                        frame = ch.Buffer.GetRange(0, FrameLength).ToArray();
                        ch.Buffer.RemoveRange(0, FrameLength);
                    }

                    if (ch.Buffer.Count > MaxBuffer)
                    {
                        ch.Buffer.Clear();
                    }
                }

                if (frame != null)
                {
                    ParseAndInsert(ch.Channel, frame);
                }
            }
            catch
            {
                // 瞬时读错误忽略。
            }
        }

        /// <summary>
        /// 解析 23 字节数据帧（1-based 偏移）并入库：
        ///   10–13 → reading（大端 4 字节 ÷100）
        ///   15–16 → raw
        ///   17–18 → raw_ref
        ///   19    → temperature（>127 时 -256）
        /// </summary>
        private void ParseAndInsert(int channel, byte[] frame)
        {
            try
            {
                long readingRaw = ((long)frame[9] << 24) | ((long)frame[10] << 16)
                    | ((long)frame[11] << 8) | frame[12];
                double reading = readingRaw / 100.0;
                int raw = (frame[14] << 8) | frame[15];
                int rawRef = (frame[16] << 8) | frame[17];
                int temperature = frame[18];
                if (temperature > 127)
                {
                    temperature -= 256;
                }

                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _db.InsertSample(ts, channel, reading, raw, rawRef, temperature, _statusProvider());
            }
            catch (Exception ex)
            {
                _onError?.Invoke($"Insert failed for channel {channel}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            ClosePorts();
        }
    }
}
