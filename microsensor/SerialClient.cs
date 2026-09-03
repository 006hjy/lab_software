using System;
using System.Collections.Generic;
using System.IO.Ports;

namespace microsensor
{
    /// <summary>
    /// 串口客户端：封装 System.IO.Ports.SerialPort，提供协议交换方法。
    /// 行为对齐 Python 版：0.5s 超时，失败后重连并重试一次。
    /// </summary>
    public class SerialClient : IDisposable
    {
        private SerialPort _serial;
        private string _port = "";
        private int _baud = Constants.DefaultBaud;

        public string Port => _port;
        public int Baud => _baud;

        /// <summary>串口当前是否处于打开状态。</summary>
        public bool IsOpen => _serial != null && _serial.IsOpen;

        /// <summary>配置端口与波特率；若正在打开的串口参数变化则先关闭。</summary>
        public void Configure(string port, int baud)
        {
            port = (port ?? "").Trim();
            if (string.IsNullOrEmpty(port))
            {
                Close();
                _port = "";
                _baud = baud;
                return;
            }

            if (_serial != null && _serial.IsOpen && (port != _port || baud != _baud))
                Close();

            _port = port;
            _baud = baud;
        }

        /// <summary>确保串口已打开，返回是否可用。</summary>
        public bool EnsureOpen()
        {
            if (string.IsNullOrEmpty(_port))
                return false;
            if (_serial != null && _serial.IsOpen)
                return true;

            Logger.Log($"EnsureOpen: opening {_port}@{_baud}");
            var sp = new SerialPort(_port, _baud, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadBufferSize = 4096,
                WriteBufferSize = 4096,
            };
            try
            {
                sp.Open();
            }
            catch (Exception ex)
            {
                Logger.Log($"EnsureOpen: OPEN FAILED {_port}: {ex.GetType().Name}: {ex.Message}");
                sp.Dispose();
                throw new InvalidOperationException($"Failed to open serial port {_port}: {ex.Message}", ex);
            }
            Logger.Log($"EnsureOpen: opened {_port}");
            _serial = sp;
            return true;
        }

        /// <summary>关闭串口。</summary>
        public void Close()
        {
            if (_serial != null)
            {
                try
                {
                    if (_serial.IsOpen)
                        _serial.Close();
                }
                catch
                {
                    // 忽略关闭异常
                }
                finally
                {
                    _serial.Dispose();
                    _serial = null;
                }
            }
        }

        /// <summary>关闭并重开串口。</summary>
        public void Reconnect()
        {
            try
            {
                Close();
                EnsureOpen();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ERROR]:{e.Message}");
            }
        }

        /// <summary>发送命令并读取指定长度的响应，超时返回 null。</summary>
        private byte[] Exchange(byte[] tx, int expectedLen, double timeoutSec = Constants.SerialTimeoutSeconds)
        {
            bool opened;
            try
            {
                opened = EnsureOpen();
            }
            catch (Exception ex)
            {
                Logger.Log($"Exchange: EnsureOpen threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            if (!opened)
            {
                Logger.Log("Exchange: EnsureOpen returned false (no port)");
                return null;
            }

            Logger.Log($"Exchange: tx={BitConverter.ToString(tx).Replace("-", " ")} expect={expectedLen}B");
            var sp = _serial;
            try
            {
                sp.DiscardInBuffer();
                sp.DiscardOutBuffer();
                sp.WriteTimeout = (int)(timeoutSec * 1000);

                sp.Write(tx, 0, tx.Length);

                var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                var buf = new byte[expectedLen];
                int got = 0;
                while (got < expectedLen)
                {
                    var remainingMs = (int)((deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remainingMs <= 0)
                        break;
                    sp.ReadTimeout = remainingMs;
                    int n;
                    try
                    {
                        n = sp.Read(buf, got, expectedLen - got);
                    }
                    catch (TimeoutException)
                    {
                        Logger.Log($"Exchange: read timeout, got {got}/{expectedLen} bytes");
                        break;
                    }
                    if (n <= 0)
                        break;
                    got += n;
                }
                if (got >= expectedLen)
                {
                    Logger.Log($"Exchange: rx={BitConverter.ToString(buf).Replace("-", " ")}");
                    return buf;
                }
                Logger.Log($"Exchange: incomplete frame {got}/{expectedLen} bytes");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exchange: exception {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>发送命令并在缓冲区中寻找 ACK 帧，超时返回 null。</summary>
        private byte[] ExchangeAck(byte[] tx, double timeoutSec = Constants.SerialTimeoutSeconds,
            Func<byte[], bool> ackCheck = null)
        {
            if (ackCheck == null)
                ackCheck = Protocol.IsWriteConfigAck;

            if (!EnsureOpen())
                return null;

            var sp = _serial;
            try
            {
                sp.DiscardInBuffer();
                sp.DiscardOutBuffer();
                sp.WriteTimeout = (int)(timeoutSec * 1000);

                sp.Write(tx, 0, tx.Length);

                var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                var buf = new List<byte>();
                while (true)
                {
                    var remainingMs = (int)((deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remainingMs <= 0)
                        break;
                    sp.ReadTimeout = remainingMs;

                    var chunk = new byte[64];
                    int n;
                    try
                    {
                        n = sp.Read(chunk, 0, 64);
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
                    if (n <= 0)
                        break;

                    for (int i = 0; i < n; i++)
                        buf.Add(chunk[i]);

                    for (int i = 0; i <= buf.Count - 4; i++)
                    {
                        if (ackCheck(buf.GetRange(i, 4).ToArray()))
                            return buf.ToArray();
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取 16 通道电压，失败重连后返回 null（不重试，对齐 Python 行为）。</summary>
        public int[] ReadVoltages()
        {
            var frame = Exchange(Protocol.ReadVoltagesTx, 36);
            if (frame == null)
            {
                Reconnect();
                return null;
            }
            try
            {
                return Protocol.ParseVoltageResponse(frame);
            }
            catch (ProtocolException)
            {
                return null;
            }
        }

        /// <summary>读取配置 (vref_byte, bias_byte, resistor)，失败重连重试一次。</summary>
        public (int VrefByte, int BiasByte, int Resistor)? ReadConfig()
        {
            var frame = Exchange(Protocol.ReadConfigTx, 7);
            if (frame == null)
            {
                Logger.Log("ReadConfig: first attempt failed, reconnecting and retrying");
                Reconnect(); // 重试一次
                frame = Exchange(Protocol.ReadConfigTx, 7);
                if (frame == null)
                {
                    Logger.Log("ReadConfig: retry also failed");
                    return null;
                }
            }
            try
            {
                return Protocol.ParseConfigResponse(frame);
            }
            catch (ProtocolException)
            {
                return null;
            }
        }

        /// <summary>写入配置，等待 ACK，失败重连重试一次。</summary>
        public bool WriteConfig(int vrefByte, int biasByte, int resistor)
        {
            var tx = Protocol.BuildWriteConfigRequest(vrefByte, biasByte, resistor);
            var ack = ExchangeAck(tx);
            if (ack == null)
            {
                Reconnect(); // 重试一次
                ack = ExchangeAck(tx);
            }
            return ack != null;
        }

        /// <summary>读取 SensorID，失败重连重试一次。</summary>
        public int? ReadSensorId()
        {
            var frame = Exchange(Protocol.ReadSensorIdTx, 5);
            if (frame == null)
            {
                Reconnect(); // 重试一次
                frame = Exchange(Protocol.ReadSensorIdTx, 5);
                if (frame == null)
                    return null;
            }
            try
            {
                return Protocol.ParseSensorIdResponse(frame);
            }
            catch (ProtocolException)
            {
                return null;
            }
        }

        /// <summary>写入 SensorID，等待 ACK，失败重连重试一次。</summary>
        public bool WriteSensorId(int sensorId)
        {
            var tx = Protocol.BuildWriteSensorIdRequest(sensorId);
            var ack = ExchangeAck(tx, ackCheck: Protocol.IsWriteSensorIdAck);
            if (ack == null)
            {
                Reconnect(); // 重试一次
                ack = ExchangeAck(tx, ackCheck: Protocol.IsWriteSensorIdAck);
            }
            return ack != null;
        }

        /// <summary>读取 DAC 状态 (vref_dac, bias_dac)，失败重连重试一次。</summary>
        public (int VrefDac, int BiasDac)? ReadDacStatus()
        {
            var frame = Exchange(Protocol.ReadDacTx, 6);
            if (frame == null)
            {
                Reconnect(); // 重试一次
                frame = Exchange(Protocol.ReadDacTx, 6);
                if (frame == null)
                    return null;
            }
            try
            {
                return Protocol.ParseDacResponse(frame);
            }
            catch (ProtocolException)
            {
                return null;
            }
        }

        /// <summary>写入 DAC 状态，等待 ACK，失败重连重试一次。</summary>
        public bool WriteDacStatus(int vrefDac, int biasDac)
        {
            var tx = Protocol.BuildWriteDacRequest(vrefDac, biasDac);
            var ack = ExchangeAck(tx, ackCheck: Protocol.IsWriteDacAck);
            if (ack == null)
            {
                Reconnect(); // 重试一次
                ack = ExchangeAck(tx, ackCheck: Protocol.IsWriteDacAck);
            }
            return ack != null;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
