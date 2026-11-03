using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // What only the exe has: settings by dotted name, the data folder, saving settings, and the words its window
    // and its "recent" list show (a missing word would leave a blank label).
    internal static class ExeTests
    {
        static Config cfg;

        public static void Run()
        {
            cfg = T.Defaults();

            // Settings by dotted name go down the nested objects (11-03: "keepAwake.enabled" was looked up as one
            // flat key, so switching off "keep the PC awake" did nothing).
            T.Check("dotted name: a nested switch", () => cfg.Bool("keepAwake.enabled", false) && Json.Bool(cfg.At("keepAwake", "enabled")));
            T.Check("dotted name: a nested number", () => cfg.Int("resume.maxPerRun", 7) == Json.Int(cfg.At("resume", "maxPerRun")) && cfg.Int("resume.maxPerRun", 7) != 7);
            T.Check("dotted name: a nested decimal", () => cfg.Dbl("report.hours", 1) == Json.Dbl(cfg.At("report", "hours")) && cfg.Dbl("report.hours", 1) != 1);
            T.Check("dotted name: missing, so the fallback", () => cfg.Int("resume.nothingHere", 42) == 42 && cfg.Str("no.such.key", "fb") == "fb");
            T.Check("a plain name still works", () => cfg.Int("idleSeconds", 1) == Json.Int(cfg.At("idleSeconds")) && cfg.Int("idleSeconds", 1) != 1);

            // The footer's seconds round a half up, as the script's '{0:0}s' does (Math.Round gave 2.5 s -> "2s").
            Func<long, string> foot = ms => Records.GetRecordTurn(new[]
            {
                "{\"id\":\"x\",\"timestamp\":\"t\",\"payload\":{\"type\":\"turn_start\"}}",
                "{\"id\":\"x\",\"timestamp\":\"t\",\"payload\":{\"type\":\"usage_summary\",\"promptTurnSummaries\":[],\"elapsedTime\":" + ms + ",\"status\":\"success\"}}",
                "{\"id\":\"x\",\"timestamp\":\"t\",\"payload\":{\"type\":\"turn_end\",\"stopReason\":\"end_turn\"}}"
            }).Footer;
            T.Check("footer seconds like the script's: " + foot(2500) + " " + foot(3500) + " " + foot(2499), () => foot(2500).EndsWith(" 3s") && foot(3500).EndsWith(" 4s") && foot(2499).EndsWith(" 2s"));

            // The data folder: KAC_DATA_DIR (these tests use one), every state file in it, and a lock of its own.
            string data = Environment.GetEnvironmentVariable("KAC_DATA_DIR");
            T.Check("the data folder is KAC_DATA_DIR", () => cfg.DataDir == Path.GetFullPath(data));
            var files = new[] { cfg.ConfigPath, cfg.LogPath, cfg.NumbersPath, cfg.QueuePath, cfg.AllowPath, cfg.ReportStatePath, cfg.ActivityPath };
            T.Check("every state file is in the data folder", () => files.All(f => Path.GetDirectoryName(f) == cfg.DataDir) && files.Distinct().Count() == files.Length);
            string mine = Config.InstanceSuffix();
            Environment.SetEnvironmentVariable("KAC_DATA_DIR", Path.Combine(data, "other"));
            string other = Config.InstanceSuffix();
            Environment.SetEnvironmentVariable("KAC_DATA_DIR", null);
            string real = Config.InstanceSuffix();
            Environment.SetEnvironmentVariable("KAC_DATA_DIR", data);
            T.Check("a test copy gets its own lock, the real one none: " + mine + " / " + other + " / [" + real + "]",
                () => mine.Length > 1 && other.Length > 1 && mine != other && real == "" && Config.InstanceSuffix() == mine);

            // Saving: only what you changed goes into config.json, nested, merged with what was there; a fresh load
            // reads it back and keeps the other defaults.
            string saveDir = Path.Combine(data, "save");
            Environment.SetEnvironmentVariable("KAC_DATA_DIR", saveDir);
            try
            {
                var c = Config.Load();
                c.SaveUser(new Dictionary<string, object> { { "keepAwake", new Dictionary<string, object> { { "enabled", false } } } });
                c.SaveUser(new Dictionary<string, object> { { "notify", new Dictionary<string, object> { { "events", new[] { "needsYou" } } } } });
                string text = File.ReadAllText(c.ConfigPath, Encoding.UTF8);
                var back = Config.Load();
                T.Check("saved: only the changes, not the whole tree", () => !text.Contains("\"words\"") && !text.Contains("\"untilDone\""));
                T.Check("saved: the window's copy sees it at once", () => !c.Bool("keepAwake.enabled", true));
                T.Check("saved: a fresh load reads both changes", () => !back.Bool("keepAwake.enabled", true)
                    && string.Join(",", Json.StrArr(back.At("notify", "events"))) == "needsYou");
                T.Check("saved: the other defaults next to them are kept", () => back.Int("keepAwake.minutes", 0) == cfg.Int("keepAwake.minutes", -1)
                    && Json.Str(back.At("notify", "webhook")) == Json.Str(cfg.At("notify", "webhook")));
            }
            finally { Environment.SetEnvironmentVariable("KAC_DATA_DIR", data); }

            // The words of the window, in both languages.
            object zh = cfg.At("words", "zh", "ui"), en = cfg.At("words", "en", "ui");
            var zk = Keys(zh, ""); var ek = Keys(en, "");
            T.Check("window words: the same keys in both languages: " + string.Join(", ", zk.Except(ek).Concat(ek.Except(zk))),
                () => zk.SetEquals(ek));
            var holes = zk.Intersect(ek).Where(k => !Holes(Json.Str(At(zh, k))).SetEquals(Holes(Json.Str(At(en, k))))).ToList();
            T.Check("window words: the same {placeholders} in both languages: " + string.Join(", ", holes), () => holes.Count == 0);
            var actBad = new List<string>();
            foreach (string lang in new[] { "zh", "en" })
                foreach (var kv in Json.Obj(cfg.At("words", lang, "ui", "act")) ?? new Dictionary<string, object>())
                    if (!Holes(Json.Str(kv.Value)).IsSubsetOf(new[] { "n", "d", "need" })) actBad.Add(lang + "." + kv.Key);
            T.Check("recent words: only {n}, {d} and {need}, which the window fills in: " + string.Join(", ", actBad), () => actBad.Count == 0);

            string repo = T.Repo();
            if (repo == null) { T.Skipped("the window's and the watcher's sources (this exe is not inside the repository)"); return; }
            string app = Path.Combine(repo, "app");
            // Every word the window and the tray ask for (U("key") and F("key", ...)), plus the ones picked in code.
            var used = new HashSet<string>(new[] { "stop", "continueNow", "continue", "pendResume", "pendPush", "pendQueued", "retry", "retryNoTab", "retryDraft" });
            foreach (string f in new[] { "MainWindow.xaml.cs", "Shell.cs" })
                foreach (string k in FirstArgLiterals(File.ReadAllText(Path.Combine(app, f)), "U", "F")) used.Add(k);
            var noWord = new List<string>();
            foreach (string k in used.OrderBy(x => x, StringComparer.Ordinal))
                foreach (string lang in new[] { "zh", "en" })
                    if (Json.Str(cfg.At("words", lang, "ui", k)).Length == 0) noWord.Add(lang + "." + k);
            T.Check("window words: all " + used.Count + " the window asks for are there: " + string.Join(", ", noWord), () => used.Count > 40 && noWord.Count == 0);
            // Every kind of "recent" line the watcher writes (Act("kind", ...)), and a "sent." for every message kind.
            var kinds = new HashSet<string>();
            foreach (string f in new[] { "Watcher.cs", "WatcherRemote.cs", "Activity.cs" })
                foreach (string k in FirstArgLiterals(File.ReadAllText(Path.Combine(app, f)), "Act"))
                    if (!k.EndsWith(".")) kinds.Add(k);
            foreach (string k in new[] { "resume", "verify", "contract", "push", "pushGoal", "queued" }) kinds.Add("sent." + k);
            kinds.Add("need");
            var noAct = new List<string>();
            foreach (string k in kinds.OrderBy(x => x, StringComparer.Ordinal))
                foreach (string lang in new[] { "zh", "en" })
                    if (Json.Str(cfg.At("words", lang, "ui", "act", k)).Length == 0) noAct.Add(lang + "." + k);
            T.Check("recent words: all " + kinds.Count + " kinds the watcher writes are there: " + string.Join(", ", noAct), () => kinds.Count > 15 && noAct.Count == 0);
        }

        // "a", "a.b", ... for every string under a word tree.
        static HashSet<string> Keys(object node, string prefix)
        {
            var set = new HashSet<string>();
            var o = Json.Obj(node);
            if (o == null) { set.Add(prefix); return set; }
            foreach (var kv in o) set.UnionWith(Keys(kv.Value, prefix.Length > 0 ? prefix + "." + kv.Key : kv.Key));
            return set;
        }
        static object At(object node, string dotted) { return Json.At(node, dotted.Split('.')); }
        static HashSet<string> Holes(string s)
        {
            return new HashSet<string>(Regex.Matches(s ?? "", @"\{(\w+)\}").Cast<Match>().Select(m => m.Groups[1].Value));
        }

        // The string literals in the first argument of every call to one of the names, e.g. U(paused ? "a" : "b")
        // gives a and b. Enough for this code: no escapes inside the keys.
        static List<string> FirstArgLiterals(string src, params string[] names)
        {
            var outp = new List<string>();
            foreach (Match m in Regex.Matches(src, @"\b(" + string.Join("|", names.Select(Regex.Escape)) + @")\("))
            {
                int i = m.Index + m.Length, depth = 0;
                var sb = new StringBuilder();
                bool inStr = false;
                for (; i < src.Length; i++)
                {
                    char ch = src[i];
                    if (inStr)
                    {
                        if (ch == '\\') { i++; continue; }
                        if (ch == '"') { inStr = false; outp.Add(sb.ToString()); sb.Clear(); }
                        else sb.Append(ch);
                        continue;
                    }
                    if (ch == '"') { inStr = true; continue; }
                    if (ch == '(') depth++;
                    else if (ch == ')') { if (depth == 0) break; depth--; }
                    else if (ch == ',' && depth == 0) break;
                }
            }
            return outp;
        }
    }
}
