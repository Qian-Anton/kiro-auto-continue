using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KiroAutoContinue
{
    // Unit tests for the exe's logic: the same cases as tests\*.Tests.ps1 for the script version (in v0.2), plus a few for
    // what only the exe has. Kiro is not needed and nothing is sent. Settings are the defaults built into the
    // exe, read through a scratch data folder (KAC_DATA_DIR), never yours.
    //   KiroAutoContinue.Tests.exe              all tests; exit code = failures
    //   KiroAutoContinue.Tests.exe --replay F   no tests: for every session in ~\.kiro\sessions, what the exe
    //                                           reads from it and would do, one JSON line each, to compare with
    //                                           the script version (read only; long texts as hashes)
    // ASCII only: the Chinese text comes from config.default.json or from code points.
    internal static class TestMain
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            string data = Path.Combine(Path.GetTempPath(), "kac_unit_" + System.Diagnostics.Process.GetCurrentProcess().Id);
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("KAC_DATA_DIR", data);
            try
            {
                if (args.Length >= 2 && args[0] == "--replay") return Replay.Run(args[1]);
                T.Run("Policy", PolicyTests.Run);
                T.Run("Records", RecordsTests.Run);
                T.Run("Remote", RemoteTests.Run);
                T.Run("Report", ReportTests.Run);
                T.Run("Notify", NotifyTests.Run);
                T.Run("Panel", PanelTests.Run);
                T.Run("Credits", CreditsTests.Run);
                T.Run("Exe only", ExeTests.Run);
                Console.WriteLine();
                Console.WriteLine("{0} passed, {1} failed{2}", T.Pass, T.Fail, T.Skip > 0 ? ", " + T.Skip + " skipped" : "");
                return T.Fail;
            }
            finally { try { Directory.Delete(data, true); } catch { } }
        }
    }

    // The checks and a few helpers shared by the test files.
    internal static class T
    {
        public static int Pass, Fail, Skip;
        static int sPass, sFail;
        static string suite = "";

        // One file of tests. If its setup throws, that counts as one failure and the next file still runs.
        public static void Run(string name, Action body)
        {
            suite = name; sPass = 0; sFail = 0;
            try { body(); }
            catch (Exception e) { Fail++; sFail++; Console.WriteLine("FAIL " + name + ": stopped early: " + e.GetType().Name + ": " + e.Message); }
            Console.WriteLine("{0,-9} {1} passed, {2} failed", name, sPass, sFail);
        }

        // A check that throws fails (with the reason) instead of stopping the run.
        public static void Check(string what, Func<bool> test)
        {
            bool ok; string why = "";
            try { ok = test(); }
            catch (Exception e) { ok = false; why = " (threw " + e.GetType().Name + ": " + e.Message + ")"; }
            if (ok) { Pass++; sPass++; }
            else { Fail++; sFail++; Console.WriteLine("FAIL " + suite + ": " + what + why); }
        }

        public static void Skipped(string what) { Skip++; Console.WriteLine("skip " + suite + ": " + what); }

        public static string U(params int[] cp)
        {
            var sb = new StringBuilder();
            foreach (int c in cp) sb.Append((char)c);
            return sb.ToString();
        }

        // A template's first line (a template is a string or an array of lines).
        public static string First(object node)
        {
            if (node is string s) return s;
            var l = Json.StrArr(node);
            return l.Count > 0 ? l[0] : "";
        }

        // PowerShell's -like: * any run, ? one character.
        public static bool Like(string s, string pattern)
        {
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(s ?? "", rx, RegexOptions.Singleline);
        }

        // Fresh defaults (the scratch data folder has no config.json).
        public static Config Defaults() { return Config.Load(); }

        // Set a value deep in a config's tree, making the objects on the way.
        public static void SetPath(Config cfg, object value, params string[] path)
        {
            var cur = cfg.Root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                object next;
                if (!cur.TryGetValue(path[i], out next) || !(next is Dictionary<string, object>))
                {
                    next = new Dictionary<string, object>();
                    cur[path[i]] = next;
                }
                cur = (Dictionary<string, object>)next;
            }
            cur[path[path.Length - 1]] = value;
        }

        // The repository (for the checks that read the app's sources): up from this exe to the folder holding
        // app\KiroAutoContinue.csproj; null when the exe was copied somewhere else.
        public static string Repo()
        {
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "app", "KiroAutoContinue.csproj"))) return d.FullName;
            return null;
        }
    }
}
