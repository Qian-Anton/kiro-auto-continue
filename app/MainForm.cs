using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KiroAutoContinue
{
    // The window: three tabs (sessions, phone, settings). All Chinese text comes from words.<lang>.ui in the
    // config, so this file stays ASCII. Reads the watcher's snapshot on a timer; posts actions to the watcher.
    internal sealed class MainForm : Form
    {
        readonly Config cfg;
        readonly Watcher watcher;
        bool loading;
        object ui;

        TabControl tabs;
        TabPage pSess, pPhone, pSet;

        // sessions
        Label sessStatus; Button pauseBtn; ListView list; ColumnHeader colNum, colName, colState;
        Button stopBtn, contBtn; TextBox logBox;
        // phone
        Label notifyTitle, notifyHelp, remoteTitle, remoteHelp, testState, remoteState, pairLabel, botIdLbl, secretLbl;
        TextBox webhook, botId, secret; Button testBtn, connectBtn;
        CheckBox evNeedsYou, evDone, evMorning;
        // settings
        CheckBox startChk, awakeChk; Label idleLbl, idleSuffix, langLbl, localNote; NumericUpDown idleNum; ComboBox langCombo;

        Timer timer;

        public MainForm(Config cfg, Watcher watcher)
        {
            this.cfg = cfg; this.watcher = watcher;
            Build();
            Localize();
            LoadSettings();
            foreach (string l in watcher.Recent) AppendLog(l);
            watcher.OnLog += OnWatcherLog;
            timer = new Timer { Interval = 2000 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();
            int ti;
            if (int.TryParse(Environment.GetEnvironmentVariable("KAC_TAB"), out ti) && ti >= 0 && ti < tabs.TabPages.Count)
                tabs.SelectedIndex = ti;
        }

        string U(string key) { return Json.Str(Json.At(ui, key)); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            watcher.OnLog -= OnWatcherLog;
            base.OnFormClosed(e);
        }

        const int Pad = 16;
        const int Wide = 868;   // content width inside the 900 client

        void Build()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 620);
            MinimumSize = new Size(720, 520);

            tabs = new TabControl { Dock = DockStyle.Fill };
            pSess = new TabPage(); pPhone = new TabPage(); pSet = new TabPage();
            tabs.TabPages.AddRange(new[] { pSess, pPhone, pSet });
            Controls.Add(tabs);

            BuildSessions();
            BuildPhone();
            BuildSettings();
        }

        void BuildSessions()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(Pad, 10, Pad, 4) };
            sessStatus = new Label { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
            pauseBtn = new Button { Dock = DockStyle.Right, Width = 120 };
            pauseBtn.Click += (s, e) => { watcher.Pause(!watcher.Paused); Tick(); };
            top.Controls.Add(sessStatus);
            top.Controls.Add(pauseBtn);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 200, Padding = new Padding(Pad, 0, Pad, 12) };
            var btnRow = new Panel { Dock = DockStyle.Top, Height = 42 };
            contBtn = new Button { Location = new Point(0, 6), Size = new Size(110, 30), Enabled = false };
            stopBtn = new Button { Location = new Point(120, 6), Size = new Size(90, 30), Enabled = false };
            contBtn.Click += (s, e) => { string id = SelectedId(); if (id != null) watcher.RequestContinue(id); };
            stopBtn.Click += (s, e) => { string id = SelectedId(); if (id != null) watcher.RequestStop(id); };
            btnRow.Controls.Add(contBtn);
            btnRow.Controls.Add(stopBtn);
            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 8.5f)
            };
            bottom.Controls.Add(logBox);
            bottom.Controls.Add(btnRow);

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            colNum = list.Columns.Add("#", 46);
            colName = list.Columns.Add("", 540);
            colState = list.Columns.Add("", 232);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();

            pSess.Controls.Add(list);
            pSess.Controls.Add(bottom);
            pSess.Controls.Add(top);
            pSess.Padding = new Padding(Pad, 8, Pad, 8);
        }

        void BuildPhone()
        {
            var l = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            notifyTitle = new Label { Location = new Point(Pad, 14), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            notifyHelp = new Label { Location = new Point(Pad, 40), Size = new Size(Wide, 22), Anchor = l, ForeColor = Color.DimGray };
            webhook = new TextBox { Location = new Point(Pad, 70), Size = new Size(Wide - 160, 26), Anchor = l };
            testBtn = new Button { Location = new Point(Pad + Wide - 148, 69), Size = new Size(148, 28), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            testBtn.Click += (s, e) => DoTestNotice();
            testState = new Label { Location = new Point(Pad, 102), Size = new Size(Wide, 20), Anchor = l, ForeColor = Color.DimGray };
            evNeedsYou = new CheckBox { Location = new Point(Pad, 128), AutoSize = true };
            evDone = new CheckBox { Location = new Point(Pad, 156), AutoSize = true };
            evMorning = new CheckBox { Location = new Point(Pad, 184), AutoSize = true };
            evNeedsYou.CheckedChanged += (s, e) => SaveEvents();
            evDone.CheckedChanged += (s, e) => SaveEvents();
            evMorning.CheckedChanged += (s, e) => { if (!loading) Save("report.time", evMorning.Checked ? "08:00" : ""); };

            remoteTitle = new Label { Location = new Point(Pad, 226), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            remoteHelp = new Label { Location = new Point(Pad, 252), Size = new Size(Wide, 40), Anchor = l, ForeColor = Color.DimGray };
            botIdLbl = new Label { Location = new Point(Pad, 302), AutoSize = true };
            botId = new TextBox { Location = new Point(Pad + 80, 299), Size = new Size(Wide - 80, 26), Anchor = l };
            secretLbl = new Label { Location = new Point(Pad, 334), AutoSize = true };
            secret = new TextBox { Location = new Point(Pad + 80, 331), Size = new Size(Wide - 80, 26), Anchor = l, UseSystemPasswordChar = true };
            connectBtn = new Button { Location = new Point(Pad, 366), Size = new Size(100, 30) };
            connectBtn.Click += (s, e) => DoConnect();
            remoteState = new Label { Location = new Point(Pad + 112, 372), AutoSize = true, ForeColor = Color.DimGray };
            pairLabel = new Label { Location = new Point(Pad, 406), Size = new Size(Wide, 40), Anchor = l, ForeColor = Color.FromArgb(30, 60, 160) };

            pPhone.Controls.AddRange(new Control[] { notifyTitle, notifyHelp, webhook, testBtn, testState, evNeedsYou, evDone, evMorning,
                remoteTitle, remoteHelp, botIdLbl, botId, secretLbl, secret, connectBtn, remoteState, pairLabel });
        }

        void BuildSettings()
        {
            startChk = new CheckBox { Location = new Point(Pad, 22), AutoSize = true };
            startChk.CheckedChanged += (s, e) => { if (!loading) { SetStartup(startChk.Checked); Save("startWithWindows", startChk.Checked); } };
            awakeChk = new CheckBox { Location = new Point(Pad, 56), AutoSize = true };
            awakeChk.CheckedChanged += (s, e) => { if (!loading) Save("keepAwake.enabled", awakeChk.Checked); };
            idleLbl = new Label { Location = new Point(Pad, 96), AutoSize = true };
            idleNum = new NumericUpDown { Location = new Point(Pad + 170, 93), Size = new Size(76, 26), Minimum = 10, Maximum = 1800, Increment = 10 };
            idleNum.ValueChanged += (s, e) => { if (!loading) Save("idleSeconds", (int)idleNum.Value); };
            idleSuffix = new Label { Location = new Point(Pad + 256, 96), AutoSize = true };
            langLbl = new Label { Location = new Point(Pad, 134), AutoSize = true };
            langCombo = new ComboBox { Location = new Point(Pad + 170, 131), Size = new Size(160, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            langCombo.SelectedIndexChanged += (s, e) => OnLangChanged();
            localNote = new Label { Location = new Point(Pad, 180), Size = new Size(Wide, 40), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ForeColor = Color.DimGray };

            pSet.Controls.AddRange(new Control[] { startChk, awakeChk, idleLbl, idleNum, idleSuffix, langLbl, langCombo, localNote });
        }

        void Localize()
        {
            ui = Json.At(cfg.Words(cfg.UiLang()), "ui");
            Text = U("appTitle");
            pSess.Text = U("tabSessions"); pPhone.Text = U("tabPhone"); pSet.Text = U("tabSettings");
            pauseBtn.Text = watcher.Paused ? U("resume") : U("pause");
            colNum.Text = U("colNum"); colName.Text = U("colSession"); colState.Text = U("colState");
            contBtn.Text = U("continueNow"); stopBtn.Text = U("stop");

            notifyTitle.Text = U("notifyTitle"); notifyHelp.Text = U("notifyHelp");
            testBtn.Text = U("testNotify");
            evNeedsYou.Text = U("evNeedsYou"); evDone.Text = U("evDone"); evMorning.Text = U("evMorning");
            remoteTitle.Text = U("remoteTitle"); remoteHelp.Text = U("remoteHelp");
            botIdLbl.Text = "Bot ID"; secretLbl.Text = "Secret"; connectBtn.Text = U("connect");

            startChk.Text = U("settingsStart"); awakeChk.Text = U("settingsAwake");
            string isd = U("settingsIdle"); int ix = isd.IndexOf("{n}");
            idleLbl.Text = ix >= 0 ? isd.Substring(0, ix).Trim() : isd;
            idleSuffix.Text = ix >= 0 ? isd.Substring(ix + 3).Trim() : "";
            langLbl.Text = cfg.UiLang() == "zh" ? "\u8bed\u8a00" : "Language";
            localNote.Text = U("localNote");

            loading = true;
            int sel = langCombo.SelectedIndex;
            langCombo.Items.Clear();
            langCombo.Items.Add(U("langAuto")); langCombo.Items.Add("\u4e2d\u6587"); langCombo.Items.Add("English");
            langCombo.SelectedIndex = sel >= 0 ? sel : LangIndex();
            loading = false;
        }

        int LangIndex()
        {
            string s = cfg.Str("language", "auto");
            if (s == "zh") return 1;
            if (s == "en") return 2;
            return 0;
        }

        void LoadSettings()
        {
            loading = true;
            webhook.Text = Json.Str(cfg.At("notify", "webhook"));
            var events = Json.StrArr(cfg.At("notify", "events"));
            evNeedsYou.Checked = events.Contains("needsYou");
            evDone.Checked = events.Contains("done");
            evMorning.Checked = Json.Str(cfg.At("report", "time")).Length > 0;
            botId.Text = Json.Str(cfg.At("remote", "botId"));
            secret.Text = Json.Str(cfg.At("remote", "secret"));
            startChk.Checked = cfg.Bool("startWithWindows", false);
            awakeChk.Checked = cfg.Bool("keepAwake.enabled", true);
            decimal idle = cfg.Int("idleSeconds", 180);
            idleNum.Value = Math.Min(idleNum.Maximum, Math.Max(idleNum.Minimum, idle));
            langCombo.SelectedIndex = LangIndex();
            loading = false;
        }

        void Save(string dotted, object value)
        {
            var parts = dotted.Split('.');
            var root = new Dictionary<string, object>();
            var cur = root;
            for (int i = 0; i < parts.Length - 1; i++) { var d = new Dictionary<string, object>(); cur[parts[i]] = d; cur = d; }
            cur[parts[parts.Length - 1]] = value;
            cfg.SaveUser(root);
        }

        void SaveEvents()
        {
            if (loading) return;
            var evs = new List<string>();
            if (evNeedsYou.Checked) evs.Add("needsYou");
            if (evDone.Checked) evs.Add("done");
            Save("notify.events", evs.ToArray());
        }

        void DoTestNotice()
        {
            Save("notify.webhook", webhook.Text.Trim());
            if (webhook.Text.Trim().Length == 0) { testState.Text = U("needWebhook"); return; }
            testState.Text = U("connecting");
            testBtn.Enabled = false;
            var bg = new System.ComponentModel.BackgroundWorker();
            string err = null;
            bg.DoWork += (s, e) => err = watcher.TestNotice();
            bg.RunWorkerCompleted += (s, e) =>
            {
                testBtn.Enabled = true;
                testState.Text = string.IsNullOrEmpty(err) ? U("testSent") : Policy.FormatText(U("testFail"), new Dictionary<string, string> { { "why", err } });
            };
            bg.RunWorkerAsync();
        }

        void DoConnect()
        {
            if (botId.Text.Trim().Length == 0 || secret.Text.Trim().Length == 0) { remoteState.Text = U("needBot"); return; }
            Save("remote.botId", botId.Text.Trim());
            Save("remote.secret", secret.Text.Trim());
            watcher.ReloadRemote();
            remoteState.Text = U("connecting");
        }

        void OnLangChanged()
        {
            if (loading) return;
            string v = langCombo.SelectedIndex == 1 ? "zh" : langCombo.SelectedIndex == 2 ? "en" : "auto";
            Save("language", v);
            Localize();
            Tick();
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("KiroAutoContinue", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("KiroAutoContinue", false);
                }
            }
            catch { }
        }

        string SelectedId()
        {
            if (list.SelectedItems.Count == 0) return null;
            return list.SelectedItems[0].Tag as string;
        }

        void UpdateButtons()
        {
            string id = SelectedId();
            var row = watcher.LastSnapshot.FirstOrDefault(r => r.Id == id);
            stopBtn.Enabled = row != null && row.Running;
            contBtn.Enabled = row != null && !row.Running;
        }

        void Tick()
        {
            pauseBtn.Text = watcher.Paused ? U("resume") : U("pause");
            var rows = watcher.LastSnapshot;
            int waiting = rows.Count(r => r.Pending || r.Queued);
            int needs = rows.Count(r => r.NeedsYou);
            string head = watcher.Paused ? U("paused") : U("running");
            sessStatus.Text = head + "    " + Policy.FormatText(U("summary"),
                new Dictionary<string, string> { { "sessions", rows.Count.ToString() }, { "waiting", waiting.ToString() }, { "needs", needs.ToString() } });

            string keep = SelectedId();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rows)
            {
                string tail = "";
                if (r.NeedsYou && r.Need.Length > 0) tail = "  (" + r.Need + ")";
                else if (r.Queued) tail = "  (+)";
                var it = new ListViewItem(r.Number.ToString());
                it.SubItems.Add(r.Workspace + " | " + r.Title);
                it.SubItems.Add(r.State + tail);
                it.Tag = r.Id;
                if (r.NeedsYou) it.ForeColor = Color.FromArgb(176, 32, 32);
                else if (r.Running) it.ForeColor = Color.FromArgb(24, 96, 24);
                if (r.Id == keep) it.Selected = true;
                list.Items.Add(it);
            }
            list.EndUpdate();
            UpdateButtons();

            string st = watcher.RemoteStateText;
            remoteState.Text = st == "online" ? U("online") : (st == "connecting" || st == "starting") ? U("connecting") : U("offline");
            if (watcher.PairCode != null)
                pairLabel.Text = Policy.FormatText(U("pair"), new Dictionary<string, string> { { "code", watcher.PairCode } });
            else if (watcher.HasPaired())
                pairLabel.Text = Policy.FormatText(U("pairedWith"), new Dictionary<string, string> { { "who", watcher.PairedWho() } });
            else pairLabel.Text = U("notPaired");
        }

        void OnWatcherLog(string line)
        {
            if (IsHandleCreated) try { BeginInvoke((Action)(() => AppendLog(line))); } catch { }
        }

        void AppendLog(string line)
        {
            if (logBox.Lines.Length > 400) logBox.Lines = logBox.Lines.Skip(logBox.Lines.Length - 300).ToArray();
            logBox.AppendText(line + "\r\n");
        }
    }
}
