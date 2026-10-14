using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>A thin, fully dark native progress bar.</summary>
    public class ProgressBarDark : Control
    {
        private int value;

        public ProgressBarDark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 10;
            BackColor = Theme.BgInput;
        }

        public int Value
        {
            get { return value; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                if (this.value != v) { this.value = v; Invalidate(); }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(track, Height / 2))
            using (SolidBrush b = new SolidBrush(Theme.BgInput))
            {
                e.Graphics.FillPath(b, path);
            }
            int w = (int)((Width - 1) * (value / 100.0));
            if (w > 2)
            {
                Rectangle fill = new Rectangle(0, 0, w, Height - 1);
                using (GraphicsPath path = Theme.Rounded(fill, Height / 2))
                using (LinearGradientBrush b = new LinearGradientBrush(fill, Theme.AccentDeep, Theme.Accent, 0f))
                {
                    e.Graphics.FillPath(b, path);
                }
            }
        }
    }

    /// <summary>Native 1-click OTA update dialog (replaces the HTML modal).</summary>
    public class UpdateDialog : Form
    {
        private readonly UpdateInfo info;
        private FlatButton btnApply;
        private FlatButton btnLater;
        private Label lblProgressPct;
        private Panel progressBox;
        private ProgressBarDark progressBar;
        private Point dragOffset;
        private bool dragging;

        public event Action ApplyRequested;

        public UpdateDialog(UpdateInfo updateInfo)
        {
            info = updateInfo;
            Build();
        }

        private void Build()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Size = new Size(620, 580);
            BackColor = Theme.BgBase;
            Padding = new Padding(1);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            TableLayoutPanel rootGrid = new TableLayoutPanel();
            rootGrid.Dock = DockStyle.Fill;
            rootGrid.ColumnCount = 1;
            rootGrid.RowCount = 3;
            rootGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rootGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            rootGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rootGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 72f));
            rootGrid.BackColor = Theme.BgBase;
            Controls.Add(rootGrid);

            // ---- Header (draggable) ----
            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.ColumnCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.BackColor = Theme.BgRail;
            header.Margin = new Padding(0);
            header.MouseDown += HeaderMouseDown;
            header.MouseMove += HeaderMouseMove;
            header.MouseUp += (s, e) => dragging = false;
            rootGrid.Controls.Add(header, 0, 0);

            Label title = new Label();
            title.Text = "🚀  Доступно обновление Google Flow Desktop";
            title.Font = Theme.F12B;
            title.ForeColor = Theme.TextMain;
            title.AutoSize = true;
            title.Margin = new Padding(20, 16, 0, 0);
            title.MouseDown += HeaderMouseDown;
            title.MouseMove += HeaderMouseMove;
            title.MouseUp += (s, e) => dragging = false;
            header.Controls.Add(title, 0, 0);

            FlatButton close = Ui.Button("✕", ButtonKind.Nav, 36);
            close.Height = 32;
            close.Margin = new Padding(0, 12, 14, 0);
            close.Click += (s, e) => Close();
            header.Controls.Add(close, 1, 0);

            // ---- Body ----
            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 1;
            body.RowCount = 3;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Padding = new Padding(20, 16, 20, 12);
            body.BackColor = Theme.BgBase;
            body.Margin = new Padding(0);
            rootGrid.Controls.Add(body, 0, 1);

            Label verRow = new Label();
            verRow.Text = "Текущая  v" + (info.CurrentVersion ?? "1.0.0") + "      ➔      Новая  v" +
                          (info.LatestVersion ?? "1.0.0") + "      " + (info.ReleaseDate ?? "");
            verRow.Font = Theme.F10B;
            verRow.ForeColor = Theme.Accent;
            verRow.AutoSize = true;
            verRow.Margin = new Padding(0, 0, 0, 10);
            body.Controls.Add(verRow, 0, 0);

            CardLayout notesCard = new CardLayout();
            notesCard.Dock = DockStyle.Fill;
            notesCard.Margin = new Padding(0, 0, 0, 8);

            InputHost notesHost = new InputHost();
            notesHost.MakeMultiline(12, true);
            notesHost.SetReadOnly(true);
            notesHost.SetCue("");
            notesHost.Height = 320;
            notesHost.Inner.Font = Theme.F10;

            List<string> lines = new List<string>();
            if (info.ReleaseNotes != null && info.ReleaseNotes.Count > 0)
            {
                foreach (string n in info.ReleaseNotes) lines.Add("•  " + n);
            }
            else
            {
                lines.Add("•  Улучшена производительность и стабильность.");
            }
            notesHost.Inner.Text = string.Join("\r\n\r\n", lines.ToArray());
            notesCard.AddBlock(Ui.HeaderRow("📋 Что нового в этом релизе", null));
            notesCard.AddBlock(notesHost);
            body.Controls.Add(notesCard, 0, 1);

            progressBox = new Panel();
            progressBox.Dock = DockStyle.Fill;
            progressBox.Height = 46;
            progressBox.Visible = false;
            progressBox.BackColor = Theme.BgBase;
            progressBox.Margin = new Padding(0, 6, 0, 0);

            lblProgressPct = new Label();
            lblProgressPct.Text = "Загрузка обновления с GitHub... 0%";
            lblProgressPct.Font = Theme.F9B;
            lblProgressPct.ForeColor = Theme.TextMuted;
            lblProgressPct.AutoSize = true;
            lblProgressPct.Margin = new Padding(0, 0, 0, 6);
            progressBox.Controls.Add(lblProgressPct);

            progressBar = new ProgressBarDark();
            progressBar.Dock = DockStyle.Bottom;
            progressBar.Height = 10;
            progressBox.Controls.Add(progressBar);
            body.Controls.Add(progressBox, 0, 2);

            // ---- Footer ----
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Theme.BgRail;
            footer.Margin = new Padding(0);
            rootGrid.Controls.Add(footer, 0, 2);

            FlowLayoutPanel footerRow = new FlowLayoutPanel();
            footerRow.Dock = DockStyle.Fill;
            footerRow.FlowDirection = FlowDirection.RightToLeft;
            footerRow.WrapContents = false;
            footerRow.Padding = new Padding(16, 15, 16, 15);
            footerRow.BackColor = Theme.BgRail;
            footer.Controls.Add(footerRow);

            btnApply = Ui.Button("🚀 Обновить и перезапустить", ButtonKind.Accent, 236);
            btnApply.Height = 40;
            btnApply.Margin = new Padding(8, 0, 0, 0);
            btnApply.Click += (s, e) => BeginApply();
            footerRow.Controls.Add(btnApply);

            btnLater = Ui.Button("Позже", ButtonKind.Ghost, 110);
            btnLater.Height = 40;
            btnLater.Click += (s, e) => Close();
            footerRow.Controls.Add(btnLater);
        }

        private void HeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragOffset = new Point(e.X, e.Y);
            }
        }

        private void HeaderMouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Point screen = ((Control)sender).PointToScreen(new Point(e.X, e.Y));
                Location = new Point(screen.X - dragOffset.X, screen.Y - dragOffset.Y);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindowChrome(this);
        }

        private void BeginApply()
        {
            btnApply.Enabled = false;
            btnApply.Text = "⏳ Загрузка...";
            btnLater.Enabled = false;
            progressBox.Visible = true;
            if (ApplyRequested != null) ApplyRequested();
        }

        public void SetProgress(int pct)
        {
            if (progressBar != null) progressBar.Value = pct;
            if (lblProgressPct != null) lblProgressPct.Text = "Загрузка обновления с GitHub... " + pct + "%";
        }

        public void ShowError(string message)
        {
            if (lblProgressPct != null)
            {
                lblProgressPct.Text = "Ошибка обновления: " + message;
                lblProgressPct.ForeColor = Theme.Red;
            }
            if (btnApply != null) { btnApply.Enabled = true; btnApply.Text = "🔁 Попробовать снова"; }
            if (btnLater != null) btnLater.Enabled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.BorderCard))
            {
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
