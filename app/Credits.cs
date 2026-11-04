using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KiroAutoContinue
{
    // A pool of credits on top of the plan's, such as trial credits.
    internal sealed class CreditBonus
    {
        public string Name = "", Status = "";
        public double Used, Limit;
        public bool Trial;
    }

    // One bar of the window's credits card: this month's plan credits, a bonus pool, or the overage.
    internal sealed class CreditRow
    {
        public string Kind = "";            // "month", "trial", "bonus" or "over"
        public double Used, Limit;          // the overage has no limit, so no bar
        public double Fraction { get { return Limit > 0 ? Math.Min(1, Math.Max(0, Used / Limit)) : (Used > 0 ? 1 : 0); } }
        // "ok"; "warn" from 80 % on; "full" once used up (and the overage)
        public string Tone { get { double f = Fraction; return Kind == "over" || f >= 1 ? "full" : f >= 0.8 ? "warn" : "ok"; } }
    }

    // The credits as Kiro last saw them: one answer to its GetUsageLimits request.
    internal sealed class CreditInfo
    {
        public DateTime At;                 // when Kiro asked (local time, from its log line)
        public string Plan = "";            // as Kiro names it, e.g. "KIRO FREE"
        public double Used, Limit;          // the plan's credits this month
        public double Overage;              // credits used past the limit (when overage is on)
        public List<CreditBonus> Bonuses = new List<CreditBonus>();
    }

    // Kiro writes every answer about the account's credits into its client log,
    //   %APPDATA%\Kiro\logs\<start time>\window<N>\exthost\kiro.kiroAgent\q-client.log
    // as a line "yyyy-MM-dd HH:mm:ss.fff [info] {json}" with "commandName":"GetUsageLimitsCommand". The newest one
    // is what Kiro's status bar shows. This reads it (read only: no login token, no network) and words it as Kiro
    // gave it, with the time Kiro asked. No estimate on top of Kiro's numbers: they do not always agree with the
    // credits the session records show. Nothing to show when Kiro has not asked yet, or once Kiro writes its log
    // differently; the rest of the exe does not depend on it.
    internal static class Credits
    {
        public static readonly string LogsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kiro", "logs");

        const string Marker = "\"commandName\":\"GetUsageLimitsCommand\"";

        // One log line; null unless it is an answer to GetUsageLimits with the numbers in it (a failed request has
        // an "error" and no "output").
        public static CreditInfo ParseLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf(Marker, StringComparison.Ordinal) < 0) return null;
            if (line.IndexOf("\"output\":", StringComparison.Ordinal) < 0) return null;
            DateTime at;
            if (line.Length < 23 || !DateTime.TryParseExact(line.Substring(0, 23), "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out at)) return null;
            int brace = line.IndexOf('{');
            if (brace < 0) return null;
            object j;
            try { j = Json.Parse(line.Substring(brace)); } catch { return null; }
            object o = Json.At(j, "output");
            if (Json.Obj(o) == null) return null;
            object b = null;
            foreach (object x in Json.Arr(Json.At(o, "usageBreakdownList")))
            {
                if (Json.Obj(x) == null) continue;
                if (b == null) b = x;
                if (Json.Str(Json.At(x, "resourceType")) == "CREDIT") { b = x; break; }
            }
            if (b == null) return null;
            var c = new CreditInfo
            {
                At = at,
                Plan = Json.Str(Json.At(o, "subscriptionInfo", "subscriptionTitle")),
                Used = Num(b, "currentUsageWithPrecision", "currentUsage"),
                Limit = Num(b, "usageLimitWithPrecision", "usageLimit"),
                Overage = Num(b, "currentOveragesWithPrecision", "currentOverages")
            };
            foreach (object x in Json.Arr(Json.At(b, "bonuses")))
            {
                if (Json.Obj(x) == null) continue;
                string name = Json.Str(Json.At(x, "displayName"));
                c.Bonuses.Add(new CreditBonus
                {
                    Name = name,
                    Status = Json.Str(Json.At(x, "status")),
                    Used = Num(x, "currentUsageWithPrecision", "currentUsage"),
                    Limit = Num(x, "usageLimitWithPrecision", "usageLimit"),
                    Trial = (name + " " + Json.Str(Json.At(x, "description"))).IndexOf("trial", StringComparison.OrdinalIgnoreCase) >= 0
                });
            }
            return c;
        }

        static double Num(object node, string precise, string plain)
        {
            return Json.Has(node, precise) ? Json.Dbl(Json.At(node, precise)) : Json.Dbl(Json.At(node, plain));
        }

        // The newest answer among some log lines; null if there is none.
        public static CreditInfo NewestIn(IEnumerable<string> lines)
        {
            CreditInfo best = null;
            foreach (string l in lines)
            {
                var c = ParseLine(l);
                if (c != null && (best == null || c.At >= best.At)) best = c;
            }
            return best;
        }

        // Kiro keeps its logs open while it runs: read them sharing every access.
        static IEnumerable<string> ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                string l;
                var lines = new List<string>();
                while ((l = r.ReadLine()) != null) lines.Add(l);
                return lines;
            }
        }

        sealed class Seen { public long Length; public DateTime Written; public CreditInfo Info; }
        static readonly Dictionary<string, Seen> seen = new Dictionary<string, Seen>(StringComparer.OrdinalIgnoreCase);
        static readonly object gate = new object();

        // The newest answer in all of Kiro's client logs (root: the logs folder; a test passes its own). A log is
        // read again only when its size or time changed, so this is cheap to call every few seconds.
        public static CreditInfo Latest(string root = null)
        {
            root = root ?? LogsRoot;
            var files = new List<string>();
            try
            {
                if (Directory.Exists(root))
                    foreach (string run in Directory.GetDirectories(root))
                        foreach (string win in Directory.GetDirectories(run, "window*"))
                        {
                            string f = Path.Combine(win, "exthost", "kiro.kiroAgent", "q-client.log");
                            if (File.Exists(f)) files.Add(f);
                        }
            }
            catch { }
            lock (gate)
            {
                CreditInfo best = null;
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    keep.Add(f);
                    Seen s;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (!seen.TryGetValue(f, out s) || s.Length != fi.Length || s.Written != fi.LastWriteTimeUtc)
                        {
                            s = new Seen { Length = fi.Length, Written = fi.LastWriteTimeUtc, Info = NewestIn(ReadShared(f)) };
                            seen[f] = s;
                        }
                    }
                    catch { continue; }
                    if (s.Info != null && (best == null || s.Info.At > best.At)) best = s.Info;
                }
                foreach (string gone in new List<string>(seen.Keys)) if (!keep.Contains(gone)) seen.Remove(gone);
                return best;
            }
        }

        static bool Expired(CreditBonus bn) { return bn.Status.IndexOf("EXPIRED", StringComparison.OrdinalIgnoreCase) >= 0; }

        // When Kiro asked: today's time as HH:mm, an older one with its date.
        public static string AtText(CreditInfo c, DateTime now)
        {
            return c.At.ToString(c.At.Date == now.Date ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // The window's credits card, top to bottom: this month, each bonus pool that has not expired, the overage.
        public static List<CreditRow> Rows(CreditInfo c)
        {
            var rows = new List<CreditRow>();
            if (c == null) return rows;
            rows.Add(new CreditRow { Kind = "month", Used = c.Used, Limit = c.Limit });
            foreach (var bn in c.Bonuses)
                if (!Expired(bn)) rows.Add(new CreditRow { Kind = bn.Trial ? "trial" : "bonus", Used = bn.Used, Limit = bn.Limit });
            if (c.Overage > 0) rows.Add(new CreditRow { Kind = "over", Used = c.Overage });
            return rows;
        }

        // How long ago Kiro asked, in words.<lang>.credits: just now, 12 min ago, 2 h ago, 3 days ago.
        public static string Ago(DateTime at, DateTime now, object words)
        {
            double min = (now - at).TotalMinutes;
            if (min < 1) return Json.Str(Json.At(words, "agoNow"));
            string key = min < 60 ? "agoMin" : min < 48 * 60 ? "agoHour" : "agoDay";
            int n = (int)(min < 60 ? min : min < 48 * 60 ? min / 60 : min / 1440);
            return Policy.FormatText(Json.At(words, key), new Dictionary<string, string> { { "n", n.ToString(CultureInfo.InvariantCulture) } });
        }

        // The card's corner: when Kiro asked and how long ago that is ("" with nothing to show).
        public static string Checked(CreditInfo c, object words, DateTime now)
        {
            if (c == null || Json.Obj(words) == null) return "";
            return Policy.FormatText(Json.At(words, "checked"), new Dictionary<string, string>
            {
                { "at", AtText(c, now) }, { "ago", Ago(c.At, now, words) }
            });
        }

        // One bar's figures: "50 / 50", or the overage alone.
        public static string Value(CreditRow r, object words)
        {
            if (r.Kind == "over") return N(r.Used);
            return Policy.FormatText(Json.At(words, "value"), new Dictionary<string, string> { { "used", N(r.Used) }, { "limit", N(r.Limit) } });
        }

        // The line for the phone's status and the morning summary, from words.<lang>.credits; "" when there is
        // nothing to show.
        public static string Text(CreditInfo c, object words, DateTime now)
        {
            if (c == null || Json.Obj(words) == null) return "";
            string at = AtText(c, now);
            string plan = PlanName(c.Plan);
            var bonuses = new StringBuilder();
            foreach (var bn in c.Bonuses)
            {
                if (Expired(bn)) continue;
                bonuses.Append(Policy.FormatText(Json.At(words, bn.Trial ? "trial" : "bonus"),
                    new Dictionary<string, string> { { "used", N(bn.Used) }, { "limit", N(bn.Limit) } }));
            }
            string over = c.Overage > 0
                ? Policy.FormatText(Json.At(words, "over"), new Dictionary<string, string> { { "n", N(c.Overage) } }) : "";
            return Policy.FormatText(Json.At(words, "line"), new Dictionary<string, string>
            {
                { "at", at },
                { "plan", plan.Length > 0 ? Policy.FormatText(Json.At(words, "plan"), new Dictionary<string, string> { { "plan", plan } }) : "" },
                { "used", N(c.Used) },
                { "limit", N(c.Limit) },
                { "bonuses", bonuses.ToString() },
                { "over", over }
            });
        }

        // "KIRO FREE" as the status bar writes it: "Kiro Free". A name not in capitals stays as it is.
        public static string PlanName(string title)
        {
            string t = (title ?? "").Trim();
            if (t.Length == 0 || t != t.ToUpperInvariant()) return t;
            var words = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                words[i] = words[i].Substring(0, 1) + words[i].Substring(1).ToLowerInvariant();
            return string.Join(" ", words);
        }

        // 50, 306.07, 1,234.5
        public static string N(double x) { return x.ToString("#,0.##", CultureInfo.InvariantCulture); }
    }
}
