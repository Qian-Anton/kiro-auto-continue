using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KiroAutoContinue
{
    // --preview: the window with no watcher running (nothing is sent, typed or saved; the single-instance lock is
    // not taken, so it runs next to a real one). --shot=<png> draws it off screen at the screen's DPI, writes the
    // picture and <png>.txt (every text that is cut off, overlaps another, or sticks out of the window) and exits
    // with 1 if there was any.
    internal static class Preview
    {
        public static int Run(Options o)
        {
            Config cfg = Config.Load();
            if (o.Lang.Length > 0) cfg.Root["language"] = o.Lang;   // this run only
            var watcher = new Watcher(cfg, true);
            watcher.PreviewSeed(o.Sample);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var win = new MainWindow(cfg, watcher, true);
            if (o.Width > 0) win.Width = o.Width;
            if (o.Height > 0) win.Height = o.Height;
            win.SelectPage(o.Page);
            int code = 0;
            if (o.Shot.Length > 0)
            {
                // just right of every screen (not -32000: that is where Windows parks minimized windows, and
                // a window there is never drawn)
                win.ShowInTaskbar = false;
                win.ShowActivated = false;
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 200;
                win.Top = SystemParameters.VirtualScreenTop;
                bool done = false;
                win.Loaded += (s, e) =>
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    t.Tick += (s2, e2) =>
                    {
                        t.Stop();
                        if (done) return;
                        done = true;
                        try { code = Shot(win, o.Shot); }
                        catch (Exception ex) { File.WriteAllText(o.Shot + ".txt", "ERROR " + ex); code = 2; }
                        win.Close();
                    };
                    t.Start();
                };
                var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                guard.Tick += (s, e) =>
                {
                    guard.Stop();
                    if (done) return;
                    done = true;
                    File.WriteAllText(o.Shot + ".txt", "TIMEOUT: the window never got drawn");
                    code = 3;
                    win.Close();
                };
                guard.Start();
            }
            app.Run(win);
            return code;
        }

        static int Shot(Window win, string path)
        {
            var root = (FrameworkElement)win.Content;
            root.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(root);
            int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var under = new DrawingVisual();
            using (var dc = under.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            rtb.Render(under);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            var report = UiCheck.Run(root, dpi.PixelsPerDip);
            report.Insert(0, string.Format(CultureInfo.InvariantCulture, "window {0:0}x{1:0} at {2:0} dpi -> {3}x{4} px",
                root.ActualWidth, root.ActualHeight, dpi.PixelsPerInchX, w, h));
            File.WriteAllText(path + ".txt", string.Join("\r\n", report) + "\r\n", new UTF8Encoding(false));
            return report.Any(UiCheck.IsProblem) ? 1 : 0;
        }
    }

    // Checks every text on screen: CUT (a one-line text wider than its box, not trimmed with "..."), CUT-V (a
    // wrapped text taller than its box), CLIP (WPF clips it to its layout slot), OVERLAP (two texts on top of
    // each other), OUTSIDE (past the window's edge). "trimmed" only notes a text shortened with "...".
    internal static class UiCheck
    {
        public static bool IsProblem(string line)
        {
            return line.StartsWith("CUT", StringComparison.Ordinal) || line.StartsWith("CLIP", StringComparison.Ordinal)
                || line.StartsWith("OVERLAP", StringComparison.Ordinal) || line.StartsWith("OUTSIDE", StringComparison.Ordinal);
        }

        sealed class Seen { public TextBlock Tb; public Rect Ink; }

        public static List<string> Run(FrameworkElement root, double ppd)
        {
            var outp = new List<string>();
            var seen = new List<Seen>();
            Walk(root, root, ppd, outp, seen);
            for (int i = 0; i < seen.Count; i++)
                for (int j = i + 1; j < seen.Count; j++)
                {
                    var r = Rect.Intersect(seen[i].Ink, seen[j].Ink);
                    if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
                    outp.Add("OVERLAP [" + Short(seen[i].Tb.Text) + "] x [" + Short(seen[j].Tb.Text) + "] " + R(r));
                }
            outp.Insert(0, "texts " + seen.Count + ", problems " + outp.Count(IsProblem));
            return outp;
        }

        static void Walk(DependencyObject d, FrameworkElement root, double ppd, List<string> outp, List<Seen> seen)
        {
            var fe = d as FrameworkElement;
            if (fe != null && !fe.IsVisible) return;
            var tb = d as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text) && tb.ActualWidth > 0 && tb.ActualHeight > 0) Check(tb, root, ppd, outp, seen);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), root, ppd, outp, seen);
        }

        static void Check(TextBlock tb, FrameworkElement root, double ppd, List<string> outp, List<Seen> seen)
        {
            var tf = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection, tf, tb.FontSize, Brushes.Black,
                null, TextOptions.GetTextFormattingMode(tb), ppd);
            double need = ft.WidthIncludingTrailingWhitespace;
            bool wraps = tb.TextWrapping != TextWrapping.NoWrap;
            string name = "[" + Short(tb.Text) + "]";
            if (!wraps && need > tb.ActualWidth + 0.75)
                outp.Add((tb.TextTrimming == TextTrimming.None ? "CUT " : "trimmed ") + name + " needs " + need.ToString("0.#", CultureInfo.InvariantCulture)
                    + " has " + tb.ActualWidth.ToString("0.#", CultureInfo.InvariantCulture));
            if (wraps)
            {
                ft.MaxTextWidth = Math.Max(1, tb.ActualWidth);
                if (!double.IsNaN(tb.LineHeight)) ft.LineHeight = tb.LineHeight;
                if (ft.Height > tb.ActualHeight + 0.75)
                    outp.Add("CUT-V " + name + " needs " + ft.Height.ToString("0.#", CultureInfo.InvariantCulture) + " has " + tb.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture));
            }
            if (LayoutInformation.GetLayoutClip(tb) != null) outp.Add("CLIP " + name);

            // where the letters are: as wide as the text (not the whole stretched box), cut to what scroll views show
            double inkW = wraps ? tb.ActualWidth : Math.Min(need, tb.ActualWidth), x0 = 0;
            if (tb.TextAlignment == TextAlignment.Center) x0 = (tb.ActualWidth - inkW) / 2;
            else if (tb.TextAlignment == TextAlignment.Right) x0 = tb.ActualWidth - inkW;
            Rect ink = tb.TransformToAncestor(root).TransformBounds(new Rect(x0, 0, inkW, tb.ActualHeight));
            for (DependencyObject p = VisualTreeHelper.GetParent(tb); p != null && p != root; p = VisualTreeHelper.GetParent(p))
            {
                var sp = p as ScrollContentPresenter;
                if (sp == null) continue;
                ink.Intersect(sp.TransformToAncestor(root).TransformBounds(new Rect(0, 0, sp.ActualWidth, sp.ActualHeight)));
                if (ink.IsEmpty) return;   // scrolled out of view
            }
            if (ink.Left < -0.5 || ink.Top < -0.5 || ink.Right > root.ActualWidth + 0.5 || ink.Bottom > root.ActualHeight + 0.5)
                outp.Add("OUTSIDE " + name + " " + R(ink));
            seen.Add(new Seen { Tb = tb, Ink = ink });
        }

        static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > 28 ? s.Substring(0, 28) + "..." : s;
        }

        static string R(Rect r)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0},{1:0} {2:0}x{3:0})", r.X, r.Y, r.Width, r.Height);
        }
    }
}
