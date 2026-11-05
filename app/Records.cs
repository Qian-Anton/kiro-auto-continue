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
        public bool Ended, Credits, Cancelled, Recovered, Restart, CreditsBack, Unfinished;
        public string Footer = "", Final = "", Stop = "", ErrorType = "", ErrorText = "";
        public int ToolCalls, Items;
        public string StartedAt = "", EndedAt = "", LastAt = "";
        // The question Kiro holds a running turn on, if any (a pending_interaction with no interaction_resolved yet).
        // Ask is "retry" for Kiro's own "keep iterating?", asked after a tool failed several times in a row (options
        // Cancel / Keep Iterating), and "question" for anything else (the agent's own question, a tool approval).
        public string Ask = "", AskId = "", AskText = "", AskAt = "";
        public List<string> AskOptions = new List<string>();
        public int Retries;   // Kiro's "keep iterating?" questions answered in this turn
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

        public sealed class LineRead { public List<string> Lines = new List<string>(); public long End, Bytes; }   // Bytes: read from the file

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
                res.Bytes = n;
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
        static readonly Regex RxCallId = new Regex("\"toolCallId\":\"([^\"]+)\"", RegexOptions.Compiled);

        // Kiro's own question after a tool failed several times in a row ("I've been trying to use execute_pwsh but
        // it's failed 7 times in a row..."): its id is failure-intervention-prompt-<n>, its options Cancel and this.
        public const string RetryPrompt = "failure-intervention-prompt", KeepIterating = "Keep Iterating";

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
            public string Type, Text = "", Stop = "", ErrorType = "", Status = "", At = "", Id = "";
            public bool Recovered;
            public long Ms;
            public double Credits;
            public List<string> Options = new List<string>();
        }

        static bool IsRetry(Entry e) { return e.Id.StartsWith(RetryPrompt, StringComparison.Ordinal) && e.Options.Contains(KeepIterating); }

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
                case "pending_interaction":
                    {
                        // a short line, so parsed whole for the question and its options
                        object pl = null;
                        if (line.Length < 262144) { try { pl = Json.At(Json.Parse(line), "payload"); } catch { } }
                        var c = RxCallId.Match(line);
                        e = new Entry { Type = "ask", Id = c.Success ? c.Groups[1].Value : "", Text = Json.Str(Json.At(pl, "question")) };
                        foreach (object o in Json.Arr(Json.At(pl, "options")))
                        {
                            string label = o is string str ? str : Json.Str(Json.At(o, "title"));
                            if (label.Length == 0) label = Json.Str(Json.At(o, "label"));
                            if (label.Length > 0) e.Options.Add(label);
                        }
                        break;
                    }
                case "interaction_resolved":
                    {
                        var c = RxCallId.Match(line);
                        e = new Entry { Type = "answer", Id = c.Success ? c.Groups[1].Value : "" };
                        break;
                    }
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

        public static Turn GetRecordTurn(IEnumerable<string> lines) { return BuildTurn(ToEntries(lines)); }

        static List<Entry> ToEntries(IEnumerable<string> lines)
        {
            var entries = new List<Entry>();
            foreach (string l in lines) if (!string.IsNullOrEmpty(l)) { var e = ToEntry(l); if (e != null) entries.Add(e); }
            return entries;
        }

        // The last turn, from a record's entries in order. It uses everything from the last turn_start on, and before
        // that only the user messages (TurnCache keeps just those). Leaves the entries as they are.
        static Turn BuildTurn(List<Entry> entries)
        {
            var turn = new Turn();
            for (int i = entries.Count - 1; i >= 0 && turn.Users.Count < 30; i--)
                if (entries[i].Type == "user") turn.Users.Add(entries[i].Text);

            int s = -1;
            for (int i = entries.Count - 1; i >= 0; i--) if (entries[i].Type == "start") { s = i; break; }
            if (s < 0) return turn;

            turn.StartedAt = entries[s].At; turn.LastAt = entries[s].At;
            var says = new List<string>();
            var open = new List<Entry>();   // questions asked in this turn, not answered yet
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
                else if (e.Type == "ask") open.Add(e);
                else if (e.Type == "answer")
                {
                    int k = open.FindIndex(x => x.Id == e.Id);
                    if (k >= 0) { if (IsRetry(open[k])) turn.Retries++; open.RemoveAt(k); }
                }
            }
            if (!turn.Ended && open.Count > 0)
            {
                var a = open[open.Count - 1];
                turn.Ask = IsRetry(a) ? "retry" : "question";
                turn.AskId = a.Id; turn.AskText = a.Text; turn.AskAt = a.At; turn.AskOptions = new List<string>(a.Options);
            }
            turn.Final = string.Join("\n", says).Trim();
            turn.Restart = turn.Recovered;
            turn.Cancelled = turn.Stop == "cancelled" && !turn.Recovered;
            // Kiro ends some long turns while the agent still had work to do, and records no error: end_turn with no
            // reply after the last tool call, tool_use, or max_tokens. Such a turn counts as cut off. See
            // docs/how-it-works.md for how often, and what you typed next.
            bool noReply = turn.Ended && turn.Stop == "end_turn" && !turn.Recovered && turn.Final.Length == 0;
            turn.Unfinished = noReply || (turn.Ended && !turn.Recovered && (turn.Stop == "tool_use" || turn.Stop == "max_tokens"));
            // Credits is named after the chat footer the first version read; it means the turn ended normally. The
            // number of credits is only shown in Footer: a cut-off turn can have credits too.
            turn.Credits = turn.Ended && turn.Stop != "error" && !turn.Recovered && !turn.Unfinished;

            var parts = new List<string> { turn.Stop };
            if (turn.Stop == "error" && turn.ErrorType.Length > 0) parts = new List<string> { "error (" + turn.ErrorType + ")" };
            if (noReply) parts.Add(turn.ToolCalls > 0 ? "no reply after the last tool call" : "no reply");
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

        const long MaxTail = 67108864L;   // 64 MB

        // The last turn of a session, reading back from the end, four times further each round (up to 64 MB)
        // until a user message turns up. It reads all of that again each time; the watcher reads through TurnCache.
        public static Turn ReadSessionTurn(string dir, long tail = 4194304)
        {
            long end, bytes;
            return BuildTurn(ReadTail(Path.Combine(dir, "messages.jsonl"), tail, out end, out bytes));
        }

        // The entries of a record's tail, read the way ReadSessionTurn reads it. end: just after the last complete
        // line read; bytes: how much was read, all rounds together.
        static List<Entry> ReadTail(string path, long tail, out long end, out long bytes)
        {
            long size = 0; try { size = new FileInfo(path).Length; } catch { }
            long n = Math.Max(65536L, tail);
            bytes = 0;
            while (true)
            {
                var r = ReadLines(path, 0, n);
                bytes += r.Bytes;
                var entries = ToEntries(r.Lines);
                if (entries.Exists(e => e.Type == "user") || n >= size || n >= MaxTail) { end = r.End; return entries; }
                n *= 4;
            }
        }

        // The last turn of each session, kept from one read to the next, so that a read takes only the lines written
        // since the one before. The watcher reads the same sessions every few seconds (the window's list, and the
        // check for a question Kiro holds a turn on). Reading each tail afresh every time (4 MB, and 84 MB when the
        // last user message is more than 64 MB back) came to 234 MB read and about 1 GB of garbage a poll, and up
        // to 8 GB of memory (11-05).
        public sealed class TurnCache
        {
            sealed class Item
            {
                public long End;         // where the next read starts: just after the last complete line read
                public byte[] Mark;      // the bytes just before End, to tell a record that grew from one written anew
                public List<Entry> Entries;
                public DateTime Used;
            }

            readonly Dictionary<string, Item> items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            readonly object gate = new object();
            DateTime pruned = DateTime.Now;

            public int Count { get { lock (gate) return items.Count; } }
            public long BytesRead { get { lock (gate) return bytesRead; } }
            long bytesRead;

            // How many entries it keeps for a session (0 if none): for the tests.
            public int Kept(string dir)
            {
                lock (gate) { Item it; return items.TryGetValue(Path.Combine(dir, "messages.jsonl"), out it) ? it.Entries.Count : 0; }
            }

            // What ReadSessionTurn(dir, tail) gives. The record is read from its tail again only when it is new here,
            // got shorter, grew by more than 64 MB, or no longer has the same bytes before where the last read ended;
            // otherwise only the lines added since. A new Turn each time: callers change the one they get.
            public Turn Read(string dir, long tail = 4194304)
            {
                string path = Path.Combine(dir, "messages.jsonl");
                long size = -1;
                try { var fi = new FileInfo(path); if (fi.Exists) size = fi.Length; } catch { }
                lock (gate)
                {
                    DateTime now = DateTime.Now;
                    if (now < pruned || (now - pruned).TotalMinutes >= 10) { Prune(now); pruned = now; }
                    if (size < 0) { items.Remove(path); return BuildTurn(new List<Entry>()); }
                    Item it;
                    bool fresh = !items.TryGetValue(path, out it) || size < it.End || size - it.End > MaxTail;
                    if (!fresh && size > it.End && !Same(BytesBefore(path, it.End), it.Mark)) fresh = true;
                    bool read = false;
                    if (fresh)
                    {
                        long end, bytes;
                        it = new Item { Entries = ReadTail(path, tail, out end, out bytes), End = end };
                        bytesRead += bytes; read = true;
                        items[path] = it;
                    }
                    else if (size > it.End)
                    {
                        var r = ReadLines(path, it.End, MaxTail);
                        bytesRead += r.Bytes; read = true;
                        it.Entries.AddRange(ToEntries(r.Lines));
                        it.End = r.End;
                    }
                    if (read) it.Mark = BytesBefore(path, it.End);
                    Trim(it.Entries);
                    it.Used = now;
                    var turn = BuildTurn(it.Entries);
                    // a tail read that found no complete line ends mid-line: not a place to go on from
                    if (it.End > 0 && (it.Mark == null || it.Mark.Length == 0 || it.Mark[it.Mark.Length - 1] != 10)) items.Remove(path);
                    return turn;
                }
            }

            // A session not read for an hour (deleted, or gone from the window's list) is dropped; read again, it
            // starts from the tail.
            void Prune(DateTime now)
            {
                foreach (string k in items.Where(kv => now < kv.Value.Used || (now - kv.Value.Used).TotalHours >= 1).Select(kv => kv.Key).ToList())
                    items.Remove(k);
            }

            // Keeps what BuildTurn uses: everything from the last turn_start on and, before it, the newest user
            // messages, 30 in all.
            static void Trim(List<Entry> entries)
            {
                int s = entries.FindLastIndex(e => e.Type == "start");
                int lim = s >= 0 ? s : entries.Count;
                int want = 30;
                for (int i = lim; i < entries.Count; i++) if (entries[i].Type == "user") want--;
                var keep = new List<Entry>();
                for (int i = lim - 1; i >= 0 && keep.Count < want; i--) if (entries[i].Type == "user") keep.Add(entries[i]);
                if (keep.Count == lim) return;
                keep.Reverse();
                entries.RemoveRange(0, lim);
                entries.InsertRange(0, keep);
            }

            // The (up to) 64 bytes before pos; null if they cannot be read.
            static byte[] BytesBefore(string path, long pos)
            {
                int n = (int)Math.Min(64L, Math.Max(0L, pos));
                var b = new byte[n];
                if (n == 0) return b;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < pos) return null;
                        fs.Seek(pos - n, SeekOrigin.Begin);
                        int got = 0;
                        while (got < n) { int k = fs.Read(b, got, n - got); if (k <= 0) return null; got += k; }
                        return b;
                    }
                }
                catch { return null; }
            }

            static bool Same(byte[] a, byte[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
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
            try
            {
                if (ps.Length == 0) return null;
                DateTime? min = null;
                foreach (var p in ps) { try { DateTime t = p.StartTime; if (min == null || t < min.Value) min = t; } catch { } }
                return min ?? DateTime.MinValue;
            }
            finally { foreach (var p in ps) p.Dispose(); }   // StartTime opens a handle to each process
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
