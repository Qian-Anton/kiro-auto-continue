using System;
using System.Runtime.InteropServices;
using Accessibility;

namespace KiroAutoContinue
{
    // Win32 helpers: user idle time, keeping Windows awake, and typing into Chromium's chat box through MSAA.
    // Port of the C# embedded in src/Native.ps1.
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [DllImport("oleacc.dll")] private static extern int AccessibleChildren(
            IAccessible acc, int start, int count, [Out] object[] kids, out int got);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // While on, Windows does not go to sleep on idle. Holds on the calling thread until reset or it exits.
        public static bool KeepAwake(bool on)
        {
            return SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS) != 0;
        }

        // Seconds since the last keyboard or mouse input in this session.
        public static uint IdleSeconds()
        {
            var l = new LASTINPUTINFO();
            l.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            GetLastInputInfo(ref l);
            return ((uint)Environment.TickCount - l.dwTime) / 1000;
        }

        private static bool Near(int a, int b) { return Math.Abs(a - b) <= 2; }

        // Walk the MSAA tree of a render window, going only into nodes whose box contains the target box.
        private static IAccessible Search(IAccessible a, int L, int T, int W, int H, int depth)
        {
            if (depth > 80) return null;
            int n;
            try { n = a.accChildCount; } catch { return null; }
            if (n <= 0) return null;
            var kids = new object[n];
            int got;
            if (AccessibleChildren(a, 0, n, kids, out got) != 0) return null;
            for (int i = 0; i < got; i++)
            {
                IAccessible c = kids[i] as IAccessible;
                if (c == null) continue;
                int l, t, w, h;
                try { c.accLocation(out l, out t, out w, out h, 0); } catch { continue; }
                if (Near(l, L) && Near(t, T) && Near(w, W) && Near(h, H)) return c;
                bool inside = l <= L + 2 && t <= T + 2 && l + w >= L + W - 2 && t + h >= T + H - 2;
                if (!inside && !(w == 0 && h == 0)) continue;
                IAccessible r = Search(c, L, T, W, H, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        // Put text into the chat box (a contenteditable: Chromium turns accValue into setInnerText). "" on success.
        public static string SetValue(IntPtr hwnd, int L, int T, int W, int H, string s)
        {
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            object o;
            if (AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref iid, out o) != 0) return "no MSAA root for the chat window";
            IAccessible a = Search(o as IAccessible, L, T, W, H, 0);
            if (a == null) return "chat box not found in the MSAA tree";
            try { a.set_accValue(0, s); }
            catch (Exception e) { return "set_accValue failed: " + e.Message; }
            return "";
        }
    }
}
