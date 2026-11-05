using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // One session folder with a record.
    internal sealed class SessionFile
    {
        public string Id, Dir, Messages;
        public long Size;
        public DateTime Modified, Created;
    }

    // session.json, reduced to what is used.
    internal sealed class SessionInfo
    {
        public string Id = "", Title = "", Workspace = "", Status = "";
    }

    // The last turn of a session, in the shape the policy takes.
    internal sealed class Turn
    {
        public List<string> Users = new List<string>();   // newest first
        public bool Ended, Credits, Cancelled, Recovered, Restart, CreditsBack;
        public string Footer = "", Final = "", Stop = "", ErrorType = "", ErrorText = "";
        public int ToolCalls, Items;
        public string StartedAt = "", EndedAt = "", LastAt = "";
    }

    // Kiro's own session records: %USERPROFILE%\.kiro\sessions\<workspace>\sess_<id>\. Reading them needs no tab
    // switching and does not lag when Kiro is minimized. Port of src/Records.ps1 (in v0.2).
    internal static class Records
    {
        public static readonly string SessionsRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kiro", "sessions");

        public static List<SessionFile> GetSessionFiles()
        {
            var outp = new List<SessionFile>();
            if (!Directory.Exists(SessionsRoot)) return outp;
            foreach (string wsDir in Directory.GetDirectories(SessionsRoot))
            {
                string[] sess;
                try { sess = Directory.GetDirectories(wsDir, "sess_*"); } catch { continue; }
                foreach (string dir in sess)
                {
                    var fi = new FileInfo(Path.Combine(dir, "messages.jsonl"));
                    if (!fi.Exists) continue;
                    outp.Add(new SessionFile
                    {
                        Id = Path.GetFileName(dir),
                        Dir = dir,
                        Messages = fi.FullName,
                        Size = fi.Length,
                        Modified = fi.LastWriteTime,
                        Created = Directory.GetCreationTime(dir)
                    });
                }
            }
            return outp;
        }

        public static SessionInfo GetSessionInfo(string dir)
        {
            object j;
            try { j = Json.Parse(File.ReadAllText(Path.Combine(dir, "session.json"), Encoding.UTF8)); }
            catch { return null; }
            if (Json.Obj(j) == null) return null;
            string ws = "";
            var wp = Json.Arr(Json.At(j, "workspacePaths"));
            if (wp.Count > 0 && wp[0] != null)
            {
                string first = Json.Str(wp[0]);
                if (first.Length > 0) ws = new DirectoryInfo(first.TrimEnd('\\', '/')).Name;
            }
            return new SessionInfo
            {
                Id = Json.Str(Json.At(j, "id")),
                Title = Json.Str(Json.At(j, "title")),
                Workspace = ws,
                Status = Json.Str(Json.At(j, "status"))
            };
        }

        public sealed class LineRead { public List<string> Lines = new List<string>(); public long End; }

        // Complete lines of path from byte 'from' on, at most the last 'max' bytes. A line still being written is
        // left for next time; End is where the next read starts.
        public static LineRead ReadLines(string path, long from = 0, long max = 4194304)
        {
            var res = new LineRead { End = from };
            FileStream fs;
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch { return res; }
            try
            {
                long len = fs.Length;
                if (len < from) from = 0;
                long start = Math.Max(from, len - max);
                int count = (int)(len - start);
                if (count <= 0) { res.End = len; return res; }
                var buf = new byte[count];
                fs.Seek(start, SeekOrigin.Begin);
                int n = 0;
                while (n < count) { int got = fs.Read(buf, n, count - n); if (got <= 0) break; n += got; }
                int nl = n > 0 ? Array.LastIndexOf(buf, (byte)10, n - 1) : -1;
                if (nl < 0) { res.End = start; return res; }
                string text = Encoding.UTF8.GetString(buf, 0, nl);
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                if (start > from && lines.Count > 0) lines.RemoveAt(0);   // began mid-line
                res.Lines = lines; res.End = start + nl + 1;
                return res;
            }
            finally { fs.Dispose(); }
        }

        // Lines are matched with regexes, not parsed whole: a line can hold megabytes (images, tool output).
        static readonly Regex RxHead = new Regex("\"payload\":\\{\"type\":\"(\\w+)\"", RegexOptions.Compiled);
        static readonly Regex RxContent = new Regex("\"payload\":\\{\"type\":\"\\w+\",\"content\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        static readonly Regex RxStop = new Regex("\"stopReason\":\"(\\w+)\"", RegexOptions.Compiled);
        static readonly Regex RxStatus = new Regex("\"status\":\"(\\w+)\"", RegexOptions.Compiled);
        static readonly Regex RxElapsed = new Regex("\"elapsedTime\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxUsage = new Regex("\"usage\":([0-9.eE+-]+)", RegexOptions.Compiled);
        static readonly Regex RxSay = new Regex("\"operationType\":\"Say\"", RegexOptions.Compiled);
        static readonly Regex RxTime = new Regex("\"timestamp\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxBoundary = new Regex("\"boundarySource\":\"(\\w+)\"", RegexOptions.Compiled);
        static readonly Regex RxErrType = new Regex("\"errorType\":\"(\\w+)\"", RegexOptions.Compiled);
        static readonly Regex RxErrText = new Regex("\"message\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        static string Unescape(string inner)
        {
            try { return Json.Str(Json.Parse("\"" + inner + "\"")); } catch { return ""; }
        }
        static string ContentOf(string line)
        {
            var c = RxContent.Match(line);
            return c.Success ? Unescape(c.Groups[1].Value) : "";
        }

        public static DateTime? FromTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
            catch { return null; }
        }

        sealed class Entry
        {
            public string Type, Text = "", Stop = "", ErrorType = "", Status = "", At = "";
            public bool Recovered;
            public long Ms;
            public double Credits;
        }

        static Entry ToEntry(string line)
        {
            var m = RxHead.Match(line);
            if (!m.Success) return null;
            Entry e = null;
            switch (m.Groups[1].Value)
            {
                case "user":
                    // Kiro writes a few user lines itself, mid-turn; they are not yours.
                    if (!line.Contains("\"syntheticUserMessageReason\":\"")) e = new Entry { Type = "user", Text = ContentOf(line) };
                    break;
                case "assistant":
                    if (RxSay.IsMatch(line)) e = new Entry { Type = "say", Text = ContentOf(line) };
                    break;
                case "tool_call": e = new Entry { Type = "tool" }; break;
                case "turn_start": e = new Entry { Type = "start" }; break;
                case "turn_end":
                    {
                        var s = RxStop.Match(line); var b = RxBoundary.Match(line);
                        e = new Entry
                        {
                            Type = "end",
                            Stop = s.Success ? s.Groups[1].Value : "",
                            Recovered = b.Success && b.Groups[1].Value == "recovered"
                        };
                        break;
                    }
                case "session_metadata":
                    if (line.Contains("\"key\":\"displayError\""))
                    {
                        var et = RxErrType.Match(line); var mt = RxErrText.Match(line);
                        e = new Entry
                        {
                            Type = "error",
                            ErrorType = et.Success ? et.Groups[1].Value : "",
                            Text = mt.Success ? Unescape(mt.Groups[1].Value) : ""
                        };
                    }
                    break;
                case "usage_summary":
                    {
                        var st = RxStatus.Match(line); var el = RxElapsed.Match(line);
                        double cr = 0;
                        foreach (Match u in RxUsage.Matches(line))
                            cr += double.Parse(u.Groups[1].Value, CultureInfo.InvariantCulture);
                        e = new Entry
                        {
                            Type = "usage",
                            Status = st.Success ? st.Groups[1].Value : "",
                            Ms = el.Success ? long.Parse(el.Groups[1].Value) : 0,
                            Credits = cr
                        };
                        break;
                    }
            }
            if (e != null) { var t = RxTime.Match(line); e.At = t.Success ? t.Groups[1].Value : ""; }
            return e;
        }

        public static Turn GetRecordTurn(IEnumerable<string> lines)
        {
            var entries = new List<Entry>();
            foreach (string l in lines) if (!string.IsNullOrEmpty(l)) { var e = ToEntry(l); if (e != null) entries.Add(e); }

            var turn = new Turn();
            for (int i = entries.Count - 1; i >= 0 && turn.Users.Count < 30; i--)
                if (entries[i].Type == "user") turn.Users.Add(entries[i].Text);

            int s = -1;
            for (int i = entries.Count - 1; i >= 0; i--) if (entries[i].Type == "start") { s = i; break; }
            if (s < 0) return turn;

            turn.StartedAt = entries[s].At; turn.LastAt = entries[s].At;
            var says = new List<string>();
            Entry usage = null;
            for (int i = s + 1; i < entries.Count; i++)
            {
                var e = entries[i]; turn.Items++;
                if (e.At.Length > 0) turn.LastAt = e.At;
                if (e.Type == "tool") { turn.ToolCalls++; says.Clear(); }
                else if (e.Type == "say") { if (e.Text.Trim().Length > 0) says.Add(e.Text); }
                else if (e.Type == "usage") usage = e;
                else if (e.Type == "error") { turn.ErrorType = e.ErrorType; turn.ErrorText = e.Text; }
                else if (e.Type == "end") { turn.Ended = true; turn.Stop = e.Stop; turn.Recovered = e.Recovered; turn.EndedAt = e.At; }
            }
            turn.Final = string.Join("\n", says).Trim();
            turn.Restart = turn.Recovered;
            turn.Cancelled = turn.Stop == "cancelled" && !turn.Recovered;
            // Credits is named after the chat footer the first version read; it means the turn ended normally. The
            // number of credits is only shown in Footer: a cut-off turn can have credits too.
            turn.Credits = turn.Ended && turn.Stop != "error" && !turn.Recovered;

            var parts = new List<string> { turn.Stop };
            if (turn.Stop == "error" && turn.ErrorType.Length > 0) parts = new List<string> { "error (" + turn.ErrorType + ")" };
            if (turn.Recovered) parts.Add("recovered");
            if (usage != null)
            {
                parts.Add(usage.Status);
                parts.Add(usage.Credits.ToString("0.##", CultureInfo.InvariantCulture) + " credits");
                // formatting rounds a half second up, as the script's '{0:0}s' does (Math.Round would round 2.5 to 2)
                parts.Add((usage.Ms / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "s");
            }
            turn.Footer = string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
            return turn;
        }

        // The last turn of a session, reading back from the end, four times further each round (up to 64 MB)
        // until a user message turns up.
        public static Turn ReadSessionTurn(string dir, long tail = 4194304)
        {
            string path = Path.Combine(dir, "messages.jsonl");
            long size = 0; try { size = new FileInfo(path).Length; } catch { }
            long n = Math.Max(65536L, tail);
            while (true)
            {
                var turn = GetRecordTurn(ReadLines(path, 0, n).Lines);
                if (turn.Users.Count > 0 || n >= size || n >= 67108864L) return turn;
                n *= 4;
            }
        }

        // Is any session working? Some record written in the last 'minutes'; a sub-agent writes to
        // sub-executions\*.jsonl and leaves messages.jsonl alone for as long as it runs, so those count too.
        public static bool AnyWorking(IEnumerable<SessionFile> files, double minutes)
        {
            DateTime since = DateTime.Now.AddMinutes(-minutes);
            var list = files as IList<SessionFile> ?? files.ToList();
            foreach (var f in list) if (f.Modified >= since) return true;
            DateTime day = DateTime.Now.AddHours(-12);
            foreach (var f in list)
            {
                if (f.Modified < day) continue;
                string sub = Path.Combine(f.Dir, "sub-executions");
                if (!Directory.Exists(sub)) continue;
                foreach (string s in Directory.GetFiles(sub, "*.jsonl"))
                    if (File.GetLastWriteTime(s) >= since) return true;
            }
            return false;
        }

        // When Kiro's oldest process started (local time); null if Kiro is not running; MinValue if it runs but
        // the start time cannot be read.
        public static DateTime? GetKiroStart()
        {
            var ps = Process.GetProcessesByName("Kiro");
            if (ps.Length == 0) return null;
            DateTime? min = null;
            foreach (var p in ps) { try { DateTime t = p.StartTime; if (min == null || t < min.Value) min = t; } catch { } }
            return min ?? DateTime.MinValue;
        }

        // Should this turn be continued now that Kiro has restarted? Marks it (Restart; an open one also Ended,
        // without credits) and returns true. See docs/how-it-works.md.
        public static bool SetRestartCut(Turn turn, double windowMinutes, DateTime? kiroStart, IList<string> limitErrors)
        {
            if (turn.Restart) return true;
            if (kiroStart == null || kiroStart.Value == DateTime.MinValue) return false;
            DateTime ks = kiroStart.Value.ToUniversalTime();
            if (!turn.Ended)
            {
                var last = FromTime(turn.LastAt);
                if (last == null || last.Value.ToUniversalTime() >= ks) return false;
                turn.Ended = true; turn.Restart = true; turn.Credits = false; turn.Cancelled = false; turn.Footer = "no turn_end";
                return true;
            }
            if (turn.Stop != "error") return false;
            var end = FromTime(turn.EndedAt);
            if (end == null) return false;
            double gap = (ks - end.Value.ToUniversalTime()).TotalMinutes;
            bool limit = !string.IsNullOrEmpty(turn.ErrorType) && limitErrors != null && limitErrors.Contains(turn.ErrorType);
            if (gap < 0 || (gap > windowMinutes && !limit)) return false;
            turn.Restart = true;
            return true;
        }
    }
}
