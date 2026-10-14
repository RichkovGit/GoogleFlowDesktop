using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace GoogleFlowDesktop
{
    /// <summary>
    /// Application shell. Everything the user interacts with is a native Windows
    /// control; the only web surface is Google Flow itself inside WebView2.
    /// </summary>
    public class MainForm : Form
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private const string FlowUrl = "https://flow.google.com/";
        private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        private readonly string cacheDirectory;
        private readonly string outputDirectory;
        private readonly string statePath;
        private readonly string dgpuFlags;

        private HardwareSpecs specs;
        private IntPtr iconHandle;

        private Notifier notifier;

        private TableLayoutPanel rootGrid;
        private SplitContainer split;
        private Panel studioHost;
        private Panel flowHost;

        private WebView2 wvFlow;
        private Panel overlay;
        private Label overlayTitle;
        private Label overlaySub;
        private ProgressBar overlaySpinner;
        private FlowLayoutPanel overlayButtons;
        private FlatButton btnOverlayRetry;
        private FlatButton btnOverlayRuntime;

        private NativeStudio studio;
        private NativeTelemetry telemetry;

        private FlatButton btnSegSplit;
        private FlatButton btnSegFlow;
        private FlatButton btnSegStudio;
        private FlatButton btnNavStudio;
        private FlatButton btnNavHardware;
        private Label lblHardwareBadge;
        private Label statusLeft;
        private Label statusRight;

        private Timer telemetryTimer;
        private CoreWebView2Environment sharedEnvironment;
        private string viewMode = "split";
        private int savedSplitter = 0;
        private bool webViewReady;

        public MainForm()
        {
            GpuConfig.EnforceWindowsDirectXGpuPreference();
            specs = GpuConfig.DetectHardware();
            dgpuFlags = GpuConfig.GetChromiumDgpuArguments();

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoogleFlowDesktop");
            cacheDirectory = Path.Combine(appData, "cache");
            statePath = Path.Combine(appData, "window.json");
            outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Google Flow Desktop");
            Directory.CreateDirectory(cacheDirectory);
            Directory.CreateDirectory(outputDirectory);

            notifier = new Notifier();
            notifier.Attach(null);

            BuildUi();
            RestoreWindowState();

            telemetryTimer = new Timer();
            telemetryTimer.Interval = 2500;
            telemetryTimer.Tick += (s, e) => UpdateTelemetry();
            telemetryTimer.Start();

            this.Shown += OnFormShown;
            this.FormClosing += OnFormClosing;
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            this.Text = "Google Flow Desktop";
            this.Size = new Size(1480, 900);
            this.MinimumSize = new Size(1100, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.BgBase;
            this.Font = Theme.F9;
            this.KeyPreview = true;

            Icon appIcon = AppIcon.Create(out iconHandle);
            this.Icon = appIcon;

            this.KeyDown += OnShortcut;

            rootGrid = new TableLayoutPanel();
            rootGrid.Dock = DockStyle.Fill;
            rootGrid.ColumnCount = 1;
            rootGrid.RowCount = 3;
            rootGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rootGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            rootGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rootGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            rootGrid.BackColor = Theme.BgBase;
            this.Controls.Add(rootGrid);

            rootGrid.Controls.Add(BuildHeader(), 0, 0);
            rootGrid.Controls.Add(BuildContent(), 0, 1);
            rootGrid.Controls.Add(BuildStatusBar(), 0, 2);
        }

        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.ColumnCount = 3;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.BackColor = Theme.BgRail;
            header.Margin = new Padding(0);
            header.Padding = new Padding(14, 0, 12, 0);

            Label brand = new Label();
            brand.Text = "⚡ Google Flow Desktop";
            brand.Font = Theme.F12B;
            brand.ForeColor = Theme.Accent;
            brand.AutoSize = true;
            brand.Margin = new Padding(0, 17, 26, 0);
            header.Controls.Add(brand, 0, 0);

            FlowLayoutPanel seg = new FlowLayoutPanel();
            seg.AutoSize = true;
            seg.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            seg.WrapContents = false;
            seg.FlowDirection = FlowDirection.LeftToRight;
            seg.Margin = new Padding(0, 11, 0, 0);
            seg.BackColor = Theme.BgRail;

            btnSegSplit = SegButton("◫ Сплит");
            btnSegSplit.Click += (s, e) => SetViewMode("split");
            seg.Controls.Add(btnSegSplit);

            btnSegFlow = SegButton("🎨 Только Flow");
            btnSegFlow.Click += (s, e) => SetViewMode("flow");
            seg.Controls.Add(btnSegFlow);

            btnSegStudio = SegButton("✨ Только Студия");
            btnSegStudio.Click += (s, e) => SetViewMode("studio");
            seg.Controls.Add(btnSegStudio);
            header.Controls.Add(seg, 1, 0);

            FlowLayoutPanel right = new FlowLayoutPanel();
            right.AutoSize = true;
            right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            right.WrapContents = false;
            right.FlowDirection = FlowDirection.RightToLeft;
            right.Margin = new Padding(0, 11, 0, 0);
            right.BackColor = Theme.BgRail;

            FlatButton btnUpdate = SegButton("⬆ Обновления");
            btnUpdate.Click += (s, e) => CheckUpdates(true);
            right.Controls.Add(btnUpdate);

            FlatButton btnSnapshot = SegButton("📸 Снимок Flow");
            btnSnapshot.Click += async (s, e) => await CaptureSnapshotAsync();
            right.Controls.Add(btnSnapshot);

            FlatButton btnFolder = SegButton("📁 Папка");
            btnFolder.Click += (s, e) => OpenOutputFolder();
            right.Controls.Add(btnFolder);

            FlatButton btnReload = SegButton("⟳");
            btnReload.Width = 38;
            btnReload.Click += (s, e) => ReloadFlow();
            right.Controls.Add(btnReload);

            FlatButton btnHome = SegButton("⌂");
            btnHome.Width = 38;
            btnHome.Click += (s, e) => NavigateFlow(FlowUrl);
            right.Controls.Add(btnHome);

            lblHardwareBadge = new Label();
            lblHardwareBadge.Text = "🎮 " + specs.DgpuName;
            lblHardwareBadge.Font = Theme.F9B;
            lblHardwareBadge.ForeColor = Theme.Green;
            lblHardwareBadge.AutoSize = true;
            lblHardwareBadge.Margin = new Padding(6, 17, 0, 0);
            right.Controls.Add(lblHardwareBadge);

            header.Controls.Add(right, 2, 0);
            UpdateSegSelection("split");
            return header;
        }

        private FlatButton SegButton(string text)
        {
            FlatButton b = new FlatButton();
            b.Kind = ButtonKind.Nav;
            b.Text = text;
            b.Height = 34;
            b.AutoSize = false;
            b.Width = 132;
            b.Margin = new Padding(0, 0, 4, 0);
            b.Font = Theme.F9B;
            return b;
        }

        private Control BuildContent()
        {
            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 6;
            split.BackColor = Theme.BgBase;
            split.Margin = new Padding(0);
            // Panel1MinSize / Panel2MinSize are applied later, once the control
            // has a real width (setting them too early throws InvalidOperationException).

            // --- Left: native studio + rail ---
            studioHost = new Panel();
            studioHost.Dock = DockStyle.Fill;
            studioHost.BackColor = Theme.BgBase;
            studioHost.Controls.Add(BuildContentHost());
            studioHost.Controls.Add(BuildRail());
            split.Panel1.Controls.Add(studioHost);

            // --- Right: Google Flow webview + native overlay ---
            flowHost = new Panel();
            flowHost.Dock = DockStyle.Fill;
            flowHost.BackColor = Theme.BgBase;
            split.Panel2.Controls.Add(flowHost);

            wvFlow = new WebView2();
            wvFlow.Dock = DockStyle.Fill;
            flowHost.Controls.Add(wvFlow);

            overlay = BuildOverlay();
            flowHost.Controls.Add(overlay);
            overlay.BringToFront();

            return split;
        }

        private Control BuildRail()
        {
            TableLayoutPanel rail = new TableLayoutPanel();
            rail.Dock = DockStyle.Left;
            rail.Width = 86;
            rail.ColumnCount = 1;
            rail.RowCount = 5;
            rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 74f));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 74f));
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 74f));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 74f));
            rail.BackColor = Theme.BgRail;
            rail.Margin = new Padding(0);

            btnNavStudio = RailButton("✨\nСтудия");
            btnNavStudio.Selected = true;
            btnNavStudio.Click += (s, e) => ShowTab("studio");
            rail.Controls.Add(btnNavStudio, 0, 0);

            btnNavHardware = RailButton("🎮\nЖелезо");
            btnNavHardware.Click += (s, e) => ShowTab("hardware");
            rail.Controls.Add(btnNavHardware, 0, 1);

            FlatButton btnTrim = RailButton("🧹\nRAM");
            btnTrim.Click += (s, e) =>
            {
                GpuConfig.TrimProcessMemory();
                UpdateTelemetry();
                SetStatus("🧹 Рабочий набор памяти очищен");
            };
            rail.Controls.Add(btnTrim, 0, 3);

            FlatButton btnOta = RailButton("🔄\nOTA");
            btnOta.Click += (s, e) => CheckUpdates(true);
            rail.Controls.Add(btnOta, 0, 4);

            return rail;
        }

        private FlatButton RailButton(string text)
        {
            FlatButton b = new FlatButton();
            b.Kind = ButtonKind.Rail;
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(8, 5, 8, 5);
            b.Font = Theme.F8;
            return b;
        }

        private Control BuildContentHost()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Theme.BgBase;

            studio = new NativeStudio();
            studio.Dock = DockStyle.Fill;
            studio.SendToFlowRequested = InjectPromptIntoFlow;
            studio.StatusChanged = SetStatus;
            host.Controls.Add(studio);

            telemetry = new NativeTelemetry();
            telemetry.Dock = DockStyle.Fill;
            telemetry.Visible = false;
            telemetry.TrimRequested = () =>
            {
                GpuConfig.TrimProcessMemory();
                UpdateTelemetry();
                SetStatus("🧹 Рабочий набор памяти очищен");
            };
            telemetry.ReapplyGpuRequested = () =>
            {
                bool ok = GpuConfig.EnforceWindowsDirectXGpuPreference();
                SetStatus(ok ? "⚙️ Реестр DirectX обновлён (GpuPreference=2)" : "⚙️ Не удалось записать реестр");
            };
            telemetry.CheckUpdatesRequested = () => CheckUpdates(true);
            host.Controls.Add(telemetry);

            return host;
        }

        private Panel BuildOverlay()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Theme.BgBase;

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 1;
            grid.RowCount = 6;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 38f));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 62f));
            grid.BackColor = Theme.BgBase;
            p.Controls.Add(grid);

            overlayTitle = new Label();
            overlayTitle.Text = "⚡ Загрузка Google Flow...";
            overlayTitle.Font = Theme.F12B;
            overlayTitle.ForeColor = Theme.Accent;
            overlayTitle.TextAlign = ContentAlignment.MiddleCenter;
            overlayTitle.Dock = DockStyle.Fill;
            overlayTitle.AutoSize = true;
            grid.Controls.Add(overlayTitle, 0, 1);

            overlaySub = new Label();
            overlaySub.Text = "Аппаратное ускорение Direct3D • Country Unlocker активен";
            overlaySub.Font = Theme.F10;
            overlaySub.ForeColor = Theme.TextMuted;
            overlaySub.TextAlign = ContentAlignment.MiddleCenter;
            overlaySub.Dock = DockStyle.Fill;
            overlaySub.AutoSize = true;
            overlaySub.Margin = new Padding(0, 6, 0, 14);
            grid.Controls.Add(overlaySub, 0, 2);

            overlaySpinner = new ProgressBar();
            overlaySpinner.Style = ProgressBarStyle.Marquee;
            overlaySpinner.MarqueeAnimationSpeed = 30;
            overlaySpinner.Width = 260;
            overlaySpinner.Height = 6;
            overlaySpinner.Anchor = AnchorStyles.None;
            grid.Controls.Add(overlaySpinner, 0, 3);

            overlayButtons = new FlowLayoutPanel();
            overlayButtons.AutoSize = true;
            overlayButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            overlayButtons.WrapContents = false;
            overlayButtons.FlowDirection = FlowDirection.LeftToRight;
            overlayButtons.Anchor = AnchorStyles.None;
            overlayButtons.Margin = new Padding(0, 16, 0, 0);
            overlayButtons.BackColor = Theme.BgBase;
            overlayButtons.Visible = false;

            btnOverlayRetry = Ui.Button("↻ Повторить", ButtonKind.Ghost, 130);
            btnOverlayRetry.Height = 34;
            btnOverlayRetry.Click += (s, e) => ReloadFlow();
            overlayButtons.Controls.Add(btnOverlayRetry);

            btnOverlayRuntime = Ui.Button("📥 Установить WebView2", ButtonKind.Accent, 190);
            btnOverlayRuntime.Height = 34;
            btnOverlayRuntime.Visible = false;
            btnOverlayRuntime.Click += (s, e) => OpenExternal(WebView2DownloadUrl);
            overlayButtons.Controls.Add(btnOverlayRuntime);

            grid.Controls.Add(overlayButtons, 0, 4);
            return p;
        }

        private Control BuildStatusBar()
        {
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.ColumnCount = 2;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.BackColor = Theme.BgRail;
            bar.Margin = new Padding(0);
            bar.Padding = new Padding(14, 0, 14, 0);

            statusLeft = new Label();
            statusLeft.Text = "Готово";
            statusLeft.Font = Theme.F8;
            statusLeft.ForeColor = Theme.TextMuted;
            statusLeft.AutoSize = true;
            statusLeft.Margin = new Padding(0, 6, 0, 0);
            bar.Controls.Add(statusLeft, 0, 0);

            statusRight = new Label();
            statusRight.Text = "v" + UpdateManager.CurrentVersion + " • dGPU Direct3D";
            statusRight.Font = Theme.F8;
            statusRight.ForeColor = Theme.TextDim;
            statusRight.AutoSize = true;
            statusRight.Margin = new Padding(0, 6, 0, 0);
            bar.Controls.Add(statusRight, 1, 0);

            return bar;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindowChrome(this);
            Theme.DarkenControl(this);
        }

        // ------------------------------------------------------------------
        // Startup / WebView2
        // ------------------------------------------------------------------
        private async void OnFormShown(object sender, EventArgs e)
        {
            ApplyPanelMinimums();
            ApplyViewModeLayout();
            await InitializeWebViewAsync();

            await Task.Delay(2500);
            await CheckUpdatesAsync(false);
        }

        /// <summary>Applied after the SplitContainer has a real width.</summary>
        private void ApplyPanelMinimums()
        {
            try
            {
                split.Panel1MinSize = 380;
                split.Panel2MinSize = 360;
            }
            catch { }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrEmpty(version)) throw new Exception("WebView2 Runtime не найден");
                SetOverlay("⚡ Загрузка Google Flow...",
                    "WebView2 " + version + " • Direct3D • Country Unlocker активен", false, false);
            }
            catch (Exception ex)
            {
                ShowOverlayError("WebView2 Runtime не установлен или повреждён",
                    ex.Message + ". Установите Microsoft Edge WebView2 Runtime и повторите.", true);
                return;
            }

            try
            {
                CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions(dgpuFlags);
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(cacheDirectory, "webview2"), options);
                await wvFlow.EnsureCoreWebView2Async(sharedEnvironment);

                CoreWebView2 core = wvFlow.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsZoomControlEnabled = true;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

                await core.AddScriptToExecuteOnDocumentCreatedAsync(UnlockerScript.GetScript());

                core.NavigationStarting += (s, a) =>
                {
                    overlay.Visible = true;
                    overlaySpinner.Visible = true;
                    overlayButtons.Visible = false;
                    overlaySpinner.Style = ProgressBarStyle.Marquee;
                    SetOverlay("⚡ Подключение к Google Flow...", ShortUrl(a.Uri), false, false);
                };

                core.NavigationCompleted += (s, a) =>
                {
                    if (a.IsSuccess)
                    {
                        overlay.Visible = false;
                        webViewReady = true;
                        SetStatus("Google Flow загружен");
                    }
                    else
                    {
                        webViewReady = false;
                        ShowOverlayError("Не удалось загрузить Google Flow",
                            "Сетевая ошибка: " + a.WebErrorStatus + ". Проверьте интернет и нажмите «Повторить».", false);
                    }
                };

                core.ProcessFailed += (s, a) =>
                {
                    webViewReady = false;
                    ShowOverlayError("Процесс WebView2 остановлен",
                        a.ProcessFailedKind + " • " + a.Reason + ". Нажмите «Повторить».", false);
                };

                core.NewWindowRequested += (s, a) =>
                {
                    a.Handled = true;
                    if (a.Uri != null && (a.Uri.Contains("google.com") || a.Uri.Contains("googleusercontent")))
                    {
                        core.Navigate(a.Uri);
                    }
                    else
                    {
                        OpenExternal(a.Uri);
                    }
                };

                core.DocumentTitleChanged += (s, a) =>
                {
                    string t = core.DocumentTitle;
                    this.Text = string.IsNullOrEmpty(t) ? "Google Flow Desktop" : t + " — Google Flow Desktop";
                };

                core.Navigate(FlowUrl);
            }
            catch (Exception ex)
            {
                ShowOverlayError("Ошибка инициализации WebView2", ex.Message, true);
            }
        }

        private void SetOverlay(string title, string sub, bool retry, bool runtime)
        {
            overlayTitle.Text = title;
            overlaySub.Text = sub;
            overlaySpinner.Visible = !retry;
            overlayButtons.Visible = true;
            btnOverlayRetry.Visible = true;
            btnOverlayRuntime.Visible = runtime;
            overlay.Visible = true;
        }

        private void ShowOverlayError(string title, string sub, bool runtime)
        {
            SetOverlay("⚠ " + title, sub, true, runtime);
            SetStatus(title);
        }

        private static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.Length > 90 ? url.Substring(0, 90) + "..." : url;
        }

        private void ReloadFlow()
        {
            if (wvFlow.CoreWebView2 != null)
            {
                overlay.Visible = true;
                overlaySpinner.Visible = true;
                overlaySpinner.Style = ProgressBarStyle.Marquee;
                SetOverlay("⚡ Перезагрузка Google Flow...", "Пожалуйста, подождите", false, false);
                wvFlow.CoreWebView2.Reload();
            }
        }

        private void NavigateFlow(string url)
        {
            if (wvFlow.CoreWebView2 != null)
            {
                SetOverlay("⚡ Переход к Google Flow...", ShortUrl(url), false, false);
                wvFlow.CoreWebView2.Navigate(url);
            }
        }

        // ------------------------------------------------------------------
        // Prompt injection
        // ------------------------------------------------------------------
        private void InjectPromptIntoFlow(string promptText)
        {
            if (string.IsNullOrEmpty(promptText)) return;
            ClipboardUtil.Set(promptText);

            if (split.Panel2Collapsed) SetViewMode("split");

            if (wvFlow.CoreWebView2 == null)
            {
                SetStatus("Промпт скопирован в буфер — Flow ещё загружается");
                return;
            }

            string literal = Json.Serialize(promptText);
            string script =
                "(function(){try{if(window.__flowNativeBridge&&window.__flowNativeBridge.insertPrompt(" + literal + "))return 'ok';}catch(e){}" +
                "return 'no';})();";

            try
            {
                wvFlow.CoreWebView2.ExecuteScriptAsync(script);
                wvFlow.Focus();
                SetStatus(webViewReady ? "🚀 Промпт вставлен в Google Flow (и скопирован)" : "Промпт отправлен в Flow");
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось вставить промпт: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Snapshot
        // ------------------------------------------------------------------
        private async Task CaptureSnapshotAsync()
        {
            if (wvFlow.CoreWebView2 == null)
            {
                SetStatus("Flow ещё не готов для снимка");
                return;
            }
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    await wvFlow.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    string name = "flow_snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    string path = Path.Combine(outputDirectory, name);
                    File.WriteAllBytes(path, ms.ToArray());
                    notifier.NotifySnapshotSaved(name);
                    SetStatus("📸 Снимок сохранён: " + name);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось сохранить снимок: " + ex.Message);
            }
        }

        private void OpenOutputFolder()
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                Process.Start("explorer.exe", "\"" + outputDirectory + "\"");
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось открыть папку: " + ex.Message);
            }
        }

        private void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(url); }
            catch (Exception ex) { SetStatus("Не удалось открыть ссылку: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Tabs & view modes
        // ------------------------------------------------------------------
        private void ShowTab(string tab)
        {
            bool hardware = tab == "hardware";
            studio.Visible = !hardware;
            telemetry.Visible = hardware;
            btnNavStudio.Selected = !hardware;
            btnNavHardware.Selected = hardware;
            if (hardware) UpdateTelemetry();
        }

        private void SetViewMode(string mode)
        {
            viewMode = mode;
            UpdateSegSelection(mode);
            ApplyViewModeLayout();
        }

        private void UpdateSegSelection(string mode)
        {
            btnSegSplit.Selected = mode == "split";
            btnSegFlow.Selected = mode == "flow";
            btnSegStudio.Selected = mode == "studio";
        }

        private void ApplyViewModeLayout()
        {
            try
            {
                if (viewMode == "flow")
                {
                    split.Panel1Collapsed = true;
                    split.Panel2Collapsed = false;
                }
                else if (viewMode == "studio")
                {
                    split.Panel1Collapsed = false;
                    split.Panel2Collapsed = true;
                }
                else
                {
                    split.Panel1Collapsed = false;
                    split.Panel2Collapsed = false;
                    int target = savedSplitter > 0 ? savedSplitter : (split.Width / 2);
                    SetSplitterSafe(target);
                }
            }
            catch { }
        }

        private void SetSplitterSafe(int value)
        {
            try
            {
                if (split.Panel1Collapsed || split.Panel2Collapsed) return;
                int min = split.Panel1MinSize;
                int max = split.Width - split.Panel2MinSize - split.SplitterWidth;
                if (max < min) return;
                int v = value;
                if (v < min) v = min;
                if (v > max) v = max;
                split.SplitterDistance = v;
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Telemetry
        // ------------------------------------------------------------------
        private void UpdateTelemetry()
        {
            try
            {
                specs = GpuConfig.DetectHardware();
                double appRam = GpuConfig.GetCurrentMemoryUsageMb();
                specs.AppRamMb = appRam;

                if (lblHardwareBadge != null)
                {
                    lblHardwareBadge.Text = "🎮 " + specs.DgpuName + "  •  💾 " + appRam + " MB";
                }
                if (telemetry != null && telemetry.Visible)
                {
                    telemetry.Update(specs, appRam.ToString("0.0"));
                }
            }
            catch { }
        }

        private void SetStatus(string text)
        {
            if (statusLeft == null) return;
            try { statusLeft.Text = text; } catch { }
        }

        // ------------------------------------------------------------------
        // OTA updates
        // ------------------------------------------------------------------
        private void CheckUpdates(bool manual)
        {
            if (manual) SetStatus("🔍 Проверка обновлений на GitHub...");
            FireAndForget(CheckUpdatesAsync(manual));
        }

        /// <summary>Runs a task in the background and observes unhandled failures.</summary>
        private static void FireAndForget(Task task)
        {
            if (task == null) return;
            task.ContinueWith(delegate(Task t)
            {
                Exception ignored = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task CheckUpdatesAsync(bool manual)
        {
            UpdateInfo info;
            try
            {
                info = await UpdateManager.CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                if (manual) notifier.ShowToast("Обновления", "Ошибка проверки: " + ex.Message, true);
                return;
            }

            if (this.IsDisposed) return;

            if (info.HasUpdate)
            {
                ShowUpdateDialog(info);
            }
            else if (manual)
            {
                if (!string.IsNullOrEmpty(info.Error))
                {
                    notifier.ShowToast("Обновления", "Не удалось проверить: " + info.Error, true);
                    SetStatus("Не удалось проверить обновления: " + info.Error);
                }
                else
                {
                    notifier.ShowToast("Обновления", "У вас последняя версия v" + UpdateManager.CurrentVersion, true);
                    SetStatus("✅ Установлена последняя версия v" + UpdateManager.CurrentVersion);
                    telemetry.SetVersionStatus("Версия v" + UpdateManager.CurrentVersion + " (актуальная)", Theme.Green);
                }
            }
        }

        private void ShowUpdateDialog(UpdateInfo info)
        {
            UpdateDialog dlg = new UpdateDialog(info);
            dlg.ApplyRequested += async () =>
            {
                try
                {
                    await UpdateManager.DownloadAndApplyUpdateAsync(info.DownloadUrl, pct =>
                    {
                        if (!dlg.IsDisposed) dlg.SetProgress(pct);
                    });
                    SetStatus("Обновление загружено, перезапуск...");
                    dlg.Close();
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    if (!dlg.IsDisposed) dlg.ShowError(ex.Message);
                }
            };
            dlg.ShowDialog(this);
        }

        // ------------------------------------------------------------------
        // Shortcuts
        // ------------------------------------------------------------------
        private void OnShortcut(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                ReloadFlow();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Enter)
            {
                InjectPromptIntoFlow(studio.CompiledPrompt);
                e.Handled = true;
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.S)
            {
                FireAndForget(CaptureSnapshotAsync());
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.D1)
            {
                SetViewMode("split");
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.D2)
            {
                SetViewMode("flow");
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.D3)
            {
                SetViewMode("studio");
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------
        // Window state
        // ------------------------------------------------------------------
        private class UiState
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int W { get; set; }
            public int H { get; set; }
            public bool Maximized { get; set; }
            public int Splitter { get; set; }
            public string Mode { get; set; }
        }

        private void RestoreWindowState()
        {
            try
            {
                if (!File.Exists(statePath)) return;
                string raw = File.ReadAllText(statePath, Encoding.UTF8);
                UiState st = Json.Deserialize<UiState>(raw);
                if (st == null || st.W <= 200 || st.H <= 200) return;

                Rectangle r = new Rectangle(st.X, st.Y, st.W, st.H);
                bool onScreen = false;
                foreach (Screen s in Screen.AllScreens)
                {
                    if (s.WorkingArea.IntersectsWith(r)) { onScreen = true; break; }
                }
                if (onScreen)
                {
                    this.StartPosition = FormStartPosition.Manual;
                    this.Bounds = r;
                }
                this.WindowState = st.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
                savedSplitter = st.Splitter;
                if (!string.IsNullOrEmpty(st.Mode)) viewMode = st.Mode;
                UpdateSegSelection(viewMode);
            }
            catch { }
        }

        private void SaveWindowState()
        {
            try
            {
                UiState st = new UiState();
                Rectangle r = this.WindowState == FormWindowState.Normal ? this.Bounds : this.RestoreBounds;
                st.X = r.X;
                st.Y = r.Y;
                st.W = r.Width;
                st.H = r.Height;
                st.Maximized = this.WindowState == FormWindowState.Maximized;
                st.Splitter = (!split.Panel1Collapsed && !split.Panel2Collapsed) ? split.SplitterDistance : savedSplitter;
                st.Mode = viewMode;
                File.WriteAllText(statePath, Json.Serialize(st), Encoding.UTF8);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Shutdown
        // ------------------------------------------------------------------
        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            telemetryTimer.Stop();
            SaveWindowState();

            try
            {
                if (wvFlow != null && wvFlow.CoreWebView2 != null) wvFlow.Dispose();
            }
            catch { }

            notifier.Dispose();
            AppIcon.Release(iconHandle);
        }
    }
}
