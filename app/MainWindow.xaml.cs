using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KiroAutoContinue
{
    // One session row. Updated in place on every refresh, so the stop button's "are you sure" and the wait after
    // a click survive the refresh.
    internal sealed class RowVM : INotifyPropertyChanged
    {
        public string Id = "";
        public string Action = "";                    // stop, continueNow, continue
        public DateTime ArmedUntil = DateTime.MinValue;  // stop asks once more until then
        public DateTime BusyUntil = DateTime.MinValue;   // a click is being carried out until then...
        public bool BusyRunning;                         // ...or until Running is no longer this

        int number; string title = "", workspace = "", extra = "", stateText = "", kind = "", actionText = "";
        bool armed, actionEnabled = true;
        public int Number { get { return number; } set { Set(ref number, value); } }
        public string Title { get { return title; } set { Set(ref title, value); } }
        public string Workspace { get { return workspace; } set { Set(ref workspace, value); } }
        public string Extra { get { return extra; } set { Set(ref extra, value); } }
        public string StateText { get { return stateText; } set { Set(ref stateText, value); } }
        public string Kind { get { return kind; } set { Set(ref kind, value); } }
        public string ActionText { get { return actionText; } set { Set(ref actionText, value); } }
        public bool Armed { get { return armed; } set { Set(ref armed, value); } }
        public bool ActionEnabled { get { return actionEnabled; } set { Set(ref actionEnabled, value); } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // One line of "recent". Never changes after it is made (the list is rebuilt when the language changes).
    internal sealed class ActVM : INotifyPropertyChanged
    {
        public string Time { get; set; }
        public string Text { get; set; }
        public string Sub { get; set; }
        public string Full { get; set; }
        public string Tone { get; set; }
        public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    }

    internal partial class MainWindow : Window
    {
        readonly Config cfg;
        readonly Watcher watcher;
        readonly bool preview;   // --preview: nothing is saved, sent or typed
        readonly ObservableCollection<RowVM> rows = new ObservableCollection<RowVM>();
        readonly ObservableCollection<ActVM> recent = new ObservableCollection<ActVM>();
        readonly DispatcherTimer timer;
        object ui, rw, cw;
        bool loading, botEdited, wasPaused;
        Storyboard pulse;
        int phoneTick;

        // The status line for the tray icon's tooltip, on every refresh.
        public event Action<string> SummaryChanged;

        internal MainWindow(Config cfg, Watcher watcher, bool preview)
        {
            this.cfg = cfg; this.watcher = watcher; this.preview = preview;
            InitializeComponent();
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico", UriKind.Absolute)); } catch { }
            list.ItemsSource = rows;
            recentList.ItemsSource = recent;
            Localize();
            LoadSettings();
            watcher.OnActivity += OnActivity;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => Refresh();
            timer.Start();
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); UpdatePulse(); };
            // a long state wraps inside its pill instead of squeezing the session titles
            Resources["PillMax"] = 240.0;
            listScroll.SizeChanged += (s, e) => Resources["PillMax"] = Math.Max(130.0, Math.Floor(listScroll.ViewportWidth * 0.38));
            // in a short window "recent" gives up height first, so the session list keeps room for a few rows
            sessPage.SizeChanged += (s, e) => FitRecent();
            recentList.SizeChanged += (s, e) => FitRecent();
            pages.SelectionChanged += (s, e) => { if (e.Source == pages) { phoneTick = 0; Refresh(); } };
            Closed += (s, e) => { watcher.OnActivity -= OnActivity; timer.Stop(); };
            Refresh();
        }

        public void SelectPage(int i) { if (i >= 0 && i < pages.Items.Count) pages.SelectedIndex = i; }

        // --- words ---
        string U(string key) { return Json.Str(Json.At(ui, key)); }
        string F(string key, params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return Policy.FormatText(Json.At(ui, key), d);
        }
        static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsLower(s[0])) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        void Localize()
        {
            object w = cfg.Words(cfg.UiLang());
            ui = Json.At(w, "ui"); rw = Json.At(w, "remote"); cw = Json.At(w, "credits");
            Title = U("appTitle");
            tabSessions.Header = U("tabSessions"); tabPhone.Header = U("tabPhone"); tabSettings.Header = U("tabSettings");
            colNum.Text = U("colNum"); colSession.Text = U("colSession"); colState.Text = U("colState");
            emptyText.Text = U("empty");
            recentTitle.Text = U("recent"); openLogBtn.Content = U("openLog"); recentEmpty.Text = U("recentEmpty");

            notifyTitle.Text = U("notifyTitle"); notifyHelp.Text = U("notifyHelp"); webhookHint.Text = U("webhookHint");
            testBtn.Content = U("testNotify");
            evNeedsYou.Content = U("evNeedsYou"); evDone.Content = U("evDone"); evMorning.Content = U("evMorning");
            remoteTitle.Text = U("remoteTitle"); remoteHelp.Text = U("remoteHelp");
            connectBtn.Content = U("connect"); remoteNote.Text = U("remoteNote"); pairLead.Text = U("pairLead");
            phoneFoot.Text = U("localNote");

            startTitle.Text = U("settingsStart"); startHint.Text = U("settingsStartHint");
            awakeTitle.Text = U("settingsAwake"); awakeHint.Text = U("settingsAwakeHint");
            idleTitle.Text = U("idleTitle"); idleHint.Text = U("idleHint"); idleUnit.Text = U("idleUnit");
            langTitle.Text = U("settingsLang");
            langAuto.Content = U("langAuto"); langZh.Content = U("langZh"); langEn.Content = U("langEn");
            setFoot.Text = U("localNote");

            RebuildRecent();
            foreach (var vm in rows) vm.ActionText = "";   // the next refresh writes them in the new language
        }

        // --- recent ---
        internal string ActivityText(Activity a) { return MakeAct(a).Text; }

        ActVM MakeAct(Activity a)
        {
            string kind = a.Kind ?? "", key = kind, tone = "info", need = "";
            if (kind.StartsWith("need.", StringComparison.Ordinal))
            {
                key = "need"; tone = "need";
                need = Json.Str(Json.At(rw, "need", kind.Substring(5)));
                if (need.Length == 0) need = kind.Substring(5);
            }
            else if (kind == "done" || kind == "phone.paired") tone = "good";
            else if (kind == "kiro.restart" || kind.StartsWith("window.not", StringComparison.Ordinal)) tone = "warn";
            else if (kind.StartsWith("sent.", StringComparison.Ordinal) || kind == "window.continue" || kind == "window.stop" || kind.StartsWith("phone.", StringComparison.Ordinal)) tone = "act";
            string tpl = Json.Str(Json.At(ui, "act", key));
            if (tpl.Length == 0) tpl = kind;
            string text = Policy.FormatText(tpl, new Dictionary<string, string>
            {
                { "n", a.Number.ToString(CultureInfo.InvariantCulture) }, { "d", a.Detail ?? "" }, { "need", need }
            });
            string title = a.Title ?? "";
            return new ActVM
            {
                Time = a.At.Date == DateTime.Today ? a.At.ToString("HH:mm") : a.At.ToString("MM-dd HH:mm"),
                Text = text,
                Sub = title.Length > 0 ? U("sep") + title : "",
                Full = title.Length > 0 ? text + "\n" + title : text,
                Tone = tone
            };
        }

        void RebuildRecent()
        {
            recent.Clear();
            var all = watcher.RecentActivities();
            for (int i = all.Count - 1; i >= 0 && recent.Count < 100; i--) recent.Add(MakeAct(all[i]));
            recentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnActivity(Activity a)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                recent.Insert(0, MakeAct(a));
                while (recent.Count > 100) recent.RemoveAt(recent.Count - 1);
                recentEmpty.Visibility = Visibility.Collapsed;
                // a click from this window that did not go through: give the button back now
                if (a.Kind.StartsWith("window.", StringComparison.Ordinal))
                    foreach (var vm in rows) if (vm.Id == a.Id) vm.BusyUntil = DateTime.MinValue;
                Refresh();
            }));
        }

        // --- the refresh (every second) ---
        void Refresh()
        {
            var snap = watcher.LastSnapshot ?? new List<SessionRow>();
            int waiting = snap.Count(r => r.Pending || r.QueuedCount > 0);
            int needs = snap.Count(r => r.NeedsYou);
            bool paused = watcher.Paused;
            string summary = F("summary", "sessions", snap.Count.ToString(CultureInfo.InvariantCulture),
                "waiting", waiting.ToString(CultureInfo.InvariantCulture), "needs", needs.ToString(CultureInfo.InvariantCulture));
            var h = SummaryChanged; if (h != null) h((paused ? U("paused") : U("running")) + "\n" + summary);
            if (!IsVisible) return;

            statusTitle.Text = paused ? U("paused") : U("running");
            statusSummary.Text = summary;
            string credits = Credits.Text(watcher.LastCredits, cw, DateTime.Now);
            statusCredits.Text = credits;
            statusCredits.Visibility = credits.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            pauseBtn.Content = paused ? U("resume") : U("pause");
            if (paused != wasPaused || statusCard.Tag == null) { wasPaused = paused; statusCard.Tag = "set"; CardColors(paused); UpdatePulse(); }

            SyncRows(snap);
            emptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (pages.SelectedItem == tabPhone && phoneTick-- <= 0) { phoneTick = 2; RefreshPhone(); }
        }

        static readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();
        static SolidColorBrush B(string hex)
        {
            SolidColorBrush b;
            if (!brushes.TryGetValue(hex, out b))
            {
                b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze(); brushes[hex] = b;
            }
            return b;
        }

        // Green while it watches, amber while paused (the sketch's card).
        void CardColors(bool paused)
        {
            statusCard.Background = B(paused ? "#FFFBEB" : "#ECFDF5");
            statusCard.BorderBrush = B(paused ? "#FDE68A" : "#A7F3D0");
            statusTitle.Foreground = B(paused ? "#92400E" : "#065F46");
            statusSummary.Foreground = B(paused ? "#B45309" : "#047857");
            statusCredits.Foreground = statusSummary.Foreground;
            statusDot.Fill = B(paused ? "#F59E0B" : "#10B981");
            statusHalo.Fill = statusDot.Fill;
            pauseBtn.BorderBrush = B(paused ? "#FCD34D" : "#6EE7B7");
            pauseBtn.Foreground = B(paused ? "#92400E" : "#065F46");
        }

        // A soft ring going out from the green dot while it watches.
        void UpdatePulse()
        {
            bool on = IsVisible && !watcher.Paused;
            if (on && pulse == null)
            {
                var sc = new ScaleTransform(1, 1);
                statusHalo.RenderTransform = sc;
                var d = new Duration(TimeSpan.FromSeconds(2.4));
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var ax = new DoubleAnimation(1, 2.3, d) { EasingFunction = ease };
                var ay = new DoubleAnimation(1, 2.3, d) { EasingFunction = ease };
                var ao = new DoubleAnimation(0.5, 0, d);
                Storyboard.SetTarget(ax, statusHalo); Storyboard.SetTargetProperty(ax, new PropertyPath("RenderTransform.ScaleX"));
                Storyboard.SetTarget(ay, statusHalo); Storyboard.SetTargetProperty(ay, new PropertyPath("RenderTransform.ScaleY"));
                Storyboard.SetTarget(ao, statusHalo); Storyboard.SetTargetProperty(ao, new PropertyPath("Opacity"));
                pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(pulse, 30);
                pulse.Children.Add(ax); pulse.Children.Add(ay); pulse.Children.Add(ao);
                pulse.Begin(this, true);
            }
            else if (!on && pulse != null)
            {
                pulse.Stop(this); pulse = null; statusHalo.Opacity = 0;
            }
        }

        // "recent" is at most 150 high and shrinks in a short window; always a whole number of lines, so no
        // line is cut in half at the bottom.
        void FitRecent()
        {
            double line = 23;
            var first = recent.Count > 0 ? recentList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement : null;
            if (first != null && first.ActualHeight > 8) line = first.ActualHeight;
            double room = Math.Max(line * 2, Math.Min(150.0, sessPage.ActualHeight - 330));
            double fit = Math.Floor(room / line) * line;
            if (Math.Abs(recentScroll.MaxHeight - fit) > 0.5) recentScroll.MaxHeight = fit;
        }

        void SyncRows(List<SessionRow> snap)
        {
            var ids = new HashSet<string>(snap.Select(r => r.Id));
            for (int i = rows.Count - 1; i >= 0; i--) if (!ids.Contains(rows[i].Id)) rows.RemoveAt(i);
            for (int i = 0; i < snap.Count; i++)
            {
                var r = snap[i];
                int at = -1;
                for (int j = 0; j < rows.Count; j++) if (rows[j].Id == r.Id) { at = j; break; }
                RowVM vm;
                if (at < 0) { vm = new RowVM { Id = r.Id }; rows.Insert(Math.Min(i, rows.Count), vm); }
                else { vm = rows[at]; if (at != i) rows.Move(at, i); }
                Fill(vm, r);
            }
        }

        void Fill(RowVM vm, SessionRow r)
        {
            DateTime now = DateTime.Now;
            vm.Number = r.Number;
            vm.Title = r.Title;
            vm.Workspace = r.Workspace;
            string extra = "";
            if (r.UntilDone) extra += U("sep") + U("badgeUntilDone");
            if (r.QueuedCount > 0) extra += U("sep") + F("badgeQueued", "k", r.QueuedCount.ToString(CultureInfo.InvariantCulture));
            vm.Extra = extra;

            if (r.NeedsYou) { vm.Kind = "needs"; vm.StateText = Cap(r.Need.Length > 0 ? r.Need : r.State); }
            else if (r.Pending) { vm.Kind = "pending"; vm.StateText = PendingText(r, now); }
            else if (r.Attention == "done" && r.Need.Length > 0 && !r.Running) { vm.Kind = "goal"; vm.StateText = Cap(r.Need); }
            else { vm.Kind = r.Kind; vm.StateText = Cap(r.State); }

            string act = r.Running ? "stop" : r.Pending ? "continueNow" : "continue";
            if (vm.Action != act) { vm.Action = act; vm.ArmedUntil = DateTime.MinValue; }
            bool armed = act == "stop" && vm.ArmedUntil > now;
            bool busy = vm.BusyUntil > now && vm.BusyRunning == r.Running;
            if (!busy) vm.BusyUntil = DateTime.MinValue;
            vm.Armed = armed && !busy;
            vm.ActionEnabled = !busy;
            vm.ActionText = busy ? U("sending") : armed ? U("stopConfirm") : U(act);
        }

        // "Cut off, continues in 2 min" and the like: what is coming for this row, and when.
        string PendingText(SessionRow r, DateTime now)
        {
            string when;
            if (r.Wait == "idle") when = U("whenIdle");
            else if (r.Wait == "kiro") when = U("whenKiro");
            else
            {
                double s = (r.Due - now).TotalSeconds;
                if (s <= 3) when = U("whenNow");
                else if (s < 60) when = F("whenSec", "n", ((int)Math.Ceiling(s)).ToString(CultureInfo.InvariantCulture));
                else when = F("whenMin", "n", ((int)Math.Ceiling(s / 60)).ToString(CultureInfo.InvariantCulture));
            }
            string key = r.Wait == "notab" ? "retryNoTab" : r.Wait == "draft" ? "retryDraft" : r.Wait == "retry" ? "retry"
                : r.Plan == "resume" ? "pendResume" : r.Plan == "queued" ? "pendQueued" : "pendPush";
            return Cap(F(key, "when", when));
        }

        void OnRowAction(object sender, RoutedEventArgs e)
        {
            var vm = (sender as FrameworkElement)?.DataContext as RowVM;
            if (vm == null) return;
            DateTime now = DateTime.Now;
            if (vm.Action == "stop")
            {
                if (vm.ArmedUntil <= now) { vm.ArmedUntil = now.AddSeconds(4); Refresh(); return; }
                vm.ArmedUntil = DateTime.MinValue;
                vm.BusyRunning = true; vm.BusyUntil = now.AddSeconds(45);
                if (!preview) watcher.RequestStop(vm.Id);
            }
            else
            {
                vm.BusyRunning = false; vm.BusyUntil = now.AddSeconds(45);
                if (!preview) watcher.RequestContinue(vm.Id);
            }
            Refresh();
        }

        void OnPause(object sender, RoutedEventArgs e)
        {
            if (!preview) watcher.Pause(!watcher.Paused);
            Refresh();
        }

        void OnOpenLog(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(cfg.LogPath) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start("notepad.exe", "\"" + cfg.LogPath + "\""); } catch { }
            }
        }

        // --- phone ---
        void RefreshPhone()
        {
            string st = watcher.RemoteStateText;
            bool set = Json.Str(cfg.At("remote", "botId")).Length > 0 && Json.Str(cfg.At("remote", "secret")).Length > 0;
            if (st == "online") { remoteState.Text = U("online"); remoteDot.Fill = B("#10B981"); }
            else if (st == "connecting" || st == "starting") { remoteState.Text = U("connecting"); remoteDot.Fill = B("#F59E0B"); }
            else { remoteState.Text = set ? U("offline") : U("notSet"); remoteDot.Fill = B("#9CA3AF"); }
            remoteNote.Visibility = botEdited ? Visibility.Visible : Visibility.Collapsed;

            string code = watcher.PairCode;
            if (code != null)
            {
                pairBox.Visibility = Visibility.Visible;
                pairCode.Text = (Json.StrArr(Json.At(rw, "cmd", "pair")).FirstOrDefault() ?? "pair") + " " + code;
                pairInfo.Visibility = Visibility.Collapsed;
            }
            else
            {
                pairBox.Visibility = Visibility.Collapsed;
                pairInfo.Visibility = Visibility.Visible;
                pairInfo.Text = watcher.HasPaired() ? F("pairedWith", "who", watcher.PairedWho())
                    : st == "online" ? U("notPaired") : U("pairWait");
            }
        }

        void OnWebhookChanged(object sender, TextChangedEventArgs e)
        {
            webhookHint.Visibility = webhook.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnWebhookCommit(object sender, KeyboardFocusChangedEventArgs e)
        {
            string url = webhook.Text.Trim();
            if (url != Json.Str(cfg.At("notify", "webhook"))) Save("notify.webhook", url);
        }

        void ShowTest(string text, string hex)
        {
            testState.Text = text; testState.Foreground = B(hex); testState.Visibility = Visibility.Visible;
        }

        void OnTest(object sender, RoutedEventArgs e)
        {
            string url = webhook.Text.Trim();
            Save("notify.webhook", url);
            if (url.Length == 0) { ShowTest(U("needWebhook"), "#B45309"); return; }
            if (preview) return;
            ShowTest(U("connecting"), "#6B7280");
            testBtn.IsEnabled = false;
            Task.Run(() => watcher.TestNotice()).ContinueWith(t =>
            {
                testBtn.IsEnabled = true;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (string.IsNullOrEmpty(err)) ShowTest(U("testSent"), "#047857");
                else ShowTest(F("testFail", "why", err), "#B91C1C");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OnEvents(object sender, RoutedEventArgs e)
        {
            var evs = new List<string>();
            if (evNeedsYou.IsChecked == true) evs.Add("needsYou");
            if (evDone.IsChecked == true) evs.Add("done");
            Save("notify.events", evs.ToArray());
        }

        void OnMorning(object sender, RoutedEventArgs e) { Save("report.time", evMorning.IsChecked == true ? "08:00" : ""); }

        void OnBotEdited(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            botEdited = botId.Text.Trim() != Json.Str(cfg.At("remote", "botId")) || secret.Password.Trim() != Json.Str(cfg.At("remote", "secret"));
            remoteNote.Visibility = botEdited ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnConnect(object sender, RoutedEventArgs e)
        {
            string id = botId.Text.Trim(), sec = secret.Password.Trim();
            if (id.Length == 0 || sec.Length == 0) { remoteState.Text = U("needBot"); remoteDot.Fill = B("#F59E0B"); return; }
            Save("remote.botId", id);
            Save("remote.secret", sec);
            botEdited = false;
            if (!preview) watcher.ReloadRemote();
            remoteState.Text = U("connecting"); remoteDot.Fill = B("#F59E0B");
            remoteNote.Visibility = Visibility.Collapsed;
            phoneTick = 2;
        }

        // --- settings ---
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "KiroAutoContinue";
        static string RunValue() { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray"; }

        static string StartupEntry()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false)) return k == null ? null : k.GetValue(RunName) as string; }
            catch { return null; }
        }

        void SetStartup(bool on)
        {
            if (preview) return;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, RunValue()); else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { watcher.LogUi("start with Windows not changed: " + ex.Message); }
        }

        void LoadSettings()
        {
            loading = true;
            webhook.Text = Json.Str(cfg.At("notify", "webhook"));
            webhookHint.Visibility = webhook.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var events = Json.StrArr(cfg.At("notify", "events"));
            evNeedsYou.IsChecked = events.Contains("needsYou");
            evDone.IsChecked = events.Contains("done");
            evMorning.IsChecked = Json.Str(cfg.At("report", "time")).Length > 0;
            botId.Text = Json.Str(cfg.At("remote", "botId"));
            secret.Password = Json.Str(cfg.At("remote", "secret"));
            // the switch shows what Windows will really do; an entry left over from a moved exe is pointed here
            string entry = StartupEntry();
            startChk.IsChecked = entry != null;
            if (entry != null && entry != RunValue()) SetStartup(true);
            awakeChk.IsChecked = cfg.Bool("keepAwake.enabled", true);
            idleBox.Text = cfg.Int("idleSeconds", 180).ToString(CultureInfo.InvariantCulture);
            string lang = cfg.Str("language", "auto");
            langZh.IsChecked = lang == "zh"; langEn.IsChecked = lang == "en"; langAuto.IsChecked = lang != "zh" && lang != "en";
            loading = false;
        }

        void Save(string dotted, object value)
        {
            if (loading || preview) return;
            var parts = dotted.Split('.');
            var root = new Dictionary<string, object>();
            var cur = root;
            for (int i = 0; i < parts.Length - 1; i++) { var d = new Dictionary<string, object>(); cur[parts[i]] = d; cur = d; }
            cur[parts[parts.Length - 1]] = value;
            try { cfg.SaveUser(root); }
            catch (Exception ex) { watcher.LogUi("setting " + dotted + " not saved: " + ex.Message); }
        }

        void OnStartup(object sender, RoutedEventArgs e)
        {
            bool on = startChk.IsChecked == true;
            SetStartup(on);
            Save("startWithWindows", on);
        }

        void OnAwake(object sender, RoutedEventArgs e) { Save("keepAwake.enabled", awakeChk.IsChecked == true); }

        void SetIdle(int v)
        {
            v = Math.Max(10, Math.Min(1800, v));
            idleBox.Text = v.ToString(CultureInfo.InvariantCulture);
            if (v != cfg.Int("idleSeconds", 180)) Save("idleSeconds", v);
        }

        void OnIdleStep(object sender, RoutedEventArgs e)
        {
            int cur, step;
            if (!int.TryParse(idleBox.Text.Trim(), out cur)) cur = cfg.Int("idleSeconds", 180);
            int.TryParse(Convert.ToString(((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture), out step);
            SetIdle(cur + step);
        }

        void OnIdleCommit(object sender, KeyboardFocusChangedEventArgs e)
        {
            int v;
            if (int.TryParse(idleBox.Text.Trim(), out v)) SetIdle(v);
            else idleBox.Text = cfg.Int("idleSeconds", 180).ToString(CultureInfo.InvariantCulture);
        }

        void OnIdleKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            int v;
            if (int.TryParse(idleBox.Text.Trim(), out v)) SetIdle(v);
            e.Handled = true;
        }

        void OnLang(object sender, RoutedEventArgs e)
        {
            string v = langZh.IsChecked == true ? "zh" : langEn.IsChecked == true ? "en" : "auto";
            if (v == cfg.Str("language", "auto")) return;
            Save("language", v);
            if (preview) cfg.Root["language"] = v;
            Localize();
            Refresh();
            var h = LanguageChanged; if (h != null) h();
        }

        // The tray menu follows the language too.
        public event Action LanguageChanged;
    }
}
