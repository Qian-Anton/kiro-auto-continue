using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // Remote.cs and the phone's "continue" in Policy.cs: commands, frames, the status words. Connects to nothing.
    // The cases of tests\Remote.Tests.ps1, one for one.
    internal static class RemoteTests
    {
        static Config cfg;
        static Remote.Command Cmd(string t) { return Remote.ParseCommand(t, cfg); }
        static object Body(string json) { return Json.Parse(json); }
        static string Shortest(object node) { return Json.StrArr(node).OrderBy(x => x.Length).First(); }
        static string Longest(object node) { return Json.StrArr(node).OrderByDescending(x => x.Length).First(); }
        static string Fmt(object tpl, params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return Policy.FormatText(tpl, d);
        }

        public static void Run()
        {
            cfg = T.Defaults();
            object zc = cfg.At("words", "zh", "remote", "cmd");
            string ST = T.First(Json.At(zc, "status")), HE = T.First(Json.At(zc, "help")), SE = T.First(Json.At(zc, "send")), PA = T.First(Json.At(zc, "pair"));
            string HAO = T.First(Json.At(zc, "number"));
            string CO = Shortest(Json.At(zc, "continue")), COL = Longest(Json.At(zc, "continue"));
            string WORK = T.U(0x5199, 0x5B8C, 0x6D4B, 0x8BD5);
            string QF = T.U(0xFF1F);
            Remote.Command c;

            // Commands, Chinese and English, with the forms a phone keyboard gives.
            c = Cmd(ST); T.Check("status", () => c.Kind == "status");
            c = Cmd(ST + QF); T.Check("status with a full-width question mark", () => c.Kind == "status");
            c = Cmd("STATUS"); T.Check("status in English, any case", () => c.Kind == "status");
            c = Cmd("?"); T.Check("a question mark alone", () => c.Kind == "status");
            c = Cmd(HE); T.Check("help", () => c.Kind == "help");
            c = Cmd(CO + " 2"); T.Check("continue 2", () => c.Kind == "continue" && c.N == 2);
            c = Cmd(CO + "2" + HAO); T.Check("continue with the number word, no space", () => c.Kind == "continue" && c.N == 2);
            c = Cmd(COL + " 12"); T.Check("the long word for continue", () => c.Kind == "continue" && c.N == 12);
            c = Cmd(COL); T.Check("continue without a number", () => c.Kind == "continue" && c.N == 0);
            c = Cmd("c 3"); T.Check("c 3", () => c.Kind == "continue" && c.N == 3);
            c = Cmd("continue 4"); T.Check("continue 4", () => c.Kind == "continue" && c.N == 4);
            c = Cmd("2 " + WORK); T.Check("2 and a task: " + c.Kind + " " + c.N + " [" + c.Text + "]", () => c.Kind == "send" && c.N == 2 && c.Text == WORK);
            c = Cmd("2" + HAO + WORK); T.Check("number word, then the text without a space", () => c.Kind == "send" && c.N == 2 && c.Text == WORK);
            c = Cmd("2" + HAO + " " + WORK); T.Check("number word and a space", () => c.Kind == "send" && c.N == 2 && c.Text == WORK);
            c = Cmd("3" + T.U(0xFF1A) + WORK); T.Check("a full-width colon", () => c.Kind == "send" && c.N == 3 && c.Text == WORK);
            c = Cmd(SE + " 5 " + WORK); T.Check("the send word first", () => c.Kind == "send" && c.N == 5 && c.Text == WORK);
            c = Cmd("send 6 run the tests"); T.Check("send 6 in English", () => c.Kind == "send" && c.N == 6 && c.Text == "run the tests");
            c = Cmd("12: fix it"); T.Check("12: fix it", () => c.Kind == "send" && c.N == 12 && c.Text == "fix it");
            c = Cmd("2" + WORK); T.Check("a digit glued to text is not a command", () => c.Kind == "unknown");
            c = Cmd("23"); T.Check("a number alone is not a command", () => c.Kind == "unknown");
            c = Cmd(PA + " 123456"); T.Check("pair", () => c.Kind == "pair" && c.Code == "123456");
            c = Cmd("pair 0042"); T.Check("pair in English keeps leading zeros", () => c.Kind == "pair" && c.Code == "0042");
            c = Cmd("hello"); T.Check("anything else: unknown", () => c.Kind == "unknown");
            c = Cmd(""); T.Check("empty: unknown", () => c.Kind == "unknown");

            // What arrives: text with an @mention in front, full-width digits, a voice transcript, an image.
            object b = Body("{\"msgtype\":\"text\",\"text\":{\"content\":\"@kiro " + ST + "\"}}");
            T.Check("the @mention is dropped", () => Remote.GetRemoteText(b) == ST);
            b = Body("{\"msgtype\":\"text\",\"text\":{\"content\":\"" + T.U(0xFF12) + " " + WORK + "\"}}");
            string txt = Remote.GetRemoteText(b);
            T.Check("full-width digit made plain: " + txt, () => txt == "2 " + WORK && Cmd(txt).Kind == "send");
            b = Body("{\"msgtype\":\"voice\",\"voice\":{\"content\":\"" + ST + "\"}}");
            T.Check("a voice message: its transcript", () => Remote.GetRemoteText(b) == ST);
            b = Body("{\"msgtype\":\"image\",\"image\":{\"url\":\"x\"}}");
            T.Check("an image: no text", () => Remote.GetRemoteText(b) == "");

            // Frames.
            object j = Json.Parse(Remote.ReplyFrame("req1", "stream_1", "line 1\n" + WORK, true));
            T.Check("reply frame: respond to the callback, a finished stream", () => Json.Str(Json.At(j, "cmd")) == "aibot_respond_msg"
                && Json.Str(Json.At(j, "headers", "req_id")) == "req1" && Json.Str(Json.At(j, "body", "msgtype")) == "stream"
                && Json.Str(Json.At(j, "body", "stream", "id")) == "stream_1" && Json.At(j, "body", "stream", "finish") is bool f1 && f1);
            T.Check("reply frame: text, line break and Chinese kept", () => Json.Str(Json.At(j, "body", "stream", "content")) == "line 1\n" + WORK);
            j = Json.Parse(Remote.ReplyFrame("req1", "stream_1", "wait", false));
            T.Check("reply frame: an unfinished stream", () => Json.At(j, "body", "stream", "finish") is bool f2 && !f2);
            T.Check("req ids: prefix, milliseconds, 8 hex digits", () => Regex.IsMatch(Remote.NewReqId("ping"), "^ping_\\d{13}_[0-9a-f]{8}$"));

            // How a turn stands.
            object rw = cfg.At("words", "zh", "remote");
            Func<bool, string, bool, bool, string, Turn> TT = (ended, stop, cancelled, restart, at) =>
                new Turn { Ended = ended, Stop = stop, Cancelled = cancelled, Restart = restart, EndedAt = at };
            var now = new DateTime(2026, 11, 2, 16, 0, 0);
            string today = new DateTime(2026, 11, 2, 14, 5, 0).ToUniversalTime().ToString("o");
            string yesterday = new DateTime(2026, 11, 1, 23, 10, 0).ToUniversalTime().ToString("o");
            T.Check("running", () => Remote.TurnState(TT(false, "", false, false, ""), rw, now) == Json.Str(Json.At(rw, "state", "running")));
            string s = Remote.TurnState(TT(true, "end_turn", false, false, today), rw, now);
            T.Check("finished today: " + s, () => s == Fmt(Json.At(rw, "state", "done"), "at", "14:05"));
            s = Remote.TurnState(TT(true, "error", false, false, yesterday), rw, now);
            T.Check("cut off yesterday: " + s, () => s == Fmt(Json.At(rw, "state", "cut"), "at", "11-01 23:10"));
            s = Remote.TurnState(TT(true, "cancelled", true, false, today), rw, now);
            T.Check("stopped by you", () => s == Fmt(Json.At(rw, "state", "stopped"), "at", "14:05"));
            s = Remote.TurnState(TT(true, "error", false, true, today), rw, now);
            T.Check("cut short by a restart", () => s == Json.Str(Json.At(rw, "state", "restart")));
            s = Fmt(Json.At(rw, "line"), "n", "3", "name", "ws | t", "state", "x");
            T.Check("a status line: " + s, () => s.StartsWith("3") && s.Contains("ws | t") && s.EndsWith("x"));

            // "continue N" from the phone.
            string TASK = T.U(0x5199, 0x4E00, 0x4E2A, 0x5C0F, 0x6E38, 0x620F) + " A";
            Func<string, bool, bool, bool, bool, Turn> T2 = (user, ended, credits, cancelled, restart) =>
                new Turn { Users = new List<string> { user }, Ended = ended, Credits = credits, Cancelled = cancelled, Restart = restart };
            string zhCont = Json.Str(cfg.At("words", "zh", "remote", "continueText"));
            string x = Policy.ManualContinue(T2(TASK, true, false, false, false), cfg);
            T.Check("after a cut-off: the continue message quoting the task", () => x.StartsWith(T.First(cfg.At("words", "zh", "resume"))) && x.Contains(TASK));
            x = Policy.ManualContinue(T2(TASK, true, false, false, true), cfg);
            T.Check("after a restart: the restart message", () => x.StartsWith(T.First(cfg.At("words", "zh", "restart"))) && x.Contains(TASK));
            x = Policy.ManualContinue(T2(TASK, true, true, false, false), cfg);
            T.Check("after a normal finish: a plain continue", () => x == zhCont);
            x = Policy.ManualContinue(T2(TASK, true, true, true, false), cfg);
            T.Check("after your own stop: a plain continue", () => x == zhCont);
            T.Check("while it runs: nothing", () => Policy.ManualContinue(T2(TASK, false, false, false, false), cfg) == null);
            x = Policy.ManualContinue(T2("please refactor the parser", true, true, false, false), cfg);
            T.Check("an English task: an English continue", () => x == Json.Str(cfg.At("words", "en", "remote", "continueText")));
            T.Check("a plain continue is not quoted as a task later", () => Policy.IsBare(zhCont, cfg));

            // Stop, clear, and new sessions.
            string STP = Shortest(Json.At(zc, "stop")), CLR = Shortest(Json.At(zc, "clear")), NEW = Shortest(Json.At(zc, "new"));
            c = Cmd(STP + " 2"); T.Check("stop 2", () => c.Kind == "stop" && c.N == 2);
            c = Cmd(STP + "3" + HAO); T.Check("stop with the number word", () => c.Kind == "stop" && c.N == 3);
            c = Cmd("stop 4"); T.Check("stop 4 in English", () => c.Kind == "stop" && c.N == 4);
            c = Cmd(CLR + " 2"); T.Check("clear 2", () => c.Kind == "clear" && c.N == 2);
            c = Cmd(NEW + " my_project " + WORK); T.Check("new, with a workspace", () => c.Kind == "new" && c.Text == "my_project " + WORK);
            c = Cmd(NEW + T.U(0xFF1A) + WORK); T.Check("new, after a full-width colon", () => c.Kind == "new" && c.Text == WORK);
            c = Cmd(NEW + WORK); T.Check("the word glued to text is not a new session", () => c.Kind == "unknown");
            c = Cmd("new proj fix the build"); T.Check("new in English", () => c.Kind == "new" && c.Text == "proj fix the build");
            var ws2 = new List<string> { "my_project", "website" };
            var sp = Remote.SplitNew("my_project " + WORK, ws2); T.Check("workspace by name", () => sp.Workspace == "my_project" && sp.Text == WORK);
            sp = Remote.SplitNew("web " + WORK, ws2); T.Check("workspace by the start of its name", () => sp.Workspace == "website" && sp.Text == WORK);
            sp = Remote.SplitNew("MY_PROJECT fix it", ws2); T.Check("workspace in any case", () => sp.Workspace == "my_project" && sp.Text == "fix it");
            sp = Remote.SplitNew(WORK, new List<string> { "my_project" }); T.Check("one window: the workspace may be left out", () => sp.Workspace == "my_project" && sp.Text == WORK);
            sp = Remote.SplitNew(WORK, ws2); T.Check("two windows and no name: cannot tell", () => sp.Workspace == "");
            sp = Remote.SplitNew("my_project", ws2); T.Check("a name and no text", () => sp.Workspace == "my_project" && sp.Text == "");

            // Mixed messages, and a message the bot sends on its own.
            b = Body("{\"msgtype\":\"mixed\",\"mixed\":{\"msg_item\":[{\"msgtype\":\"text\",\"text\":{\"content\":\"" + ST + "\"}},{\"msgtype\":\"image\",\"image\":{\"url\":\"x\"}}]}}");
            T.Check("a mixed message: its text", () => Remote.GetRemoteText(b) == ST);
            j = Json.Parse(Remote.PushFrame("zhangsan", "a\n" + WORK));
            T.Check("push frame: to the user, as markdown", () => Json.Str(Json.At(j, "cmd")) == "aibot_send_msg"
                && Json.Str(Json.At(j, "headers", "req_id")).StartsWith("aibot_send_msg_") && Json.Str(Json.At(j, "body", "chatid")) == "zhangsan"
                && Json.Str(Json.At(j, "body", "msgtype")) == "markdown" && Json.Str(Json.At(j, "body", "markdown", "content")) == "a\n" + WORK);

            // Every word the watcher uses, in both languages.
            var keys = new[] { "help", "numbered", "line", "hint", "none", "pending", "untilDone", "needFormat", "continueText", "noNumber", "noSuch",
                "working", "sending", "sent", "notSent", "notAllowed", "pairHint", "paired", "pairWrong", "pairClosed", "textOnly", "queuedAnn",
                "queued", "queueSent", "cleared", "nothingQueued", "stopping", "stopped", "notRunning", "notStopped", "newOpening", "newOpened",
                "newOpenedNoNumber", "newWhich", "newNoText", "notOpened", "countAnn" };
            foreach (string lang in new[] { "zh", "en" })
            {
                object r = cfg.At("words", lang, "remote");
                Func<object, bool> has = n => n != null && (n is string str ? str.Length > 0 : Json.Arr(n).Count > 0 || Json.Obj(n) != null);
                var missing = keys.Where(k => !has(Json.At(r, k))).ToList();
                foreach (string k in new[] { "running", "done", "cut", "stopped", "restart" }) if (!has(Json.At(r, "state", k))) missing.Add("state." + k);
                foreach (string k in new[] { "outOfCredits", "gaveUp", "cannotSend", "tooMany", "stuck", "idle", "maxPushes", "done" }) if (!has(Json.At(r, "need", k))) missing.Add("need." + k);
                foreach (string k in new[] { "busy", "noTab", "twoTabs", "notShown", "draft", "working", "noCancel", "noWindow", "noButton", "newTab", "notReady" })
                    if (!has(Json.At(r, "why", k))) missing.Add("why." + k);
                foreach (string k in new[] { "status", "help", "continue", "stop", "clear", "new", "send", "pair" }) if (Json.StrArr(Json.At(r, "cmd", k)).Count == 0) missing.Add("cmd." + k);
                foreach (string k in new[] { "title", "summary" }) if (!has(Json.At(r, "report", k))) missing.Add("report." + k);
                T.Check("remote words " + lang + " complete: " + string.Join(", ", missing), () => missing.Count == 0);
            }
        }
    }
}
