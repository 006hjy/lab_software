using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace yixing_chamber
{
    /// <summary>
    /// 益兴（Yixing）试验箱串口通讯，仅支持温度控制，无湿度功能。
    ///
    /// 帧格式：地址(01) + 功能码 + 地址码(2 BE) + 数值(2 BE) + CRC16-Modbus(2 LE)。
    /// 串口参数：9600、8 数据位、无校验、1 停止位。
    ///
    /// 模型与 chamber 一致：一个专用 I/O 线程每秒轮询——依次发送
    /// “读当前温度 / 读设定温度 / 读运行状态”，等待对应响应帧；
    /// 任一步超时或出错即判定断线，关闭连接并每 2 秒自动重连直到成功。
    /// 所有串口访问共用一把锁，UI/管道写命令串行排在轮询之后。
    /// </summary>
    public sealed class YixingSerial : IDisposable
    {
        // ---- 功能码 / 寄存器 ----
        public const byte DeviceAddress = 0x01;
        public const byte FuncReadHolding = 0x03;   // 读保持寄存器（温度）
        public const byte FuncWriteSingle = 0x06;   // 写单个寄存器（设定温度）
        public const byte FuncWriteCoil = 0x05;     // 写线圈（运行/停止）
        public const byte FuncReadCoil = 0x01;      // 读线圈（运行状态）

        public const ushort AddrReadTemp = 0x0218;  // 当前实际温度
        public const ushort AddrSetTemp = 0x074E;   // 设定温度
        public const ushort AddrRunStop = 0x04B0;   // 运行 / 停止线圈

        public const ushort RunValue = 0xFF00;
        public const ushort StopValue = 0x0000;

        private const int BaudRate = 9600;
        private const int ResponseTimeoutMs = 300;   // 单条命令等待响应的超时
        private const int PollIntervalMs = 1000;
        private const int ReconnectDelayMs = 2000;
        private const int ProbeTimeoutMs = 500;

        // 读运行状态：01 01 04 B0 00 01 FD 1D
        private static readonly byte[] ProbeCommand =
            { 0x01, 0x01, 0x04, 0xB0, 0x00, 0x01, 0xFD, 0x1D };

        // ---- 状态 ----
        private readonly object _ioLock = new object();
        private SerialPort _port;
        private Thread _ioThread;
        private volatile bool _running;
        private string _portName;

        /// <summary>I/O 线程收到一轮完整状态时触发（有效帧 + CRC 通过）。</summary>
        public event Action<YixingStatus> StatusReceived;

        /// <summary>I/O 线程连接状态变化时触发。</summary>
        public event Action<YixingConnectionState> StateChanged;

        /// <summary>串口是否已打开并轮询。</summary>
        public bool Connected { get; private set; }

        /// <summary>是否正在自动重连。</summary>
        public bool Reconnecting { get; private set; }

        public string PortName { get { return _portName; } }

        /// <summary>后台打开串口并开始轮询；失败时自动重连。</summary>
        public void Start(string portName)
        {
            Stop();
            _portName = portName;
            _running = true;
            _ioThread = new Thread(IoLoop)
            {
                IsBackground = true,
                Name = "YixingSerialIo"
            };
            _ioThread.Start();
        }

        /// <summary>停止轮询、关闭串口并取消重连。</summary>
        public void Stop()
        {
            _running = false;
            ClosePort();
            RaiseState(false, false, "Disconnected");
        }

        /// <summary>写入设定温度（×10，有符号 16 位）。</summary>
        public bool WriteSetTemp(double temp)
        {
            return WriteFrame(FuncWriteSingle, AddrSetTemp, (ushort)Math.Round(temp * 10.0));
        }

        /// <summary>开始运行。</summary>
        public bool StartChamber()
        {
            return WriteFrame(FuncWriteCoil, AddrRunStop, RunValue);
        }

        /// <summary>停止运行。</summary>
        public bool StopChamber()
        {
            return WriteFrame(FuncWriteCoil, AddrRunStop, StopValue);
        }

        /// <summary>
        /// 扫描所有串口，返回能正确应答“读运行状态”命令的端口。
        /// </summary>
        public static List<string> DetectPorts()
        {
            string[] allPorts = SerialPort.GetPortNames();
            var responding = new ConcurrentBag<string>();

            Parallel.ForEach(allPorts, portName =>
            {
                if (ProbePort(portName))
                {
                    responding.Add(portName);
                }
            });

            var result = new List<string>(responding);
            result.Sort((a, b) => PortNumber(a).CompareTo(PortNumber(b)));
            return result;
        }

        // ================= 内部 =================

        private bool WriteFrame(byte func, ushort register, ushort value)
        {
            lock (_ioLock)
            {
                SerialPort port = _port;
                if (port == null || !port.IsOpen)
                {
                    return false;
                }
                try
                {
                    byte[] frame = BuildWriteFrame(func, register, value);
                    port.Write(frame, 0, frame.Length);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private void IoLoop()
        {
            while (_running)
            {
                if (!OpenPort())
                {
                    if (!_running)
                    {
                        return;
                    }
                    RaiseState(false, true, "Connection failed — retrying in 2 s…");
                    SleepInterruptible(ReconnectDelayMs);
                    continue;
                }

                RaiseState(true, false, null);

                while (_running && Connected)
                {
                    int cycleStart = Environment.TickCount;
                    bool lost = false;

                    lock (_ioLock)
                    {
                        try
                        {
                            SerialPort port = _port;
                            if (port == null || !port.IsOpen)
                            {
                                lost = true;
                            }
                            else
                            {
                                // 读当前实际温度 → 01 03 02 XX XX CRC
                                byte[] tempFrame = Query(port, FuncReadHolding, 0x02, 7, AddrReadTemp);
                                // 读设定温度 → 01 03 02 XX XX CRC
                                byte[] setFrame = Query(port, FuncReadHolding, 0x02, 7, AddrSetTemp);
                                // 读运行状态 → 01 01 01 XX CRC
                                byte[] runFrame = Query(port, FuncReadCoil, 0x01, 6, AddrRunStop);

                                if (tempFrame != null && setFrame != null && runFrame != null)
                                {
                                    double curTemp = (short)((tempFrame[3] << 8) | tempFrame[4]) / 10.0;
                                    double tgtTemp = (short)((setFrame[3] << 8) | setFrame[4]) / 10.0;
                                    byte runState = runFrame[3];
                                    StatusReceived?.Invoke(new YixingStatus
                                    {
                                        CurTemp = curTemp,
                                        TgtTemp = tgtTemp,
                                        RunState = runState
                                    });
                                }
                                else
                                {
                                    lost = true; // 任一步超时/帧无效
                                }
                            }
                        }
                        catch
                        {
                            lost = true; // 端口关闭或硬件错误
                        }
                    }

                    if (lost)
                    {
                        if (!_running)
                        {
                            return;
                        }
                        ClosePort();
                        RaiseState(false, true, "No response within timeout — auto-reconnecting…");
                        break; // 回到外层循环重开
                    }

                    int elapsed = Environment.TickCount - cycleStart;
                    int sleepMs = PollIntervalMs - elapsed;
                    if (sleepMs > 0 && _running)
                    {
                        Thread.Sleep(sleepMs);
                    }
                }
            }
        }

        private bool OpenPort()
        {
            lock (_ioLock)
            {
                try
                {
                    var port = new SerialPort(_portName, BaudRate, Parity.None, 8, StopBits.One);
                    port.ReadTimeout = ResponseTimeoutMs;
                    port.WriteTimeout = 1000;
                    port.Open();
                    _port = port;
                    Connected = true;
                    return true;
                }
                catch
                {
                    ClosePort();
                    return false;
                }
            }
        }

        private void ClosePort()
        {
            SerialPort port = _port;
            _port = null;
            Connected = false;
            if (port != null)
            {
                // 不持锁关闭：若 I/O 线程阻塞在读取中，此操作会使其立即退出。
                try { port.Close(); } catch { }
                port.Dispose();
            }
        }

        /// <summary>
        /// 发送一条读命令并等待期望的响应帧（长度、帧头、CRC 均校验）。
        /// 噪声 / 写命令回显会被跳过。返回 null 表示超时或帧无效。
        /// </summary>
        private static byte[] Query(SerialPort port, byte func, byte byteCount, int totalLength, ushort register)
        {
            byte[] cmd = BuildReadFrame(func, register);
            port.Write(cmd, 0, cmd.Length);
            return ReadExpectedFrame(port, func, byteCount, totalLength, ResponseTimeoutMs);
        }

        private static byte[] ReadExpectedFrame(SerialPort port, byte func, byte byteCount, int totalLength, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            var frame = new byte[totalLength];
            int count = 0;

            while (count < totalLength)
            {
                int remaining = deadline - Environment.TickCount;
                if (remaining <= 0)
                {
                    return null;
                }

                int b;
                try
                {
                    port.ReadTimeout = Math.Max(1, remaining);
                    b = port.ReadByte();
                }
                catch (TimeoutException)
                {
                    return null;
                }

                if (count == 0)
                {
                    if (b != DeviceAddress)
                    {
                        continue; // 跳过噪声直到帧头
                    }
                }
                else if (count == 1)
                {
                    if (b != func)
                    {
                        count = 0; // 不是期望功能码 → 重新找帧头
                        continue;
                    }
                }
                else if (count == 2)
                {
                    if (b != byteCount)
                    {
                        count = 0; // 字节数不符 → 重新找帧头
                        continue;
                    }
                }

                frame[count++] = (byte)b;

                if (count == totalLength)
                {
                    ushort crc = Crc16Modbus(frame, 0, totalLength - 2);
                    ushort received = (ushort)(frame[totalLength - 2] | (frame[totalLength - 1] << 8));
                    if (crc == received)
                    {
                        return frame;
                    }
                    count = 0; // CRC 错误 → 继续在截止时间内扫描
                }
            }
            return null;
        }

        private static byte[] BuildReadFrame(byte func, ushort register)
        {
            var frame = new byte[8];
            frame[0] = DeviceAddress;
            frame[1] = func;
            frame[2] = (byte)(register >> 8);
            frame[3] = (byte)(register & 0xFF);
            frame[4] = 0x00; // 数量 1
            frame[5] = 0x01;

            ushort crc = Crc16Modbus(frame, 0, 6);
            frame[6] = (byte)(crc & 0xFF);
            frame[7] = (byte)(crc >> 8);
            return frame;
        }

        private static byte[] BuildWriteFrame(byte func, ushort register, ushort value)
        {
            var frame = new byte[8];
            frame[0] = DeviceAddress;
            frame[1] = func;
            frame[2] = (byte)(register >> 8);
            frame[3] = (byte)(register & 0xFF);
            frame[4] = (byte)(value >> 8);
            frame[5] = (byte)(value & 0xFF);

            ushort crc = Crc16Modbus(frame, 0, 6);
            frame[6] = (byte)(crc & 0xFF);
            frame[7] = (byte)(crc >> 8);
            return frame;
        }

        private static ushort Crc16Modbus(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0
                        ? (ushort)((crc >> 1) ^ 0xA001)
                        : (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        private void SleepInterruptible(int ms)
        {
            int remaining = ms;
            while (remaining > 0 && _running)
            {
                Thread.Sleep(Math.Min(100, remaining));
                remaining -= 100;
            }
        }

        private void RaiseState(bool connected, bool reconnecting, string message)
        {
            Connected = connected;
            Reconnecting = reconnecting;
            StateChanged?.Invoke(new YixingConnectionState
            {
                Connected = connected,
                Reconnecting = reconnecting,
                Message = message
            });
        }

        /// <summary>
        /// 探测单个端口：发送“读运行状态”命令（01 01 04 B0 00 01 FD 1D），
        /// 只有返回正确的状态响应帧（01 01 01 XX + CRC16-Modbus 校验通过）
        /// 才判定为 yixing chamber 端口。
        /// </summary>
        private static bool ProbePort(string portName)
        {
            try
            {
                using (var sp = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout = ProbeTimeoutMs;
                    sp.WriteTimeout = ProbeTimeoutMs;
                    sp.Open();

                    sp.Write(ProbeCommand, 0, ProbeCommand.Length);

                    // 状态响应帧：01 01 01 <state> CRC(2)，共 6 字节。
                    byte[] frame = ReadExpectedFrame(sp, FuncReadCoil, 0x01, 6, ProbeTimeoutMs);
                    return frame != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int PortNumber(string portName)
        {
            int idx = portName.IndexOf("COM", StringComparison.OrdinalIgnoreCase);
            int number;
            return idx >= 0 && int.TryParse(portName.Substring(idx + 3), out number)
                ? number
                : int.MaxValue;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>一次完整轮询解析出的状态（仅温度）。</summary>
    public sealed class YixingStatus
    {
        public double CurTemp { get; set; }
        public double TgtTemp { get; set; }
        public byte RunState { get; set; }
    }

    /// <summary>连接状态（由 <see cref="YixingSerial.StateChanged"/> 发布）。</summary>
    public sealed class YixingConnectionState
    {
        public bool Connected { get; set; }
        public bool Reconnecting { get; set; }
        public string Message { get; set; }
    }
}
