using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace lab_software
{
    /// <summary>
    /// Modbus-RTU serial communication with the chamber, rewritten from scratch.
    ///
    /// Model: one dedicated I/O thread performs the whole request/response
    /// cycle synchronously — send the read-status command, wait for the
    /// 109-byte frame (500 ms timeout), parse and publish it. Every serial
    /// access is guarded by a single lock, so writes issued from the UI or
    /// named-pipe threads are serialized behind the in-flight read.
    ///
    /// If no response arrives in time, the connection is released and the
    /// I/O thread automatically reconnects until it succeeds (or Stop is
    /// called). The class never touches the UI; consumers marshal events.
    /// </summary>
    public sealed class ChamberSerial : IDisposable
    {
        // ---- Modbus constants ----
        public const byte DeviceAddress = 0x01;
        public const byte FuncRead = 0x03;
        public const byte FuncWriteSingle = 0x06;
        public const ushort AddrSetTemp = 0x003C;
        public const ushort AddrSetHumidity = 0x003D;
        public const ushort AddrRunStop = 0x003F;
        public const int StatusFrameLength = 109;

        private const int BaudRate = 9600;
        private const int ReadResponseTimeoutMs = 500;
        private const int WriteTimeoutMs = 1000;
        private const int PollIntervalMs = 1000;
        private const int ReconnectDelayMs = 2000;
        private const int ProbeTimeoutMs = 500;

        private static readonly byte[] ProbeCommand =
            { 0x01, 0x03, 0x00, 0x0A, 0x00, 0x34, 0x64, 0x1F };

        // ---- State ----
        private readonly object _ioLock = new object();
        private SerialPort _port;
        private Thread _ioThread;
        private volatile bool _running;
        private string _portName;

        /// <summary>Raised on the I/O thread for every valid status frame.</summary>
        public event Action<ChamberStatus> StatusReceived;

        /// <summary>Raised on the I/O thread when the connection state changes.</summary>
        public event Action<ChamberConnectionState> StateChanged;

        /// <summary>True while the serial port is open and polling.</summary>
        public bool Connected { get; private set; }

        /// <summary>True while the I/O thread is retrying to (re)connect.</summary>
        public bool Reconnecting { get; private set; }

        /// <summary>Name of the port being used (null when stopped).</summary>
        public string PortName { get { return _portName; } }

        /// <summary>
        /// Open <paramref name="portName"/> in the background and start polling.
        /// The I/O thread reconnects automatically on failure.
        /// </summary>
        public void Start(string portName)
        {
            Stop();
            _portName = portName;
            _running = true;
            _ioThread = new Thread(IoLoop)
            {
                IsBackground = true,
                Name = "ChamberSerialIo"
            };
            _ioThread.Start();
        }

        /// <summary>Stop polling, close the port and cancel any reconnect.</summary>
        public void Stop()
        {
            _running = false;
            ClosePort();
            RaiseState(false, false, "Disconnected");
        }

        /// <summary>
        /// Write a single Modbus register. Returns false when the port is not
        /// open; write failures are left to the poll loop to detect.
        /// </summary>
        public bool WriteRegister(ushort register, short value)
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
                    byte[] frame = BuildWriteFrame(register, value);
                    port.Write(frame, 0, frame.Length);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Scan all serial ports and return those that answer the read-status
        /// command within 500 ms. Used by the UI "Refresh" button.
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

        // ================= Internal =================

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
                                port.Write(ProbeCommand, 0, ProbeCommand.Length);

                                byte[] data = ReceiveResponse(port, ReadResponseTimeoutMs);
                                if (data != null
                                    && data.Length >= StatusFrameLength
                                    && IsValidStatusFrame(data))
                                {
                                    // 取最后 109 字节作为状态帧（跳过前导杂字节）。
                                    var frame = new byte[StatusFrameLength];
                                    Array.Copy(data, data.Length - StatusFrameLength,
                                        frame, 0, StatusFrameLength);
                                    ChamberStatus status;
                                    if (TryParse(frame, out status))
                                    {
                                        StatusReceived?.Invoke(status);
                                    }
                                }
                                else
                                {
                                    lost = true; // no valid frame within 0.5 s
                                }
                            }
                        }
                        catch
                        {
                            lost = true; // port closed or hardware error
                        }
                    }

                    if (lost)
                    {
                        if (!_running)
                        {
                            return;
                        }
                        ClosePort();
                        RaiseState(false, true, "No response within 0.5 s — auto-reconnecting…");
                        break; // back to the outer loop → reopen / retry
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
                    port.ReadTimeout = ReadResponseTimeoutMs;
                    port.WriteTimeout = WriteTimeoutMs;
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
                // Close without holding the lock: if the I/O thread is blocked
                // in a read, this aborts it so the thread can exit promptly.
                try { port.Close(); } catch { }
                port.Dispose();
            }
        }

        private static bool TryParse(byte[] frame, out ChamberStatus status)
        {
            status = new ChamberStatus
            {
                CurTemp = (short)((frame[3] << 8) | frame[4]) / 10.0,
                TgtTemp = (short)((frame[5] << 8) | frame[6]) / 10.0,
                CurHum = (short)((frame[11] << 8) | frame[12]) / 10.0,
                TgtHum = (short)((frame[13] << 8) | frame[14]) / 10.0,
                RunState = frame[46]
            };
            return true;
        }

        private static byte[] BuildWriteFrame(ushort register, short value)
        {
            var frame = new byte[8];
            frame[0] = DeviceAddress;
            frame[1] = FuncWriteSingle;
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
            StateChanged?.Invoke(new ChamberConnectionState
            {
                Connected = connected,
                Reconnecting = reconnecting,
                Message = message
            });
        }

        /// <summary>
        /// Probe one port: receive the response within 0.5 s, require at least
        /// 109 bytes, then validate the last 109 bytes as a status frame
        /// (header 01 03 + CRC). Returns false on timeout or error.
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

                    byte[] data = ReceiveResponse(sp, ProbeTimeoutMs);
                    // 先验证长度 >= 109，再取最后 109 字节做校验。
                    return data != null
                        && data.Length >= StatusFrameLength
                        && IsValidStatusFrame(data);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Read the response within <paramref name="timeoutMs"/>, treating the
        /// transmission as complete when no new byte arrives for a short quiet
        /// interval. Returns the bytes received, or null when nothing arrived.
        /// </summary>
        private static byte[] ReceiveResponse(SerialPort sp, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            var data = new List<byte>(256);
            bool quiet;
            do
            {
                int remaining = deadline - Environment.TickCount;
                if (remaining <= 0)
                {
                    break;
                }
                sp.ReadTimeout = Math.Min(80, Math.Max(1, remaining));
                try
                {
                    data.Add((byte)sp.ReadByte());
                    quiet = false;
                }
                catch (TimeoutException)
                {
                    quiet = true;
                }
            } while (!quiet || data.Count == 0);

            return data.Count > 0 ? data.ToArray() : null;
        }

        /// <summary>
        /// Validate the last <see cref="StatusFrameLength"/> bytes as a status
        /// frame: header 01 03 and CRC16-Modbus.
        /// </summary>
        private static bool IsValidStatusFrame(byte[] data)
        {
            if (data.Length < StatusFrameLength)
            {
                return false;
            }

            int start = data.Length - StatusFrameLength;
            if (data[start] != 0x01 || data[start + 1] != 0x03)
            {
                return false;
            }

            ushort crc = Crc16Modbus(data, start, StatusFrameLength - 2);
            ushort received = (ushort)(data[start + 107] | (data[start + 108] << 8));
            return crc == received;
        }

        /// <summary>Extract the numeric part of a port name (e.g. "COM10" → 10).</summary>
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

    /// <summary>Decoded chamber status (one valid read-status frame).</summary>
    public sealed class ChamberStatus
    {
        public double CurTemp { get; set; }
        public double TgtTemp { get; set; }
        public double CurHum { get; set; }
        public double TgtHum { get; set; }
        public byte RunState { get; set; }
    }

    /// <summary>Connection state published through <see cref="ChamberSerial.StateChanged"/>.</summary>
    public sealed class ChamberConnectionState
    {
        public bool Connected { get; set; }
        public bool Reconnecting { get; set; }
        public string Message { get; set; }
    }
}
