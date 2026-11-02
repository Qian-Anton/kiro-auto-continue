using System;
using System.Threading;
using System.Windows;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace KiroAutoContinue
{
    // The tray icon and the one window. Closing the window only hides it (the first time, a balloon says so);
    // clicking the icon shows it again; the menu also pauses and exits. Starting the exe a second time signals
    // this one to show its window. A session that needs you, or an until-done task that finished, pops a balloon.
    internal sealed class Shell
    {
        public static readonly string ShowEventName = "KiroAutoContinue.Show" + Config.InstanceSuffix();
        public static readonly string ExitEventName = "KiroAutoContinue.Exit" + Config.InstanceSuffix();

        readonly Config cfg;
        readonly Watcher watcher;
        readonly Application app;
        readonly MainWindow win;
        readonly WF.NotifyIcon tray;
        readonly WF.ToolStripMenuItem miShow, miPause, miExit;
        object ui;
        bool exiting;
        string tip = "";

        public Shell(Config cfg, Watcher watcher, Application app, bool startHidden)
        {
            this.cfg = cfg; this.watcher = watcher; this.app = app;
            ui = Json.At(cfg.Words(cfg.UiLang()), "ui");

            win = new MainWindow(cfg, watcher, false);
            win.Closing += (s, e) =>
            {
                if (exiting) return;
                e.Cancel = true;
                win.Hide();
                HintOnce();
            };
            win.SummaryChanged += SetTip;
            win.LanguageChanged += Words;

            var menu = new WF.ContextMenuStrip { ShowImageMargin = false };
            miShow = new WF.ToolStripMenuItem("", null, (s, e) => ShowWindow());
            miShow.Font = new SD.Font(menu.Font, SD.FontStyle.Bold);
            miPause = new WF.ToolStripMenuItem("", null, (s, e) => { watcher.Pause(!watcher.Paused); Words(); });
            miExit = new WF.ToolStripMenuItem("", null, (s, e) => Exit());
            menu.Items.AddRange(new WF.ToolStripItem[] { miShow, miPause, new WF.ToolStripSeparator(), miExit });
            menu.Opening += (s, e) => Words();

            tray = new WF.NotifyIcon { Icon = TrayIcon(), ContextMenuStrip = menu, Text = U("appTitle"), Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };
            tray.BalloonTipClicked += (s, e) => ShowWindow();
            watcher.OnActivity += OnActivity;
            Words();
            ListenForShow();
            if (!startHidden) ShowWindow();
        }

        string U(string k) { return Json.Str(Json.At(ui, k)); }

        void Words()
        {
            ui = Json.At(cfg.Words(cfg.UiLang()), "ui");
            miShow.Text = U("show");
            miPause.Text = watcher.Paused ? U("resume") : U("pause");
            miExit.Text = U("exit");
        }

        static SD.Icon TrayIcon()
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
                if (info != null) using (var s = info.Stream) return new SD.Icon(s, WF.SystemInformation.SmallIconSize);
            }
            catch { }
            return SD.SystemIcons.Application;
        }

        // The tooltip holds 63 characters at most.
        void SetTip(string status)
        {
            string t = U("appTitle") + "\n" + status;
            if (t.Length > 63) t = t.Substring(0, 63);
            if (t == tip) return;
            tip = t;
            try { tray.Text = t; } catch { }
        }

        void HintOnce()
        {
            if (cfg.Bool("trayHinted", false)) return;
            try { cfg.SaveUser(new System.Collections.Generic.Dictionary<string, object> { { "trayHinted", true } }); } catch { }
            tray.ShowBalloonTip(6000, U("appTitle"), U("trayHint"), WF.ToolTipIcon.Info);
        }

        void OnActivity(Activity a)
        {
            bool need = a.Kind.StartsWith("need.", StringComparison.Ordinal), done = a.Kind == "done";
            if (!need && !done) return;
            app.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (exiting) return;
                string text = win.ActivityText(a) + (a.Title.Length > 0 ? "\n" + a.Title : "");
                tray.ShowBalloonTip(8000, U(need ? "balloonNeed" : "balloonDone"), text, need ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);
            }));
        }

        public void ShowWindow()
        {
            if (!win.IsVisible) win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            // Activate alone only flashes the taskbar button when another program has the focus
            win.Topmost = true; win.Topmost = false;
            win.Focus();
        }

        // A second start of the exe sets one of these: show the window, or (with --exit) quit the way the tray
        // menu's Exit does (an update can close the running copy cleanly).
        void ListenForShow()
        {
            EventWaitHandle show, quit;
            try
            {
                show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                quit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            }
            catch { return; }
            var t = new Thread(() =>
            {
                while (true)
                {
                    int which;
                    try { which = WaitHandle.WaitAny(new WaitHandle[] { show, quit }); } catch { return; }
                    if (exiting) return;
                    try { app.Dispatcher.BeginInvoke(which == 1 ? (Action)Exit : ShowWindow); } catch { return; }
                    if (which == 1) return;
                }
            }) { IsBackground = true, Name = "kac-show" };
            t.Start();
        }

        void Exit()
        {
            exiting = true;
            watcher.OnActivity -= OnActivity;
            tray.Visible = false;
            tray.Dispose();
            win.Close();
            app.Shutdown();
        }
    }
}
