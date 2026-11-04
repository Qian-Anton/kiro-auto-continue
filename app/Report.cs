using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // The morning status, counted from the watcher's own log. Port of src/Report.ps1 (in v0.2).
    internal static class Report
    {
        public sealed class Counts
        {
            public Dictionary<string, int> Resumes = new Dictionary<string, int>();
            public Dictionary<string, int> Pushes = new Dictionary<string, int>();
            public Dictionary<string, int> Needs = new Dictionary<string, int>();
            public Dictionary<string, int> Done = new Dictionary<string, int>();
        }

        static readonly Regex LogLine = new Regex(@"^(\d\d)-(\d\d) (\d\d):(\d\d):(\d\d)  (.+?) : (.*)$");

        static void Inc(Dictionary<string, int> t, string k) { int v; t.TryGetValue(k, out v); t[k] = v + 1; }

        // Counts per session name ("workspace | title") in the log lines written since 'since'. The log has no
        // year: a date after 'now' belongs to the year before.
        public static Counts LogCounts(IEnumerable<string> lines, DateTime since, DateTime now)
        {
            var c = new Counts();
            foreach (string line in lines)
            {
                var m = LogLine.Match(line ?? "");
                if (!m.Success) continue;
                DateTime at;
                try
                {
                    at = new DateTime(now.Year,
                        int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value));
                }
                catch { continue; }
                if (at > now.AddMinutes(1)) at = at.AddYears(-1);
                if (at < since) continue;
                string name = m.Groups[6].Value, rest = m.Groups[7].Value;
                if (Regex.IsMatch(rest, @"; sent resume\b")) Inc(c.Resumes, name);
                else if (Regex.IsMatch(rest, @"; sent (contract|push|pushGoal|verify|queued)\b")) Inc(c.Pushes, name);
                if (rest.StartsWith("NEEDS YOU - ")) c.Needs[name] = 1;
                if (rest.StartsWith("DONE - ")) c.Done[name] = 1;
            }
            return c;
        }

        public static int Total(Dictionary<string, int> table)
        {
            int n = 0; foreach (int v in table.Values) n += v; return n;
        }

        // When today's report is due: 'time' ("08:00") today; null when reports are off or the time is unreadable.
        public static DateTime? ReportDue(string time, DateTime now)
        {
            if (string.IsNullOrEmpty(time)) return null;
            var m = Regex.Match(time, @"^\s*(\d{1,2}):(\d\d)\s*$");
            if (!m.Success) return null;
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
            if (h > 23 || mi > 59) return null;
            return now.Date.AddHours(h).AddMinutes(mi);
        }
    }
}
