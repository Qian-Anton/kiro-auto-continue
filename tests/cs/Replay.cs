using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KiroAutoContinue
{
    // --replay <file>: for every session record on this PC, what the exe reads from it and would do, one JSON
    // line each: the last turn, whether a Kiro restart cut it short, the decision with a fresh state, and what
    // "continue N" from the phone would type. Read only; texts are written as hashes. Compared with the same
    // lines from the script version, this shows the two read and decide alike on real records.
    internal static class Replay
    {
        public static int Run(string outPath)
        {
            Config cfg = Config.Load();
            var limits = Json.StrArr(cfg.At("resume", "limitErrors"));
            double window = cfg.Dbl("restartWindowMinutes", 10);
            long tail = (long)cfg.Dbl("recordTailBytes", 4194304);
            DateTime? ks = Records.GetKiroStart();
            var lines = new List<string>();
            foreach (var f in Records.GetSessionFiles().OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var o = new Dictionary<string, object> { { "id", f.Id }, { "size", f.Size } };
                try
                {
                    var info = Records.GetSessionInfo(f.Dir);
                    o["ws"] = info != null ? info.Workspace : "(none)";
                    o["title_h"] = info != null ? H(info.Title) : "(none)";
                    Turn t = Records.ReadSessionTurn(f.Dir, tail);
                    Put(o, "", t);
                    o["cut"] = Records.SetRestartCut(t, window, ks, limits);
                    Put(o, "c_", t);
                    var d = Policy.Decide(t, new SessionState(), cfg);
                    o["d_action"] = d.Action; o["d_kind"] = d.Kind; o["d_why"] = Cut160(d.Why); o["d_text"] = H(d.Text);
                    o["d_after"] = d.After; o["d_code"] = d.Code; o["d_set"] = SetText(d);
                    string m = Policy.ManualContinue(t, cfg);
                    o["manual"] = m == null ? "(null)" : H(m);
                }
                catch (Exception e) { o["error"] = e.GetType().Name + ": " + e.Message; }
                lines.Add(Json.Write(o));
            }
            File.WriteAllText(outPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            Console.WriteLine("replayed " + lines.Count + " sessions -> " + outPath);
            return 0;
        }

        static void Put(Dictionary<string, object> o, string p, Turn t)
        {
            o[p + "users"] = t.Users.Count; o[p + "users_h"] = H(string.Join("\u0001", t.Users));
            o[p + "ended"] = t.Ended; o[p + "credits"] = t.Credits; o[p + "cancelled"] = t.Cancelled;
            o[p + "recovered"] = t.Recovered; o[p + "restart"] = t.Restart;
            o[p + "footer"] = t.Footer; o[p + "final_h"] = H(t.Final); o[p + "stop"] = t.Stop;
            o[p + "errtype"] = t.ErrorType; o[p + "errtext_h"] = H(t.ErrorText);
            o[p + "tools"] = t.ToolCalls; o[p + "items"] = t.Items;
            o[p + "started"] = t.StartedAt; o[p + "ended_at"] = t.EndedAt; o[p + "last"] = t.LastAt;
        }

        static string SetText(Decision d)
        {
            var parts = new List<string>();
            foreach (string k in d.Set.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                object v = d.Set[k];
                if (k == "Mode")
                {
                    var m = v as UntilDone;
                    parts.Add(m == null ? "Mode=null" : "Mode=P" + m.Pushes + ",I" + m.Idle + ",V" + (m.Verifying ? 1 : 0) + ",G" + H(m.Goal) + ",S" + H(m.Source));
                }
                else parts.Add(k + "=" + Json.Str(v));
            }
            return string.Join(";", parts);
        }

        static string Cut160(string s) { s = s ?? ""; return s.Length > 160 ? s.Substring(0, 160) : s; }

        // The first 8 bytes of SHA-256 in hex, and the length: the same text gives the same value in both versions.
        static string H(string s)
        {
            if (s == null) return "(null)";
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("x2"));
                return sb + ":" + s.Length;
            }
        }
    }
}
