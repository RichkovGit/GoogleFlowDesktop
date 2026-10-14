using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>
    /// Visual language of the app: a dark "Obsidian / Fluent" palette plus small
    /// native control building blocks (rounded cards, flat buttons, chip toggles).
    /// Everything here is 100% native GDI+ — there is no HTML/CSS anywhere.
    /// </summary>
    public static class Theme
    {
        // ---- Palette (mirrors the original Obsidian design tokens) ----
        public static readonly Color BgBase = Color.FromArgb(11, 12, 16);
        public static readonly Color BgRail = Color.FromArgb(14, 15, 22);
        public static readonly Color BgSurface = Color.FromArgb(19, 20, 29);
        public static readonly Color BgCard = Color.FromArgb(23, 24, 34);
        public static readonly Color BgCardHover = Color.FromArgb(30, 32, 46);
        public static readonly Color BgInput = Color.FromArgb(15, 16, 23);
        public static readonly Color BorderSubtle = Color.FromArgb(38, 42, 56);
        public static readonly Color BorderCard = Color.FromArgb(42, 47, 62);

        public static readonly Color Accent = Color.FromArgb(0, 210, 255);
        public static readonly Color AccentHover = Color.FromArgb(56, 189, 248);
        public static readonly Color AccentDeep = Color.FromArgb(0, 150, 205);
        public static readonly Color AccentSoft = Color.FromArgb(22, 0, 210, 255);
        public static readonly Color Purple = Color.FromArgb(168, 85, 247);
        public static readonly Color Green = Color.FromArgb(52, 211, 153);
        public static readonly Color Amber = Color.FromArgb(245, 158, 11);
        public static readonly Color Red = Color.FromArgb(248, 113, 113);

        public static readonly Color TextMain = Color.FromArgb(248, 250, 252);
        public static readonly Color TextMuted = Color.FromArgb(148, 163, 184);
        public static readonly Color TextDim = Color.FromArgb(100, 116, 139);

        // ---- Cached fonts (never create fonts per paint) ----
        public static readonly Font F8 = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font F9 = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font F9B = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font F10 = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font F10B = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font F11B = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font F12B = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Mono = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------------
        // Win32: native dark title bar, rounded corners, dark common controls
        // ------------------------------------------------------------------
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subApp, string subIdList);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int appMode);

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /// <summary>Opt the whole process into the dark common-control theme.</summary>
        public static void EnableDarkMode()
        {
            try { SetPreferredAppMode(2 /* ForceDark */); } catch { }
        }

        /// <summary>Paint the standard title bar dark + rounded corners (Win10/11).</summary>
        public static void ApplyWindowChrome(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try
            {
                if (Environment.OSVersion.Version.Major < 10) return;
                int on = 1;
                if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
                }
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }
        }

        /// <summary>Dark scrollbars / borders for a single native control.</summary>
        public static void DarkenControl(Control c)
        {
            if (c == null) return;
            try
            {
                if (c.IsHandleCreated)
                {
                    AllowDarkModeForWindow(c.Handle, true);
                    SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
                }
            }
            catch { }
        }

        public static void SetDoubleBuffered(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(c, true, null);
            }
            catch { }
        }
    }

    /// <summary>A rounded, bordered card container (used as the page building block).</summary>
    public class CardLayout : TableLayoutPanel
    {
        public int Radius { get; set; }
        public Color BorderColor { get; set; }

        public CardLayout()
        {
            Radius = 12;
            BorderColor = Theme.BorderCard;
            BackColor = Theme.BgCard;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16, 12, 16, 14);
            Margin = new Padding(0, 0, 0, 14);
            Anchor = AnchorStyles.Left | AnchorStyles.Right;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Appends a full-width block as a new auto-sized row.</summary>
        public void AddBlock(Control child)
        {
            int row = RowStyles.Count;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowCount = RowStyles.Count;
            child.Dock = DockStyle.Top;
            child.Margin = new Padding(0, 0, 0, 8);
            Controls.Add(child, 0, row);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Radius))
            using (SolidBrush brush = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Radius))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.DrawPath(pen, path);
            }
            base.OnPaint(e);
        }
    }

    public enum ButtonKind
    {
        Accent,
        Ghost,
        Nav,
        Rail,
        Chip,
        Danger
    }

    /// <summary>Fully owner-drawn flat button. One class covers accents, ghosts, chips and rail items.</summary>
    public class FlatButton : Button
    {
        private bool hover;
        private bool pressed;
        private bool selected;

        public ButtonKind Kind { get; set; }
        public int Radius { get; set; }
        public Color AccentColor { get; set; }
        public bool Pill { get; set; }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected != value)
                {
                    selected = value;
                    Invalidate();
                }
            }
        }

        public FlatButton()
        {
            Kind = ButtonKind.Ghost;
            Radius = 8;
            AccentColor = Theme.Accent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.BgCard;
            ForeColor = Theme.TextMain;
            Font = Theme.F9B;
            Cursor = Cursors.Hand;
            Height = 32;
            TabStop = true;
            Padding = new Padding(10, 0, 10, 0);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            pressed = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BgBase);

            Color fill;
            Color border;
            Color fore;
            int radius = Pill ? Height / 2 : Radius;

            switch (Kind)
            {
                case ButtonKind.Accent:
                    fill = pressed ? Theme.AccentDeep : (hover ? Theme.AccentHover : Theme.AccentDeep);
                    if (!hover && !pressed) fill = Theme.Accent;
                    border = Color.Empty;
                    fore = Color.FromArgb(6, 12, 18);
                    break;
                case ButtonKind.Danger:
                    fill = hover ? Color.FromArgb(120, 30, 40) : Color.FromArgb(60, 24, 32);
                    border = Color.FromArgb(120, Theme.Red);
                    fore = Theme.Red;
                    break;
                case ButtonKind.Nav:
                    fill = selected ? Theme.BgCardHover : (hover ? Theme.BgSurface : Color.Transparent);
                    border = Color.Empty;
                    fore = selected ? AccentColor : Theme.TextMuted;
                    break;
                case ButtonKind.Rail:
                    fill = selected ? Theme.AccentSoft : (hover ? Theme.BgSurface : Color.Transparent);
                    border = Color.Empty;
                    fore = selected ? AccentColor : Theme.TextMuted;
                    break;
                case ButtonKind.Chip:
                    fill = selected ? Theme.AccentSoft : (hover ? Theme.BgCardHover : Theme.BgInput);
                    border = selected ? AccentColor : (hover ? Theme.BorderCard : Theme.BorderSubtle);
                    fore = selected ? AccentColor : Theme.TextMuted;
                    break;
                default: // Ghost
                    if (selected)
                    {
                        fill = Theme.AccentSoft;
                        border = AccentColor;
                        fore = AccentColor;
                    }
                    else
                    {
                        fill = pressed ? Theme.BgSurface : (hover ? Theme.BgCardHover : Theme.BgCard);
                        border = hover ? Theme.BorderCard : Theme.BorderSubtle;
                        fore = Theme.TextMain;
                    }
                    break;
            }

            if (!Enabled)
            {
                fore = Theme.TextDim;
                fill = Theme.BgSurface;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (fill != Color.Transparent)
            {
                using (GraphicsPath path = Theme.Rounded(r, radius))
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
            }

            if (Kind == ButtonKind.Rail && selected)
            {
                using (SolidBrush b = new SolidBrush(AccentColor))
                {
                    g.FillRectangle(b, new Rectangle(0, 8, 3, Height - 16));
                }
            }

            if (border != Color.Empty)
            {
                using (GraphicsPath path = Theme.Rounded(r, radius))
                using (Pen pen = new Pen(border))
                {
                    g.DrawPath(pen, path);
                }
            }

            Rectangle textRect = new Rectangle(Padding.Left, 0, Width - Padding.Left - Padding.Right, Height);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Text, Font, textRect, fore, flags);

            if (Focused && ShowFocusCues)
            {
                Rectangle fr = new Rectangle(2, 2, Width - 5, Height - 5);
                ControlPaint.DrawFocusRectangle(g, fr, fore, Color.Transparent);
            }
        }
    }
}
