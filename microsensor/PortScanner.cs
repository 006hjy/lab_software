using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;

namespace microsensor
{
    /// <summary>
    /// 端口扫描器：后台线程并发探测端口（发送读配置命令，校验 7 字节响应），
    /// 返回有效端口列表。事件在后台线程触发，UI 需自行调度到界面线程。
    /// </summary>
    public class PortScanner
    {
        public event Action ScanStarted;
        public event Action<IReadOnlyList<string>> ScanFinished;

        private readonly object _lock = new object();
        private Task _task;

        public bool IsRunning
        {
            get { lock (_lock) return _task != null && !_task.IsCompleted; }
        }

        /// <summary>启动扫描；正在扫描时忽略新请求。</summary>
        public void Start(IReadOnlyList<string> ports, int baud, string skipPort = "",
            int timeoutMs = (int)(Constants.SerialTimeoutSeconds * 1000))
        {
            lock (_lock)
            {
                if (IsRunning)
                    return;
                _task = Task.Run(() => RunScan(new List<string>(ports), baud, skipPort, timeoutMs));
            }
        }

        private void RunScan(List<string> ports, int baud, string skipPort, int timeoutMs)
        {
            ScanStarted?.Invoke();

            var results = new ConcurrentDictionary<string, bool>();
            var toProbe = ports.Where(p => p != skipPort).ToList();

            if (toProbe.Count > 0)
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(8, toProbe.Count),
                };
                Parallel.ForEach(toProbe, options, port =>
                {
                    results[port] = ProbePort(port, baud, timeoutMs);
                });
            }

            var final = new List<string>();
            if (!string.IsNullOrEmpty(skipPort) && ports.Contains(skipPort))
                final.Add(skipPort);
            foreach (var p in ports)
            {
                if (p == skipPort)
                    continue;
                if (results.TryGetValue(p, out var ok) && ok)
                    final.Add(p);
            }

            ScanFinished?.Invoke(final);
        }

        /// <summary>严格探测：发送读配置命令，仅当收到合法 7 字节配置帧（头+校验和）才返回 true。</summary>
        public static bool ProbePort(string portName, int baud, int timeoutMs = 500)
        {
            using (var sp = new SerialPort(portName, baud, Parity.None, 8, StopBits.One))
            {
                sp.Handshake = Handshake.None;
                try
                {
                    sp.Open();
                    sp.DiscardInBuffer();
                    sp.DiscardOutBuffer();
                    sp.WriteTimeout = timeoutMs;

                    sp.Write(Protocol.ReadConfigTx, 0, Protocol.ReadConfigTx.Length);

                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    var buf = new List<byte>();
                    while (buf.Count < 7 && DateTime.UtcNow < deadline)
                    {
                        var remainingMs = (int)((deadline - DateTime.UtcNow).TotalMilliseconds);
                        if (remainingMs <= 0)
                            break;
                        sp.ReadTimeout = remainingMs;

                        var chunk = new byte[7 - buf.Count];
                        int n;
                        try
                        {
                            n = sp.Read(chunk, 0, chunk.Length);
                        }
                        catch (TimeoutException)
                        {
                            break;
                        }
                        if (n <= 0)
                            break;
                        for (int i = 0; i < n; i++)
                            buf.Add(chunk[i]);
                    }

                    if (buf.Count < 7)
                        return false;
                    try
                    {
                        Protocol.ParseConfigResponse(buf.Take(7).ToArray());
                        return true;
                    }
                    catch (ProtocolException)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
