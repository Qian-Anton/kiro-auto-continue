using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // Policy.cs: made-up turns in, decisions out. The cases of tests\Policy.Tests.ps1, one for one.
    internal static class PolicyTests
    {
        static Config cfg;
        static string TRIG, DONE, STUCK, JIXU, TASK, AUTO_RESUME, AUTO_PUSH, G;

        static Turn MkTurn(IEnumerable<string> users, bool credits = true, string final = "", int tools = 1, bool cancelled = false, bool ended = true)
        {
            return new Turn { Users = users.ToList(), Ended = ended, Credits = credits, Cancelled = cancelled, Footer = "f", Final = final, ToolCalls = tools, Items = 1 };
        }
        static Decision D(Turn t, SessionState st, Config c = null) { return Policy.Decide(t, st, c ?? cfg); }
        static SessionState New() { return new SessionState(); }
        static SessionState Mk(int pushes, int idle)
        {
            return new SessionState { Tries = 0, Mode = new UntilDone { Source = G, Goal = TASK, Pushes = pushes, Verifying = false, Idle = idle } };
        }
        static int SetInt(Decision d, string k) { object v; return d.Set.TryGetValue(k, out v) ? Json.Int(v, -1) : -1; }
        static bool SetsModeOff(Decision d) { return d.Set.ContainsKey("Mode") && d.Set["Mode"] == null; }
        static UntilDone SetMode(Decision d) { return (UntilDone)d.Set["Mode"]; }
        static string W(string lang, string key) { return Policy.FormatText(cfg.At("words", lang, key), new Dictionary<string, string>()); }
        static string FirstW(string lang, string key) { return T.First(cfg.At("words", lang, key)); }

        // Every string under words, except the marks, line by line.
        static IEnumerable<string> Lines(object node)
        {
            if (node is string s) { foreach (string l in s.Split('\n')) yield return l; yield break; }
            var o = Json.Obj(node);
            if (o != null) { foreach (var kv in o) if (kv.Key != "marks") foreach (string l in Lines(kv.Value)) yield return l; yield break; }
            foreach (var x in Json.Arr(node)) foreach (string l in Lines(x)) yield return l;
        }

        public static void Run()
        {
            cfg = T.Defaults();
            TRIG = Json.Str(cfg.At("words", "zh", "marks", "trigger"));
            DONE = Json.Str(cfg.At("words", "zh", "marks", "done"));
            STUCK = Json.Str(cfg.At("words", "zh", "marks", "stuck"));
            JIXU = T.U(0x7EE7, 0x7EED);
            TASK = T.U(0x5199, 0x4E00, 0x4E2A, 0x5C0F, 0x6E38, 0x620F) + " A";
            var prefixes = Json.StrArr(cfg.At("autoPrefixes"));
            AUTO_RESUME = prefixes[0]; AUTO_PUSH = prefixes[1];
            G = TRIG + TASK;
            string restart0 = FirstW("zh", "restart");

            // Plain sessions: finish, interruption, backoff, cap, nothing to quote, your own stop, no footer.
            var st = New(); st.Tries = 2;
            var d = D(MkTurn(new[] { TASK }), st);
            T.Check("finish -> none", () => d.Action == "none");
            T.Check("finish resets tries", () => SetInt(d, "Tries") == 0);
            st = New();
            d = D(MkTurn(new[] { TASK }, false), st);
            T.Check("cut -> send resume", () => d.Action == "send" && d.Kind == "resume");
            T.Check("resume quotes the task", () => d.Text.Contains(TASK));
            T.Check("Chinese task -> Chinese resume", () => d.Text.StartsWith(AUTO_RESUME));
            T.Check("first resume after 60 s", () => d.After == 60);
            d.Apply(st);
            T.Check("tries 1", () => st.Tries == 1);
            d = D(MkTurn(new[] { d.Text, TASK }, false), st);
            T.Check("second resume skips its own message and waits 300 s", () => d.Text.Contains(TASK) && d.After == 300);
            d = D(MkTurn(new[] { TASK }, false), new SessionState { Tries = 3 });
            T.Check("after three continues: stop", () => d.Action == "stop");
            d = D(MkTurn(new[] { JIXU + T.U(0x3002), AUTO_RESUME + "x" }, false), New());
            T.Check("nothing to quote -> resumeNoTask", () => d.Action == "send" && d.Text == W("zh", "resumeNoTask"));
            // A line of only = or - under text makes Kiro's chat render that text as a heading (11-02): no such lines.
            var bad = Lines(cfg.At("words")).Where(l => Regex.IsMatch(l.Trim(), @"^[=\-]{2,}$")).ToList();
            T.Check("no template line is only = or -", () => bad.Count == 0);
            st = new SessionState { Tries = 1, Mode = new UntilDone { Source = "a", Goal = "a", Pushes = 3, Verifying = false, Idle = 0 } };
            d = D(MkTurn(new[] { TASK }, true, "", 1, true), st);
            T.Check("your stop -> none", () => d.Action == "none");
            T.Check("your stop ends until-done", () => SetsModeOff(d));
            // Cut short by a Kiro restart: the restart message, with the same backoff as any cut-off.
            var tr = MkTurn(new[] { TASK }, false); tr.Restart = true;
            d = D(tr, New());
            T.Check("restart -> restart message quoting the task", () => d.Kind == "resume" && d.Text.StartsWith(restart0) && d.Text.Contains(TASK) && d.After == 60);
            tr = MkTurn(new[] { AUTO_RESUME + "x" }, false); tr.Restart = true;
            d = D(tr, New());
            T.Check("restart, nothing to quote -> restartNoTask", () => d.Text == W("zh", "restartNoTask"));
            // Out of credits: no try spent; once Kiro restarts, or the credits are back, it is continued.
            var tl = MkTurn(new[] { TASK }, false); tl.ErrorType = "OverageLimitReachedError";
            d = D(tl, New());
            T.Check("out of credits -> stop, no try spent", () => d.Action == "stop" && d.Why.StartsWith("out of credits") && !d.Set.ContainsKey("Tries"));
            tl.Restart = true;
            d = D(tl, New());
            T.Check("out of credits, then Kiro restarted -> restart message", () => d.Kind == "resume" && d.Text.StartsWith(restart0));
            var tc = MkTurn(new[] { TASK }, false); tc.ErrorType = "ConnectionResetError";
            d = D(tc, New());
            T.Check("a dropped connection -> resume as before", () => d.Kind == "resume" && d.Text.StartsWith(AUTO_RESUME) && !d.Text.StartsWith(restart0));
            var tb = MkTurn(new[] { TASK }, false); tb.ErrorType = "OverageLimitReachedError"; tb.CreditsBack = true;
            d = D(tb, New());
            T.Check("out of credits, then credits back -> the plain continue message", () => d.Kind == "resume" && d.Text.StartsWith(AUTO_RESUME) && d.Text.Contains(TASK) && !d.Text.StartsWith(restart0));
            var cfgT = T.Defaults(); T.SetPath(cfgT, true, "testing", "cancelIsInterrupt");
            d = D(MkTurn(new[] { TASK }, true, "", 1, true), New(), cfgT);
            T.Check("testing: your stop counts as a cut", () => d.Kind == "resume");
            d = D(MkTurn(new[] { TASK }, false, "", 1, false, false), New());
            T.Check("no footer -> wait", () => d.Action == "wait");

            // Until-done: contract, pushes, goal every fifth push, verify on done, then stop.
            st = New();
            d = D(MkTurn(new[] { G }, true, "shall I go on?"), st);
            T.Check("trigger + early stop -> contract", () => d.Kind == "contract");
            T.Check("contract carries the goal without the mark", () => d.Text.Contains(TASK) && !d.Text.Contains(TRIG));
            T.Check("contract names both marks", () => d.Text.Contains(DONE) && d.Text.Contains(STUCK));
            d.Apply(st);
            T.Check("pushes 1", () => st.Mode != null && st.Mode.Pushes == 1);
            var users = new List<string> { d.Text, G }; var kinds = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                d = D(MkTurn(users, true, "report", 2), st);
                kinds.Add(d.Kind); d.Apply(st); users.Insert(0, d.Text);
            }
            T.Check("push kinds " + string.Join(",", kinds), () => string.Join(",", kinds) == "push,push,push,push,pushGoal");
            d = D(MkTurn(users, true, "all good\n" + DONE, 3), st);
            T.Check("done -> verify", () => d.Kind == "verify");
            d.Apply(st); users.Insert(0, d.Text);
            d = D(MkTurn(users, true, "checked\n" + DONE, 1), st);
            T.Check("done again -> done", () => d.Action == "done");
            d.Apply(st);
            T.Check("until-done off after done", () => st.Mode == null);

            // Until-done: stuck, idle pushes, the cap, a new message from you, a restart of this tool.
            d = D(MkTurn(new[] { G }, true, "need a key\n" + STUCK + " API key"), New());
            T.Check("stuck -> stop with the reason", () => d.Action == "stop" && d.Why.Contains("API key"));
            d = D(MkTurn(new[] { AUTO_PUSH + "x", G }, true, "ok", 0), Mk(4, 2));
            T.Check("third push without work -> stop", () => d.Action == "stop");
            d = D(MkTurn(new[] { AUTO_PUSH + "x", G }, true, "ok", 5), Mk(4, 2));
            T.Check("a turn with work -> push, idle back to 0", () => d.Action == "send" && SetMode(d).Idle == 0);
            d = D(MkTurn(new[] { AUTO_PUSH + "x", G }, true, "ok", 5), Mk(30, 0));
            T.Check("30 pushes -> stop", () => d.Action == "stop");
            d = D(MkTurn(new[] { "new instructions", AUTO_PUSH + "x", G }, true, "ok", 2), Mk(3, 0));
            T.Check("a new message from you ends until-done", () => d.Action == "none" && SetsModeOff(d));
            d = D(MkTurn(new[] { AUTO_PUSH + "b", AUTO_PUSH + "a", G }, true, "report", 2), New());
            T.Check("after a restart: count earlier pushes, no second contract", () => d.Kind == "push" && SetMode(d).Pushes == 3);

            // The code each stop and done carries (the notice's headline), and the words for every notice in both languages.
            var codes = new Dictionary<string, string>();
            codes["gaveUp"] = D(MkTurn(new[] { TASK }, false), new SessionState { Tries = 3 }).Code;
            var t9 = MkTurn(new[] { TASK }, false); t9.ErrorType = "UsageLimitReachedError";
            codes["outOfCredits"] = D(t9, New()).Code;
            codes["stuck"] = D(MkTurn(new[] { G }, true, "x\n" + STUCK + " y"), New()).Code;
            codes["idle"] = D(MkTurn(new[] { AUTO_PUSH + "x", G }, true, "ok", 0), Mk(4, 2)).Code;
            codes["maxPushes"] = D(MkTurn(new[] { AUTO_PUSH + "x", G }, true, "ok", 5), Mk(30, 0)).Code;
            var vs = new SessionState { Tries = 0, Mode = new UntilDone { Source = G, Goal = TASK, Pushes = 2, Verifying = true, Idle = 0 } };
            codes["done"] = D(MkTurn(new[] { AUTO_PUSH + "v", G }, true, "checked\n" + DONE, 1), vs).Code;
            foreach (var kv in codes) T.Check("code " + kv.Key + ": " + kv.Value, () => kv.Value == kv.Key);
            foreach (string lang in new[] { "zh", "en" })
                foreach (string k in codes.Keys.Concat(new[] { "cannotSend", "tooMany", "test", "prefix" }))
                    T.Check("notice words " + lang + "." + k, () => Json.Str(cfg.At("words", lang, "notice", k)).Length > 0);

            // Language, marks only on the last lines, bare continues.
            d = D(MkTurn(new[] { "please refactor the parser" }, false), New());
            T.Check("English task -> English resume", () => d.Text.StartsWith("(auto-continue)"));
            d = D(MkTurn(new[] { "[until-done] refactor the parser" }, true, "shall I?"), New());
            T.Check("English trigger -> English contract", () => d.Kind == "contract" && d.Text.Contains("[DONE]") && d.Text.Contains("Goal: refactor the parser"));
            d = D(MkTurn(new[] { G }, true, "I will write " + DONE + " at the end\nline 2\nline 3", 2), New());
            T.Check("a mark above the last two lines does not count", () => d.Kind == "contract");
            T.Check("bare continue with punctuation", () => Policy.IsBare(JIXU + T.U(0x3002), cfg));
            T.Check("a real message is not bare", () => !Policy.IsBare(TASK, cfg));
            var cfgU = T.Defaults(); T.SetPath(cfgU, false, "untilDone", "enabled");
            T.Check("until-done off in config -> none", () => D(MkTurn(new[] { G }, true, "shall I?"), New(), cfgU).Action == "none");
        }
    }
}
