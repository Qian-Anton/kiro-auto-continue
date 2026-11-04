using System.Collections.Generic;

namespace KiroAutoContinue
{
    // Panel.cs: which items of the chat panel's message list hold the newest user message. Made-up lists, in the
    // shape Kiro's panel has: per turn a checkpoint bar, your message, the "Kiro" label, the reply (paragraphs,
    // tool-call toggles, the copy menu).
    internal static class PanelTests
    {
        static PanelItem Bar() { return new PanelItem("kiro-checkpoint-bar", "Group"); }
        static PanelItem Msg() { return new PanelItem("", "Group"); }
        static PanelItem Txt() { return new PanelItem("", "Text"); }
        static PanelItem Img() { return new PanelItem("", "Image"); }
        static PanelItem Role() { return new PanelItem("agent-message-role", "Text"); }
        static PanelItem Toggle() { return new PanelItem("collapsible-toggle", "Button"); }
        static PanelItem Copy() { return new PanelItem("kiro-copy-more-options", "Group"); }
        static string Pick(params PanelItem[] items) { return string.Join(",", Panel.UserItems(new List<PanelItem>(items))); }

        public static void Run()
        {
            // indices:   0      1      2       3      4         5       6      7      8       9
            T.Check("two turns with replies: the newest message", () => Pick(Bar(), Msg(), Role(), Msg(), Toggle(), Copy(), Bar(), Msg(), Role(), Msg()) == "7");
            T.Check("its message in several items, an image skipped", () => Pick(Bar(), Msg(), Role(), Msg(), Bar(), Txt(), Img(), Msg(), Role(), Msg()) == "5,7");
            // 11-04: the newest turn ended before Kiro wrote anything (no label under it)
            T.Check("the newest turn got no reply: its own message, not the one before", () => Pick(Bar(), Msg(), Role(), Msg(), Copy(), Bar(), Msg()) == "6");
            T.Check("no reply, an error and a retry button under it: up to the button", () => Pick(Bar(), Msg(), Role(), Msg(), Copy(), Bar(), Msg(), Txt(), Toggle()) == "6,7");
            T.Check("a new session whose first turn got no reply", () => Pick(Bar(), Msg()) == "1");
            T.Check("a turn that has only just begun", () => Pick(Bar(), Msg(), Role(), Msg(), Bar(), Msg()) == "5");
            // as before for everything else
            T.Check("no checkpoint bars: back from the label, as before", () => Pick(Msg(), Role(), Msg()) == "0");
            T.Check("back from the label it stops at a button, as before", () => Pick(Bar(), Msg(), Toggle(), Txt(), Role(), Msg()) == "3");
            T.Check("a bar right before the label: nothing", () => Pick(Msg(), Role(), Msg(), Bar(), Role(), Msg()) == "");
            T.Check("no bar and no label: nothing", () => Pick(Msg(), Msg()) == "");
            T.Check("an empty list: nothing", () => Pick() == "");
        }
    }
}
