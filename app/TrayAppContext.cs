using System;
using System.Drawing;
using System.Windows.Forms;

namespace KiroAutoContinue
{
    // Keeps running in the tray when the window is closed. Double-click or "Open" shows it; "Exit" quits.
    internal sealed class TrayAppContext : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly MainForm form;
        readonly Watcher watcher;
        readonly object ui;

        public TrayAppContext(Config cfg, Watcher watcher)
        {
            this.watcher = watcher;
            ui = Json.At(cfg.Words(cfg.UiLang()), "ui");

            form = new MainForm(cfg, watcher);
            form.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.Hide(); }
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(U("show"), null, (s, e) => ShowForm());
            menu.Items.Add(U("exit"), null, (s, e) => ExitApp());

            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = U("appTitle"),
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += (s, e) => ShowForm();
            watcher.OnNotice += OnNotice;

            form.Show();
        }

        string U(string k) { return Json.Str(Json.At(ui, k)); }

        void OnNotice(string tag, string text)
        {
            try { if (form.IsHandleCreated) form.BeginInvoke((Action)(() => tray.ShowBalloonTip(6000, tag, text, ToolTipIcon.Info))); }
            catch { }
        }

        void ShowForm()
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        void ExitApp()
        {
            watcher.OnNotice -= OnNotice;
            tray.Visible = false;
            tray.Dispose();
            watcher.StopAndWait();
            ExitThread();
        }
    }
}
