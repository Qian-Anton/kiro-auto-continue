# Win32 helpers: user idle time, keeping Windows awake, console QuickEdit, and typing into Chromium's chat box
# through MSAA.
# Keep every .ps1 in this project ASCII: Windows PowerShell 5.1 reads BOM-less scripts in the system code page.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, Accessibility
if (-not ('KacNative' -as [type])) {
Add-Type -ReferencedAssemblies Accessibility -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using Accessibility;
public static class KacNative {
  [StructLayout(LayoutKind.Sequential)] struct LII { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LII p);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
  [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
  [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);
  [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
  [DllImport("oleacc.dll")] static extern int AccessibleChildren(IAccessible acc, int start, int count, [Out] object[] kids, out int got);
  [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

  // While on, Windows does not go to sleep on idle (ES_CONTINUOUS | ES_SYSTEM_REQUIRED); the display may still turn
  // off, and the lid, the power button or Start > Sleep still put it to sleep. Holds while this thread lives.
  public static bool KeepAwake(bool on) { return SetThreadExecutionState(on ? 0x80000001u : 0x80000000u) != 0; }

  // Seconds since the last keyboard or mouse input in this session.
  public static uint IdleSeconds() {
    LII l = new LII(); l.cbSize = (uint)Marshal.SizeOf(l); GetLastInputInfo(ref l);
    return ((uint)Environment.TickCount - l.dwTime) / 1000;
  }
  // A click inside a console with QuickEdit on pauses the script until a key is pressed; turn QuickEdit off.
  public static void NoQuickEdit() {
    IntPtr h = GetStdHandle(-10); uint m;
    if (GetConsoleMode(h, out m)) SetConsoleMode(h, (m & ~0x40u) | 0x80u);
  }
  static bool Near(int a, int b) { return Math.Abs(a - b) <= 2; }
  // Walk the MSAA tree of a render window, going only into nodes whose box contains the target box.
  static IAccessible Search(IAccessible a, int L, int T, int W, int H, int depth) {
    if (depth > 80) return null;
    int n = 0; try { n = a.accChildCount; } catch { return null; }
    if (n <= 0) return null;
    object[] kids = new object[n]; int got = 0;
    if (AccessibleChildren(a, 0, n, kids, out got) != 0) return null;
    for (int i = 0; i < got; i++) {
      IAccessible c = kids[i] as IAccessible; if (c == null) continue;
      int l, t, w, h;
      try { c.accLocation(out l, out t, out w, out h, 0); } catch { continue; }
      if (Near(l, L) && Near(t, T) && Near(w, W) && Near(h, H)) return c;
      bool inside = l <= L + 2 && t <= T + 2 && l + w >= L + W - 2 && t + h >= T + H - 2;
      if (!inside && !(w == 0 && h == 0)) continue;
      IAccessible r = Search(c, L, T, W, H, depth + 1); if (r != null) return r;
    }
    return null;
  }
  // Put text into the chat box (a contenteditable: Chromium turns accValue into setInnerText). "" on success.
  public static string SetValue(IntPtr hwnd, int L, int T, int W, int H, string s) {
    Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); object o;
    if (AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref iid, out o) != 0) return "no MSAA root for the chat window";
    IAccessible a = Search((IAccessible)o, L, T, W, H, 0);
    if (a == null) return "chat box not found in the MSAA tree";
    try { a.set_accValue(0, s); } catch (Exception e) { return "set_accValue failed: " + e.Message; }
    return "";
  }
}
"@
}
[void][KacNative]::SetProcessDPIAware()

# The lock screen runs LogonUI.exe; the input desktop stays "Default" on Windows 11, so it cannot tell.
function Test-KacLocked { return [bool](Get-Process -Name LogonUI -ErrorAction SilentlyContinue) }
