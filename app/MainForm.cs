using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KiroAutoContinue
{
    // The window: three tabs (sessions, phone, settings). Laid out with TableLayoutPanel rows so every label
    // sizes to its own text and nothing overlaps at any DPI. All Chinese text comes from words.<lang>.ui.
    internal sealed class MainForm : Form
    {
        readonly Config cfg;
        readonly Watcher watcher;
        bool loading;
        object ui;

        TabControl tabs;
        TabPage pSess, pPhone, pSet;

        Label sessStatus; Button pauseBtn; ListView list; ColumnHeader colNum, colName, colState;
        Button stopBtn, contBtn; TextBox logBox;
        Label notifyTitle, notifyHelp, remoteTitle, remoteHelp, testState, remoteState, pairLabel, botIdLbl, secretLbl;
        TextBox webhook, botId, secret; Button testBtn, connectBtn;
        CheckBox evNeedsYou, evDone, evMorning;
        CheckBox startChk, awakeChk; Label idleLbl, idleSuffix, langLbl, localNote; NumericUpDown idleNum; ComboBox langCombo;

        Timer timer;
        static readonly Color Accent = Color.FromArgb(30, 60, 160);

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

        void Build()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9.75f);
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(960, 720);
            MinimumSize = new Size(740, 560);

            tabs = new TabControl { Dock = DockStyle.Fill };
            pSess = new TabPage { BackColor = Color.White, UseVisualStyleBackColor = true };
            pPhone = new TabPage { BackColor = Color.White, UseVisualStyleBackColor = true };
            pSet = new TabPage { BackColor = Color.White, UseVisualStyleBackColor = true };
            tabs.TabPages.AddRange(new[] { pSess, pPhone, pSet });
            Controls.Add(tabs);

            BuildSessions();
            BuildPhone();
            BuildSettings();
        }

        // --- layout helpers ---
        TableLayoutPanel Column(TabPage page)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true,
                Padding = new Padding(20, 10, 20, 14),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                BackColor = Color.White
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            page.Controls.Add(t);
            return t;
        }
        static void Add(TableLayoutPanel t, Control c)
        {
            int r = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, r);
            t.RowCount = r + 1;
        }
        Label Header() { return new Label { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 3), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold) }; }
        Label Hint() { return new Label { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 4), ForeColor = Color.DimGray }; }
        static CheckBox Chk() { return new CheckBox { AutoSize = true, Margin = new Padding(0, 2, 0, 2) }; }
        static Panel Spacer(int h) { return new Panel { Height = h, Margin = new Padding(0), Dock = DockStyle.Top }; }

        void BuildSessions()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(20, 12, 20, 6), BackColor = Color.White };
            sessStatus = new Label { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold) };
            pauseBtn = new Button { Dock = DockStyle.Right, Width = 110, FlatStyle = FlatStyle.System };
            pauseBtn.Click += (s, e) => { watcher.Pause(!watcher.Paused); Tick(); };
            top.Controls.Add(sessStatus);
            top.Controls.Add(pauseBtn);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 196, Padding = new Padding(20, 0, 20, 14), BackColor = Color.White };
            var btnRow = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White };
            contBtn = new Button { Location = new Point(0, 8), Size = new Size(104, 30), Enabled = false, FlatStyle = FlatStyle.System };
            stopBtn = new Button { Location = new Point(114, 8), Size = new Size(84, 30), Enabled = false, FlatStyle = FlatStyle.System };
            contBtn.Click += (s, e) => { string id = SelectedId(); if (id != null) watcher.RequestContinue(id); };
            stopBtn.Click += (s, e) => { string id = SelectedId(); if (id != null) watcher.RequestStop(id); };
            btnRow.Controls.Add(contBtn);
            btnRow.Controls.Add(stopBtn);
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 250), Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.FixedSingle };
            bottom.Controls.Add(logBox);
            bottom.Controls.Add(btnRow);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
            colNum = list.Columns.Add("#", 46);
            colName = list.Columns.Add("", 470);
            colState = list.Columns.Add("", 200);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.Resize += (s, e) => { int w = list.ClientSize.Width - colNum.Width - colState.Width - 2; if (w > 140) colName.Width = w; };

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8), BackColor = Color.White };
            host.Controls.Add(list);

            pSess.Controls.Add(host);
            pSess.Controls.Add(bottom);
            pSess.Controls.Add(top);
        }

        void BuildPhone()
        {
            var t = Column(pPhone);
            notifyTitle = Header();
            notifyHelp = Hint();

            var wrow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 2, 0, 2) };
            wrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            wrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            wrow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            webhook = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 8, 2) };
            testBtn = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0), FlatStyle = FlatStyle.System };
            testBtn.Click += (s, e) => DoTestNotice();
            wrow.Controls.Add(webhook, 0, 0);
            wrow.Controls.Add(testBtn, 1, 0);

            testState = Hint();
            evNeedsYou = Chk(); evDone = Chk(); evMorning = Chk();
            evNeedsYou.CheckedChanged += (s, e) => SaveEvents();
            evDone.CheckedChanged += (s, e) => SaveEvents();
            evMorning.CheckedChanged += (s, e) => { if (!loading) Save("report.time", evMorning.Checked ? "08:00" : ""); };

            remoteTitle = Header();
            remoteHelp = Hint();
            botIdLbl = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 10, 0), Text = "Bot ID" };
            botId = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
            var brow = LabelRow(botIdLbl, botId);
            secretLbl = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 10, 0), Text = "Secret" };
            secret = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), UseSystemPasswordChar = true };
            var srow = LabelRow(secretLbl, secret);

            connectBtn = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 4, 16, 4), Margin = new Padding(0, 6, 0, 0), FlatStyle = FlatStyle.System };
            connectBtn.Click += (s, e) => DoConnect();
            remoteState = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(14, 12, 0, 0), ForeColor = Color.DimGray };
            var crow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 2, 0, 2) };
            crow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            crow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            crow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            crow.Controls.Add(connectBtn, 0, 0);
            crow.Controls.Add(remoteState, 1, 0);

            pairLabel = new Label { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0), ForeColor = Accent };

            Add(t, notifyTitle); Add(t, notifyHelp); Add(t, wrow); Add(t, testState);
            Add(t, evNeedsYou); Add(t, evDone); Add(t, evMorning);
            Add(t, Spacer(2));
            Add(t, remoteTitle); Add(t, remoteHelp); Add(t, brow); Add(t, srow); Add(t, crow); Add(t, pairLabel);
        }

        TableLayoutPanel LabelRow(Control label, Control field)
        {
            var r = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 2, 0, 2) };
            r.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            r.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            r.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            r.Controls.Add(label, 0, 0);
            r.Controls.Add(field, 1, 0);
            return r;
        }

        void BuildSettings()
        {
            var t = Column(pSet);
            startChk = Chk();
            startChk.CheckedChanged += (s, e) => { if (!loading) { SetStartup(startChk.Checked); Save("startWithWindows", startChk.Checked); } };
            awakeChk = Chk();
            awakeChk.CheckedChanged += (s, e) => { if (!loading) Save("keepAwake.enabled", awakeChk.Checked); };

            idleLbl = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) };
            idleNum = new NumericUpDown { Width = 78, Minimum = 10, Maximum = 1800, Increment = 10, Margin = new Padding(0, 3, 8, 3), TextAlign = HorizontalAlignment.Center };
            idleNum.ValueChanged += (s, e) => { if (!loading) Save("idleSeconds", (int)idleNum.Value); };
            idleSuffix = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 0) };
            var irow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 10, 0, 2) };
            for (int i = 0; i < 3; i++) irow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            irow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            irow.Controls.Add(idleLbl, 0, 0); irow.Controls.Add(idleNum, 1, 0); irow.Controls.Add(idleSuffix, 2, 0);

            langLbl = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 0) };
            langCombo = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 0, 3) };
            langCombo.SelectedIndexChanged += (s, e) => OnLangChanged();
            var lrow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 2) };
            lrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lrow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lrow.Controls.Add(langLbl, 0, 0); lrow.Controls.Add(langCombo, 1, 0);

            localNote = new Label { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 16, 0, 0), ForeColor = Color.DimGray };

            Add(t, startChk); Add(t, awakeChk); Add(t, irow); Add(t, lrow); Add(t, localNote);
        }

        void Localize()
        {
            ui = Json.At(cfg.Words(cfg.UiLang()), "ui");
            Text = U("appTitle");
            pSess.Text = "  " + U("tabSessions") + "  "; pPhone.Text = "  " + U("tabPhone") + "  "; pSet.Text = "  " + U("tabSettings") + "  ";
            pauseBtn.Text = watcher.Paused ? U("resume") : U("pause");
            colNum.Text = U("colNum"); colName.Text = U("colSession"); colState.Text = U("colState");
            contBtn.Text = U("continueNow"); stopBtn.Text = U("stop");

            notifyTitle.Text = U("notifyTitle"); notifyHelp.Text = U("notifyHelp");
            testBtn.Text = U("testNotify");
            evNeedsYou.Text = U("evNeedsYou"); evDone.Text = U("evDone"); evMorning.Text = U("evMorning");
            remoteTitle.Text = U("remoteTitle"); remoteHelp.Text = U("remoteHelp");
            connectBtn.Text = U("connect");

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
            sessStatus.Text = head + "     " + Policy.FormatText(U("summary"),
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
                else if (r.Running) it.ForeColor = Color.FromArgb(24, 110, 40);
                else it.ForeColor = Color.FromArgb(70, 70, 70);
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
