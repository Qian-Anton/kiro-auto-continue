using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace KiroAutoContinue
{
    // Settings: the defaults baked into the exe (config.default.json, an embedded resource), with the user's
    // config.json merged on top, key by key. Everything the user can change, the logs, and the small state
    // files live in %APPDATA%\KiroAutoContinue, so the exe runs from anywhere (even a read-only folder).
    internal sealed class Config
    {
        public Dictionary<string, object> Root { get; private set; }
        public string DataDir { get; private set; }

        public string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        public string LogPath { get { return Path.Combine(DataDir, "kiro-auto-continue.log"); } }
        public string NumbersPath { get { return Path.Combine(DataDir, "session-numbers.json"); } }
        public string QueuePath { get { return Path.Combine(DataDir, "queue.json"); } }
        public string AllowPath { get { return Path.Combine(DataDir, "remote-allow.json"); } }
        public string ReportStatePath { get { return Path.Combine(DataDir, "report-state.json"); } }
        public string ActivityPath { get { return Path.Combine(DataDir, "activity.jsonl"); } }

        private Config() { }

        // For tests only: KAC_DATA_DIR puts the settings, logs and state somewhere else, and InstanceSuffix gives
        // that copy its own single-instance lock, so it can run next to the real one.
        static string DataDirOverride() { return Environment.GetEnvironmentVariable("KAC_DATA_DIR"); }
        public static string InstanceSuffix()
        {
            string o = DataDirOverride();
            if (string.IsNullOrEmpty(o)) return "";
            uint h = 2166136261;
            foreach (char ch in Path.GetFullPath(o).ToLowerInvariant()) { h ^= ch; h *= 16777619; }
            return "." + h.ToString("x8");
        }

        public static Config Load()
        {
            var c = new Config();
            string over = DataDirOverride();
            c.DataDir = !string.IsNullOrEmpty(over) ? Path.GetFullPath(over) : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KiroAutoContinue");
            Directory.CreateDirectory(c.DataDir);

            var def = Json.Obj(Json.Parse(ReadEmbeddedDefault())) ?? new Dictionary<string, object>();
            var user = ReadUserConfig(c.ConfigPath);
            if (user != null) Merge(def, user);
            c.Root = def;
            return c;
        }

        private static string ReadEmbeddedDefault()
        {
            var asm = Assembly.GetExecutingAssembly();
            string name = asm.GetName().Name + ".config.default.json";
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("embedded config.default.json is missing (" + name + ")");
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        private static Dictionary<string, object> ReadUserConfig(string path)
        {
            try { if (File.Exists(path)) return Json.Obj(Json.Parse(File.ReadAllText(path, Encoding.UTF8))); }
            catch { }
            return null;
        }

        // Deep-merge over into baseObj: both objects -> recurse; otherwise over wins.
        private static void Merge(Dictionary<string, object> baseObj, Dictionary<string, object> over)
        {
            foreach (var kv in over)
            {
                object existing;
                if (baseObj.TryGetValue(kv.Key, out existing)
                    && existing is Dictionary<string, object> be
                    && kv.Value is Dictionary<string, object> oe)
                    Merge(be, oe);
                else
                    baseObj[kv.Key] = kv.Value;
            }
        }

        // Write only the user's overrides to %APPDATA%\config.json (never the whole merged tree). Caller passes
        // the keys that the GUI owns; nested paths use "a.b".
        public void SaveUser(Dictionary<string, object> overrides)
        {
            var existing = ReadUserConfig(ConfigPath) ?? new Dictionary<string, object>();
            Merge(existing, overrides);
            AtomicWrite(ConfigPath, Pretty(existing));
            Merge(Root, overrides); // keep the in-memory copy in step
        }

        public static void AtomicWrite(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        // --- typed access to the merged tree; "a.b" goes down nested objects (no key has a dot in it) ---
        public object At(params string[] keys) { return Json.At(Root, keys); }
        object Dotted(string key) { return Json.At(Root, key.Split('.')); }
        public int Int(string key, int fb = 0) { return Json.Int(Dotted(key), fb); }
        public double Dbl(string key, double fb = 0) { return Json.Dbl(Dotted(key), fb); }
        public bool Bool(string key, bool fb = false) { return Json.Bool(Dotted(key), fb); }
        public string Str(string key, string fb = "") { return Json.Str(Dotted(key), fb); }

        // The word tree for a language (words.zh / words.en), falling back to en.
        public object Words(string lang)
        {
            var w = Json.At(Root, "words", lang);
            return w ?? Json.At(Root, "words", "en");
        }

        // The language the messages use: the "language" setting, or, with "auto", zh when the text has Chinese.
        public string Lang(string sampleText)
        {
            string set = Str("language", "auto");
            if (!string.IsNullOrEmpty(set) && set != "auto") return set;
            if (!string.IsNullOrEmpty(sampleText))
                foreach (char ch in sampleText) if (ch >= 0x3400 && ch <= 0x9fff) return "zh";
            return "en";
        }

        // The language for notices and the GUI: the setting, or Windows' own language with "auto".
        public string UiLang()
        {
            string set = Str("language", "auto");
            if (!string.IsNullOrEmpty(set) && set != "auto") return set;
            return System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh") ? "zh" : "en";
        }

        private static string Pretty(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        // A small pretty-printer for the user config file (so a human can read and edit it).
        private static void Write(StringBuilder sb, object v, int depth)
        {
            string pad = new string(' ', depth * 2);
            string pad2 = new string(' ', (depth + 1) * 2);
            if (v is Dictionary<string, object> o)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in o)
                {
                    sb.Append(pad2).Append(Quote(kv.Key)).Append(": ");
                    Write(sb, kv.Value, depth + 1);
                    if (++i < o.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(pad).Append('}');
            }
            else if (v is object[] a)
            {
                sb.Append('[');
                for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(", "); Write(sb, a[i], depth + 1); }
                sb.Append(']');
            }
            else if (v is System.Collections.IEnumerable e && !(v is string))
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in e) { if (!first) sb.Append(", "); first = false; Write(sb, x, depth + 1); }
                sb.Append(']');
            }
            else if (v is bool b) sb.Append(b ? "true" : "false");
            else if (v == null) sb.Append("null");
            else if (v is string s) sb.Append(Quote(s));
            else sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
