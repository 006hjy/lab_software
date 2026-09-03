using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Threading;

namespace microsensor
{
    /// <summary>
    /// 采集控制器：100ms 定时器轮询系统秒数，秒变化时读取 16 通道电压并入库。
    /// 行为对齐 Python 版（每秒最多一次，靠秒数去重）。
    /// </summary>
    public class AcquisitionController
    {
        private readonly SerialClient _serial;
        private readonly SampleDatabase _db;
        private readonly Action<string> _onError;
        private readonly Func<string> _pipeNameProvider;
        private readonly DispatcherTimer _timer;
        private System.Threading.Timer _statusTimer;
        private volatile string _lastStatus;

        private bool _running;
        private long? _lastSecond;

        public AcquisitionController(SerialClient serial, SampleDatabase db, Action<string> onError, Func<string> pipeNameProvider)
        {
            _serial = serial;
            _db = db;
            _onError = onError;
            _pipeNameProvider = pipeNameProvider ?? (() => null);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += OnTick;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            _running = true;
            _lastSecond = null;
            _timer.Start();
            _statusTimer = new System.Threading.Timer(OnStatusTick, null, 0, 1000);
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
            _statusTimer?.Dispose();
            _statusTimer = null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_running)
                return;

            var nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_lastSecond.HasValue && _lastSecond.Value == nowSecond)
                return;
            _lastSecond = nowSecond;

            var values = _serial.ReadVoltages();
            if (values == null)
                return;

            try
            {
                _db.InsertSample(nowSecond, values, _lastStatus);
            }
            catch (Exception ex)
            {
                _onError?.Invoke($"Failed to insert sample: {ex.Message}");
            }
        }

        /// <summary>
        /// 每秒向 task_manager 管道发送 get_status，缓存原始 JSON 响应供入库使用。
        /// 运行在线程池线程上，不阻塞 UI。
        /// </summary>
        private void OnStatusTick(object state)
        {
            _lastStatus = QueryTaskManagerStatus(_pipeNameProvider());
        }

        /// <summary>向 task_manager 发送 get_status，返回原始 JSON 行；失败返回 null。</summary>
        private static string QueryTaskManagerStatus(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName))
                return null;

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
                        // writer 先释放（此时管道仍打开），再释放 reader；
                        // 释放异常吞掉，避免覆盖真实结果。
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
    }
}
