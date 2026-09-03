using System;
using System.Linq;

namespace microsensor
{
    /// <summary>协议层异常。</summary>
    public class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>串口协议：命令构建、响应解析、校验和。</summary>
    public static class Protocol
    {
        // 读 16 通道电压：tx 02 0D 00 0F -> rx 36 字节
        public static readonly byte[] ReadVoltagesTx = { 0x02, 0x0D, 0x00, 0x0F };

        // 读配置：tx 02 03 00 05 -> rx 7 字节
        public static readonly byte[] ReadConfigTx = { 0x02, 0x03, 0x00, 0x05 };
        public static readonly byte[] WriteConfigPrefix = { 0x02, 0x03, 0x03 };
        public static readonly byte[] WriteConfigAck = { 0x02, 0x03, 0x00, 0x05 };

        // SensorID：读 tx 02 0E 00 10 -> rx 5 字节；写 tx 02 0E 01 <id> <cs>
        public static readonly byte[] ReadSensorIdTx = { 0x02, 0x0E, 0x00, 0x10 };
        public static readonly byte[] WriteSensorIdPrefix = { 0x02, 0x0E, 0x01 };
        public static readonly byte[] SensorIdAck = { 0x02, 0x0E, 0x00, 0x10 };

        // DAC 状态：读 tx 02 0F 00 11 -> rx 6 字节；写 tx 02 0F 02 <vref> <bias> <cs>
        public static readonly byte[] ReadDacTx = { 0x02, 0x0F, 0x00, 0x11 };
        public static readonly byte[] WriteDacPrefix = { 0x02, 0x0F, 0x02 };
        public static readonly byte[] DacAck = { 0x02, 0x0F, 0x00, 0x11 };

        /// <summary>校验和：所有字节求和 &amp; 0xFF。</summary>
        public static byte Checksum(byte[] data)
        {
            uint sum = 0;
            foreach (var b in data) sum += b;
            return (byte)(sum & 0xFF);
        }

        /// <summary>校验帧的最后一个字节是否为前序字节的校验和。</summary>
        public static bool VerifyChecksum(byte[] frame)
        {
            if (frame == null || frame.Length < 2) return false;
            return Checksum(frame.Take(frame.Length - 1).ToArray()) == frame[frame.Length - 1];
        }

        /// <summary>解析电压响应帧，返回 16 通道小端序整数。</summary>
        public static int[] ParseVoltageResponse(byte[] frame)
        {
            if (frame == null || frame.Length != 36)
                throw new ProtocolException($"Unexpected voltage frame length: {frame?.Length}");
            if (frame[0] != 0x02 || frame[1] != 0x0D || frame[2] != 0x20)
                throw new ProtocolException("Unexpected voltage frame header");
            if (!VerifyChecksum(frame))
                throw new ProtocolException("Voltage frame checksum mismatch");

            var values = new int[16];
            for (int i = 0; i < 16; i++)
                values[i] = frame[3 + i * 2] | (frame[4 + i * 2] << 8);
            return values;
        }

        /// <summary>
        /// 解析配置响应帧，返回原始字节 (vref_byte, bias_byte, resistor)。
        /// DAC 开启时字节=电压*100；DAC 关闭时字节=档位索引 0~6。
        /// </summary>
        public static (int VrefByte, int BiasByte, int Resistor) ParseConfigResponse(byte[] frame)
        {
            if (frame == null || frame.Length != 7)
                throw new ProtocolException($"Unexpected config frame length: {frame?.Length}");
            if (frame[0] != 0x02 || frame[1] != 0x03 || frame[2] != 0x03)
                throw new ProtocolException("Unexpected config frame header");
            if (!VerifyChecksum(frame))
                throw new ProtocolException("Config frame checksum mismatch");

            return (frame[3], frame[4], frame[5]);
        }

        /// <summary>
        /// 构建写配置请求帧。DAC 开启时传电压*100，DAC 关闭时传档位索引 0~6。
        /// </summary>
        public static byte[] BuildWriteConfigRequest(int vrefByte, int biasByte, int resistor)
        {
            if (vrefByte < 0 || vrefByte > 250)
                throw new ProtocolException("Vref byte out of range");
            if (biasByte < 0 || biasByte > 250)
                throw new ProtocolException("Bias byte out of range");
            if (resistor < 0 || resistor > 7)
                throw new ProtocolException("Resistor out of range");

            var payload = new byte[]
            {
                0x02, 0x03, 0x03, (byte)vrefByte, (byte)biasByte, (byte)resistor,
            };
            var frame = new byte[7];
            Array.Copy(payload, frame, 6);
            frame[6] = Checksum(payload);
            return frame;
        }

        /// <summary>判断是否为写配置 ACK 帧（02 03 00 05）。</summary>
        public static bool IsWriteConfigAck(byte[] frame)
        {
            return frame != null && frame.Length >= 4
                && frame[0] == WriteConfigAck[0] && frame[1] == WriteConfigAck[1]
                && frame[2] == WriteConfigAck[2] && frame[3] == WriteConfigAck[3];
        }

        /// <summary>解析 SensorID 响应帧（02 0E 01 &lt;id&gt; &lt;cs&gt;），返回 ID。</summary>
        public static int ParseSensorIdResponse(byte[] frame)
        {
            if (frame == null || frame.Length != 5)
                throw new ProtocolException($"Unexpected sensor id frame length: {frame?.Length}");
            if (frame[0] != 0x02 || frame[1] != 0x0E || frame[2] != 0x01)
                throw new ProtocolException("Unexpected sensor id frame header");
            if (!VerifyChecksum(frame))
                throw new ProtocolException("Sensor id frame checksum mismatch");
            return frame[3];
        }

        /// <summary>构建写 SensorID 请求帧（02 0E 01 &lt;id&gt; &lt;cs&gt;）。</summary>
        public static byte[] BuildWriteSensorIdRequest(int sensorId)
        {
            var payload = new byte[] { 0x02, 0x0E, 0x01, (byte)sensorId };
            var frame = new byte[5];
            Array.Copy(payload, frame, 4);
            frame[4] = Checksum(payload);
            return frame;
        }

        /// <summary>判断是否为写 SensorID ACK 帧（02 0E 00 10）。</summary>
        public static bool IsWriteSensorIdAck(byte[] frame)
        {
            return frame != null && frame.Length >= 4
                && frame[0] == SensorIdAck[0] && frame[1] == SensorIdAck[1]
                && frame[2] == SensorIdAck[2] && frame[3] == SensorIdAck[3];
        }

        /// <summary>解析 DAC 状态响应帧（02 0F 02 &lt;vref&gt; &lt;bias&gt; &lt;cs&gt;）。</summary>
        public static (int VrefDac, int BiasDac) ParseDacResponse(byte[] frame)
        {
            if (frame == null || frame.Length != 6)
                throw new ProtocolException($"Unexpected dac frame length: {frame?.Length}");
            if (frame[0] != 0x02 || frame[1] != 0x0F || frame[2] != 0x02)
                throw new ProtocolException("Unexpected dac frame header");
            if (!VerifyChecksum(frame))
                throw new ProtocolException("Dac frame checksum mismatch");
            return (frame[3], frame[4]);
        }

        /// <summary>构建写 DAC 状态请求帧（02 0F 02 &lt;vref&gt; &lt;bias&gt; &lt;cs&gt;）。</summary>
        public static byte[] BuildWriteDacRequest(int vrefDac, int biasDac)
        {
            if (vrefDac != 0 && vrefDac != 1)
                throw new ProtocolException("Vref dac status must be 0 or 1");
            if (biasDac != 0 && biasDac != 1)
                throw new ProtocolException("Bias dac status must be 0 or 1");

            var payload = new byte[] { 0x02, 0x0F, 0x02, (byte)vrefDac, (byte)biasDac };
            var frame = new byte[6];
            Array.Copy(payload, frame, 5);
            frame[5] = Checksum(payload);
            return frame;
        }

        /// <summary>判断是否为写 DAC ACK 帧（02 0F 00 11）。</summary>
        public static bool IsWriteDacAck(byte[] frame)
        {
            return frame != null && frame.Length >= 4
                && frame[0] == DacAck[0] && frame[1] == DacAck[1]
                && frame[2] == DacAck[2] && frame[3] == DacAck[3];
        }
    }
}
