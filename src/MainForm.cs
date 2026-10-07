using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace GoogleFlowDesktop
{
    public class MainForm : Form
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private readonly string projectsDirectory;
        private readonly string cacheDirectory;
        private readonly string uiDirectory;
        private readonly string dgpuFlags;

        private LocalServer server;
        private WorkerDispatcher dispatcher;

        private Panel permanentTopBar;
        private SplitContainer split;
        private WebView2 wvStudio;
        private WebView2 wvFlow;
        private Panel flowLoadingPanel;
        private Label lblLoadingStatus;

        private Button btnModeSplit;
        private Button btnModeFlow;
        private Button btnModeStudio;
        private Button btnActionSend;
        private Button btnActionReload;
        private Button btnActionHome;
        private Label lblHardwareBadge;

        private Timer telemetryTimer;
        private CoreWebView2Environment sharedEnvironment;
        private HardwareSpecs currentSpecs;

        public MainForm()
        {
            // Enforce Windows DirectX High-Performance preference
            GpuConfig.EnforceWindowsDirectXGpuPreference();

            currentSpecs = GpuConfig.DetectHardware();
            dgpuFlags = GpuConfig.GetChromiumDgpuArguments();

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".google_flow_desktop");
            projectsDirectory = Path.Combine(appData, "projects");
            cacheDirectory = Path.Combine(appData, "cache");
            uiDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");

            Directory.CreateDirectory(projectsDirectory);
            Directory.CreateDirectory(cacheDirectory);

            dispatcher = new WorkerDispatcher(projectsDirectory);
            BindDispatcherEvents();

            server = new LocalServer(uiDirectory, projectsDirectory);

            InitializePermanentHeaderAndLayout();

            telemetryTimer = new Timer();
            telemetryTimer.Interval = 2500;
            telemetryTimer.Tick += (s, e) =>
            {
                GpuConfig.TrimProcessMemory();
                UpdateHardwareTelemetry();
            };
            telemetryTimer.Start();
        }

        private void InitializePermanentHeaderAndLayout()
        {
            this.Text = "Google Flow - " + currentSpecs.DgpuName + " (dGPU)";
            this.Size = new Size(1500, 920);
            this.MinimumSize = new Size(1024, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(16, 18, 26);

            // ========================================================
            // 1. PERMANENT TOP HEADER BAR (NEVER COLLAPSES!)
            // ========================================================
            permanentTopBar = new Panel();
            permanentTopBar.Dock = DockStyle.Top;
            permanentTopBar.Height = 46;
            permanentTopBar.BackColor = Color.FromArgb(17, 20, 30);
            permanentTopBar.Padding = new Padding(10, 0, 10, 0);
            this.Controls.Add(permanentTopBar);

            // App Brand
            Label lblBrand = new Label();
            lblBrand.Text = "⚡ Google Flow Desktop";
            lblBrand.ForeColor = Color.FromArgb(0, 210, 255);
            lblBrand.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            lblBrand.AutoSize = true;
            lblBrand.Location = new Point(12, 13);
            permanentTopBar.Controls.Add(lblBrand);

            int curX = 230;
            int curY = 7;

            // Segmented View Mode Buttons (ALWAYS VISIBLE!)
            btnModeSplit = CreateHeaderButton("◫ Сплит (50/50)", new Point(curX, curY), 130, true);
            btnModeSplit.Click += (s, e) => SetViewMode("split");
            permanentTopBar.Controls.Add(btnModeSplit);
            curX += 135;

            btnModeFlow = CreateHeaderButton("🎨 Только Flow", new Point(curX, curY), 125, false);
            btnModeFlow.Click += (s, e) => SetViewMode("flow");
            permanentTopBar.Controls.Add(btnModeFlow);
            curX += 130;

            btnModeStudio = CreateHeaderButton("✨ Только Студия", new Point(curX, curY), 130, false);
            btnModeStudio.Click += (s, e) => SetViewMode("studio");
            permanentTopBar.Controls.Add(btnModeStudio);
            curX += 140;

            // Flow Quick Action Buttons
            btnActionSend = CreateHeaderButton("🚀 Вставить во Flow", new Point(curX, curY), 160, false, true);
            btnActionSend.Click += (s, e) =>
            {
                ExecuteJsStudio("postHostMessage({ type: 'SEND_TO_FLOW', prompt: getCompiledPrompt() });");
            };
            permanentTopBar.Controls.Add(btnActionSend);
            curX += 166;

            btnActionReload = CreateHeaderButton("🔄", new Point(curX, curY), 36, false);
            btnActionReload.Click += (s, e) =>
            {
                if (wvFlow.CoreWebView2 != null)
                {
                    flowLoadingPanel.Visible = true;
                    wvFlow.CoreWebView2.Reload();
                }
            };
            permanentTopBar.Controls.Add(btnActionReload);
            curX += 42;

            btnActionHome = CreateHeaderButton("🏠", new Point(curX, curY), 36, false);
            btnActionHome.Click += (s, e) =>
            {
                if (wvFlow.CoreWebView2 != null)
                {
                    flowLoadingPanel.Visible = true;
                    wvFlow.CoreWebView2.Navigate("https://flow.google.com/");
                }
            };
            permanentTopBar.Controls.Add(btnActionHome);
            curX += 46;

            // Live Hardware Badge on the right of the header bar
            lblHardwareBadge = new Label();
            lblHardwareBadge.Text = string.Format("🎮 {0} ({1}) • 💾 {2} MB", currentSpecs.DgpuName, currentSpecs.DgpuVram, currentSpecs.AppRamMb);
            lblHardwareBadge.ForeColor = Color.FromArgb(52, 211, 153);
            lblHardwareBadge.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            lblHardwareBadge.AutoSize = true;
            lblHardwareBadge.Location = new Point(curX + 15, 14);
            permanentTopBar.Controls.Add(lblHardwareBadge);

            // ========================================================
            // 2. DUAL SPLIT CONTAINER (Below Permanent Header)
            // ========================================================
            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 4;
            split.BackColor = Color.FromArgb(28, 32, 46);
            this.Controls.Add(split);
            split.BringToFront();

            // Set 50/50 initially
            split.SplitterDistance = 680;

            // Panel 1: Studio WebView2
            wvStudio = new WebView2();
            wvStudio.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(wvStudio);

            // Panel 2: Google Flow WebView2 + Fluent Loading Overlay
            Panel flowHostPanel = new Panel();
            flowHostPanel.Dock = DockStyle.Fill;
            flowHostPanel.BackColor = Color.FromArgb(13, 15, 22);
            split.Panel2.Controls.Add(flowHostPanel);

            wvFlow = new WebView2();
            wvFlow.Dock = DockStyle.Fill;
            flowHostPanel.Controls.Add(wvFlow);

            // Loading screen overlay for Flow
            flowLoadingPanel = new Panel();
            flowLoadingPanel.Dock = DockStyle.Fill;
            flowLoadingPanel.BackColor = Color.FromArgb(13, 15, 22);
            flowHostPanel.Controls.Add(flowLoadingPanel);
            flowLoadingPanel.BringToFront();

            BuildLoadingScreenUi(flowLoadingPanel);

            this.Shown += async (s, e) =>
            {
                await InitializeSharedWebViewsAsync();
                Task.Run(async () =>
                {
                    await Task.Delay(3500);
                    var info = await UpdateManager.CheckForUpdatesAsync();
                    if (info.HasUpdate)
                    {
                        ExecuteJsStudio(string.Format("if(window.onUpdateAvailable) window.onUpdateAvailable({0});", Json.Serialize(info)));
                    }
                });
            };
        }

        private Button CreateHeaderButton(string text, Point loc, int width, bool isSelected, bool isAccent = false)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = loc;
            btn.Width = width;
            btn.Height = 32;
            btn.FlatStyle = FlatStyle.Flat;
            btn.Cursor = Cursors.Hand;
            btn.Font = new Font("Segoe UI", 9, FontStyle.Bold);

            if (isAccent)
            {
                btn.FlatAppearance.BorderSize = 0;
                btn.BackColor = Color.FromArgb(0, 160, 220);
                btn.ForeColor = Color.White;
            }
            else if (isSelected)
            {
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Color.FromArgb(0, 210, 255);
                btn.BackColor = Color.FromArgb(32, 42, 62);
                btn.ForeColor = Color.FromArgb(0, 210, 255);
            }
            else
            {
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Color.FromArgb(42, 48, 66);
                btn.BackColor = Color.FromArgb(24, 28, 40);
                btn.ForeColor = Color.FromArgb(210, 218, 230);
            }
            return btn;
        }

        private void BuildLoadingScreenUi(Panel parent)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.ColumnCount = 1;
            table.RowCount = 3;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 35F));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 65F));
            parent.Controls.Add(table);

            Panel emptyTop = new Panel();
            table.Controls.Add(emptyTop, 0, 0);

            lblLoadingStatus = new Label();
            lblLoadingStatus.Text = "⚡ Загрузка Google Flow на " + currentSpecs.DgpuName + "...";
            lblLoadingStatus.ForeColor = Color.FromArgb(0, 210, 255);
            lblLoadingStatus.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            lblLoadingStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblLoadingStatus.Dock = DockStyle.Fill;
            table.Controls.Add(lblLoadingStatus, 0, 1);

            Label lblSub = new Label();
            lblSub.Text = "🔓 Разблокировка Google Labs Country Unlocker v4.1 активна";
            lblSub.ForeColor = Color.FromArgb(148, 163, 184);
            lblSub.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            lblSub.TextAlign = ContentAlignment.MiddleCenter;
            lblSub.Dock = DockStyle.Fill;
            table.Controls.Add(lblSub, 0, 2);
        }

        private async Task InitializeSharedWebViewsAsync()
        {
            try
            {
                string sharedCache = Path.Combine(cacheDirectory, "shared_profile");
                var envOptions = new CoreWebView2EnvironmentOptions(dgpuFlags);
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, sharedCache, envOptions);

                await wvStudio.EnsureCoreWebView2Async(sharedEnvironment);
                await wvFlow.EnsureCoreWebView2Async(sharedEnvironment);

                // Setup Studio WebView
                wvStudio.CoreWebView2.Settings.IsStatusBarEnabled = false;
                wvStudio.WebMessageReceived += OnStudioWebMessage;
                wvStudio.CoreWebView2.Navigate(string.Format("http://127.0.0.1:{0}/index.html", server.Port));

                // Setup Flow WebView
                string unlockerCode = UnlockerScript.GetScript();
                await wvFlow.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(unlockerCode);

                wvFlow.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
                wvFlow.CoreWebView2.Settings.IsStatusBarEnabled = false;
                wvFlow.WebMessageReceived += OnFlowWebMessage;

                wvFlow.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    flowLoadingPanel.Visible = false;
                };

                wvFlow.CoreWebView2.NavigationStarting += (s, e) =>
                {
                    if (lblLoadingStatus != null)
                    {
                        lblLoadingStatus.Text = "⚡ Подключение к " + e.Uri + "...";
                    }
                };

                wvFlow.CoreWebView2.Navigate("https://flow.google.com/");
            }
            catch (Exception ex)
            {
                if (lblLoadingStatus != null)
                {
                    lblLoadingStatus.Text = "Ошибка инициализации: " + ex.Message;
                }
            }
        }

        public void SetViewMode(string mode)
        {
            btnModeSplit.BackColor = Color.FromArgb(24, 28, 40);
            btnModeSplit.ForeColor = Color.FromArgb(210, 218, 230);
            btnModeFlow.BackColor = Color.FromArgb(24, 28, 40);
            btnModeFlow.ForeColor = Color.FromArgb(210, 218, 230);
            btnModeStudio.BackColor = Color.FromArgb(24, 28, 40);
            btnModeStudio.ForeColor = Color.FromArgb(210, 218, 230);

            if (mode == "flow")
            {
                split.Panel1Collapsed = true;
                split.Panel2Collapsed = false;
                btnModeFlow.BackColor = Color.FromArgb(32, 42, 62);
                btnModeFlow.ForeColor = Color.FromArgb(0, 210, 255);
            }
            else if (mode == "studio")
            {
                split.Panel1Collapsed = false;
                split.Panel2Collapsed = true;
                btnModeStudio.BackColor = Color.FromArgb(32, 42, 62);
                btnModeStudio.ForeColor = Color.FromArgb(0, 210, 255);
            }
            else // split 50/50
            {
                split.Panel1Collapsed = false;
                split.Panel2Collapsed = false;
                split.SplitterDistance = this.Width / 2 - 10;
                btnModeSplit.BackColor = Color.FromArgb(32, 42, 62);
                btnModeSplit.ForeColor = Color.FromArgb(0, 210, 255);
            }

            ExecuteJsStudio(string.Format("if(window.onViewModeChanged) window.onViewModeChanged('{0}');", mode));
        }

        private void OnFlowWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                var data = Json.Deserialize<Dictionary<string, object>>(raw);
                if (data == null) return;
                string mtype = data.ContainsKey("type") ? data["type"].ToString() : "";
                if (mtype == "SET_VIEW_MODE")
                {
                    string mode = data.ContainsKey("mode") ? data["mode"].ToString() : "split";
                    this.BeginInvoke(new Action(() => SetViewMode(mode)));
                }
            }
            catch { }
        }

        private void OnStudioWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                var data = Json.Deserialize<Dictionary<string, object>>(raw);
                if (data == null) return;

                string mtype = data.ContainsKey("type") ? data["type"].ToString() : "";

                switch (mtype)
                {
                    case "SET_VIEW_MODE":
                        string mode = data.ContainsKey("mode") ? data["mode"].ToString() : "split";
                        this.BeginInvoke(new Action(() => SetViewMode(mode)));
                        break;

                    case "RELOAD_FLOW":
                        if (wvFlow.CoreWebView2 != null)
                        {
                            flowLoadingPanel.Visible = true;
                            wvFlow.CoreWebView2.Reload();
                        }
                        break;

                    case "NAVIGATE_FLOW":
                        string targetUrl = data.ContainsKey("url") ? data["url"].ToString() : "https://flow.google.com/";
                        if (wvFlow.CoreWebView2 != null)
                        {
                            flowLoadingPanel.Visible = true;
                            wvFlow.CoreWebView2.Navigate(targetUrl);
                        }
                        break;

                    case "SEND_TO_FLOW":
                        string promptText = data.ContainsKey("prompt") ? data["prompt"].ToString() : "";
                        InjectPromptIntoFlow(promptText);
                        break;

                    case "ADD_SINGLE_TASK":
                        if (data.ContainsKey("task"))
                        {
                            var tDict = data["task"] as Dictionary<string, object>;
                            if (tDict != null)
                            {
                                var task = new TaskItem
                                {
                                    TaskId = tDict.ContainsKey("task_id") ? tDict["task_id"].ToString() : ("task_" + Guid.NewGuid().ToString("N").Substring(0, 8)),
                                    Prompt = tDict.ContainsKey("prompt") ? tDict["prompt"].ToString() : "",
                                    NegativePrompt = tDict.ContainsKey("negative_prompt") ? tDict["negative_prompt"].ToString() : "",
                                    AspectRatio = tDict.ContainsKey("aspect_ratio") ? tDict["aspect_ratio"].ToString() : "1:1",
                                    MediaType = tDict.ContainsKey("media_type") ? tDict["media_type"].ToString() : "image",
                                    Seed = tDict.ContainsKey("seed") ? Convert.ToUInt32(tDict["seed"]) : TaskBatchEngine.GenerateSecureSeed()
                                };
                                dispatcher.AddTask(task);
                            }
                        }
                        break;

                    case "LAUNCH_MULTI_SEED":
                        string p = data.ContainsKey("prompt") ? data["prompt"].ToString() : "";
                        int count = data.ContainsKey("count") ? Convert.ToInt32(data["count"]) : 4;
                        string mtypeVal = data.ContainsKey("media_type") ? data["media_type"].ToString() : "image";
                        string ar = data.ContainsKey("aspect_ratio") ? data["aspect_ratio"].ToString() : "1:1";
                        string neg = data.ContainsKey("negative_prompt") ? data["negative_prompt"].ToString() : "";
                        dispatcher.AddBatch(TaskBatchEngine.GenerateMultiSeedBatch(p, count, "Flow High-Quality (dGPU)", neg, ar, mtypeVal));
                        break;

                    case "LAUNCH_CARTESIAN":
                        string sub = data.ContainsKey("subject") ? data["subject"].ToString() : "";
                        var styles = ExtractStringList(data, "styles");
                        var lights = ExtractStringList(data, "lights");
                        string arCart = data.ContainsKey("aspect_ratio") ? data["aspect_ratio"].ToString() : "16:9";
                        string negCart = data.ContainsKey("negative_prompt") ? data["negative_prompt"].ToString() : "";
                        dispatcher.AddBatch(TaskBatchEngine.GenerateCartesianMatrixBatch(sub, styles, lights, negCart, arCart));
                        break;

                    case "LAUNCH_REF_MAPPER":
                        string pRef = data.ContainsKey("prompt") ? data["prompt"].ToString() : "";
                        var paths = ExtractStringList(data, "paths");
                        string arRef = data.ContainsKey("aspect_ratio") ? data["aspect_ratio"].ToString() : "1:1";
                        string negRef = data.ContainsKey("negative_prompt") ? data["negative_prompt"].ToString() : "";
                        dispatcher.AddBatch(TaskBatchEngine.GenerateBatchReferenceMapper(paths, pRef, negRef, arRef));
                        break;

                    case "PAUSE_QUEUE":
                        dispatcher.Pause();
                        break;

                    case "RESUME_QUEUE":
                        dispatcher.Resume();
                        break;

                    case "CLEAR_COMPLETED_TASKS":
                        dispatcher.ClearCompleted();
                        break;

                    case "RETRY_TASK":
                        string tid = data.ContainsKey("task_id") ? data["task_id"].ToString() : "";
                        dispatcher.RetryTask(tid);
                        break;

                    case "OPEN_PROJECTS_FOLDER":
                        Process.Start("explorer.exe", projectsDirectory);
                        break;

                    case "TRIM_MEMORY":
                        GpuConfig.TrimProcessMemory();
                        UpdateHardwareTelemetry();
                        break;

                    case "REAPPLY_GPU":
                        GpuConfig.EnforceWindowsDirectXGpuPreference();
                        break;

                    case "REFRESH_GALLERY":
                    case "UI_READY":
                        SendGalleryItemsToUi();
                        UpdateHardwareTelemetry();
                        break;

                    case "CHECK_UPDATES":
                        Task.Run(async () =>
                        {
                            var info = await UpdateManager.CheckForUpdatesAsync();
                            ExecuteJsStudio(string.Format("if(window.onUpdateCheckResult) window.onUpdateCheckResult({0});", Json.Serialize(info)));
                        });
                        break;

                    case "INSTALL_UPDATE":
                        string dlUrl = data.ContainsKey("download_url") ? data["download_url"].ToString() : "";
                        if (!string.IsNullOrEmpty(dlUrl))
                        {
                            Task.Run(async () =>
                            {
                                await UpdateManager.DownloadAndApplyUpdateAsync(dlUrl, (pct) =>
                                {
                                    ExecuteJsStudio(string.Format("if(window.onUpdateDownloadProgress) window.onUpdateDownloadProgress({0});", pct));
                                });
                            });
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainForm] WebMessage error: " + ex.Message);
            }
        }

        private List<string> ExtractStringList(Dictionary<string, object> dict, string key)
        {
            var res = new List<string>();
            if (dict.ContainsKey(key))
            {
                var arr = dict[key] as System.Collections.ArrayList;
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        if (item != null) res.Add(item.ToString());
                    }
                }
            }
            return res;
        }

        private void InjectPromptIntoFlow(string promptText)
        {
            if (string.IsNullOrEmpty(promptText)) return;
            try
            {
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // 1. Copy directly to Windows Clipboard so user always has it for Ctrl+V
                            try { Clipboard.SetText(promptText); } catch { }

                            // 2. If currently in studio-only mode, switch to split mode so user sees Flow Canvas
                            if (split.Panel2Collapsed)
                            {
                                SetViewMode("split");
                            }

                            // 3. Inject into Flow WebView DOM
                            if (wvFlow != null && !wvFlow.IsDisposed && wvFlow.CoreWebView2 != null)
                            {
                                string esc = Json.Serialize(promptText);
                                string script = string.Format(@"
(function() {{
    const text = {0};
    if (window.__flowNativeBridge && window.__flowNativeBridge.insertPrompt(text)) {{
        return true;
    }}
    const selectors = [
        'textarea',
        '[contenteditable=""true""]',
        'input[type=""text""]',
        '[role=""textbox""]',
        'div[aria-label*=""prompt"" i]',
        'p[data-placeholder]'
    ];
    for (const sel of selectors) {{
        const elems = document.querySelectorAll(sel);
        for (const el of elems) {{
            if (el.offsetParent !== null) {{
                el.focus();
                if (el.tagName.toLowerCase() === 'textarea' || el.tagName.toLowerCase() === 'input') {{
                    el.value = text;
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }} else {{
                    el.innerText = text;
                    el.textContent = text;
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                }}
                return true;
            }}
        }}
    }}
    return false;
}})();
", esc);
                                wvFlow.CoreWebView2.ExecuteScriptAsync(script);
                                wvFlow.Focus();
                            }
                        }
                        catch { }
                    }));
                }
            }
            catch { }
        }

        private void ExecuteJsStudio(string js)
        {
            try
            {
                if (wvStudio != null && !wvStudio.IsDisposed && wvStudio.CoreWebView2 != null && this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (wvStudio != null && !wvStudio.IsDisposed && wvStudio.CoreWebView2 != null)
                            {
                                wvStudio.CoreWebView2.ExecuteScriptAsync(js);
                            }
                        }
                        catch { }
                    }));
                }
            }
            catch { }
        }

        private void BindDispatcherEvents()
        {
            dispatcher.TaskAdded += (task) =>
            {
                var dict = new Dictionary<string, object>
                {
                    { "task_id", task.TaskId },
                    { "prompt", task.Prompt },
                    { "seed", task.Seed },
                    { "aspect_ratio", task.AspectRatio },
                    { "media_type", task.MediaType },
                    { "status", task.Status },
                    { "progress", task.Progress }
                };
                ExecuteJsStudio(string.Format("window.onTaskAdded({0});", Json.Serialize(dict)));
            };

            dispatcher.BatchStarted += (bid, total) =>
            {
                ExecuteJsStudio(string.Format("window.onBatchStarted('{0}', {1});", bid, total));
            };

            dispatcher.ItemProgress += (tid, status, p) =>
            {
                ExecuteJsStudio(string.Format("window.onItemProgress('{0}', '{1}', {2});", tid, status, p));
            };

            dispatcher.ItemCompleted += (tid, outP, sideP) =>
            {
                ExecuteJsStudio(string.Format("window.onItemCompleted('{0}', {1}, {2});", tid, Json.Serialize(outP), Json.Serialize(sideP)));
            };

            dispatcher.ItemFailed += (tid, err) =>
            {
                ExecuteJsStudio(string.Format("window.onItemFailed('{0}', {1});", tid, Json.Serialize(err)));
            };

            dispatcher.BatchFinished += (bid, s, f) =>
            {
                ExecuteJsStudio(string.Format("window.onBatchFinished('{0}', {1}, {2});", bid, s, f));
            };
        }

        private void SendGalleryItemsToUi()
        {
            var items = new List<Dictionary<string, object>>();
            try
            {
                foreach (string f in Directory.GetFiles(projectsDirectory))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".mp4" || ext == ".webp")
                    {
                        string fileName = Path.GetFileName(f);
                        string sidecarFile = Path.Combine(projectsDirectory, Path.GetFileNameWithoutExtension(f) + ".json");
                        object meta = null;
                        if (File.Exists(sidecarFile))
                        {
                            try
                            {
                                string jsonStr = File.ReadAllText(sidecarFile);
                                meta = Json.DeserializeObject(jsonStr);
                            }
                            catch { }
                        }

                        var item = new Dictionary<string, object>
                        {
                            { "filename", fileName },
                            { "media_url", string.Format("http://127.0.0.1:{0}/media/{1}", server.Port, fileName) },
                            { "metadata", meta }
                        };
                        items.Add(item);
                    }
                }
            }
            catch { }

            string js = "window.onGalleryUpdate(" + Json.Serialize(items) + ");";
            ExecuteJsStudio(js);
        }

        private void UpdateHardwareTelemetry()
        {
            currentSpecs.AppRamMb = GpuConfig.GetCurrentMemoryUsageMb();

            if (lblHardwareBadge != null)
            {
                lblHardwareBadge.Text = string.Format("🎮 {0} ({1}) • 💾 {2} MB", currentSpecs.DgpuName, currentSpecs.DgpuVram, currentSpecs.AppRamMb);
            }

            var telem = new Dictionary<string, object>
            {
                { "dgpu_name", currentSpecs.DgpuName },
                { "dgpu_vram", currentSpecs.DgpuVram },
                { "dgpu_driver", currentSpecs.DgpuDriver },
                { "igpu_name", currentSpecs.IgpuName },
                { "igpu_vram", currentSpecs.IgpuVram },
                { "cpu_name", currentSpecs.CpuName },
                { "cpu_cores", currentSpecs.CpuCores },
                { "cpu_threads", currentSpecs.CpuThreads },
                { "total_ram_gb", currentSpecs.TotalRamGb },
                { "free_ram_gb", currentSpecs.FreeRamGb },
                { "app_ram_mb", currentSpecs.AppRamMb }
            };

            ExecuteJsStudio("window.onTelemetryUpdate(" + Json.Serialize(telem) + ");");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            telemetryTimer.Stop();
            server.Stop();
            base.OnFormClosing(e);
        }
    }
}
