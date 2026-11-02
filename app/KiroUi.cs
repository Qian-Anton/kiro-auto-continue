using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace KiroAutoContinue
{
    internal sealed class KiroWindow { public AutomationElement El; public IntPtr Hwnd; public string Name; public string Workspace; }
    internal sealed class KiroTab { public AutomationElement El; public string Id; public string Title; public bool Selected; public string State; }
    internal sealed class KiroInput { public AutomationElement Editor; public AutomationElement Send; public bool SendEnabled; public string Text; public bool Empty; }
    internal sealed class TabHit { public KiroWindow Win; public AutomationElement Root; public KiroTab Tab; }
    internal sealed class TabFind { public int Count; public TabHit Hit; }

    // Reading and driving the Kiro chat panel through UI Automation. Port of src/KiroUi.ps1. All calls run on the
    // watcher's STA thread, which first calls Init() to search the raw view (Chromium's plain divs are not in the
    // control view).
    internal static class KiroUi
    {
        static readonly TreeWalker Walker = TreeWalker.RawViewWalker;
        static readonly Condition Any = Condition.TrueCondition;
        static readonly Dictionary<IntPtr, AutomationElement> Roots = new Dictionary<IntPtr, AutomationElement>();

        public static void Init()
        {
            var cr = new CacheRequest { TreeFilter = Automation.RawViewCondition };
            cr.Push();
            Native.SetProcessDPIAware();
        }

        static Condition ByClass(string c) { return new PropertyCondition(AutomationElement.ClassNameProperty, c); }
        static Condition ByType(ControlType t) { return new PropertyCondition(AutomationElement.ControlTypeProperty, t); }
        static Condition ByName(string n) { return new PropertyCondition(AutomationElement.NameProperty, n); }

        public static bool Locked() { return Process.GetProcessesByName("LogonUI").Length > 0; }

        // Top-level Kiro windows. The title reads "<editor tab> - <workspace> - Kiro".
        public static List<KiroWindow> GetWindows()
        {
            var ids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("Kiro")) ids.Add(p.Id);
            var outp = new List<KiroWindow>();
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Any))
            {
                try
                {
                    var c = w.Current;
                    if (!ids.Contains(c.ProcessId) || c.ClassName != "Chrome_WidgetWin_1") continue;
                    string name = c.Name ?? "";
                    var parts = name.Split(new[] { " - " }, StringSplitOptions.None);
                    string ws = parts.Length >= 2 ? parts[parts.Length - 2] : name;
                    outp.Add(new KiroWindow { El = w, Hwnd = new IntPtr(c.NativeWindowHandle), Name = name, Workspace = ws });
                }
                catch { }
            }
            return outp;
        }

        // The chat panel's root (id=root, holding the tab bar), cached per window.
        public static AutomationElement GetChatRoot(KiroWindow win)
        {
            AutomationElement r;
            if (Roots.TryGetValue(win.Hwnd, out r) && r != null)
            {
                try { if (r.FindFirst(TreeScope.Descendants, ByClass("tab-bar")) != null) return r; } catch { }
                Roots.Remove(win.Hwnd);
            }
            var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, "root");
            try
            {
                foreach (AutomationElement c in win.El.FindAll(TreeScope.Descendants, byId))
                    if (c.FindFirst(TreeScope.Descendants, ByClass("tab-bar")) != null) { Roots[win.Hwnd] = c; return c; }
            }
            catch { }
            return null;
        }

        public static List<KiroTab> GetTabs(AutomationElement root)
        {
            var tabs = new List<KiroTab>();
            foreach (AutomationElement t in root.FindAll(TreeScope.Descendants, ByClass("tab-bar-item")))
            {
                bool sel = false;
                try { sel = ((SelectionItemPattern)t.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected; } catch { }
                string title = t.Current.Name ?? "";
                var label = t.FindFirst(TreeScope.Descendants, ByClass("tab-bar-item-label"));
                AutomationElement lt = label != null ? label.FindFirst(TreeScope.Descendants, ByType(ControlType.Text)) : null;
                if (lt != null && !string.IsNullOrEmpty(lt.Current.Name)) title = lt.Current.Name;
                else { int ix = title.IndexOf(" Close "); if (ix >= 0) title = title.Substring(ix + 7); }
                var ind = t.FindFirst(TreeScope.Children, ByClass("tab-bar-item-indicator"));
                string state = ind != null ? (ind.Current.Name ?? "") : "";
                tabs.Add(new KiroTab { El = t, Id = string.Join(".", t.GetRuntimeId()), Title = title, Selected = sel, State = state });
            }
            return tabs;
        }

        public static KiroTab FindTab(AutomationElement root, string id)
        {
            foreach (var t in GetTabs(root)) if (t.Id == id) return t;
            return null;
        }

        // Select a tab and wait until it is the one shown.
        public static bool ShowTab(AutomationElement root, string id)
        {
            for (int k = 0; k < 2; k++)
            {
                var t = FindTab(root, id);
                if (t == null) return false;
                if (t.Selected) return true;
                try { ((SelectionItemPattern)t.El.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); } catch { }
                for (int i = 0; i < 10; i++) { Thread.Sleep(500); t = FindTab(root, id); if (t != null && t.Selected) return true; }
            }
            return false;
        }

        static AutomationElement ItemList(AutomationElement root)
        {
            var c = root.FindFirst(TreeScope.Descendants, ByClass("session-view-content"));
            return c ?? root.FindFirst(TreeScope.Descendants, ByClass("session-view-timeline"));
        }

        static string TextOf(AutomationElement el, string sep = "")
        {
            var cur = el.Current;
            if (cur.ControlType == ControlType.List)
            {
                var lines = new List<string>();
                foreach (AutomationElement li in el.FindAll(TreeScope.Children, ByType(ControlType.ListItem))) lines.Add((li.Current.Name ?? "").Trim());
                if (lines.Count > 0) return string.Join("\n", lines);
            }
            string n = cur.Name ?? "";
            if (n.Trim().Length > 0) return n;
            var parts = new List<string>();
            foreach (AutomationElement t in el.FindAll(TreeScope.Descendants, ByType(ControlType.Text))) parts.Add(t.Current.Name ?? "");
            return string.Join(sep, parts);
        }

        // The newest user message the panel shows: everything between the newest "Kiro" label and the item before it.
        static string ShownUser(AutomationElement root)
        {
            var content = ItemList(root);
            if (content == null) return "";
            var roles = content.FindAll(TreeScope.Children, ByClass("agent-message-role"));
            if (roles.Count == 0) return "";
            var parts = new List<string>();
            AutomationElement p = roles[roles.Count - 1];
            for (int i = 0; i < 80; i++)
            {
                p = Walker.GetPreviousSibling(p);
                if (p == null) break;
                var c = p.Current; string cls = c.ClassName ?? "";
                if (c.ControlType == ControlType.Image) continue;
                if (c.ControlType == ControlType.Button || cls.Contains("kiro-checkpoint-bar") || cls.Contains("kiro-copy-more-options") || cls.Contains("agent-message-role")) break;
                parts.Insert(0, TextOf(p, "\n"));
            }
            return string.Join("\n", parts);
        }

        static string ViewMark(AutomationElement root)
        {
            var content = ItemList(root);
            if (content == null) return "none";
            var bars = content.FindAll(TreeScope.Children, ByClass("kiro-checkpoint-bar"));
            var last = Walker.GetLastChild(content);
            string lastName = last != null ? (last.Current.Name ?? "").Trim() : "";
            return bars.Count + "|" + ShownUser(root).Trim() + "|" + lastName;
        }

        // Show a tab and wait until its contents reach UIA and stop changing.
        public static bool OpenTab(AutomationElement root, string id, int seconds = 20)
        {
            var t = FindTab(root, id);
            if (t == null) return false;
            if (t.Selected) return true;
            string before = ViewMark(root);
            if (!ShowTab(root, id)) return false;
            bool changed = false;
            for (int i = 0; i < seconds * 2 && !changed; i++) { Thread.Sleep(500); changed = ViewMark(root) != before; }
            if (!changed) return false;
            string a = ViewMark(root);
            for (int i = 0; i < 10; i++) { Thread.Sleep(1000); string b = ViewMark(root); if (b == a) return true; a = b; }
            return false;
        }

        static string Squash(string s) { return Regex.Replace(s ?? "", @"[\s\p{P}\p{S}]", ""); }

        public static bool SameText(string a, string b)
        {
            string x = Squash(a), y = Squash(b);
            int n = Math.Min(20, Math.Min(x.Length, y.Length));
            if (n == 0) return x.Length == 0 && y.Length == 0;
            return x.Substring(0, n) == y.Substring(0, n);
        }

        public static bool WaitShownUser(AutomationElement root, string text, int seconds = 20)
        {
            for (int i = 0; i <= seconds; i++) { if (SameText(ShownUser(root), text)) return true; Thread.Sleep(1000); }
            return false;
        }

        // The tab of a session: in the Kiro window of its workspace (any if none matches), the tabs with its title.
        public static TabFind FindSessionTab(SessionInfo info)
        {
            var wins = GetWindows();
            var mine = wins.Where(w => w.Workspace == info.Workspace).ToList();
            if (mine.Count == 0) mine = wins;
            var hits = new List<TabHit>();
            foreach (var w in mine)
            {
                var root = GetChatRoot(w);
                if (root == null) continue;
                foreach (var t in GetTabs(root)) if (t.Title == info.Title) hits.Add(new TabHit { Win = w, Root = root, Tab = t });
            }
            return new TabFind { Count = hits.Count, Hit = hits.Count == 1 ? hits[0] : null };
        }

        public static KiroInput GetInput(AutomationElement root, List<string> placeholders)
        {
            var wrap = root.FindFirst(TreeScope.Descendants, ByClass("chat-input-wrapper"));
            if (wrap == null) return null;
            AutomationElement ed = null, send = null;
            foreach (AutomationElement x in wrap.FindAll(TreeScope.Descendants, Any))
            {
                var c = x.Current;
                if (ed == null && (c.ClassName ?? "").Contains("chat-input-content")) ed = x;
                if (c.ControlType == ControlType.Button)
                {
                    var icon = Walker.GetFirstChild(x);
                    if (icon != null && (icon.Current.ClassName ?? "").Contains("codicon-arrow-up")) send = x;
                }
            }
            var txt = new List<string>();
            if (ed != null)
                foreach (AutomationElement t in ed.FindAll(TreeScope.Descendants, ByType(ControlType.Text)))
                {
                    string n = t.Current.Name ?? "";
                    if (n.Trim().Length > 0 && !placeholders.Contains(n.Trim())) txt.Add(n);
                }
            bool en = send != null && send.Current.IsEnabled;
            bool empty = txt.Count == 0 || (send != null && !en);
            return new KiroInput { Editor = ed, Send = send, SendEnabled = en, Text = string.Join("\n", txt), Empty = empty };
        }

        static string SetInputText(KiroInput inp, string s)
        {
            AutomationElement p = inp.Editor; IntPtr h = IntPtr.Zero;
            while (p != null)
            {
                if (p.Current.ClassName == "Chrome_RenderWidgetHostHWND") { h = new IntPtr(p.Current.NativeWindowHandle); break; }
                p = Walker.GetParent(p);
            }
            if (h == IntPtr.Zero) return "no render window above the chat box";
            var r = inp.Editor.Current.BoundingRectangle;
            return Native.SetValue(h, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, s);
        }

        static bool Holds(KiroInput inp, string text)
        {
            string want = Squash(text), got = Squash(inp.Text ?? "");
            if (want.Length == 0) return false;
            return got.StartsWith(want.Substring(0, Math.Min(12, want.Length)));
        }

        public static bool NewSession(AutomationElement root)
        {
            var b = root.FindFirst(TreeScope.Descendants, ByName("New session"));
            if (b == null) return false;
            try { ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { return false; }
            return true;
        }

        public static bool CloseTab(AutomationElement root, string id)
        {
            var t = FindTab(root, id);
            if (t == null) return false;
            var b = t.El.FindFirst(TreeScope.Descendants, ByClass("tab-bar-item-close"));
            if (b == null) return false;
            try { ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { return false; }
            return true;
        }

        // Type text into the shown session's chat box and send it, then wait for its tab to say Working.
        // "" once sent, otherwise why not. Never types over a draft (returns "draft").
        public static string SendMessage(AutomationElement root, string id, string text, List<string> placeholders)
        {
            var inp = GetInput(root, placeholders);
            if (inp == null || inp.Editor == null) return "chat box not found";
            if (!inp.Empty) return "draft";
            bool typed = false; string why = "";
            for (int attempt = 0; attempt < 2 && !typed; attempt++)
            {
                string err = SetInputText(inp, text);
                if (!string.IsNullOrEmpty(err)) why = err;
                for (int i = 0; i < 30 && !typed; i++)
                {
                    Thread.Sleep(500);
                    inp = GetInput(root, placeholders);
                    if (inp == null || inp.Editor == null) return "chat box not found";
                    typed = Holds(inp, text);
                }
                if (!typed && !inp.Empty) return "the box holds something else: [" + Policy.Short(inp.Text, 40) + "]";
            }
            if (!typed) return why.Length > 0 ? why : "the text never showed up in the box";
            for (int i = 0; i < 20 && !inp.SendEnabled; i++) { Thread.Sleep(500); inp = GetInput(root, placeholders); }
            if (inp == null || !inp.SendEnabled) return "the send button stays greyed out";
            for (int k = 0; k < 2; k++)
            {
                if (k > 0)
                {
                    inp = GetInput(root, placeholders);
                    if (inp == null) break;
                    if (!Holds(inp, text)) return "";       // the box emptied: it went out
                    if (!inp.SendEnabled) break;
                }
                try { ((InvokePattern)inp.Send.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { }
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(1000);
                    var t = FindTab(root, id);
                    if (t != null && t.State == "Working") return "";
                }
            }
            inp = GetInput(root, placeholders);
            if (inp != null && !Holds(inp, text)) return "";
            return "pressed send twice; the text is still in the box";
        }

        // Stop the running turn of the shown session with its Cancel button. "" once its tab leaves Working.
        public static string StopTurn(AutomationElement root, string id)
        {
            var cond = new AndCondition(ByType(ControlType.Button), ByName("Cancel"));
            var all = root.FindAll(TreeScope.Descendants, cond);
            if (all.Count == 0) return "noCancel";
            try { ((InvokePattern)all[all.Count - 1].GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { }
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(500);
                var t = FindTab(root, id);
                if (t != null && t.State != "Working") return "";
            }
            return "the tab still says Working";
        }
    }
}
