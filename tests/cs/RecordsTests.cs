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

            // A question Kiro holds a running turn on (pending_interaction): its own "keep iterating?" after a tool
            // kept failing, and the agent's own question. Answered ones (interaction_resolved) are counted.
            string askR = L("pending_interaction", ",\"toolCallId\":\"failure-intervention-prompt-1793\",\"question\":\"failed 7 times in a row\",\"options\":[{\"title\":\"Cancel\"},{\"title\":\"Keep Iterating\"}]");
            string resR = L("interaction_resolved", ",\"toolCallId\":\"failure-intervention-prompt-1793\",\"selectedOption\":\"Keep Iterating\"");
            string askR2 = L("pending_interaction", ",\"toolCallId\":\"failure-intervention-prompt-1888\",\"question\":\"failed again\",\"options\":[{\"title\":\"Cancel\"},{\"title\":\"Keep Iterating\"}]");
            string askQ = L("pending_interaction", ",\"toolCallId\":\"toolu_bdrk_01ABC\",\"question\":\"Reconnect the VPN?\",\"options\":[{\"title\":\"I reconnected, continue\"},{\"title\":\"Stop\"}]");
            t = Get(u1, start, tool, askR);
            T.Check("open keep-iterating question: running, Ask retry, options and text read", () => !t.Ended && t.Ask == "retry" && t.AskOptions.Contains("Keep Iterating") && t.AskText == "failed 7 times in a row");
            T.Check("open question: nothing pressed yet", () => t.Retries == 0 && t.AskId.EndsWith("1793"));
            t = Get(u1, start, tool, askR, resR, askR2);
            T.Check("answered once, asking again: still retry, one press counted, the new id", () => !t.Ended && t.Ask == "retry" && t.Retries == 1 && t.AskId.EndsWith("1888"));
            t = Get(u1, start, askR, resR, say1, usage, end);
            T.Check("answered then the turn ended: no open question, the press still counted", () => t.Ended && t.Ask == "" && t.Retries == 1);
            t = Get(u1, start, tool, askQ);
            T.Check("the agent's own question: Ask question, not retry", () => !t.Ended && t.Ask == "question" && t.AskOptions.Count == 2);

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

            // The cache the watcher reads through: the turn a fresh read gives, reading only what was written since
            // the last read.
            string sdir = Path.Combine(Path.GetTempPath(), "kac_cache_" + Guid.NewGuid().ToString("N"));
            string rec = Path.Combine(sdir, "messages.jsonl");
            Directory.CreateDirectory(sdir);
            try
            {
                var cache = new Records.TurnCache();
                Func<string> fresh = () => Show(Records.ReadSessionTurn(sdir));
                var c = cache.Read(sdir);
                T.Check("cache: no record: an empty turn, nothing kept", () => !c.Ended && c.Users.Count == 0 && cache.Count == 0);
                File.WriteAllText(rec, u1 + "\n" + start + "\n" + say1 + "\n" + tool + "\n", utf8);
                c = cache.Read(sdir);
                long had = cache.BytesRead;
                T.Check("cache: first read, as a fresh read: running, one tool call", () => Show(c) == fresh() && !c.Ended && c.ToolCalls == 1);
                T.Check("cache: the first read takes a small record whole", () => had == new FileInfo(rec).Length);
                cache.Read(sdir);
                T.Check("cache: nothing new, nothing read", () => cache.BytesRead == had);
                string more = say2 + "\n" + usage + "\n" + end + "\n";
                File.AppendAllText(rec, more, utf8);
                c = cache.Read(sdir);
                T.Check("cache: the turn ended, as a fresh read says", () => Show(c) == fresh() && c.Ended && c.Credits && c.Final.StartsWith("all done"));
                T.Check("cache: only the lines added were read", () => cache.BytesRead - had == utf8.GetByteCount(more));
                string before = Show(c);
                File.AppendAllText(rec, "{\"id\":\"half", utf8);
                c = cache.Read(sdir);
                T.Check("cache: half a line waits for the rest", () => Show(c) == before);
                File.AppendAllText(rec, "\",\"payload\":{\"type\":\"assistant\",\"content\":\"x\",\"operationType\":\"Think\"}}\n" +
                    L("user", ",\"content\":\"second task\"") + "\n" + start + "\n" + tool + "\n", utf8);
                c = cache.Read(sdir);
                T.Check("cache: the rest of the line and a new turn, as a fresh read", () => Show(c) == fresh() && !c.Ended && c.Users.Count == 2 && c.Users[0] == "second task");
                c.Users.Clear(); c.Ended = true; c.Footer = "changed";
                var c2 = cache.Read(sdir);
                T.Check("cache: a new turn each time, so changing one changes nothing kept", () => Show(c2) == fresh() && c2.Users.Count == 2 && !c2.Ended);
                File.AppendAllText(rec, askR + "\n", utf8);
                c = cache.Read(sdir);
                c.AskOptions.Clear();
                c2 = cache.Read(sdir);
                T.Check("cache: a question asked; its options are not shared", () => Show(c2) == fresh() && c2.Ask == "retry" && c2.AskOptions.Contains("Keep Iterating"));
                File.AppendAllText(rec, resR + "\n", utf8);
                c = cache.Read(sdir);
                T.Check("cache: answered: one press counted, as a fresh read", () => Show(c) == fresh() && c.Ask == "" && c.Retries == 1);

                // written anew: shorter, or longer but with other bytes before where the last read ended
                File.WriteAllText(rec, u1 + "\n" + start + "\n" + aborted + "\n" + L("turn_end", ",\"stopReason\":\"cancelled\"") + "\n", utf8);
                c = cache.Read(sdir);
                T.Check("cache: a shorter record is read anew", () => Show(c) == fresh() && c.Cancelled);
                long len = new FileInfo(rec).Length;
                File.WriteAllText(rec, L("user", ",\"content\":\"" + new string('b', (int)len) + "\"") + "\n" + start + "\n" + tool + "\n", utf8);
                c = cache.Read(sdir);
                T.Check("cache: a longer record written anew is read anew", () => Show(c) == fresh() && !c.Ended && c.Users.Count == 1 && c.Users[0].StartsWith("bbb"));

                // Many turns: it keeps the last turn and, before it, the newest user messages up to 30 in all. A user line
                // after its turn_start counts among the 30.
                var many = new List<string>();
                for (int i = 0; i < 40; i++) many.AddRange(new[] { L("user", ",\"content\":\"task " + i + "\""), start, tool, say2, usage, end });
                var cache2 = new Records.TurnCache();
                File.WriteAllText(rec, string.Join("\n", many) + "\n", utf8);
                c = cache2.Read(sdir);
                T.Check("cache: 40 turns, as a fresh read: 30 users, newest first", () => Show(c) == fresh() && c.Users.Count == 30 && c.Users[0] == "task 39");
                T.Check("cache: 40 turns: 30 user lines and the last turn's 5 kept, not " + cache2.Kept(sdir), () => cache2.Kept(sdir) == 35);
                var added = new List<string>();
                for (int i = 40; i < 45; i++) added.AddRange(new[] { L("user", ",\"content\":\"task " + i + "\""), start, tool, say2, usage, end });
                added.AddRange(new[] { start, L("user", ",\"content\":\"late\""), tool });
                File.AppendAllText(rec, string.Join("\n", added) + "\n", utf8);
                c = cache2.Read(sdir);
                T.Check("cache: 6 turns added, a user line inside the last: as a fresh read", () => Show(c) == fresh() && !c.Ended && c.Users[0] == "late" && c.Users[1] == "task 44" && c.Users.Count == 30);
                T.Check("cache: then 29 user lines and the last turn's 3 kept, not " + cache2.Kept(sdir), () => cache2.Kept(sdir) == 32);

                File.Delete(rec);
                c = cache2.Read(sdir);
                T.Check("cache: the record deleted: an empty turn, dropped", () => !c.Ended && c.Users.Count == 0 && cache2.Count == 0);
            }
            finally { try { Directory.Delete(sdir, true); } catch { } }
        }

        // Every field of a turn in one string, to compare a cached read with a fresh one.
        static string Show(Turn t)
        {
            return string.Join("|", new[]
            {
                string.Join("\u0001", t.Users), t.Ended.ToString(), t.Credits.ToString(), t.Cancelled.ToString(), t.Recovered.ToString(),
                t.Restart.ToString(), t.CreditsBack.ToString(), t.Unfinished.ToString(), t.Footer, t.Final, t.Stop, t.ErrorType, t.ErrorText,
                t.ToolCalls.ToString(), t.Items.ToString(), t.StartedAt, t.EndedAt, t.LastAt, t.Ask, t.AskId, t.AskText, t.AskAt,
                string.Join("\u0001", t.AskOptions), t.Retries.ToString()
            });
        }
    }
}
