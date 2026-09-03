using System;
using System.Linq;
using System.Windows;

namespace microsensor
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args != null && e.Args.Contains("--selftest"))
            {
                var ok = SelfTest.Run();
                Shutdown(ok ? 0 : 1);
            }
        }
    }
}
