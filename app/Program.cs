using System;
using System.Windows.Automation;
using System.Windows.Forms;

namespace KiroAutoContinue
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Config cfg = Config.Load();
            // Touch UI Automation so the linker pulls in UIAutomationClient/Types + WindowsBase early; the real
            // watcher and GUI replace this stub in the next rounds.
            AutomationElement root = AutomationElement.RootElement;
            object ui = Json.At(cfg.Words(cfg.UiLang()), "ui");
            string title = Json.Str(Json.At(ui, "appTitle"), "Kiro Auto Continue");
            MessageBox.Show(
                "Loaded config.\nData dir: " + cfg.DataDir + "\nUIA root found: " + (root != null),
                title);
        }
    }
}
