using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace KiroAutoContinue
{
    // The rest of the watcher: the small state files (numbers, queue, paired accounts, report state), the WeCom
    // command handlers, the morning status, and the hooks the GUI calls. Port of the phone/report parts of
    // kiro-auto-continue.ps1 (in v0.2).
    internal sealed partial class Watcher
    {
        sealed class NewResult { public string Why = ""; public string Id = ""; }

        static string Fmt(object node, string key, Dictionary<string, string> vars) { return Policy.FormatText(Json.At(node, key), vars); }
        static string Why(object rw, string why) { string w = Json.Str(Json.At(rw, "why", why)); return w.Length > 0 ? w : why; }
        static Dictionary<string, string> V(string k, string v) { return new Dictionary<string, string> { { k, v } }; }

        void WriteJsonFile(string path, object value) { try { Config.AtomicWrite(path, Json.Write(value)); } catch { } }

        // --- session numbers (logs\session-numbers.json), the same across runs ---
        Dictionary<string, int> GetNumbers()
        {
            var m = new Dictionary<string, int>();
            try
            {
                var j = Json.Obj(Json.Parse(File.ReadAllText(cfg.NumbersPath, Encoding.UTF8)));
                if (j != null) foreach (var kv in j) m[kv.Key] = Json.Int(kv.Value);
            }
            catch { }
            return m;
        }
        readonly Dictionary<string, int> tempNumbers = new Dictionary<string, int>();   // readOnly: numbers not saved
        int GetNumber(string id)
        {
            var m = GetNumbers();
            foreach (var kv in tempNumbers) if (!m.ContainsKey(kv.Key)) m[kv.Key] = kv.Value;
            int v;
            if (!m.TryGetValue(id, out v))
            {
                int max = 0; foreach (int x in m.Values) if (x > max) max = x;
                v = max + 1; m[id] = v;
                if (readOnly) tempNumbers[id] = v; else WriteJsonFile(cfg.NumbersPath, m);
            }
            return v;
        }
        string FindNumbered(int n) { foreach (var kv in GetNumbers()) if (kv.Value == n) return kv.Key; return null; }
        SessionFile GetNumberedFile(int n)
        {
            string id = FindNumbered(n);
            return id == null ? null : Records.GetSessionFiles().FirstOrDefault(x => x.Id == id);
        }
        string SessionLabel(SessionFile f)
        {
            string n; if (names.TryGetValue(f.Id, out n)) return n;
            var info = Records.GetSessionInfo(f.Dir);
            if (info != null) { n = info.Workspace + " | " + info.Title; names[f.Id] = n; return n; }
            return f.Id;
        }

        // --- who may command the bot (remote.allowFrom + remote-allow.json, kept out of git) ---
        HashSet<string> Allowed()
        {
            var set = new HashSet<string>();
            foreach (string u in Json.StrArr(cfg.At("remote", "allowFrom"))) if (u.Length > 0) set.Add(u);
            try { var j = Json.Parse(File.ReadAllText(cfg.AllowPath, Encoding.UTF8)); foreach (string u in Json.StrArr(Json.At(j, "users"))) if (u.Length > 0) set.Add(u); }
            catch { }
            return set;
        }
        void AddPaired(string user)
        {
            var users = new List<string>();
            try { var j = Json.Parse(File.ReadAllText(cfg.AllowPath, Encoding.UTF8)); foreach (string u in Json.StrArr(Json.At(j, "users"))) if (u.Length > 0) users.Add(u); }
            catch { }
            if (!users.Contains(user)) users.Add(user);
            WriteJsonFile(cfg.AllowPath, new Dictionary<string, object> { { "users", users.ToArray() } });
        }
        public bool HasPaired() { return Allowed().Count > 0; }
        public string PairedWho() { return string.Join(", ", Allowed()); }

        void SendReply(string req, string sid, string content, bool finish = true)
        {
            if (remote != null) remote.Outbox.Enqueue(Remote.ReplyFrame(req, sid, content, finish));
        }
        // A message the bot sends on its own, to 'to', or to every paired account when 'to' is empty.
        void SendPush(string to, string text)
        {
            if (remote == null) return;
            var targets = new List<string>();
            if (!string.IsNullOrEmpty(to)) targets.Add(to); else targets.AddRange(Allowed());
            foreach (string u in targets) remote.Outbox.Enqueue(Remote.PushFrame(u, text));
        }

        // --- the queue of messages waiting for a running session (logs\queue.json) ---
        Dictionary<string, List<Dictionary<string, object>>> GetQueueAll()
        {
            var all = new Dictionary<string, List<Dictionary<string, object>>>();
            try
            {
                var j = Json.Obj(Json.Parse(File.ReadAllText(cfg.QueuePath, Encoding.UTF8)));
                if (j != null) foreach (var kv in j)
                    {
                        var list = new List<Dictionary<string, object>>();
                        foreach (var it in Json.Arr(kv.Value)) { var o = Json.Obj(it); if (o != null) list.Add(o); }
                        all[kv.Key] = list;
                    }
            }
            catch { }
            return all;
        }
        void SaveQueueAll(Dictionary<string, List<Dictionary<string, object>>> all)
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in all) if (kv.Value.Count > 0) o[kv.Key] = kv.Value.Cast<object>().ToArray();
            WriteJsonFile(cfg.QueuePath, o);
        }
        List<Dictionary<string, object>> GetQueued(string id)
        {
            var all = GetQueueAll(); List<Dictionary<string, object>> l;
            return all.TryGetValue(id, out l) ? l : new List<Dictionary<string, object>>();
        }
        Dictionary<string, object> GetQueuedFirst(string id) { var q = GetQueued(id); return q.Count > 0 ? q[0] : null; }
        int AddQueued(string id, string text, string from)
        {
            var all = GetQueueAll(); List<Dictionary<string, object>> l;
            if (!all.TryGetValue(id, out l)) { l = new List<Dictionary<string, object>>(); all[id] = l; }
            l.Add(new Dictionary<string, object> { { "Text", text }, { "From", from }, { "At", DateTime.Now.ToString("o") } });
            SaveQueueAll(all);
            return l.Count;
        }
        void RemoveQueuedFirst(string id)
        {
            var all = GetQueueAll(); List<Dictionary<string, object>> l;
            if (all.TryGetValue(id, out l) && l.Count > 0) { l.RemoveAt(0); SaveQueueAll(all); }
        }
        int ClearQueued(string id)
        {
            var all = GetQueueAll(); List<Dictionary<string, object>> l; int n = 0;
            if (all.TryGetValue(id, out l)) { n = l.Count; all.Remove(id); SaveQueueAll(all); }
            return n;
        }

        // Before typing a command: wait for remote.idleSeconds without input, at most remote.waitSeconds.
        bool WaitRemoteIdle()
        {
            DateTime t0 = DateTime.Now;
            while (Native.IdleSeconds() < cfg.Int("remote.idleSeconds", 15))
            {
                if ((DateTime.Now - t0).TotalSeconds >= cfg.Int("remote.waitSeconds", 60)) return false;
                Thread.Sleep(1000);
            }
            return true;
        }

        // One line per session written in the last 'hours', newest first, with its number and how it stands.
        List<string> StatusLines(object rw, double hours, Report.Counts counts)
        {
            DateTime since = DateTime.Now.AddHours(-hours);
            var files = Records.GetSessionFiles().Where(f => f.Modified >= since)
                .OrderByDescending(f => f.Modified).Take(cfg.Int("remote.maxList", 10)).ToList();
            var lines = new List<string>();
            foreach (var f in files)
            {
                var info = Records.GetSessionInfo(f.Dir); if (info == null) continue;
                var t = ReadTurn(f.Dir); SetRestartCutNow(t);
                string nm = info.Workspace + " | " + info.Title;
                string s = Remote.TurnState(t, rw, DateTime.Now);
                if (pending.ContainsKey(f.Id)) s += Json.Str(Json.At(rw, "pending"));
                string code;
                if (attention.TryGetValue(f.Id, out code) && code != null)
                {
                    string need = Json.Str(Json.At(rw, "need", code));
                    if (need.Length > 0) s += Fmt(rw, "needFormat", V("what", need));
                }
                SessionState st;
                if (state.TryGetValue(f.Id, out st) && st.Mode != null) s += Fmt(rw, "untilDone", V("pushes", st.Mode.Pushes.ToString()));
                int qn = GetQueued(f.Id).Count;
                if (qn > 0) s += Fmt(rw, "queuedAnn", V("k", qn.ToString()));
                if (counts != null) { int rc; if (counts.Resumes.TryGetValue(nm, out rc) && rc > 0) s += Fmt(rw, "countAnn", V("k", rc.ToString())); }
                lines.Add(Fmt(rw, "line", new Dictionary<string, string> { { "n", GetNumber(f.Id).ToString() }, { "name", nm }, { "state", s } }));
            }
            return lines;
        }

        // The credits as Kiro last saw them, in one line ("" before Kiro asked).
        string CreditsLine() { return Credits.Text(Credits.Latest(), Json.At(W(), "credits"), DateTime.Now); }

        string RemoteStatus(object rw)
        {
            double hours = cfg.Dbl("remote.listHours", 24);
            var lines = StatusLines(rw, hours, null);
            string text;
            if (lines.Count == 0) text = Fmt(rw, "none", V("hours", hours.ToString("0.##", CultureInfo.InvariantCulture)));
            else { lines.Add(""); lines.Add(Fmt(rw, "hint", new Dictionary<string, string>())); text = string.Join("\n", lines); }
            string credits = CreditsLine();
            return credits.Length > 0 ? credits + "\n\n" + text : text;
        }

        // waitIdle: false for a click in the window (you are in this window, not typing into Kiro).
        string SendRemoteText(SessionFile f, Turn turn, string text, bool waitIdle = true)
        {
            var info = Records.GetSessionInfo(f.Dir);
            if (info == null) return "cannot read its session.json";
            if (waitIdle && !WaitRemoteIdle()) return "busy";
            var found = KiroUi.FindSessionTab(info);
            if (found.Count == 0) return "noTab";
            if (found.Count > 1) return "twoTabs";
            var root = found.Hit.Root; var tab = found.Hit.Tab;
            if (tab.State == "Working" && !turn.Restart) return "working";
            var back = KiroUi.GetTabs(root).FirstOrDefault(t => t.Selected);
            try
            {
                string why = KiroUi.OpenRemoteTab(root, tab, turn.Ended, turn.Users.Count > 0 ? turn.Users[0] : "");
                if (why.Length > 0) return why;
                string err = KiroUi.SendMessage(root, tab.Id, text, Json.StrArr(cfg.At("placeholders")));
                return err == "draft" ? "draft" : err;
            }
            finally { if (back != null && back.Id != tab.Id && KiroUi.IsShown(root, tab.Id)) KiroUi.ShowTab(root, back.Id); }
        }

        string StopRemoteSession(SessionFile f, Turn turn, bool waitIdle = true)
        {
            var info = Records.GetSessionInfo(f.Dir);
            if (info == null) return "cannot read its session.json";
            if (waitIdle && !WaitRemoteIdle()) return "busy";
            var found = KiroUi.FindSessionTab(info);
            if (found.Count == 0) return "noTab";
            if (found.Count > 1) return "twoTabs";
            var root = found.Hit.Root; var tab = found.Hit.Tab;
            var back = KiroUi.GetTabs(root).FirstOrDefault(t => t.Selected);
            try
            {
                string why = KiroUi.OpenRemoteTab(root, tab, turn.Ended, turn.Users.Count > 0 ? turn.Users[0] : "");
                if (why.Length > 0) return why;
                Thread.Sleep(1000);
                string stop = KiroUi.StopTurn(root, tab.Id);
                // its record is the surer sign: 11-04 a turn stopped at once (cancelled in the record), yet its tab
                // still said Working 20 s later, and the stop was reported as failed
                if (stop == "the tab still says Working" && ReadTurn(f.Dir).Ended) return "";
                return stop;
            }
            finally { if (back != null && back.Id != tab.Id && KiroUi.IsShown(root, tab.Id)) KiroUi.ShowTab(root, back.Id); }
        }

        NewResult NewRemoteSession(string workspace, string text)
        {
            var r = new NewResult();
            if (!WaitRemoteIdle()) { r.Why = "busy"; return r; }
            var win = KiroUi.GetWindows().FirstOrDefault(w => w.Workspace == workspace);
            AutomationElement root = win != null ? KiroUi.GetChatRoot(win) : null;
            if (root == null) { r.Why = "noWindow"; return r; }
            DateTime t0 = DateTime.Now;
            string why = KiroUi.OpenNewSession(root, text, Json.StrArr(cfg.At("placeholders")));
            if (why.Length > 0) { r.Why = why; return r; }
            for (int i = 0; i < 20 && r.Id.Length == 0; i++)
            {
                Thread.Sleep(1000);
                foreach (var f in Records.GetSessionFiles().Where(f => f.Created >= t0.AddSeconds(-5)))
                {
                    var u = ReadTurn(f.Dir).Users;
                    if (u.Count > 0 && KiroUi.SameText(u[0], text)) { r.Id = f.Id; break; }
                }
            }
            return r;
        }

        // "continue N" (text null) and "N <text>".
        void InvokeRemoteSend(string req, string sid, int n, string text, object rw, string from)
        {
            if (n <= 0) { SendReply(req, sid, Json.Str(Json.At(rw, "noNumber"))); return; }
            var vars = V("n", n.ToString());
            var f = GetNumberedFile(n);
            if (f == null) { SendReply(req, sid, Fmt(rw, "noSuch", vars)); return; }
            string id = f.Id, name = SessionLabel(f); bool isSend = text != null;
            var turn = ReadTurn(f.Dir); SetRestartCutNow(turn);
            if (!turn.Ended)
            {
                if (!isSend) { SendReply(req, sid, Fmt(rw, "working", vars)); return; }
                int k = AddQueued(id, text, from);
                Log(name + " : a message from your phone waits for this turn to finish (" + k + " waiting): [" + Policy.Short(text, 60) + "]");
                Act("phone.queue", id);
                SendReply(req, sid, Fmt(rw, "queued", new Dictionary<string, string> { { "n", n.ToString() }, { "k", k.ToString() } }));
                return;
            }
            if (!isSend) text = Policy.ManualContinue(turn, cfg);
            SendReply(req, sid, Fmt(rw, "sending", vars), false);
            string why = SendRemoteText(f, turn, text);
            if (why == "")
            {
                var st = StateOf(id); st.Tries = 0; pending.Remove(id); attention.Remove(id);
                Log(name + " : sent from your phone: [" + Policy.Short(text, 60) + "]");
                Act("phone.send", id);
                SendReply(req, sid, Fmt(rw, "sent", vars));
            }
            else if (why == "working" && isSend)
            {
                int k = AddQueued(id, text, from);
                Log(name + " : a message from your phone waits for this turn to finish (" + k + " waiting): [" + Policy.Short(text, 60) + "]");
                Act("phone.queue", id);
                SendReply(req, sid, Fmt(rw, "queued", new Dictionary<string, string> { { "n", n.ToString() }, { "k", k.ToString() } }));
            }
            else
            {
                Log(name + " : not sent from your phone: " + why);
                SendReply(req, sid, Fmt(rw, "notSent", V("why", Why(rw, why))));
            }
        }

        void InvokeRemoteStop(string req, string sid, int n, object rw)
        {
            if (n <= 0) { SendReply(req, sid, Json.Str(Json.At(rw, "noNumber"))); return; }
            var vars = V("n", n.ToString());
            var f = GetNumberedFile(n);
            if (f == null) { SendReply(req, sid, Fmt(rw, "noSuch", vars)); return; }
            string name = SessionLabel(f);
            var turn = ReadTurn(f.Dir); SetRestartCutNow(turn);
            if (turn.Ended) { SendReply(req, sid, Fmt(rw, "notRunning", vars)); return; }
            SendReply(req, sid, Fmt(rw, "stopping", vars), false);
            string why = StopRemoteSession(f, turn);
            if (why == "") { Log(name + " : stopped from your phone"); Act("phone.stop", f.Id); SendReply(req, sid, Fmt(rw, "stopped", vars)); }
            else { Log(name + " : not stopped from your phone: " + why); SendReply(req, sid, Fmt(rw, "notStopped", V("why", Why(rw, why)))); }
        }

        void InvokeRemoteClear(string req, string sid, int n, object rw)
        {
            if (n <= 0) { SendReply(req, sid, Json.Str(Json.At(rw, "noNumber"))); return; }
            string id = FindNumbered(n);
            int k = id != null ? ClearQueued(id) : 0;
            if (k > 0) { Log("remote: dropped " + k + " message(s) waiting for #" + n); SendReply(req, sid, Fmt(rw, "cleared", new Dictionary<string, string> { { "n", n.ToString() }, { "k", k.ToString() } })); }
            else SendReply(req, sid, Fmt(rw, "nothingQueued", V("n", n.ToString())));
        }

        void InvokeRemoteNew(string req, string sid, string text, object rw)
        {
            var open = KiroUi.GetWindows().Select(w => w.Workspace).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            var c = Remote.SplitNew(text, open);
            if (c.Workspace.Length == 0) { SendReply(req, sid, Fmt(rw, "newWhich", V("list", string.Join(", ", open)))); return; }
            if (c.Text.Length == 0) { SendReply(req, sid, Fmt(rw, "newNoText", V("ws", c.Workspace))); return; }
            SendReply(req, sid, Fmt(rw, "newOpening", V("ws", c.Workspace)), false);
            var r = NewRemoteSession(c.Workspace, c.Text);
            if (r.Why.Length > 0)
            {
                Log("remote: no new session in " + c.Workspace + ": " + r.Why);
                SendReply(req, sid, Fmt(rw, "notOpened", V("why", Why(rw, r.Why))));
                return;
            }
            Log("remote: opened a new session in " + c.Workspace + " from your phone: [" + Policy.Short(c.Text, 60) + "]");
            Act("phone.new", "", c.Workspace);
            if (r.Id.Length > 0) SendReply(req, sid, Fmt(rw, "newOpened", V("n", GetNumber(r.Id).ToString())));
            else SendReply(req, sid, Fmt(rw, "newOpenedNoNumber", V("ws", c.Workspace)));
        }

        void InvokeRemoteFrame(string raw)
        {
            object fr = Json.Parse(raw);
            if (Json.Str(Json.At(fr, "cmd")) != "aibot_msg_callback") return;
            object b = Json.At(fr, "body");
            string req = Json.Str(Json.At(fr, "headers", "req_id"));
            string mid = Json.Str(Json.At(b, "msgid"));
            if (mid.Length > 0)
            {
                if (remoteSeenSet.Contains(mid)) return;
                remoteSeen.Enqueue(mid); remoteSeenSet.Add(mid);
                while (remoteSeen.Count > 200) remoteSeenSet.Remove(remoteSeen.Dequeue());
            }
            string from = Json.Str(Json.At(b, "from", "userid"));
            object rw = Rw();
            string text = Remote.GetRemoteText(b);
            var c = Remote.ParseCommand(text, cfg);
            string sid = Remote.NewReqId("stream");
            if (!Allowed().Contains(from))
            {
                if (c.Kind == "pair" && pairCode != null)
                {
                    if (c.Code == pairCode)
                    {
                        AddPaired(from); pairCode = null;
                        Log("remote: paired with " + from + "; only paired accounts are obeyed");
                        Act("phone.paired", "", from);
                        SendReply(req, sid, Json.Str(Json.At(rw, "paired")));
                    }
                    else
                    {
                        pairTries++;
                        if (pairTries >= 5) { pairCode = null; Log("remote: five wrong pairing codes; pairing is off until this starts again"); }
                        SendReply(req, sid, Json.Str(Json.At(rw, "pairWrong")));
                    }
                    return;
                }
                Log("remote: ignored a message from " + from + ", who has not paired");
                string msg = Json.Str(Json.At(rw, "notAllowed"));
                if (pairCode != null) msg += "\n" + Json.Str(Json.At(rw, "pairHint"));
                SendReply(req, sid, msg);
                return;
            }
            if (text.Length == 0)
            {
                Log("remote: " + from + " sent a " + Json.Str(Json.At(b, "msgtype")) + " message; only text is read");
                SendReply(req, sid, Json.Str(Json.At(rw, "textOnly")));
                return;
            }
            string what = c.Kind; if (c.N > 0) what += " " + c.N; if (c.Kind == "unknown") what += ": [" + Policy.Short(text, 40) + "]";
            Log("remote: " + from + " sent " + what);
            switch (c.Kind)
            {
                case "status": SendReply(req, sid, RemoteStatus(rw)); break;
                case "continue": InvokeRemoteSend(req, sid, c.N, null, rw, from); break;
                case "send": InvokeRemoteSend(req, sid, c.N, c.Text, rw, from); break;
                case "stop": InvokeRemoteStop(req, sid, c.N, rw); break;
                case "clear": InvokeRemoteClear(req, sid, c.N, rw); break;
                case "new": InvokeRemoteNew(req, sid, c.Text, rw); break;
                case "pair": SendReply(req, sid, Json.Str(Json.At(rw, "pairClosed"))); break;
                default: SendReply(req, sid, Policy.FormatText(Json.At(rw, "help"), new Dictionary<string, string>())); break;
            }
        }

        void PumpRemote()
        {
            string line; while (remote.Log.TryDequeue(out line)) Log("remote: " + line);
            string raw;
            for (int i = 0; i < 5 && remote.Inbox.TryDequeue(out raw); i++)
            {
                try { InvokeRemoteFrame(raw); }
                catch (Exception e) { Log("remote: error while handling a message: " + e.Message); }
            }
        }

        void StartRemoteIfConfigured()
        {
            if (Json.Str(cfg.At("remote", "botId")).Length == 0 || Json.Str(cfg.At("remote", "secret")).Length == 0) return;
            remote = new RemoteClient();
            remote.Start(cfg);
            if (Allowed().Count == 0)
            {
                var rb = new byte[4];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rb);
                uint v = BitConverter.ToUInt32(rb, 0) % 1000000;
                pairCode = v.ToString("D6");
                string pw = Json.StrArr(Json.At(Rw(), "cmd", "pair")).FirstOrDefault() ?? "pair";
                Log("remote: no account has paired yet; to pair yours, send \"" + pw + " " + pairCode + "\" to the bot in WeCom");
            }
        }

        // The GUI saved new Bot ID / Secret: drop the old connection and start again with them.
        public void ReloadRemote()
        {
            Post(() =>
            {
                if (remote != null) { remote.StopAndWait(); remote = null; }
                pairCode = null; pairTries = 0;
                StartRemoteIfConfigured();
            });
        }

        // The GUI's "send a test" button (runs on the GUI thread; HTTP only). "" once sent, else why.
        public string TestNotice() { return Notify.Send(cfg, Notify.Format(cfg, "test", Environment.MachineName, "")); }

        // --- the morning status (report.*) ---
        string ReportText()
        {
            object w = W(); object rw = Json.At(w, "remote");
            double hours = cfg.Dbl("report.hours", 12);
            DateTime now = DateTime.Now;
            var lines = new List<string>();
            try { var all = File.ReadAllLines(cfg.LogPath); lines = all.Skip(Math.Max(0, all.Length - 20000)).ToList(); } catch { }
            var counts = Report.LogCounts(lines, now.AddHours(-hours), now);
            var rows = StatusLines(rw, hours, counts);
            if (rows.Count == 0) return null;
            string head = Json.Str(Json.At(w, "notice", "prefix")) + Policy.FormatText(Json.At(rw, "report", "title"), V("hours", hours.ToString("0.##", CultureInfo.InvariantCulture)));
            string sum = Policy.FormatText(Json.At(rw, "report", "summary"), new Dictionary<string, string>
            {
                { "resumes", Report.Total(counts.Resumes).ToString() },
                { "pushes", Report.Total(counts.Pushes).ToString() },
                { "needs", counts.Needs.Count.ToString() }
            });
            var outp = new List<string> { head, sum };
            string credits = CreditsLine();
            if (credits.Length > 0) outp.Add(credits);
            outp.Add("");
            outp.AddRange(rows);
            return string.Join("\n", outp);
        }

        string SendReport(string text) { return SendNotice(text); }

        // A notice or the morning status, where Notify.Route says: the paired accounts through the bot, or the group.
        // "" once sent (for the bot: queued; it goes out as soon as the bot is connected), else why not.
        string SendNotice(string text)
        {
            bool paired = remote != null && Allowed().Count > 0;
            bool hook = Json.Str(cfg.At("notify", "webhook")).Length > 0;
            string route = Notify.Route(paired, remote != null && remote.AuthFailed, hook);
            if (route == "bot") { SendPush("", Notify.LimitUtf8(text, 2000)); return ""; }
            if (route == "group") return Notify.Send(cfg, text);
            if (paired) return "the bot was refused its id and secret, and there is no notify.webhook";
            return "no account paired with the bot and no notify.webhook";
        }

        void ReportCheck()
        {
            DateTime now = DateTime.Now;
            DateTime? due = Report.ReportDue(Json.Str(cfg.At("report", "time")), now);
            if (due == null || now < due.Value || now > due.Value.AddHours(cfg.Dbl("report.windowHours", 3))) return;
            string today = now.ToString("yyyy-MM-dd");
            string last = "";
            try { last = Json.Str(Json.At(Json.Parse(File.ReadAllText(cfg.ReportStatePath, Encoding.UTF8)), "last")); } catch { }
            if (last == today) return;
            WriteJsonFile(cfg.ReportStatePath, new Dictionary<string, object> { { "last", today } });
            string text = ReportText();
            if (text == null) { Log("morning status: no session wrote its record overnight; not sent"); return; }
            string err = SendReport(text);
            Log(err.Length > 0 ? "morning status not sent: " + err : "morning status sent");
        }

        // --- what the GUI reads and calls ---
        void BuildSnapshot()
        {
            object rw = Rw();
            DateTime since = DateTime.Now.AddHours(-cfg.Dbl("remote.listHours", 24));
            var rows = new List<SessionRow>();
            foreach (var f in Records.GetSessionFiles().Where(f => f.Modified >= since)
                .OrderByDescending(f => f.Modified).Take(cfg.Int("remote.maxList", 10)))
            {
                var info = Records.GetSessionInfo(f.Dir); if (info == null) continue;
                var t = ReadTurn(f.Dir); SetRestartCutNow(t);
                int qn = GetQueued(f.Id).Count;
                Pending pd;
                bool planned = pending.TryGetValue(f.Id, out pd) && pd.Plan.Length > 0;
                var row = new SessionRow
                {
                    Id = f.Id,
                    Number = GetNumber(f.Id),
                    Workspace = info.Workspace,
                    Title = info.Title,
                    State = Remote.TurnState(t, rw, DateTime.Now),
                    Kind = !t.Ended ? "running" : t.Restart ? "restart" : (t.Stop == "error" || t.Unfinished) ? "cut" : t.Cancelled ? "stopped" : "done",
                    Running = !t.Ended,
                    Pending = planned,
                    Plan = planned ? pd.Plan : "",
                    Wait = planned ? pd.Wait : "",
                    Due = planned ? pd.Due : DateTime.MinValue,
                    QueuedCount = qn,
                    Queued = qn > 0
                };
                string code;
                if (attention.TryGetValue(f.Id, out code) && code != null)
                {
                    row.Attention = code; row.NeedsYou = code != "done"; row.Need = Json.Str(Json.At(rw, "need", code));
                }
                SessionState st;
                if (state.TryGetValue(f.Id, out st) && st.Mode != null) row.UntilDone = true;
                rows.Add(row);
            }
            LastSnapshot = rows;
            LastCredits = Credits.Latest();
        }

        public void RequestContinue(string id) { Post(() => ManualContinue(id)); }
        public void RequestStop(string id) { Post(() => ManualStop(id)); }

        // A click in the window: no wait for an idle keyboard (you are in this window, not typing into Kiro).
        void ManualContinue(string id)
        {
            var f = Records.GetSessionFiles().FirstOrDefault(x => x.Id == id);
            if (f == null) return;
            string name = SessionLabel(f);
            var turn = ReadTurn(f.Dir); SetRestartCutNow(turn);
            if (!turn.Ended) { Log(name + " : still running; not continuing from the window"); Act("window.notContinued", id, Why(Rw(), "working")); return; }
            string text = Policy.ManualContinue(turn, cfg);
            string why = SendRemoteText(f, turn, text, false);
            if (why == "")
            {
                var st = StateOf(id); st.Tries = 0; pending.Remove(id); attention.Remove(id);
                Log(name + " : continued from the window");
                Act("window.continue", id);
            }
            else { Log(name + " : not continued: " + why); Act("window.notContinued", id, Why(Rw(), why)); }
        }

        void ManualStop(string id)
        {
            var f = Records.GetSessionFiles().FirstOrDefault(x => x.Id == id);
            if (f == null) return;
            string name = SessionLabel(f);
            var turn = ReadTurn(f.Dir); SetRestartCutNow(turn);
            if (turn.Ended) { Log(name + " : not running"); Act("window.notStopped", id, Why(Rw(), "notRunning")); return; }
            string why = StopRemoteSession(f, turn, false);
            if (why == "") { Log(name + " : stopped from the window"); Act("window.stop", id); }
            else { Log(name + " : not stopped: " + why); Act("window.notStopped", id, Why(Rw(), why)); }
        }

        // --preview --texts: what "status" would answer on the phone and what the morning summary would say now,
        // from the real records and logs. Read only; nothing is sent.
        public string PreviewTexts()
        {
            string report = ReportText();
            return "== status ==\n" + RemoteStatus(Rw()) + "\n\n== morning summary ==\n" + (report ??
                "(none: no session wrote its record in the last " + cfg.Dbl("report.hours", 12).ToString("0.##", CultureInfo.InvariantCulture) + " hours)") + "\n";
        }

        // --preview only: one read-only look at the records for the window. With sample, made-up states on the
        // listed sessions and a few made-up recent lines, so every style shows. Nothing is sent or written.
        public void PreviewSeed(bool sample)
        {
            BuildSnapshot();
            if (!sample) return;
            LastCredits = new CreditInfo
            {
                At = DateTime.Now.AddMinutes(-28), Plan = "KIRO PRO", Used = 306.07, Limit = 1000,
                Bonuses = { new CreditBonus { Name = "Trial credits", Status = "ACTIVE", Used = 0, Limit = 500, Trial = true } }
            };
            object rw = Rw();
            var rows = LastSnapshot.ToList();
            for (int i = rows.Count; i < 7; i++)
                rows.Add(new SessionRow { Id = "sample" + i, Number = 90 + i, Workspace = "my_project", Title = "Sample session " + i });
            DateTime now = DateTime.Now;
            Func<string, int, string> st = (k, ago) => Policy.FormatText(Json.At(rw, "state", k),
                new Dictionary<string, string> { { "at", now.AddMinutes(-ago).ToString("HH:mm") } }).Trim();
            Action<SessionRow, string, int> setKind = (r, k, ago) =>
            {
                r.Kind = k; r.Running = k == "running"; r.State = st(k, ago);
                r.Pending = false; r.NeedsYou = false; r.Need = ""; r.Attention = ""; r.QueuedCount = 0; r.Queued = false; r.UntilDone = false;
            };
            setKind(rows[0], "running", 0);
            setKind(rows[1], "cut", 1); rows[1].Pending = true; rows[1].Plan = "resume"; rows[1].Due = now.AddSeconds(130);
            setKind(rows[2], "cut", 14); rows[2].NeedsYou = true; rows[2].Attention = "outOfCredits"; rows[2].Need = Json.Str(Json.At(rw, "need", "outOfCredits"));
            setKind(rows[3], "done", 9); rows[3].Attention = "done"; rows[3].Need = Json.Str(Json.At(rw, "need", "done"));
            setKind(rows[4], "done", 2); rows[4].Pending = true; rows[4].Plan = "push"; rows[4].Wait = "idle"; rows[4].UntilDone = true;
            setKind(rows[5], "running", 0); rows[5].QueuedCount = 1; rows[5].Queued = true; rows[5].UntilDone = true;
            setKind(rows[6], "stopped", 41);
            LastSnapshot = rows;
            Action<string, SessionRow, int, string> add = (k, r, ago, d) => activities.Add(new Activity
            {
                At = now.AddMinutes(-ago), Kind = k, Id = r != null ? r.Id : "", Number = r != null ? r.Number : 0,
                Title = r != null ? r.Title : "", Detail = d ?? ""
            });
            add("paused", null, 95, ""); add("resumed", null, 93, ""); add("kiro.restart", null, 70, "");
            add("finished", rows[3], 52, ""); add("window.continue", rows[6], 41, ""); add("phone.send", rows[5], 30, "");
            add("need.outOfCredits", rows[2], 14, ""); add("done", rows[3], 9, ""); add("sent.push", rows[4], 5, "");
            add("window.notContinued", rows[6], 3, Why(rw, "noTab")); add("sent.resume", rows[1], 1, "");
        }
    }
}
