using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KiroAutoContinue
{
    // A turn that ended and has not been dealt with yet.
    internal sealed class Pending
    {
        public string Dir;
        public DateTime StopAt, Due;
        public int Fails;
        public bool Told, ToldKiro, ToldWorking, CreditsBack;
        public string Plan = "";   // what Look decided to send (a Decision.Kind); "" until it has decided
        public string Wait = "";   // why it is not sent yet: "" (its delay), idle, kiro, or a retry: notab, draft, retry
    }

    // One row for the GUI session list.
    internal sealed class SessionRow
    {
        public int Number;
        public string Id = "", Workspace = "", Title = "", State = "", Need = "";
        public bool Running, Pending, NeedsYou, Queued, UntilDone;
        public string Kind = "";        // running, done, cut, stopped, restart
        public string Attention = "";   // the notice code it is flagged with (need codes, or done)
        public string Plan = "", Wait = "";
        public DateTime Due;            // when a planned message goes (Pending only)
        public int QueuedCount;
    }

    // The engine: watches Kiro's records and acts, the same way as kiro-auto-continue.ps1 did (in v0.2). Runs on its own STA
    // thread (UI Automation and MSAA are COM). The GUI reads Snapshot()/Recent and posts actions through Request*.
    internal sealed partial class Watcher
    {
        readonly Config cfg;
        readonly Dictionary<string, long> read = new Dictionary<string, long>();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        readonly Dictionary<string, SessionState> state = new Dictionary<string, SessionState>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        int sent;
        DateTime? kiroStart;
        bool awake; DateTime awakeAt = DateTime.MinValue;
        RemoteClient remote;
        readonly Dictionary<string, string> attention = new Dictionary<string, string>();
        readonly Dictionary<string, DateTime> creditWait = new Dictionary<string, DateTime>();
        DateTime reportAt = DateTime.MinValue;
        readonly Queue<string> remoteSeen = new Queue<string>();
        readonly HashSet<string> remoteSeenSet = new HashSet<string>();
        string pairCode; int pairTries;
        DateTime startAt;
        bool first = true;
        volatile bool stop;
        volatile bool paused;
        Thread thread;
        readonly ConcurrentQueue<Action> jobs = new ConcurrentQueue<Action>();
        readonly object logLock = new object();

        public readonly List<string> Recent = new List<string>();
        public event Action<string> OnLog;
        public volatile List<SessionRow> LastSnapshot = new List<SessionRow>();
        public volatile CreditInfo LastCredits;   // the credits as Kiro last saw them; null before Kiro asked
        readonly bool readOnly;   // --preview: never writes the log, numbers or activity files

        static readonly Regex RxStop = new Regex("\"stopReason\":\"(\\w+)\"", RegexOptions.Compiled);

        public Watcher(Config cfg, bool readOnly = false)
        {
            this.cfg = cfg; this.readOnly = readOnly;
            LoadActivities();
        }

        // A line from the window (an error it caught), in the same log.
        public void LogUi(string s) { Log("window: " + s); }

        public string PairCode { get { return pairCode; } }
        public bool Paused { get { return paused; } }
        public string RemoteStateText { get { return remote != null ? remote.State : "off"; } }
        public Config Cfg { get { return cfg; } }

        object W() { return cfg.Words(cfg.UiLang()); }
        object Rw() { return Json.At(W(), "remote"); }

        public void Pause(bool on)
        {
            if (paused == on) return;
            paused = on;
            Log(on ? "paused: not continuing sessions on its own" : "watching again");
            Act(on ? "paused" : "resumed");
        }

        public void Start()
        {
            // a test copy (see KAC_DATA_DIR) can start paused, so it never types into Kiro
            if (cfg.Bool("testing.startPaused", false)) paused = true;
            thread = new Thread(Run) { IsBackground = true, Name = "kac-watcher" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void StopAndWait()
        {
            stop = true;
            if (remote != null) remote.StopAndWait();
            if (thread != null) thread.Join(5000);
        }

        // The GUI posts actions here; they run on the watcher thread so all UI Automation stays on one thread.
        public void Post(Action a) { jobs.Enqueue(a); }

        void Log(string s)
        {
            string line = DateTime.Now.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + s;
            lock (logLock)
            {
                if (!readOnly) try { File.AppendAllText(cfg.LogPath, line + "\r\n", new UTF8Encoding(false)); } catch { }
                Recent.Add(line);
                if (Recent.Count > 600) Recent.RemoveRange(0, Recent.Count - 600);
            }
            var h = OnLog; if (h != null) try { h(line); } catch { }
        }

        Turn ReadTurn(string dir) { return Records.ReadSessionTurn(dir, (long)cfg.Dbl("recordTailBytes", 4194304)); }
        bool SetRestartCutNow(Turn turn)
        {
            return Records.SetRestartCut(turn, cfg.Dbl("restartWindowMinutes", 10), Records.GetKiroStart(),
                Json.StrArr(cfg.At("resume", "limitErrors")));
        }

        SessionState StateOf(string id)
        {
            SessionState st;
            if (!state.TryGetValue(id, out st)) { st = new SessionState(); state[id] = st; }
            return st;
        }

        // A notice: logged, and (when its kind is listed in notify.events) sent to your phone: through the bot once
        // an account has paired with it, else the group's webhook. code says what happened; "done" is an
        // until-done finish, everything else needs you.
        void WriteNotice(string code, string name, string tag, string detail, string id = "")
        {
            Log(name + " : " + tag + " - " + detail);
            if (!string.IsNullOrEmpty(id)) attention[id] = code;
            Act(code == "done" ? "done" : "need." + code, id);
            if (!string.IsNullOrEmpty(id) && code == "outOfCredits") creditWait[id] = DateTime.Now;
            string kind = code == "done" ? "done" : "needsYou";
            if (!Json.StrArr(cfg.At("notify", "events")).Contains(kind)) return;
            string shown = name;
            if (!string.IsNullOrEmpty(id) && remote != null)
                shown = Policy.FormatText(Json.At(Rw(), "numbered"), new Dictionary<string, string> { { "n", GetNumber(id).ToString() } }) + name;
            // through the bot once an account has paired with it, else the group's webhook (SendNotice)
            string err = SendNotice(Notify.Format(cfg, code, shown, detail));
            if (err.Length > 0) Log("notice not sent: " + err);
        }

        void WaitLook(string id, string why, int seconds = 60, string code = "retry")
        {
            Pending pd; if (!pending.TryGetValue(id, out pd)) return;
            pd.Wait = code;
            pd.Fails++;
            if (pd.Fails >= 6)
            {
                pending.Remove(id);
                WriteNotice("cannotSend", Name(id), "NEEDS YOU", why + "; gave up after " + pd.Fails + " tries", id);
                return;
            }
            pd.Due = DateTime.Now.AddSeconds(seconds);
            Log(Name(id) + " : " + why + "; looking again in " + seconds + " s");
        }

        string Name(string id) { string n; return names.TryGetValue(id, out n) ? n : id; }

        // A turn ended a while ago: decide from the record and, only if there is something to send, find the
        // session's tab, show it, check it, type, send, and show your tab again. Port of Invoke-KacLook.
        void Look(string id)
        {
            Pending pd = pending[id];
            var info = Records.GetSessionInfo(pd.Dir);
            if (info == null) { WaitLook(id, "cannot read its session.json"); return; }
            string name = info.Workspace + " | " + info.Title; names[id] = name;
            Turn turn = ReadTurn(pd.Dir);
            SetRestartCutNow(turn);
            if (!turn.Ended) { pending.Remove(id); return; }   // running again

            bool normal = turn.Credits && !turn.Cancelled && !turn.Restart;
            if (normal && creditWait.Count > 0) ResumeCreditWaits(id, name);
            if (pd.CreditsBack) turn.CreditsBack = true;
            SessionState st = StateOf(id);

            Dictionary<string, object> queued = null;
            if (normal) queued = GetQueuedFirst(id);
            Decision d;
            if (queued != null)
                d = new Decision { Action = "send", Why = "a message from your phone was waiting [" + turn.Footer + "]", Kind = "queued", Text = Json.Str(queued["Text"]) };
            else
                d = Policy.Decide(turn, st, cfg);
            if (queued != null) d.Set["Tries"] = 0;

            if (d.Action == "none")
            {
                d.Apply(st); pending.Remove(id); Log(name + " : " + d.Why);
                Act(turn.Cancelled ? "stopped" : "finished", id);
                return;
            }
            if (d.Action == "stop") { d.Apply(st); pending.Remove(id); WriteNotice(d.Code, name, "NEEDS YOU", d.Why, id); return; }
            if (d.Action == "done") { d.Apply(st); pending.Remove(id); WriteNotice("done", name, "DONE", d.Why, id); return; }
            if (d.Action == "wait") { WaitLook(id, d.Why); return; }

            pd.Plan = d.Kind;   // from here on the window shows what is coming and when
            DateTime due = pd.StopAt.AddSeconds(d.After);
            if (due > DateTime.Now) { pd.Due = due; pd.Wait = ""; Log(name + " : " + d.Why + "; will send in " + (int)(due - DateTime.Now).TotalSeconds + " s"); return; }
            if (sent >= cfg.Int("resume.maxPerRun", 200)) { pending.Remove(id); WriteNotice("tooMany", name, "NEEDS YOU", d.Why + "; not sent, " + sent + " messages this run already", id); return; }

            DateTime? ks = Records.GetKiroStart();
            if (ks == null || (DateTime.Now - ks.Value).TotalSeconds < cfg.Int("kiroStartupSeconds", 60))
            {
                if (!pd.ToldKiro) { pd.ToldKiro = true; Log(name + " : " + d.Why + "; waiting for Kiro to start up"); }
                pd.Due = DateTime.Now.AddSeconds(15); pd.Wait = "kiro"; return;
            }
            int idleNeed = d.Kind == "queued" ? cfg.Int("remote.idleSeconds", 15) : cfg.Int("idleSeconds", 180);
            if (Native.IdleSeconds() < idleNeed)
            {
                if (!pd.Told) { pd.Told = true; Log(name + " : " + d.Why + "; will send once keyboard and mouse have been idle for " + idleNeed + " s"); }
                pd.Due = DateTime.Now.AddSeconds(15); pd.Wait = "idle"; return;
            }
            pd.Wait = "";

            var found = KiroUi.FindSessionTab(info);
            if (found.Count != 1) { WaitLook(id, found.Count == 0 ? "its tab is not open in Kiro" : "several tabs have its title", 300, found.Count == 0 ? "notab" : "retry"); return; }
            var root = found.Hit.Root; var tab = found.Hit.Tab;
            if (tab.State == "Working")
            {
                if (!turn.Restart) { pending.Remove(id); return; }
                if (!pd.ToldWorking) { pd.ToldWorking = true; Log(name + " : its tab says Working, but its record shows the turn was lost in the restart"); }
            }
            string locked = KiroUi.Locked() ? " (screen locked)" : "";
            var back = KiroUi.GetTabs(root).FirstOrDefault(t => t.Selected);
            var placeholders = Json.StrArr(cfg.At("placeholders"));
            try
            {
                if (!KiroUi.OpenTab(root, tab.Id, 40)) { WaitLook(id, "could not switch to its tab" + locked); return; }
                string lastUser = turn.Users.Count > 0 ? turn.Users[0] : "";
                if (!KiroUi.WaitShownUser(root, lastUser, 20)) { WaitLook(id, "its tab does not show its last message yet" + locked); return; }
                string err = KiroUi.SendMessage(root, tab.Id, d.Text, placeholders);
                if (err == "draft") { WaitLook(id, "the chat box holds a draft; not touching it", 300, "draft"); return; }
                if (err.Length > 0) { WaitLook(id, "could not send: " + err + locked); return; }
                d.Apply(st); sent++; pending.Remove(id);
                Log(name + " : " + d.Why + "; sent " + d.Kind + locked);
                Act("sent." + d.Kind, id);
                if (d.Kind == "queued")
                {
                    RemoveQueuedFirst(id); attention.Remove(id);
                    SendPush(Json.Str(queued["From"]),
                        Policy.FormatText(Json.At(Rw(), "queueSent"), new Dictionary<string, string> { { "n", GetNumber(id).ToString() }, { "text", Policy.Short(Json.Str(queued["Text"]), 60) } }));
                }
            }
            finally
            {
                // back to your tab, unless you went to another one meanwhile
                if (back != null && back.Id != tab.Id && KiroUi.IsShown(root, tab.Id)) KiroUi.ShowTab(root, back.Id);
            }
        }

        // On start: turns a Kiro restart cut short, from records written since 'since'.
        void AddRestartCuts(DateTime since, DateTime ks)
        {
            foreach (var f in Records.GetSessionFiles().Where(f => f.Modified >= since))
            {
                if (pending.ContainsKey(f.Id)) continue;
                var t = ReadTurn(f.Dir);
                if (!SetRestartCutNow(t)) continue;
                var info = Records.GetSessionInfo(f.Dir);
                if (info == null) continue;
                names[f.Id] = info.Workspace + " | " + info.Title;
                Log(names[f.Id] + " : its last turn was cut short by the restart [" + t.Footer + "]");
                pending[f.Id] = new Pending { Dir = f.Dir, StopAt = ks, Due = DateTime.Now.AddSeconds(cfg.Int("settleSeconds", 20)) };
            }
        }

        // On start: turns cut off (error) in the last 'minutes', which a previous watcher may still have been
        // about to continue. Out of credits is skipped here: AddCreditWaits puts those on the wait for credits.
        void AddRecentCuts(double minutes)
        {
            DateTime since = DateTime.Now.AddMinutes(-minutes);
            var limitErrors = Json.StrArr(cfg.At("resume", "limitErrors"));
            foreach (var f in Records.GetSessionFiles().Where(f => f.Modified >= since))
            {
                if (pending.ContainsKey(f.Id)) continue;
                var t = ReadTurn(f.Dir);
                if (!t.Ended || t.Stop != "error") continue;
                if (t.ErrorType.Length > 0 && limitErrors.Contains(t.ErrorType)) continue;
                var end = Records.FromTime(t.EndedAt);
                if (end == null || end.Value.ToLocalTime() < since) continue;
                var info = Records.GetSessionInfo(f.Dir);
                if (info == null) continue;
                names[f.Id] = info.Workspace + " | " + info.Title;
                Log(names[f.Id] + " : cut off at " + end.Value.ToLocalTime().ToString("HH:mm:ss") + ", before this started [" + t.Footer + "]");
                pending[f.Id] = new Pending { Dir = f.Dir, StopAt = end.Value.ToLocalTime(), Due = DateTime.Now.AddSeconds(cfg.Int("settleSeconds", 20)) };
            }
        }

        // Out of credits: those sessions wait. When another session finishes a turn normally, the credits are
        // back; continue them like a cut-off (CreditsBack lets the decision send).
        void ResumeCreditWaits(string byId, string byName)
        {
            foreach (string w in creditWait.Keys.ToList())
            {
                creditWait.Remove(w);
                if (w == byId || pending.ContainsKey(w)) continue;
                var f = Records.GetSessionFiles().FirstOrDefault(x => x.Id == w);
                if (f == null) continue;
                string label = SessionLabel(f);
                Log(label + " : credits are back (" + byName + " finished a turn); continuing it");
                // no longer "out of credits": the window shows it as about to be continued (11-04: it stayed red,
                // and was counted both as waiting and as needing you)
                attention.Remove(w);
                pending[w] = new Pending { Dir = f.Dir, StopAt = DateTime.Now.AddMinutes(-10), Due = DateTime.Now.AddSeconds(cfg.Int("settleSeconds", 20)), CreditsBack = true };
            }
        }

        // On start: sessions whose last turn ran out of credits in the last 'hours' wait for the credits to come back.
        void AddCreditWaits(double hours)
        {
            DateTime since = DateTime.Now.AddHours(-hours);
            var limitErrors = Json.StrArr(cfg.At("resume", "limitErrors"));
            foreach (var f in Records.GetSessionFiles().Where(f => f.Modified >= since))
            {
                if (pending.ContainsKey(f.Id)) continue;
                var t = ReadTurn(f.Dir);
                if (!t.Ended || t.Stop != "error" || t.ErrorType.Length == 0 || !limitErrors.Contains(t.ErrorType)) continue;
                var info = Records.GetSessionInfo(f.Dir);
                if (info == null) continue;
                names[f.Id] = info.Workspace + " | " + info.Title;
                creditWait[f.Id] = DateTime.Now; attention[f.Id] = "outOfCredits";
                Log(names[f.Id] + " : out of credits before this started; continued once credits are back or Kiro restarts");
            }
        }

        void Run()
        {
            KiroUi.Init();
            StartRemoteIfConfigured();
            startAt = DateTime.Now;
            Log(StartupLine());
            while (!stop)
            {
                try
                {
                    Action job; while (jobs.TryDequeue(out job)) { try { job(); } catch (Exception e) { Log("action error: " + e.Message); } }
                    if (remote != null) PumpRemote();
                    ScanRecords();
                    if (first) FirstPass();
                    CheckKiroRestart();
                    first = false;
                    if ((DateTime.Now - reportAt).TotalSeconds >= 60) { reportAt = DateTime.Now; ReportCheck(); }
                    CheckKeepAwake();
                    if (!paused)
                    {
                        DateTime now = DateTime.Now;
                        string due = pending.Where(kv => kv.Value.Due <= now).Select(kv => kv.Key).FirstOrDefault();
                        if (due != null)
                        {
                            try { Look(due); }
                            catch (Exception e) { Log(Name(due) + " : error while looking at it: " + e.Message); WaitLook(due, "error"); }
                        }
                    }
                    BuildSnapshot();
                }
                catch (Exception e) { Log("error: " + e.Message); }
                DateTime wake = DateTime.Now.AddSeconds(cfg.Int("pollSeconds", 5));
                while (DateTime.Now < wake && !stop && !(remote != null && remote.Inbox.Count > 0)) Thread.Sleep(300);
            }
        }

        void ScanRecords()
        {
            foreach (var f in Records.GetSessionFiles())
            {
                string id = f.Id;
                long had;
                if (!read.TryGetValue(id, out had))
                {
                    if (first || f.Created < startAt) { read[id] = f.Size; continue; }
                    read[id] = 0; had = 0;
                }
                if (f.Size == had) continue;
                var r = Records.ReadLines(f.Messages, had, (long)cfg.Dbl("recordTailBytes", 4194304));
                read[id] = r.End;
                int lastStart = -1, lastEnd = -1; string why = "";
                for (int i = 0; i < r.Lines.Count; i++)
                {
                    string l = r.Lines[i];
                    if (l.Contains("\"type\":\"turn_end\""))
                    {
                        lastEnd = i; var m = RxStop.Match(l); why = m.Success ? m.Groups[1].Value : "";
                        if (l.Contains("\"boundarySource\":\"recovered\"")) why = "lost in a Kiro restart";
                    }
                    else if (l.Contains("\"type\":\"turn_start\"")) lastStart = i;
                }
                if (lastStart > lastEnd) { pending.Remove(id); attention.Remove(id); creditWait.Remove(id); continue; }
                if (lastEnd < 0) continue;
                var info = Records.GetSessionInfo(f.Dir);
                if (info == null) continue;
                names[id] = info.Workspace + " | " + info.Title;
                Log(names[id] + " : turn ended (" + why + ")");
                DateTime now = DateTime.Now;
                pending[id] = new Pending { Dir = f.Dir, StopAt = now, Due = now.AddSeconds(cfg.Int("settleSeconds", 20)) };
            }
        }

        void FirstPass()
        {
            var busy = new List<string>();
            foreach (var f in Records.GetSessionFiles().OrderByDescending(x => x.Modified).Take(30))
            {
                var info = Records.GetSessionInfo(f.Dir);
                if (info == null || info.Status != "in_progress") continue;
                busy.Add(info.Workspace + " | " + info.Title);
            }
            Log(read.Count + " session records; working now: " + (busy.Count > 0 ? string.Join("; ", busy) : "none"));
            if (cfg.Dbl("resume.catchUpMinutes", 60) > 0) AddRecentCuts(cfg.Dbl("resume.catchUpMinutes", 60));
            if (cfg.Dbl("resume.creditWaitHours", 24) > 0) AddCreditWaits(cfg.Dbl("resume.creditWaitHours", 24));
        }

        void CheckKiroRestart()
        {
            DateTime? ks = Records.GetKiroStart();
            if (ks != null && ks.Value != DateTime.MinValue && ks != kiroStart)
            {
                double win = cfg.Dbl("restartWindowMinutes", 10);
                DateTime since = ks.Value.AddMinutes(-win);
                if (kiroStart != null && kiroStart.Value != DateTime.MinValue && kiroStart.Value < since) since = kiroStart.Value;
                if (!first) { Log("Kiro started at " + ks.Value.ToString("HH:mm:ss")); Act("kiro.restart"); }
                if (!first || (DateTime.Now - ks.Value).TotalMinutes <= win) AddRestartCuts(since, ks.Value);
            }
            if (ks != null) kiroStart = ks;
        }

        void CheckKeepAwake()
        {
            if (!cfg.Bool("keepAwake.enabled", true))
            {
                // switched off in the window while it was holding Windows awake: let go now
                if (awake) { Native.KeepAwake(false); awake = false; Log("letting Windows sleep again: keeping awake was switched off"); }
                return;
            }
            if ((DateTime.Now - awakeAt).TotalSeconds < 60) return;
            awakeAt = DateTime.Now;
            bool on = Records.AnyWorking(Records.GetSessionFiles(), cfg.Dbl("keepAwake.minutes", 60));
            if (on != awake)
            {
                bool ok = Native.KeepAwake(on); awake = on;
                if (on) Log("keeping Windows awake: a session is working" + (ok ? "" : " (the request failed)"));
                else Log("letting Windows sleep again: no session has worked for " + cfg.Int("keepAwake.minutes", 60) + " min");
            }
        }

        string StartupLine()
        {
            var flags = new List<string>();
            if (cfg.Bool("keepAwake.enabled", true)) flags.Add("keeps Windows awake while a session works");
            if (Json.Str(cfg.At("remote", "botId")).Length > 0 && Json.Str(cfg.At("remote", "secret")).Length > 0) flags.Add("takes commands from your phone through the WeCom bot");
            if (Json.Str(cfg.At("notify", "webhook")).Length > 0) flags.Add("notices to wecom: " + string.Join(", ", Json.StrArr(cfg.At("notify", "events"))));
            string tail = flags.Count > 0 ? "; " + string.Join("; ", flags) : "";
            return "watching Kiro's session records every " + cfg.Int("pollSeconds", 5) + " s; types only after " +
                cfg.Int("idleSeconds", 180) + " s without keyboard or mouse input; until-done " +
                (cfg.Bool("untilDone.enabled", true) ? "on" : "off") + tail;
        }
    }
}
