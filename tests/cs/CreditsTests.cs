using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // The credits line (Credits.cs): Kiro's answers to GetUsageLimits as its q-client.log has them (the shapes are
    // copied from real log lines, ids replaced), the newest across logs, and the words.
    internal static class CreditsTests
    {
        static string Line(string at, string level, string body)
        {
            return at + " [" + level + "] {\"clientName\":\"CodeWhispererRuntimeClient\",\"commandName\":\"GetUsageLimitsCommand\"," +
                "\"input\":{\"profileArn\":\"arn:aws:x\",\"origin\":\"AI_EDITOR\",\"resourceType\":\"AGENTIC_REQUEST\",\"isEmailRequired\":true}," +
                body + ",\"metadata\":{\"httpStatusCode\":200,\"requestId\":\"r1\",\"attempts\":1,\"totalRetryDelay\":0}}";
        }

        static string Answer(string plan, string used, string limit, string bonuses = "", string overages = "0")
        {
            return "\"output\":{\"nextDateReset\":\"2026-12-01T00:00:00.000Z\",\"usageBreakdownList\":[{\"currentUsage\":" + Math.Floor(double.Parse(used, System.Globalization.CultureInfo.InvariantCulture)) +
                ",\"currentOverages\":0,\"usageLimit\":" + limit + ",\"overageCharges\":0,\"currency\":\"USD\",\"resourceType\":\"CREDIT\",\"displayName\":\"Credit\"," +
                "\"displayNamePlural\":\"Credits\",\"currentUsageWithPrecision\":" + used + ",\"currentOveragesWithPrecision\":" + overages + ",\"usageLimitWithPrecision\":" + limit +
                ",\"unit\":\"INVOCATIONS\",\"overageRate\":0.04,\"nextDateReset\":\"2026-12-01T00:00:00.000Z\",\"overageCap\":10000,\"overageCapWithPrecision\":10000," +
                "\"bonuses\":[" + bonuses + "],\"overageCredits\":[]}]," +
                "\"subscriptionInfo\":{\"type\":\"Q_DEVELOPER_STANDALONE_FREE\",\"upgradeCapability\":\"UPGRADE_CAPABLE\",\"overageCapability\":\"OVERAGE_INCAPABLE\"," +
                "\"subscriptionManagementTarget\":\"MANAGE\",\"subscriptionTitle\":\"" + plan + "\"},\"overageConfiguration\":{\"overageStatus\":\"DISABLED\"}," +
                "\"userInfo\":{\"userId\":\"u1\",\"email\":\"***SensitiveInformation***\"}}";
        }

        static string Bonus(string name, string status, int used, int limit)
        {
            return "{\"bonusCode\":\"code#0\",\"displayName\":\"" + name + "\",\"description\":\"" + name + " from your first upgrade\",\"status\":\"" + status +
                "\",\"expiresAt\":\"2027-11-04T08:40:34.626Z\",\"currentUsage\":" + used + ",\"usageLimit\":" + limit + ",\"redeemedAt\":\"2026-11-04T08:41:01.661Z\"}";
        }

        static readonly string Trial = Bonus("Trial credits", "ACTIVE", 0, 500);
        static readonly string Free = Line("2026-11-04 21:45:38.052", "info", Answer("KIRO FREE", "50", "50", Trial));
        static readonly string Pro = Line("2026-11-04 10:35:04.487", "info", Answer("KIRO PRO", "306.07", "917", Trial));
        static readonly string Refused = "2026-11-04 10:47:10.399 [error] {\"clientName\":\"CodeWhispererRuntimeClient\",\"commandName\":\"GetUsageLimitsCommand\"," +
            "\"input\":{\"profileArn\":\"arn:aws:x\",\"origin\":\"AI_EDITOR\",\"resourceType\":\"AGENTIC_REQUEST\"},\"error\":{\"errno\":-4077,\"code\":\"ECONNRESET\"," +
            "\"syscall\":\"read\",\"$metadata\":{\"attempts\":3,\"totalRetryDelay\":133}},\"metadata\":{\"attempts\":3,\"totalRetryDelay\":133}}";
        static readonly string Empty = Line("2026-11-04 22:08:58.523", "info", "\"output\":{}");
        static readonly string Mcp = "2026-11-04 22:35:57.794 [info] {\"clientName\":\"CodeWhispererStreamingClient\",\"commandName\":\"InvokeMCPCommand\"," +
            "\"input\":{\"id\":\"tools_list\",\"method\":\"tools/list\",\"jsonrpc\":\"2.0\"},\"output\":{\"id\":\"tools_list\",\"jsonrpc\":\"2.0\",\"result\":\"***SensitiveInformation***\"},\"metadata\":{\"httpStatusCode\":200}}";

        public static void Run()
        {
            var cfg = T.Defaults();
            object zh = cfg.At("words", "zh", "credits"), en = cfg.At("words", "en", "credits");
            DateTime day = new DateTime(2026, 11, 4, 23, 10, 0);

            // reading Kiro's answers
            var f = Credits.ParseLine(Free);
            T.Check("an answer: the time Kiro asked", () => f != null && f.At == new DateTime(2026, 11, 4, 21, 45, 38, 52));
            T.Check("an answer: plan, this month's credits", () => f.Plan == "KIRO FREE" && f.Used == 50 && f.Limit == 50 && f.Overage == 0);
            T.Check("an answer: the trial credits", () => f.Bonuses.Count == 1 && f.Bonuses[0].Trial && f.Bonuses[0].Used == 0 && f.Bonuses[0].Limit == 500 && f.Bonuses[0].Status == "ACTIVE");
            var p = Credits.ParseLine(Pro);
            T.Check("an answer: the precise numbers, not the rounded ones", () => p != null && p.Used == 306.07 && p.Limit == 917);
            T.Check("a failed request (an error, no output) is not an answer", () => Credits.ParseLine(Refused) == null);
            T.Check("an empty answer (output {}) is not an answer", () => Credits.ParseLine(Empty) == null);
            T.Check("another command is not an answer", () => Credits.ParseLine(Mcp) == null);
            T.Check("a broken or cut line is not an answer, and does not throw", () => Credits.ParseLine(Free.Substring(0, Free.Length / 2)) == null
                && Credits.ParseLine("") == null && Credits.ParseLine(null) == null && Credits.ParseLine("GetUsageLimitsCommand \"output\":") == null);
            T.Check("the newest answer in a log, past the empty and the refused ones", () =>
            {
                var c = Credits.NewestIn(new[] { Pro, Refused, Free, Empty, Mcp });
                return c != null && c.At.Hour == 21 && c.Plan == "KIRO FREE";
            });
            T.Check("no answer in a log", () => Credits.NewestIn(new[] { Refused, Empty, Mcp }) == null);

            // the line
            string enFree = Credits.Text(f, en, day);
            T.Check("en: " + enFree, () => enFree == "Credits (Kiro checked at 21:45): Kiro Free, 50/50 this month, trial 0/500");
            // "credits (Kiro 21:45 checked): Kiro Free, this month 50/50, trial 0/500", in Chinese (code points: ASCII source)
            string zhWant = T.U(0x989D, 0x5EA6, 0xFF08) + "Kiro 21:45 " + T.U(0x67E5, 0x7684, 0xFF09, 0xFF1A) + "Kiro Free" + T.U(0xFF0C, 0x672C, 0x6708) +
                " 50/50" + T.U(0xFF0C, 0x8BD5, 0x7528) + " 0/500";
            T.Check("zh: the same line in Chinese", () => Credits.Text(f, zh, day) == zhWant);
            T.Check("an answer from another day keeps its date", () => Credits.Text(f, en, day.AddDays(1)).StartsWith("Credits (Kiro checked at 11-04 21:45): "));
            T.Check("precise numbers, thousands with commas", () => Credits.Text(p, en, day) == "Credits (Kiro checked at 10:35): Kiro Pro, 306.07/917 this month, trial 0/500"
                && Credits.N(1234.5) == "1,234.5" && Credits.N(10000) == "10,000" && Credits.N(47.68400213598673) == "47.68" && Credits.N(0) == "0");
            var mix = Credits.ParseLine(Line("2026-11-04 09:00:00.000", "info", Answer("KIRO PRO+", "2000", "2000",
                Bonus("Trial credits", "EXHAUSTED", 500, 500) + "," + Bonus("Old credits", "EXPIRED", 0, 100) + "," + Bonus("Welcome credits", "ACTIVE", 20, 250), "37.5")));
            string enMix = Credits.Text(mix, en, day);
            T.Check("used-up bonus shown, expired one not, others as bonus, overage when there is some: " + enMix,
                () => enMix == "Credits (Kiro checked at 09:00): Kiro Pro+, 2,000/2,000 this month, trial 500/500, bonus 20/250, 37.5 over the limit");
            var noPlan = Credits.ParseLine(Free.Replace("\"subscriptionTitle\":\"KIRO FREE\"", "\"subscriptionTitle\":\"\""));
            T.Check("no plan name: the line goes without it", () => Credits.Text(noPlan, en, day) == "Credits (Kiro checked at 21:45): 50/50 this month, trial 0/500");
            T.Check("plan names: capitals as the status bar has them, others as they are",
                () => Credits.PlanName("KIRO FREE") == "Kiro Free" && Credits.PlanName("KIRO PRO+") == "Kiro Pro+" && Credits.PlanName("Kiro Power") == "Kiro Power" && Credits.PlanName(null) == "");
            T.Check("nothing to show: no line", () => Credits.Text(null, en, day) == "" && Credits.Text(f, null, day) == "");

            // the window's card: one bar per pool, its figures, its colour, and how long ago Kiro asked
            var fr = Credits.Rows(f);
            T.Check("card: this month, then the trial", () => fr.Count == 2 && fr[0].Kind == "month" && fr[1].Kind == "trial"
                && Credits.Value(fr[0], en) == "50 / 50" && Credits.Value(fr[1], en) == "0 / 500");
            T.Check("card: used up is full, untouched is ok", () => fr[0].Fraction == 1 && fr[0].Tone == "full" && fr[1].Fraction == 0 && fr[1].Tone == "ok");
            var mr = Credits.Rows(mix);
            T.Check("card: used-up bonus, other bonus, no expired one, the overage last and barless: " + string.Join(",", mr.Select(r => r.Kind + "=" + Credits.Value(r, en))),
                () => string.Join(",", mr.Select(r => r.Kind + "=" + Credits.Value(r, en) + ":" + r.Tone)) == "month=2,000 / 2,000:full,trial=500 / 500:full,bonus=20 / 250:ok,over=37.5:full");
            T.Check("card: amber from 80 % on", () => new CreditRow { Kind = "month", Used = 40, Limit = 50 }.Tone == "warn"
                && new CreditRow { Kind = "month", Used = 39.9, Limit = 50 }.Tone == "ok" && new CreditRow { Kind = "month", Used = 60, Limit = 50 }.Fraction == 1);
            T.Check("card: nothing from Kiro, no bars", () => Credits.Rows(null).Count == 0 && Credits.Checked(null, en, day) == "");
            T.Check("how long ago: just now, minutes, hours, days", () => Credits.Ago(day.AddSeconds(-30), day, en) == "just now"
                && Credits.Ago(day.AddMinutes(-12.7), day, en) == "12 min ago" && Credits.Ago(day.AddMinutes(-154), day, en) == "2 h ago"
                && Credits.Ago(day.AddDays(-3), day, en) == "3 days ago" && Credits.Ago(day.AddMinutes(5), day, en) == "just now");
            T.Check("how long ago, in Chinese", () => Credits.Ago(day.AddMinutes(-12), day, zh) == "12 " + T.U(0x5206, 0x949F, 0x524D));
            T.Check("card corner: " + Credits.Checked(f, en, day), () => Credits.Checked(f, en, day) == "Kiro checked at 21:45 \u00B7 1 h ago"
                && Credits.Checked(f, en, day.AddDays(1)) == "Kiro checked at 11-04 21:45 \u00B7 25 h ago");

            // the words: the same keys and {placeholders} in both languages
            var zk = Json.Obj(zh) ?? new Dictionary<string, object>(); var ek = Json.Obj(en) ?? new Dictionary<string, object>();
            var wanted = new[] { "line", "plan", "trial", "bonus", "over", "title", "monthRow", "trialRow", "bonusRow", "overRow", "value", "checked", "agoNow", "agoMin", "agoHour", "agoDay" };
            var missing = wanted.Where(k => Json.Str(Json.At(zh, k)).Length == 0 || Json.Str(Json.At(en, k)).Length == 0).ToList();
            T.Check("words: every one the line and the card use, in both languages: " + string.Join(", ", missing), () => missing.Count == 0 && zk.Count == ek.Count);
            Func<string, string> holes = s => string.Join(",", Regex.Matches(s ?? "", @"\{(\w+)\}").Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal));
            var bad = zk.Keys.Where(k => holes(Json.Str(zk[k])) != holes(Json.Str(Json.At(en, k)))).ToList();
            T.Check("words: the same {placeholders} in both languages: " + string.Join(", ", bad), () => bad.Count == 0);

            // the newest answer across Kiro's logs: logs\<start>\window<N>\exthost\kiro.kiroAgent\q-client.log
            string root = Path.Combine(Path.GetTempPath(), "kac_credits_" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                Func<string, string, string> log = (run, win) =>
                {
                    string d = Path.Combine(root, run, win, "exthost", "kiro.kiroAgent");
                    Directory.CreateDirectory(d);
                    return Path.Combine(d, "q-client.log");
                };
                T.Check("no logs folder: nothing", () => Credits.Latest(Path.Combine(root, "none")) == null);
                string a = log("20261104T105113", "window1"), b = log("20261104T105113", "window2"), c = log("20261104T183551", "window1");
                File.WriteAllLines(a, new[] { Mcp, Pro, Free });                       // 21:45, in the older run's folder
                File.WriteAllLines(b, new[] { Line("2026-11-04 12:04:25.192", "info", Answer("KIRO FREE", "50", "50", Trial)) });
                File.WriteAllLines(c, new[] { Refused, Empty });                       // a newer run that has no answer yet
                Directory.CreateDirectory(Path.Combine(root, "20261104T183551", "window2"));   // a window with no log
                var got = Credits.Latest(root);
                T.Check("the newest answer of all, by when Kiro asked, not by folder", () => got != null && got.At == f.At);
                // Kiro keeps writing: a log open for writing is still read, and read again once it grew
                using (var w = new FileStream(c, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(Line("2026-11-04 23:01:02.003", "info", Answer("KIRO PRO", "12.5", "1000")) + "\n");
                    w.Write(bytes, 0, bytes.Length); w.Flush();
                    var again = Credits.Latest(root);
                    T.Check("a log Kiro holds open is read, and read again once it grew", () => again != null && again.At.Hour == 23 && again.Plan == "KIRO PRO" && again.Used == 12.5 && again.Bonuses.Count == 0);
                }
                T.Check("the newer answer reads without bonuses", () => Credits.Text(Credits.Latest(root), en, day) == "Credits (Kiro checked at 23:01): Kiro Pro, 12.5/1,000 this month");
                File.Delete(c);
                T.Check("a log that is gone no longer counts", () => { var x = Credits.Latest(root); return x != null && x.At == f.At; });
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
