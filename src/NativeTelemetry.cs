using System;
using System.Drawing;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>Native hardware / dGPU telemetry page. Replaces the HTML "Железо" tab.</summary>
    public class NativeTelemetry : UserControl
    {
        private TableLayoutPanel root;

        private Label valDgpu;
        private Label subDgpu;
        private Label valCpu;
        private Label subCpu;
        private Label valRam;
        private Label subRam;
        private Label valIgpu;
        private Label subIgpu;
        private Label valDecoder;
        private Label valUnlocker;
        private Label valVersion;

        public Action TrimRequested;
        public Action ReapplyGpuRequested;
        public Action CheckUpdatesRequested;

        public NativeTelemetry()
        {
            BackColor = Theme.BgBase;
            Dock = DockStyle.Fill;
            Build();
        }

        private void Build()
        {
            root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.AutoScroll = true;
            root.BackColor = Theme.BgBase;
            root.Padding = new Padding(18, 16, 18, 20);
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 0;
            Controls.Add(root);

            CardLayout actions = new CardLayout();
            FlatButton trim = Ui.Button("🧹 Очистить RAM процесса", ButtonKind.Accent, 210);
            trim.Height = 34;
            trim.Click += (s, e) => { if (TrimRequested != null) TrimRequested(); };

            FlatButton reg = Ui.Button("⚙️ Реестр DirectX dGPU", ButtonKind.Ghost, 210);
            reg.Height = 34;
            reg.Click += (s, e) => { if (ReapplyGpuRequested != null) ReapplyGpuRequested(); };

            FlatButton ota = Ui.Button("🔄 Проверить обновления", ButtonKind.Ghost, 200);
            ota.Height = 34;
            ota.Click += (s, e) => { if (CheckUpdatesRequested != null) CheckUpdatesRequested(); };

            WrapPanel actionsRow = new WrapPanel();
            actionsRow.Controls.Add(trim);
            actionsRow.Controls.Add(reg);
            actionsRow.Controls.Add(ota);
            actions.AddBlock(actionsRow);
            Add(actions);

            CardLayout g = new CardLayout();
            g.AddBlock(Ui.HeaderRow("🎮 Дискретная видеокарта (dGPU)", null));
            valDgpu = Ui.Value("Определяется...", Theme.Accent, 12f, true);
            subDgpu = Ui.Muted("");
            g.AddBlock(valDgpu);
            g.AddBlock(subDgpu);
            Add(g);

            CardLayout c = new CardLayout();
            c.AddBlock(Ui.HeaderRow("💻 Центральный процессор (CPU)", null));
            valCpu = Ui.Value("Определяется...", Theme.TextMain, 11f, true);
            subCpu = Ui.Muted("");
            c.AddBlock(valCpu);
            c.AddBlock(subCpu);
            Add(c);

            CardLayout r = new CardLayout();
            r.AddBlock(Ui.HeaderRow("💾 Оперативная память (RAM)", null));
            valRam = Ui.Value("Определяется...", Theme.Green, 11f, true);
            subRam = Ui.Muted("");
            r.AddBlock(valRam);
            r.AddBlock(subRam);
            Add(r);

            CardLayout i = new CardLayout();
            i.AddBlock(Ui.HeaderRow("🔋 Интегрированная графика (iGPU)", null));
            valIgpu = Ui.Value("Определяется...", Theme.TextMuted, 11f, true);
            subIgpu = Ui.Muted("Освобождена от декодирования видео");
            i.AddBlock(valIgpu);
            i.AddBlock(subIgpu);
            Add(i);

            CardLayout d = new CardLayout();
            d.AddBlock(Ui.HeaderRow("⚡ Аппаратное декодирование видео", null));
            valDecoder = Ui.Value("Direct3D 11 / DirectX — активен", Theme.Accent, 11f, true);
            d.AddBlock(valDecoder);
            d.AddBlock(Ui.Muted("use-angle=d3d11 • ignore-gpu-blocklist • accelerated-video-decode"));
            Add(d);

            CardLayout u = new CardLayout();
            u.AddBlock(Ui.HeaderRow("🔓 Разблокировка регионов (Google Labs)", null));
            valUnlocker = Ui.Value("Country Unlocker активен", Theme.Accent, 11f, true);
            u.AddBlock(valUnlocker);
            u.AddBlock(Ui.Muted("Патч Closure Stream RPC и tRPC availabilityState → AVAILABLE"));
            Add(u);

            CardLayout v = new CardLayout();
            v.AddBlock(Ui.HeaderRow("🔄 Система OTA-обновлений", null));
            valVersion = Ui.Value("Версия " + UpdateManager.CurrentVersion, Theme.Green, 11f, true);
            v.AddBlock(valVersion);
            v.AddBlock(Ui.Muted("Репозиторий: RichkovGit/GoogleFlowDesktop"));
            Add(v);
        }

        private void Add(CardLayout card)
        {
            int row = root.RowCount;
            root.RowCount = row + 1;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(card, 0, row);
        }

        public void Update(HardwareSpecs specs, string appRamMb)
        {
            if (specs == null) return;

            valDgpu.Text = specs.DgpuName + "  (" + specs.DgpuVram + ")";
            string drv = string.IsNullOrEmpty(specs.DgpuDriver) ? "" : " • Драйвер " + specs.DgpuDriver;
            subDgpu.Text = "Direct3D 11 / DirectX • GpuPreference=2 (High Performance)" + drv;

            valCpu.Text = specs.CpuName;
            subCpu.Text = specs.CpuCores + " ядер, " + specs.CpuThreads + " потоков";

            valRam.Text = specs.TotalRamGb + " GB установлено • " + specs.FreeRamGb + " GB свободно";
            subRam.Text = "Процесс Flow: ~" + appRamMb + " MB";

            valIgpu.Text = specs.IgpuName + "  (" + specs.IgpuVram + ")";
        }

        public void SetVersionStatus(string text, Color color)
        {
            if (valVersion != null)
            {
                valVersion.Text = text;
                valVersion.ForeColor = color;
            }
        }
    }
}
