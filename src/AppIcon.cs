using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GoogleFlowDesktop
{
    /// <summary>Builds the application icon at runtime (no .ico asset needed).</summary>
    public static class AppIcon
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(out IntPtr handle)
        {
            Bitmap bmp = null;
            Graphics g = null;
            try
            {
                bmp = new Bitmap(64, 64);
                g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(11, 12, 16));

                using (Brush bg = new SolidBrush(Color.FromArgb(20, 22, 32)))
                using (GraphicsPath path = Theme.Rounded(new Rectangle(2, 2, 60, 60), 14))
                {
                    g.FillPath(bg, path);
                }

                Point[] bolt = new Point[]
                {
                    new Point(38, 6),
                    new Point(18, 36),
                    new Point(31, 36),
                    new Point(25, 58),
                    new Point(46, 27),
                    new Point(33, 27)
                };

                using (Brush b = new LinearGradientBrush(new Rectangle(14, 4, 34, 56), Theme.Accent, Theme.AccentDeep, 90f))
                {
                    g.FillPolygon(b, bolt);
                }

                handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
            catch
            {
                handle = IntPtr.Zero;
                return SystemIcons.Application;
            }
            finally
            {
                if (g != null) g.Dispose();
                if (bmp != null) bmp.Dispose();
            }
        }

        public static void Release(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                try { DestroyIcon(handle); } catch { }
            }
        }
    }
}
