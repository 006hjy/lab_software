using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace smartsensor
{
    /// <summary>
    /// 每秒向 task_manager 管道发送一次 get_status，缓存原始 JSON 字符串。
    /// 采集的 16 个通道共用这一份缓存写入 status 列，避免每通道各发一次。
    /// </summary>
    public sealed class TaskManagerStatusPoller : IDisposable
    {
        private readonly Func<string> _pipeNameProvider;
        private Timer _timer;
        private volatile string _status;

        /// <summary>最近一次 get_status 的原始 JSON；未获取到时为 null。</summary>
        public string Status => _status;

        public TaskManagerStatusPoller(Func<string> pipeNameProvider)
        {
            _pipeNameProvider = pipeNameProvider ?? (() => "task_manager");
        }

        public void Start()
        {
            Stop();
            _timer = new Timer(OnTick, null, 0, 1000);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void OnTick(object state)
        {
            _status = QueryTaskManagerStatus(_pipeNameProvider());
        }

        /// <summary>向 task_manager 发送 get_status，返回原始 JSON 行；失败返回 null。</summary>
        private static string QueryTaskManagerStatus(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName))
            {
                return null;
            }

            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(1000);

                    StreamReader reader = null;
                    StreamWriter writer = null;
                    try
                    {
                        reader = new StreamReader(pipe);
                        writer = new StreamWriter(pipe) { AutoFlush = true };
                        writer.WriteLine("{\"cmd\":\"get_status\"}");
                        return reader.ReadLine();
                    }
                    finally
                    {
                        // writer 先释放（管道仍打开），再释放 reader；释放异常吞掉。
                        try { if (writer != null) writer.Dispose(); } catch { }
                        try { if (reader != null) reader.Dispose(); } catch { }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
