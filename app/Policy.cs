using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // An until-done run's state for one session.
    internal sealed class UntilDone
    {
        public string Source = "", Goal = "";
        public int Pushes, Idle;
        public bool Verifying;
        public UntilDone Clone()
        {
            return new UntilDone { Source = Source, Goal = Goal, Pushes = Pushes, Idle = Idle, Verifying = Verifying };
        }
    }

    // What the watcher remembers about a session between looks.
    internal sealed class SessionState
    {
        public int Tries;
        public UntilDone Mode;   // null: not in until-done
    }

    // What to do about a session's last turn. Set holds the state changes to apply (keys "Tries", "Mode"); a
    // present "Mode" with a null value means leave until-done.
    internal sealed class Decision
    {
        public string Action, Why, Kind = "", Text = "", Code = "";
        public int After;
        public Dictionary<string, object> Set = new Dictionary<string, object>();

        public void Apply(SessionState st)
        {
            object v;
            if (Set.TryGetValue("Tries", out v)) st.Tries = Json.Int(v);
            if (Set.TryGetValue("Mode", out v)) st.Mode = v as UntilDone;
        }
    }

    // What to do when a session stops. No UI here. Port of src/Policy.ps1 (in v0.2); every word the agent sees lives in
    // config.default.json.
    internal static class Policy
    {
        public static string Short(string s, int n = 60)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }

        // Messages this tool sent start with one of autoPrefixes.
        public static bool IsAuto(string text, Config cfg)
        {
            string t = (text ?? "").TrimStart();
            foreach (string p in Json.StrArr(cfg.At("autoPrefixes")))
                if (p.Length > 0 && t.StartsWith(p)) return true;
            return false;
        }

        static string StripWP(string s) { return Regex.Replace(s ?? "", @"[\s\p{P}]", "").ToLowerInvariant(); }

        // A bare "continue" (or an empty message) carries no task.
        public static bool IsBare(string text, Config cfg)
        {
            string t = StripWP(text);
            if (t.Length == 0) return true;
            foreach (string b in Json.StrArr(cfg.At("bareContinue"))) if (t == StripWP(b)) return true;
            return false;
        }

        // Index in users (newest first) of the newest message that is neither ours nor a bare continue; -1 if none.
        public static int FindTaskIndex(IList<string> users, Config cfg)
        {
            for (int i = 0; i < users.Count; i++)
            {
                string u = users[i] ?? "";
                if (!IsAuto(u, cfg) && !IsBare(u, cfg)) return i;
            }
            return -1;
        }

        // All marks of one kind (trigger, done, stuck) across languages.
        public static List<string> Marks(Config cfg, string kind)
        {
            var outp = new List<string>();
            var words = Json.Obj(cfg.At("words"));
            if (words != null)
                foreach (var kv in words)
                {
                    string m = Json.Str(Json.At(kv.Value, "marks", kind));
                    if (m.Length > 0) outp.Add(m);
                }
            return outp;
        }

        public static string FindMark(string text, List<string> marks)
        {
            foreach (string m in marks) if (m.Length > 0 && (text ?? "").Contains(m)) return m;
            return null;
        }

        // A template (a string or an array of lines); {name} is replaced from vars.
        public static string FormatText(object tpl, Dictionary<string, string> vars)
        {
            string s = tpl is string str ? str : string.Join("\n", Json.StrArr(tpl));
            foreach (var kv in vars) s = s.Replace("{" + kv.Key + "}", kv.Value ?? "");
            return s;
        }

        // The last n non-empty lines of a text.
        public static string Tail(string text, int n = 2)
        {
            var lines = new List<string>();
            foreach (string l in Regex.Split(text ?? "", "\r?\n")) if (l.Trim().Length > 0) lines.Add(l);
            if (lines.Count == 0) return "";
            int from = Math.Max(0, lines.Count - n);
            return string.Join("\n", lines.GetRange(from, lines.Count - from));
        }

        static Decision Dec(string action, string why, string code = "")
        {
            return new Decision { Action = action, Why = why, Code = code };
        }

        public static Decision Decide(Turn turn, SessionState st, Config cfg)
        {
            object ud = cfg.At("untilDone");
            if (!turn.Ended) return Dec("wait", "the last turn has no footer yet");

            bool cancelIsCut = Json.Bool(cfg.At("testing", "cancelIsInterrupt"));
            if (turn.Cancelled && !cancelIsCut)
            {
                var d = Dec("none", "stopped by you [" + turn.Footer + "]");
                d.Set["Tries"] = 0; d.Set["Mode"] = null;
                return d;
            }

            var users = turn.Users;
            int ti = FindTaskIndex(users, cfg);
            string task = ti >= 0 ? users[ti] : null;
            string lastUser = users.Count > 0 ? users[0] : "";
            string langFrom = task ?? lastUser;
            object w = cfg.Words(cfg.Lang(langFrom));
            var vars = new Dictionary<string, string>
            {
                { "task", task ?? "" },
                { "goal", "" },
                { "done", Json.Str(Json.At(w, "marks", "done")) },
                { "stuck", Json.Str(Json.At(w, "marks", "stuck")) }
            };

            // Interrupted: the record says error, or a Kiro restart cut the turn short. A cut-off can show credits in
            // the chat footer too; the number of credits plays no part here.
            if (!turn.Credits || turn.Cancelled)
            {
                var limitErrors = Json.StrArr(cfg.At("resume", "limitErrors"));
                if (!turn.Restart && !turn.CreditsBack && turn.ErrorType.Length > 0 && limitErrors.Contains(turn.ErrorType))
                    return Dec("stop", "out of credits [" + turn.Footer + "]; it continues once the credits are back or Kiro restarts", "outOfCredits");

                int n = st.Tries;
                if (n >= Json.Int(cfg.At("resume", "maxTries")))
                    return Dec("stop", "interrupted again after " + n + " continues [" + turn.Footer + "]", "gaveUp");

                var delays = Json.Arr(cfg.At("resume", "delaysSeconds"));
                int after = delays.Count > 0 ? Json.Int(delays[Math.Min(n, delays.Count - 1)]) : 60;
                object tpl = task != null ? Json.At(w, "resume") : Json.At(w, "resumeNoTask");
                string what = "interrupted";
                if (turn.Restart)
                {
                    tpl = task != null ? Json.At(w, "restart") : Json.At(w, "restartNoTask");
                    what = "cut short by a Kiro restart";
                }
                var d = Dec("send", what + " [" + turn.Footer + "]");
                d.Set["Tries"] = n + 1; d.Kind = "resume"; d.Text = FormatText(tpl, vars); d.After = after;
                return d;
            }

            // Finished normally. Until-done starts when your newest real message carries the trigger mark.
            var set = new Dictionary<string, object> { { "Tries", 0 } };
            UntilDone mode = st.Mode;
            if (mode != null && task != null && task != mode.Source) { mode = null; set["Mode"] = null; }
            if (mode == null)
            {
                var trig = Marks(cfg, "trigger");
                bool enabled = Json.Bool(Json.At(ud, "enabled"), true);
                if (!enabled || task == null || FindMark(task, trig) == null)
                {
                    var d = Dec("none", "finished [" + turn.Footer + "]"); d.Set = set; return d;
                }
                string goal = task; foreach (string m in trig) goal = goal.Replace(m, "");
                int pushed = 0; for (int i = 0; i < ti; i++) if (IsAuto(users[i], cfg)) pushed++;
                mode = new UntilDone { Source = task, Goal = goal.Trim(), Pushes = pushed, Verifying = false, Idle = 0 };
            }
            vars["goal"] = mode.Goal;

            string tail = Tail(turn.Final, 2);
            string stuck = FindMark(tail, Marks(cfg, "stuck"));
            if (stuck != null)
            {
                set["Mode"] = null;
                string why = tail.Substring(tail.IndexOf(stuck) + stuck.Length).Trim();
                var d = Dec("stop", "until-done: stuck, needs you: " + Short(why, 160), "stuck"); d.Set = set; return d;
            }
            if (FindMark(tail, Marks(cfg, "done")) != null)
            {
                if (Json.Bool(Json.At(ud, "verifyOnDone"), true) && !mode.Verifying)
                {
                    var m2 = mode.Clone(); m2.Verifying = true; m2.Pushes = mode.Pushes + 1; set["Mode"] = m2;
                    var d = Dec("send", "until-done: says done; asking for one check against the goal");
                    d.Set = set; d.Kind = "verify"; d.Text = FormatText(Json.At(w, "verify"), vars); return d;
                }
                set["Mode"] = null;
                var dd = Dec("done", "until-done: done after " + mode.Pushes + " pushes", "done"); dd.Set = set; return dd;
            }

            // Stopped without a mark (a question, "shall I go on?", a half-way report): push, unless it stopped helping.
            int idle = 0;
            bool noWork = turn.ToolCalls == 0 && (turn.Final ?? "").Length < Json.Int(Json.At(ud, "idleChars"));
            if (IsAuto(lastUser, cfg) && noWork) idle = mode.Idle + 1;
            if (idle >= Json.Int(Json.At(ud, "maxIdlePushes")))
            {
                set["Mode"] = null;
                var d = Dec("stop", "until-done: " + idle + " pushes in a row did no work", "idle"); d.Set = set; return d;
            }
            int maxPushes = Json.Int(Json.At(ud, "maxPushes"));
            if (mode.Pushes >= maxPushes)
            {
                set["Mode"] = null;
                var d = Dec("stop", "until-done: reached " + maxPushes + " pushes", "maxPushes"); d.Set = set; return d;
            }
            string kind = "push";
            int goalEvery = Json.Int(Json.At(ud, "goalEvery"));
            if (mode.Pushes == 0) kind = "contract";
            else if (goalEvery > 0 && mode.Pushes % goalEvery == 0) kind = "pushGoal";
            var m3 = mode.Clone(); m3.Pushes = mode.Pushes + 1; m3.Idle = idle; set["Mode"] = m3;
            var dp = Dec("send", "until-done: stopped without a mark, push " + m3.Pushes + "/" + maxPushes);
            dp.Set = set; dp.Kind = kind; dp.Text = FormatText(Json.At(w, kind), vars);
            return dp;
        }

        // What "continue N" from your phone types.
        public static string ManualContinue(Turn turn, Config cfg)
        {
            if (!turn.Ended) return null;
            var users = turn.Users;
            int ti = FindTaskIndex(users, cfg);
            string task = ti >= 0 ? users[ti] : null;
            string langFrom = users.Count > 0 ? users[0] : "";
            if (task != null) langFrom = task;
            object w = cfg.Words(cfg.Lang(langFrom));
            var vars = new Dictionary<string, string> { { "task", task ?? "" } };
            if (turn.Restart)
                return FormatText(task != null ? Json.At(w, "restart") : Json.At(w, "restartNoTask"), vars);
            if (!turn.Credits && !turn.Cancelled)
                return FormatText(task != null ? Json.At(w, "resume") : Json.At(w, "resumeNoTask"), vars);
            return Json.Str(Json.At(w, "remote", "continueText"));
        }
    }
}
