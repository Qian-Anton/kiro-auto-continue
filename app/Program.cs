using System;
using System.Threading;
using System.Windows.Forms;

namespace KiroAutoContinue
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mx = new Mutex(true, "KiroAutoContinue.SingleInstance", out created))
            {
                if (!created) return;   // already running
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Config cfg = Config.Load();
                var watcher = new Watcher(cfg);
                watcher.Start();
                using (var ctx = new TrayAppContext(cfg, watcher)) Application.Run(ctx);
                watcher.StopAndWait();
            }
        }
    }
}
