using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KiroAutoContinue
{
    // Records.cs: made-up messages.jsonl lines in, turns out. The cases of tests\Records.Tests.ps1 (in v0.2), one for one.
    internal static class RecordsTests
    {
        static string L(string type, string rest) { return "{\"id\":\"x\",\"timestamp\":\"t\",\"payload\":{\"type\":\"" + type + "\"" + rest + "}}"; }
        static Turn Get(params string[] lines) { return Records.GetRecordTurn(lines); }
        static string At(string line, string when) { return line.Replace("\"timestamp\":\"t\"", "\"timestamp\":\"" + when + "\""); }
        static DateTime Utc(string s) { return DateTime.SpecifyKind(DateTime.Parse(s, CultureInfo.InvariantCulture), DateTimeKind.Utc); }
        static bool Cut(Turn t, DateTime? kiroStart, IList<string> limits = null) { return Records.SetRestartCut(t, 10, kiroStart, limits); }

        public static void Run()
        {
            string u1 = L("user", ",\"content\":\"first task\\nline two\",\"documents\":[],\"images\":[]");
            string start = L("turn_start", ",\"executionId\":\"a\"");
            string think = L("assistant", ",\"content\":\"thinking it over\",\"operationType\":\"Think\"");
            string tool = L("tool_call", ",\"name\":\"readFile\"");
            string say1 = L("assistant", ",\"content\":\"before the tool\",\"operationType\":\"Say\"");
            string say2 = L("assistant", ",\"content\":\"all done\\n\\u3010x\\u3011\",\"operationType\":\"Say\"");
            string usage = L("usage_summary", ",\"promptTurnSummaries\":[{\"unit\":\"credit\",\"usage\":0.5},{\"unit\":\"credit\",\"usage\":0.25}],\"elapsedTime\":8286,\"status\":\"success\"");
            string end = L("turn_end", ",\"stopReason\":\"end_turn\"");

            var t = Get(u1, start, say1, think, tool, say2, usage, end);
            T.Check("normal turn: ended, credits, not cancelled", () => t.Ended && t.Credits && !t.Cancelled);
            T.Check("final is what it said after the last tool call", () => t.Final == "all done\n" + T.U(0x3010) + "x" + T.U(0x3011));
            T.Check("one tool call", () => t.ToolCalls == 1);
            T.Check("user message with its line break", () => t.Users.Count == 1 && t.Users[0] == "first task\nline two");
            T.Check("footer: " + t.Footer, () => T.Like(t.Footer, "end_turn, success, 0*75 credits, 8s"));

            string failed = L("usage_summary", ",\"promptTurnSummaries\":[],\"elapsedTime\":4123,\"status\":\"failed\"");
            t = Get(u1, start, tool, failed, L("turn_end", ",\"stopReason\":\"error\""));
            T.Check("error turn: ended, no credits", () => t.Ended && !t.Credits && !t.Cancelled && t.Stop == "error");
            string aborted = L("usage_summary", ",\"promptTurnSummaries\":[],\"elapsedTime\":6240,\"status\":\"aborted\"");
            t = Get(u1, start, aborted, L("turn_end", ",\"stopReason\":\"cancelled\""));
            T.Check("cancelled turn: ended, stopped by you", () => t.Ended && t.Cancelled);
            t = Get(u1, start, tool);
            T.Check("running turn: not ended", () => !t.Ended);
            string u2 = L("user", ",\"content\":\"second\",\"images\":[\"" + new string('A', 300000) + "\"]");
            t = Get(u1, start, say1, usage, end, u2, start, say2, usage, end);
            T.Check("two turns: users newest first, the huge line read", () => t.Users.Count == 2 && t.Users[0] == "second" && t.Users[1].StartsWith("first task"));
            T.Check("two turns: the last turn only", () => t.ToolCalls == 0 && t.Final.StartsWith("all done"));
            string synth = L("user", ",\"content\":\"Your previous response used the entire output budget on internal reasoning.\",\"_meta\":{\"kiro\":{\"syntheticUserMessageReason\":\"max-tokens-recovery\"}}");
            t = Get(u1, start, say1, synth, tool, usage, end);
            T.Check("a user line Kiro wrote itself is not yours", () => t.Users.Count == 1 && t.Users[0].StartsWith("first task"));

            // A Kiro restart (11-02, after the credits ran out): Kiro came back at 18:56:11 UTC (02:56:11 local, UTC+8).
            DateTime up = Utc("2026-11-01T18:56:11");
            // The turn_end Kiro writes for a lost turn, when that session is next used: "recovered" and "cancelled".
            string lost = "{\"id\":\"s-e-turn-end-synthetic\",\"timestamp\":\"2026-11-01T18:57:41.550Z\",\"payload\":{\"type\":\"turn_end\",\"boundarySource\":\"recovered\",\"stopReason\":\"cancelled\",\"executionId\":\"e\"}}";
            t = Get(u1, start, tool, lost);
            T.Check("recovered: ended, not stopped by you, no credits, marked", () => t.Ended && !t.Cancelled && !t.Credits && t.Recovered && t.Restart);
            T.Check("recovered: footer " + t.Footer, () => t.Footer == "cancelled, recovered");
            T.Check("recovered: counts whatever Kiro does", () => Cut(t, null) && Cut(t, up));
            T.Check("turn end time read", () => t.EndedAt == "2026-11-01T18:57:41.550Z");
            var when = Records.FromTime(t.EndedAt);
            T.Check("time parsed as UTC", () => when != null && when.Value.Kind == DateTimeKind.Utc && when.Value.Hour == 18 && when.Value.Minute == 57);
            // An error shortly before Kiro restarted counts; one long before, or after Kiro started, does not.
            var cut = new[] { u1, At(start, "2026-11-01T18:41:07.752Z"), At(tool, "2026-11-01T18:50:00.000Z"), At(L("turn_end", ",\"stopReason\":\"error\""), "2026-11-01T18:54:33.268Z") };
            t = Get(cut);
            T.Check("error, Kiro up 98 s later: cut by the restart, marked", () => Cut(t, up) && t.Restart && t.Footer == "error");
            T.Check("error, Kiro started 2 h later: not the restart", () => !Cut(Get(cut), Utc("2026-11-01T20:56:11")));
            T.Check("error, Kiro started before it: not the restart", () => !Cut(Get(cut), Utc("2026-11-01T18:00:00")));
            T.Check("error, Kiro not running: not the restart", () => !Cut(Get(cut), null));
            T.Check("error, Kiro start unreadable: not the restart", () => !Cut(Get(cut), DateTime.MinValue));
            T.Check("error, Kiro start in local time: the same", () => Cut(Get(cut), up.ToLocalTime()));
            // Left open by the restart (no turn_end): counts once Kiro started after its last line, and is then over.
            var open = new[] { u1, At(start, "2026-11-01T18:41:07.752Z"), At(say1, "2026-11-01T18:50:00.000Z"), At(tool, "2026-11-01T18:54:20.000Z") };
            t = Get(open);
            T.Check("open: not ended, last line time read", () => !t.Ended && t.LastAt == "2026-11-01T18:54:20.000Z" && t.StartedAt == "2026-11-01T18:41:07.752Z");
            T.Check("open, Kiro started after its last line: cut by the restart", () => Cut(t, up));
            T.Check("open: now ended, no credits, not stopped by you, footer " + t.Footer, () => t.Ended && t.Restart && !t.Credits && !t.Cancelled && t.Footer == "no turn_end");
            t = Get(open);
            T.Check("open, Kiro started before its last line: still running", () => !Cut(t, Utc("2026-11-01T18:54:00")) && !t.Ended && !t.Restart);
            T.Check("open, Kiro not running: not decided", () => !Cut(Get(open), null));
            T.Check("open for hours (a long command, a sub-agent): still counts", () => Cut(Get(open), Utc("2026-11-02T01:00:00")));
            T.Check("open with no turn at all: not decided", () => !Cut(Get(u1), up));
            // What the chat showed for a failed turn. Those 11-02 errors were the account running out of credits:
            // such an error counts however long before the restart.
            string shown = "{\"id\":\"c\",\"timestamp\":\"2026-11-01T18:54:48.655Z\",\"payload\":{\"type\":\"session_metadata\",\"key\":\"displayError\",\"value\":{\"message\":\"You've reached your overage limit. (Request ID: d)\",\"errorType\":\"OverageLimitReachedError\",\"retryErrorType\":\"CLIENT_ERROR\"},\"executionId\":\"e\"}}";
            string errAt = At(L("turn_end", ",\"stopReason\":\"error\""), "2026-11-01T18:54:48.656Z");
            var limitCut = new[] { u1, At(start, "2026-11-01T18:41:07.752Z"), shown, errAt };
            t = Get(limitCut);
            T.Check("error shown: " + t.ErrorType + " / " + t.ErrorText, () => t.ErrorType == "OverageLimitReachedError" && t.ErrorText == "You've reached your overage limit. (Request ID: d)");
            T.Check("error shown: footer " + t.Footer, () => t.Footer == "error (OverageLimitReachedError)");
            var limits = new List<string> { "UsageLimitReachedError", "OverageLimitReachedError" };
            T.Check("out of credits, accounts switched 9 h later: continue it", () => Cut(Get(limitCut), Utc("2026-11-02T03:56:11"), limits));
            T.Check("out of credits, no list given: only within the window", () => !Cut(Get(limitCut), Utc("2026-11-02T03:56:11")));
            T.Check("out of credits, Kiro not restarted since: no", () => !Cut(Get(limitCut), Utc("2026-11-01T18:00:00"), limits));
            var dropped = new[] { u1, At(start, "2026-11-01T18:41:07.752Z"), shown.Replace("OverageLimitReachedError", "ConnectionResetError"), errAt };
            T.Check("a dropped connection 9 h before a restart: no", () => !Cut(Get(dropped), Utc("2026-11-02T03:56:11"), limits));
            var k = Records.GetKiroStart();
            T.Check("Kiro start: none, or a time", () => k == null || k.Value == DateTime.MinValue || k.Value <= DateTime.Now);
            t = Get(u1, start, tool, usage, end);
            T.Check("a normal finish is never a restart", () => !Cut(t, up) && !t.Restart);
            string quoted = L("user", @",""content"":""say \""hi\"" and \\\""ok\\\"""",""documents"":[]");
            t = Get(quoted, start, end);
            T.Check("escaped quotes: " + (t.Users.Count > 0 ? t.Users[0] : "(none)"), () => t.Users[0] == "say \"hi\" and \\\"ok\\\"");

            // Reading the file: from the start, from where the last read ended, and with a line still being written.
            string tmp = Path.GetTempFileName();
            var utf8 = new UTF8Encoding(false);
            try
            {
                File.WriteAllText(tmp, u1 + "\n" + start + "\n" + say1 + "\n", utf8);
                var r = Records.ReadLines(tmp, 0);
                T.Check("read three lines", () => r.Lines.Count == 3 && r.End == new FileInfo(tmp).Length);
                File.AppendAllText(tmp, tool + "\n" + "{\"id\":\"half", utf8);
                var r2 = Records.ReadLines(tmp, r.End);
                T.Check("next read: the new full line, not the half one", () => r2.Lines.Count == 1 && r2.Lines[0] == tool);
                var r3 = Records.ReadLines(tmp, r2.End);
                T.Check("half a line waits for the rest", () => r3.Lines.Count == 0 && r3.End == r2.End);
                var r4 = Records.ReadLines(tmp, 0, 40);
                T.Check("a tail read drops the cut first line", () => r4.Lines.Count <= 1 && !r4.Lines.Contains(u1));
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }
}
