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

        // The line for the window, the phone's status and the morning summary, from words.<lang>.credits; "" when
        // there is nothing to show. Today's time as HH:mm, an older one with its date.
        public static string Text(CreditInfo c, object words, DateTime now)
        {
            if (c == null || Json.Obj(words) == null) return "";
            string at = c.At.ToString(c.At.Date == now.Date ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
            string plan = PlanName(c.Plan);
            var bonuses = new StringBuilder();
            foreach (var bn in c.Bonuses)
            {
                if (bn.Status.IndexOf("EXPIRED", StringComparison.OrdinalIgnoreCase) >= 0) continue;
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
