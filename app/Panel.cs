using System.Collections.Generic;

namespace KiroAutoContinue
{
    // One item of the chat panel's message list: its class name and control type ("Button", "Image", ...).
    internal sealed class PanelItem
    {
        public readonly string Class, Type;
        public PanelItem(string cls, string type) { Class = cls ?? ""; Type = type ?? ""; }
    }

    // Where the newest user message sits in the chat panel's message list. Kept apart from UI Automation so the
    // tests can feed it made-up lists. In the list, each turn reads: a checkpoint bar, your message, the "Kiro"
    // label, the reply.
    internal static class Panel
    {
        const int Max = 80;
        static bool IsRole(PanelItem i) { return i.Class.Contains("agent-message-role"); }
        static bool IsBar(PanelItem i) { return i.Class.Contains("kiro-checkpoint-bar"); }
        static bool Ends(PanelItem i)
        {
            return i.Type == "Button" || IsBar(i) || IsRole(i) || i.Class.Contains("kiro-copy-more-options");
        }

        // The items of the newest user message, in order. Normally the ones just before the newest "Kiro" label.
        // A turn that ended before Kiro wrote anything (cut off within seconds, or stopped at once) has no label,
        // and neither has a turn that has only just begun: then the newest checkpoint bar comes after the newest
        // label, and your message is what follows that bar. (11-04: going by the label alone read the message
        // before it, so a session cut off again right after it was continued could not be continued any more.)
        public static List<int> UserItems(IList<PanelItem> items)
        {
            int role = -1, bar = -1;
            for (int i = items.Count - 1; i >= 0 && (role < 0 || bar < 0); i--)
            {
                if (role < 0 && IsRole(items[i])) role = i;
                if (bar < 0 && IsBar(items[i])) bar = i;
            }
            var idx = new List<int>();
            if (role > bar)
            {
                for (int i = role - 1; i >= 0 && i >= role - Max; i--)
                {
                    if (items[i].Type == "Image") continue;
                    if (Ends(items[i])) break;
                    idx.Insert(0, i);
                }
            }
            else if (bar >= 0)
            {
                for (int i = bar + 1; i < items.Count && i <= bar + Max; i++)
                {
                    if (items[i].Type == "Image") continue;
                    if (Ends(items[i])) break;
                    idx.Add(i);
                }
            }
            return idx;
        }
    }
}
