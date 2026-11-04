using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace KiroAutoContinue
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var opt = Options.Parse(args);
            if (opt.Exit)
            {
                // ask the running copy to quit as from its tray menu; 0 if one was asked
                try { using (var ev = EventWaitHandle.OpenExisting(Shell.ExitEventName)) ev.Set(); return 0; }
                catch { return 1; }
            }
            Native.SetProcessDPIAware();
            if (opt.Preview) return Preview.Run(opt);

            bool created;
            using (var mx = new Mutex(true, "KiroAutoContinue.SingleInstance" + Config.InstanceSuffix(), out created))
            {
                if (!created)
                {
                    // already running (maybe only in the tray): bring its window up instead
                    try { using (var ev = EventWaitHandle.OpenExisting(Shell.ShowEventName)) ev.Set(); } catch { }
                    return 0;
                }
                Config cfg = Config.Load();
                var watcher = new Watcher(cfg);
                watcher.Start();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    // a slip in the window must not take the watcher down with it
                    watcher.LogUi("error: " + e.Exception.Message);
                    e.Handled = true;
                };
                var shell = new Shell(cfg, watcher, app, opt.Tray);
                app.Run();
                GC.KeepAlive(shell);
                watcher.StopAndWait();
            }
            return 0;
        }
    }

    // Command line. --tray: start hidden in the tray (what "start with Windows" uses). --exit: tell the running
    // copy to quit. For looking at the window without touching anything:
    // --preview [--sample] [--page=0|1|2] [--lang=zh|en] [--size=WxH] [--shot=out.png].
    // --texts=out.txt: no window; writes what the phone's "status" and the morning summary would say now.
    internal sealed class Options
    {
        public bool Tray, Exit, Preview, Sample;
        public int Page, Width, Height;
        public string Lang = "", Shot = "", Texts = "";

        public static Options Parse(string[] args)
        {
            var o = new Options();
            foreach (string raw in args ?? new string[0])
            {
                string a = (raw ?? "").Trim(), k = a, v = "";
                int eq = a.IndexOf('=');
                if (eq > 0) { k = a.Substring(0, eq); v = a.Substring(eq + 1).Trim('"'); }
                switch (k.ToLowerInvariant())
                {
                    case "--tray": o.Tray = true; break;
                    case "--exit": o.Exit = true; break;
                    case "--preview": o.Preview = true; break;
                    case "--sample": o.Sample = true; break;
                    case "--page": int.TryParse(v, out o.Page); break;
                    case "--lang": o.Lang = v; break;
                    case "--shot": o.Shot = v; o.Preview = true; break;
                    case "--texts": o.Texts = v; o.Preview = true; break;
                    case "--size":
                        var p = v.ToLowerInvariant().Split('x');
                        if (p.Length == 2) { int.TryParse(p[0], out o.Width); int.TryParse(p[1], out o.Height); }
                        break;
                }
            }
            return o;
        }
    }
}
