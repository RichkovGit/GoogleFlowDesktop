using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>Rounded container that hosts a borderless native TextBox.</summary>
    public class InputHost : Panel
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private const int EM_SETCUEBANNER = 0x1501;

        public TextBox Inner { get; private set; }
        public int Radius { get; set; }
        private bool focused;
        private string cue;

        public InputHost()
        {
            Radius = 10;
            BackColor = Theme.BgInput;
            Padding = new Padding(10, 9, 10, 9);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Inner = new TextBox();
            Inner.BorderStyle = BorderStyle.None;
            Inner.BackColor = Theme.BgInput;
            Inner.ForeColor = Theme.TextMain;
            Inner.Font = Theme.F10;
            Inner.Dock = DockStyle.Fill;
            Inner.ShortcutsEnabled = true;
            Controls.Add(Inner);

            Inner.GotFocus += (s, e) => { focused = true; Invalidate(); };
            Inner.LostFocus += (s, e) => { focused = false; Invalidate(); };
        }

        public override string Text
        {
            get { return Inner.Text; }
            set { Inner.Text = value; }
        }

        /// <summary>Multiline must be configured before the handle is created or the cue banner is applied.</summary>
        public void MakeMultiline(int visibleLines, bool scrollBar)
        {
            Inner.Multiline = true;
            Inner.WordWrap = true;
            Inner.AcceptsReturn = true;
            Inner.ScrollBars = scrollBar ? ScrollBars.Vertical : ScrollBars.None;
            Inner.Height = Inner.Font.Height * visibleLines + 8;
        }

        public void SetCue(string text)
        {
            cue = text;
            ApplyCue();
        }

        public void SetReadOnly(bool ro)
        {
            Inner.ReadOnly = ro;
            Inner.TabStop = !ro;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCue();
            Theme.DarkenControl(this);
        }

        private void ApplyCue()
        {
            if (!Inner.IsHandleCreated || string.IsNullOrEmpty(cue)) return;
            try { SendMessage(Inner.Handle, EM_SETCUEBANNER, (IntPtr)1, cue); } catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Radius))
            using (SolidBrush b = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(b, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Radius))
            using (Pen p = new Pen(focused ? Theme.Accent : Theme.BorderCard, focused ? 1.6f : 1f))
            {
                e.Graphics.DrawPath(p, path);
            }
            base.OnPaint(e);
        }
    }

    /// <summary>iOS/Win11 style animated on/off switch.</summary>
    public class ToggleSwitch : CheckBox
    {
        private bool hover;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Appearance = Appearance.Button;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TextAlign = ContentAlignment.MiddleLeft;
            Font = Theme.F9;
            ForeColor = Theme.TextMain;
            Cursor = Cursors.Hand;
            AutoSize = false;
            Height = 26;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BgCard);

            int w = 38;
            int h = 20;
            Rectangle track = new Rectangle(0, (Height - h) / 2, w, h);
            Color trackColor = Checked ? Theme.Accent : (hover ? Color.FromArgb(70, 76, 92) : Color.FromArgb(52, 57, 72));

            using (GraphicsPath path = Theme.Rounded(track, h / 2))
            using (SolidBrush b = new SolidBrush(trackColor))
            {
                g.FillPath(b, path);
            }

            int d = h - 4;
            int kx = Checked ? track.Right - d - 2 : track.X + 2;
            Rectangle knob = new Rectangle(kx, track.Y + 2, d, d);
            using (SolidBrush b = new SolidBrush(Checked ? Color.FromArgb(8, 16, 22) : Color.FromArgb(220, 226, 236)))
            {
                g.FillEllipse(b, knob);
            }

            Rectangle textRect = new Rectangle(w + 10, 0, Width - w - 10, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// A FlowLayoutPanel that reports a width-aware preferred size, so wrapping
    /// works correctly when it lives inside an auto-sized TableLayoutPanel row.
    /// </summary>
    public class WrapPanel : FlowLayoutPanel
    {
        public WrapPanel()
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            WrapContents = true;
            FlowDirection = FlowDirection.LeftToRight;
            Margin = new Padding(0);
            Padding = new Padding(0);
            BackColor = Theme.BgCard;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width;
            if (w <= 1 && Parent != null) w = Parent.ClientSize.Width;
            if (w <= 1) w = 620;
            return base.GetPreferredSize(new Size(w, 0));
        }
    }

    /// <summary>Small static factory helpers shared by the native panels.</summary>
    public static class Ui
    {
        public static Label Title(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.F10B;
            l.ForeColor = Theme.TextMain;
            l.AutoSize = true;
            l.Margin = new Padding(0, 1, 8, 6);
            return l;
        }

        public static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.F8;
            l.ForeColor = Theme.TextDim;
            l.AutoSize = true;
            l.Margin = new Padding(0, 3, 0, 8);
            return l;
        }

        public static Label Muted(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.F9;
            l.ForeColor = Theme.TextMuted;
            l.AutoSize = true;
            l.Margin = new Padding(0, 2, 0, 2);
            return l;
        }

        public static Label Section(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.F9B;
            l.ForeColor = Theme.TextMuted;
            l.AutoSize = true;
            l.Margin = new Padding(0, 8, 0, 6);
            return l;
        }

        public static Label Value(string text, Color color, float size, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.Font = bold ? Theme.F11B : Theme.F10;
            l.AutoSize = true;
            l.Margin = new Padding(0, 2, 0, 0);
            return l;
        }

        public static FlatButton Button(string text, ButtonKind kind, int width)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Kind = kind;
            b.Width = width;
            return b;
        }

        public static FlatButton Chip(string text)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Kind = ButtonKind.Chip;
            b.Font = Theme.F8;
            b.Pill = true;
            b.Height = 28;
            b.Padding = new Padding(12, 0, 12, 0);
            b.Margin = new Padding(0, 0, 6, 6);
            return b;
        }

        /// <summary>Header row: title on the left, actions (right aligned) on the right.</summary>
        public static TableLayoutPanel HeaderRow(string title, string hint, params Control[] rightActions)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowCount = 1;
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.Dock = DockStyle.Top;
            row.Margin = new Padding(0, 0, 0, 6);
            row.BackColor = Theme.BgCard;

            FlowLayoutPanel left = new FlowLayoutPanel();
            left.AutoSize = true;
            left.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            left.WrapContents = false;
            left.FlowDirection = FlowDirection.LeftToRight;
            left.Margin = new Padding(0);
            left.BackColor = Theme.BgCard;
            left.Controls.Add(Title(title));
            if (!string.IsNullOrEmpty(hint)) left.Controls.Add(Hint(hint));

            FlowLayoutPanel right = new FlowLayoutPanel();
            right.AutoSize = true;
            right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            right.WrapContents = false;
            right.FlowDirection = FlowDirection.RightToLeft;
            right.Margin = new Padding(0);
            right.BackColor = Theme.BgCard;
            if (rightActions != null)
            {
                foreach (Control c in rightActions) right.Controls.Add(c);
            }

            row.Controls.Add(left, 0, 0);
            row.Controls.Add(right, 1, 0);
            return row;
        }

        public static WrapPanel WrapPanel()
        {
            return new WrapPanel();
        }
    }
}
