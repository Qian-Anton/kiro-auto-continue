using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KiroAutoContinue
{
    // One thing the watcher did or saw, for the window's "recent" list and the tray balloons. Kind says what:
    // sent.<kind> (resume, verify, contract, push, pushGoal, queued), need.<code>, done, finished, stopped,
    // window.continue / window.stop / window.notContinued / window.notStopped, phone.send / phone.queue /
    // phone.stop / phone.new / phone.paired, paused, resumed, kiro.restart. The window turns it into words
    // (words.<lang>.ui.act), so the list follows the language setting. Kept in activity.jsonl across runs.
    internal sealed class Activity
    {
        public DateTime At;
        public string Kind = "", Id = "", Title = "", Detail = "";
        public int Number;
    }

    internal sealed partial class Watcher
    {
        readonly List<Activity> activities = new List<Activity>();
        public event Action<Activity> OnActivity;

        // Oldest first.
        public List<Activity> RecentActivities()
        {
            lock (logLock) return activities.ToList();
        }

        void Act(string kind, string id = "", string detail = "")
        {
            var a = new Activity { At = DateTime.Now, Kind = kind, Id = id ?? "", Detail = detail ?? "" };
            if (a.Id.Length > 0)
            {
                a.Number = GetNumber(a.Id);
                string n = Name(a.Id);
                int bar = n.IndexOf(" | ", StringComparison.Ordinal);
                a.Title = bar >= 0 ? n.Substring(bar + 3) : n;
            }
            lock (logLock)
            {
                activities.Add(a);
                if (activities.Count > 300) activities.RemoveRange(0, activities.Count - 300);
                SaveActivity(a);
            }
            var h = OnActivity; if (h != null) try { h(a); } catch { }
        }

        // Caller holds logLock.
        void SaveActivity(Activity a)
        {
            if (readOnly) return;
            try
            {
                var o = new Dictionary<string, object>
                {
                    { "t", a.At.ToString("o", CultureInfo.InvariantCulture) }, { "k", a.Kind }, { "n", a.Number },
                    { "id", a.Id }, { "ti", a.Title }, { "d", a.Detail }
                };
                string path = cfg.ActivityPath;
                File.AppendAllText(path, Json.Write(o) + "\n", new UTF8Encoding(false));
                if (new FileInfo(path).Length > 512 * 1024)
                {
                    var all = File.ReadAllLines(path, Encoding.UTF8);
                    Config.AtomicWrite(path, string.Join("\n", all.Skip(Math.Max(0, all.Length - 400))) + "\n");
                }
            }
            catch { }
        }

        void LoadActivities()
        {
            try
            {
                string path = cfg.ActivityPath;
                if (!File.Exists(path)) return;
                var all = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string l in all.Skip(Math.Max(0, all.Length - 200)))
                {
                    if (l.Trim().Length == 0) continue;
                    object o;
                    try { o = Json.Parse(l); } catch { continue; }
                    DateTime t;
                    if (!DateTime.TryParse(Json.Str(Json.At(o, "t")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) continue;
                    activities.Add(new Activity
                    {
                        At = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t,
                        Kind = Json.Str(Json.At(o, "k")),
                        Number = Json.Int(Json.At(o, "n")),
                        Id = Json.Str(Json.At(o, "id")),
                        Title = Json.Str(Json.At(o, "ti")),
                        Detail = Json.Str(Json.At(o, "d"))
                    });
                }
            }
            catch { }
        }
    }
}
