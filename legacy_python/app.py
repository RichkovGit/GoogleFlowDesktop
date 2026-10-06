"""
Google Flow Desktop - Native Windows Application Host
Integrates WinForms, Dual Edge WebView2 controls, Discrete GPU enforcement,
Google Flow & Labs Country Unlocker, Prompt Studio, and Worker Dispatcher.
"""
import os
import sys
import json
import time
import socket
import threading
import http.server
import socketserver

# Set Windows DPI Awareness
try:
    import ctypes
    ctypes.windll.user32.SetProcessDPIAware()
except Exception:
    pass

from config import (
    APP_NAME, APP_VERSION, CACHE_DIR, PROJECTS_DIR, LOGS_DIR,
    SYSTEM_PROMPT_DIRECTIVES, DEFAULT_NEGATIVE_SANITIZER
)
from gpu_manager import (
    enforce_windows_directx_dgpu_preference,
    get_dgpu_chromium_flags,
    get_primary_dgpu_name,
    trim_process_memory,
    get_system_telemetry
)
from unlocker import get_unlocker_script
from task_engine import TaskItem, TaskBatchEngine
from worker_dispatcher import WorkerDispatcher

# Load .NET Framework & WinForms via Python.NET
import clr
clr.AddReference('System.Windows.Forms')
clr.AddReference('System.Drawing')
import System
from System import Action, Func, Object, String, Uri
from System.Drawing import Color, Size, Font, FontStyle, Point, Icon
from System.Windows.Forms import (
    Form, SplitContainer, Orientation, DockStyle, Panel, Button,
    Label, ToolStrip, ToolStripButton, ToolStripSeparator, ToolStripLabel,
    Application, FlatStyle, FormStartPosition, FormWindowState
)

# Load Microsoft.Web.WebView2 assemblies
try:
    from webview.platforms.edgechromium import interop_dll_path
    clr.AddReference(interop_dll_path('Microsoft.Web.WebView2.Core.dll'))
    clr.AddReference(interop_dll_path('Microsoft.Web.WebView2.WinForms.dll'))
except Exception as e:
    print(f"[Warning] Loading WebView2 DLLs from site-packages: {e}")

from Microsoft.Web.WebView2.WinForms import WebView2
from Microsoft.Web.WebView2.Core import CoreWebView2CreationProperties

# 1. Local HTTP Server for Studio UI & Media Streaming
class StudioHttpHandler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path):
        ui_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ui')
        clean_path = path.split('?')[0]
        if clean_path.startswith('/media/'):
            filename = clean_path[7:]
            return os.path.join(PROJECTS_DIR, filename)
        rel = clean_path.lstrip('/')
        if not rel or rel == '':
            rel = 'index.html'
        return os.path.join(ui_dir, rel)

    def log_message(self, format, *args):
        pass  # Suppress HTTP access logging

def start_local_studio_server():
    sock = socket.socket()
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
    sock.close()

    server = socketserver.TCPServer(('127.0.0.1', port), StudioHttpHandler)
    t = threading.Thread(target=server.serve_forever, daemon=True)
    t.start()
    return port

# 2. Native WinForms Application Form
class GoogleFlowAppForm(Form):
    def __init__(self, http_port: int):
        super().__init__()
        self.http_port = http_port
        self.unlocker_js = get_unlocker_script()
        self.dgpu_flags = get_dgpu_chromium_flags()
        self.primary_dgpu = get_primary_dgpu_name()

        # Initialize Worker Dispatcher
        self.dispatcher = WorkerDispatcher(output_dir=PROJECTS_DIR)
        self._bind_dispatcher_events()

        # Form Window Properties
        self.Text = f"{APP_NAME} - {self.primary_dgpu} (dGPU)"
        self.Width = 1440
        self.Height = 900
        self.MinimumSize = Size(1024, 680)
        self.StartPosition = FormStartPosition.CenterScreen
        self.BackColor = Color.FromArgb(17, 20, 28)

        # Build UI Structure
        self._build_top_toolbar()
        self._build_split_containers()

        # Telemetry Timer (updates RAM/GPU every 3 sec)
        self.telemetry_timer = threading.Thread(target=self._telemetry_worker, daemon=True)
        self.telemetry_timer.start()

    def _build_top_toolbar(self):
        self.top_panel = Panel()
        self.top_panel.Dock = DockStyle.Top
        self.top_panel.Height = 46
        self.top_panel.BackColor = Color.FromArgb(15, 18, 26)
        self.Controls.Add(self.top_panel)

        # Title / GPU Label
        self.lbl_title = Label()
        self.lbl_title.Text = f"⚡ Flow Native | 🎮 {self.primary_dgpu}"
        self.lbl_title.ForeColor = Color.FromArgb(0, 210, 255)
        self.lbl_title.Font = Font("Segoe UI", 10, FontStyle.Bold)
        self.lbl_title.AutoSize = True
        self.lbl_title.Location = Point(14, 13)
        self.top_panel.Controls.Add(self.lbl_title)

        # View Switcher Buttons
        btn_x = 360
        btn_y = 7
        btn_h = 32

        # Split 50/50
        self.btn_view_split = self._create_nav_button("◫ Сплит-режим (50/50)", Point(btn_x, btn_y), 160)
        self.btn_view_split.Click += self._on_view_split
        self.top_panel.Controls.Add(self.btn_view_split)
        btn_x += 166

        # Flow Canvas 100%
        self.btn_view_flow = self._create_nav_button("🎨 Только Google Flow", Point(btn_x, btn_y), 155)
        self.btn_view_flow.Click += self._on_view_flow
        self.top_panel.Controls.Add(self.btn_view_flow)
        btn_x += 161

        # Studio 100%
        self.btn_view_studio = self._create_nav_button("✨ Только Студия", Point(btn_x, btn_y), 140)
        self.btn_view_studio.Click += self._on_view_studio
        self.top_panel.Controls.Add(self.btn_view_studio)
        btn_x += 160

        # Flow Controls
        self.btn_send_prompt = self._create_accent_button("🚀 Вставить промпт во Flow", Point(btn_x, btn_y), 180)
        self.btn_send_prompt.Click += self._on_send_prompt_to_flow
        self.top_panel.Controls.Add(self.btn_send_prompt)
        btn_x += 186

        self.btn_flow_reload = self._create_nav_button("🔄 Обновить Flow", Point(btn_x, btn_y), 130)
        self.btn_flow_reload.Click += self._on_flow_reload
        self.top_panel.Controls.Add(self.btn_flow_reload)
        btn_x += 136

        self.btn_flow_home = self._create_nav_button("🏠 Главная Flow", Point(btn_x, btn_y), 120)
        self.btn_flow_home.Click += self._on_flow_home
        self.top_panel.Controls.Add(self.btn_flow_home)

    def _create_nav_button(self, text: str, loc: Point, width: int):
        btn = Button()
        btn.Text = text
        btn.Location = loc
        btn.Width = width
        btn.Height = 32
        btn.FlatStyle = FlatStyle.Flat
        btn.FlatAppearance.BorderSize = 1
        btn.FlatAppearance.BorderColor = Color.FromArgb(40, 48, 64)
        btn.BackColor = Color.FromArgb(24, 28, 38)
        btn.ForeColor = Color.FromArgb(220, 225, 235)
        btn.Font = Font("Segoe UI", 9, FontStyle.Regular)
        btn.Cursor = System.Windows.Forms.Cursors.Hand
        return btn

    def _create_accent_button(self, text: str, loc: Point, width: int):
        btn = Button()
        btn.Text = text
        btn.Location = loc
        btn.Width = width
        btn.Height = 32
        btn.FlatStyle = FlatStyle.Flat
        btn.FlatAppearance.BorderSize = 0
        btn.BackColor = Color.FromArgb(0, 160, 220)
        btn.ForeColor = Color.White
        btn.Font = Font("Segoe UI", 9, FontStyle.Bold)
        btn.Cursor = System.Windows.Forms.Cursors.Hand
        return btn

    def _build_split_containers(self):
        self.split_container = SplitContainer()
        self.split_container.Dock = DockStyle.Fill
        self.split_container.Orientation = Orientation.Vertical
        self.split_container.SplitterWidth = 5
        self.split_container.BackColor = Color.FromArgb(30, 35, 48)
        self.Controls.Add(self.split_container)
        self.split_container.BringToFront()

        # Set 50/50 initial distance
        self.split_container.SplitterDistance = 640

        # Panel 1: Studio WebView2
        self.wv_studio = WebView2()
        self.wv_studio.Dock = DockStyle.Fill
        props_studio = CoreWebView2CreationProperties()
        props_studio.UserDataFolder = os.path.join(CACHE_DIR, "studio_profile")
        props_studio.AdditionalBrowserArguments = self.dgpu_flags + " --allow-file-access-from-files"
        self.wv_studio.CreationProperties = props_studio
        self.wv_studio.CoreWebView2InitializationCompleted += self._on_studio_ready
        self.wv_studio.WebMessageReceived += self._on_studio_web_message
        self.split_container.Panel1.Controls.Add(self.wv_studio)

        # Panel 2: Google Flow WebView2
        self.wv_flow = WebView2()
        self.wv_flow.Dock = DockStyle.Fill
        props_flow = CoreWebView2CreationProperties()
        props_flow.UserDataFolder = os.path.join(CACHE_DIR, "flow_profile")
        props_flow.AdditionalBrowserArguments = self.dgpu_flags
        self.wv_flow.CreationProperties = props_flow
        self.wv_flow.CoreWebView2InitializationCompleted += self._on_flow_ready
        self.split_container.Panel2.Controls.Add(self.wv_flow)

        # Initialize both WebViews asynchronously
        self.Shown += self._on_form_shown

    def _on_form_shown(self, sender, args):
        self.wv_studio.EnsureCoreWebView2Async(None)
        self.wv_flow.EnsureCoreWebView2Async(None)

    def _on_studio_ready(self, sender, args):
        if args.IsSuccess:
            studio_url = f"http://127.0.0.1:{self.http_port}/index.html"
            self.wv_studio.CoreWebView2.Navigate(studio_url)
            print(f"[App] Studio WebView2 loaded from {studio_url}")
        else:
            print(f"[App] Studio WebView2 init error: {args.InitializationException}")

    def _on_flow_ready(self, sender, args):
        if args.IsSuccess:
            # 1. Inject Country Unlocker & DOM Bridge at document-creation
            self.wv_flow.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(self.unlocker_js)
            print("[App] Google Flow Country Unlocker v4.1 & Bridge successfully injected!")

            # 2. Navigate to Google Flow
            self.wv_flow.CoreWebView2.Navigate("https://flow.google.com/")
        else:
            print(f"[App] Flow WebView2 init error: {args.InitializationException}")

    # View Mode Handlers
    def _on_view_split(self, sender, e):
        self.split_container.Panel1Collapsed = False
        self.split_container.Panel2Collapsed = False
        self.split_container.SplitterDistance = self.Width // 2 - 10

    def _on_view_flow(self, sender, e):
        self.split_container.Panel1Collapsed = True
        self.split_container.Panel2Collapsed = False

    def _on_view_studio(self, sender, e):
        self.split_container.Panel1Collapsed = False
        self.split_container.Panel2Collapsed = True

    def _on_flow_reload(self, sender, e):
        if self.wv_flow.CoreWebView2:
            self.wv_flow.CoreWebView2.Reload()

    def _on_flow_home(self, sender, e):
        if self.wv_flow.CoreWebView2:
            self.wv_flow.CoreWebView2.Navigate("https://flow.google.com/")

    def _on_send_prompt_to_flow(self, sender, e):
        # Requests compiled prompt from Studio to inject into Flow
        js_code = "postHostMessage({ type: 'SEND_TO_FLOW', prompt: getCompiledPrompt() });"
        self._execute_js_studio(js_code)

    # Studio WebMessage Handler (Bridge between UI and Python Host)
    def _on_studio_web_message(self, sender, args):
        try:
            msg_str = args.TryGetWebMessageAsString()
            data = json.loads(msg_str)
            mtype = data.get('type')

            if mtype == 'SEND_TO_FLOW':
                prompt_text = data.get('prompt', '')
                self._inject_prompt_into_flow(prompt_text)

            elif mtype == 'ADD_SINGLE_TASK':
                task_dict = data.get('task', {})
                task = TaskItem(
                    prompt=task_dict.get('prompt', ''),
                    negative_prompt=task_dict.get('negative_prompt', ''),
                    aspect_ratio=task_dict.get('aspect_ratio', '1:1'),
                    media_type=task_dict.get('media_type', 'image'),
                    model=task_dict.get('model', 'Flow High-Quality')
                )
                self.dispatcher.add_task(task)

            elif mtype == 'LAUNCH_MULTI_SEED':
                tasks = TaskBatchEngine.generate_multi_seed_batch(
                    base_prompt=data.get('prompt', ''),
                    count=int(data.get('count', 4)),
                    media_type=data.get('media_type', 'image'),
                    aspect_ratio=data.get('aspect_ratio', '1:1'),
                    negative_prompt=data.get('negative_prompt', '')
                )
                self.dispatcher.add_batch(tasks)

            elif mtype == 'LAUNCH_CARTESIAN':
                tasks = TaskBatchEngine.generate_cartesian_matrix_batch(
                    base_subject=data.get('subject', ''),
                    style_tags=data.get('styles', []),
                    lighting_tags=data.get('lights', []),
                    aspect_ratio=data.get('aspect_ratio', '16:9'),
                    negative_prompt=data.get('negative_prompt', '')
                )
                self.dispatcher.add_batch(tasks)

            elif mtype == 'LAUNCH_REF_MAPPER':
                tasks = TaskBatchEngine.generate_batch_reference_mapper(
                    image_paths=data.get('paths', []),
                    modifying_prompt=data.get('prompt', ''),
                    aspect_ratio=data.get('aspect_ratio', '1:1'),
                    negative_prompt=data.get('negative_prompt', '')
                )
                self.dispatcher.add_batch(tasks)

            elif mtype == 'PAUSE_QUEUE':
                self.dispatcher.pause()

            elif mtype == 'RESUME_QUEUE':
                self.dispatcher.resume()

            elif mtype == 'CLEAR_COMPLETED_TASKS':
                self.dispatcher.clear_completed()

            elif mtype == 'RETRY_TASK':
                self.dispatcher.retry_task(data.get('task_id', ''))

            elif mtype == 'OPEN_PROJECTS_FOLDER':
                os.startfile(PROJECTS_DIR)

            elif mtype == 'TRIM_MEMORY':
                trim_process_memory()

            elif mtype == 'REAPPLY_GPU':
                enforce_windows_directx_dgpu_preference()

            elif mtype == 'REFRESH_GALLERY' or mtype == 'UI_READY':
                self._send_gallery_items_to_ui()
                self._send_telemetry_to_ui()

        except Exception as e:
            print(f"[App] WebMessage error: {e}")

    def _inject_prompt_into_flow(self, prompt_text: str):
        if not self.wv_flow.CoreWebView2:
            return
        escaped_prompt = json.dumps(prompt_text)
        injection_js = f"""
        if (window.__flowNativeBridge) {{
            window.__flowNativeBridge.insertPrompt({escaped_prompt});
        }} else {{
            const ta = document.querySelector('textarea, [contenteditable="true"]');
            if (ta) {{
                ta.value = {escaped_prompt};
                ta.dispatchEvent(new Event('input', {{ bubbles: true }}));
            }}
        }}
        """
        self.Invoke(Action(lambda: self.wv_flow.CoreWebView2.ExecuteScriptAsync(injection_js)))

    def _execute_js_studio(self, js: str):
        if self.wv_studio.CoreWebView2:
            self.Invoke(Action(lambda: self.wv_studio.CoreWebView2.ExecuteScriptAsync(js)))

    # Dispatcher Event Bindings
    def _bind_dispatcher_events(self):
        self.dispatcher.on_batch_started = lambda bid, total: (
            self._execute_js_studio(f"window.onBatchStarted('{bid}', {total});")
        )
        self.dispatcher.on_item_progress = lambda tid, status, p: (
            self._execute_js_studio(f"window.onItemProgress('{tid}', '{status}', {p});")
        )
        self.dispatcher.on_item_completed = lambda tid, out_p, side_p: (
            self._execute_js_studio(f"window.onItemCompleted('{tid}', {json.dumps(out_p)}, {json.dumps(side_p)});")
        )
        self.dispatcher.on_item_failed = lambda tid, err: (
            self._execute_js_studio(f"window.onItemFailed('{tid}', {json.dumps(err)});")
        )
        self.dispatcher.on_batch_finished = lambda bid, s, f: (
            self._execute_js_studio(f"window.onBatchFinished('{bid}', {s}, {f});")
        )

    def _send_gallery_items_to_ui(self):
        items = []
        try:
            for f in os.listdir(PROJECTS_DIR):
                if f.endswith(('.png', '.jpg', '.mp4', '.webp')):
                    base = os.path.splitext(f)[0]
                    sidecar_file = os.path.join(PROJECTS_DIR, base + ".json")
                    meta = {}
                    if os.path.exists(sidecar_file):
                        try:
                            with open(sidecar_file, 'r', encoding='utf-8') as sf:
                                meta = json.load(sf)
                        except Exception:
                            pass
                    items.append({
                        "filename": f,
                        "media_url": f"http://127.0.0.1:{self.http_port}/media/{f}",
                        "metadata": meta
                    })
        except Exception:
            pass

        js = f"window.onGalleryUpdate({json.dumps(items)});"
        self._execute_js_studio(js)

    def _send_telemetry_to_ui(self):
        telem = get_system_telemetry()
        js = f"window.onTelemetryUpdate({json.dumps(telem)});"
        self._execute_js_studio(js)

    def _telemetry_worker(self):
        while True:
            time.sleep(3.5)
            try:
                trim_process_memory()
                self._send_telemetry_to_ui()
            except Exception:
                pass

def main():
    print(f"=== Starting {APP_NAME} v{APP_VERSION} ===")
    
    # 1. Enforce Windows DirectX High Performance (Discrete GPU: GTX 1650) Registry
    enforce_windows_directx_dgpu_preference()
    print("[App] Windows UserGpuPreferences configured: Discrete GPU forced (GpuPreference=2;)")

    # 2. Start local HTTP daemon for studio UI & streaming
    port = start_local_studio_server()
    print(f"[App] Local Studio HTTP server running at http://127.0.0.1:{port}")

    # 3. Launch WinForms STA Application
    form = GoogleFlowAppForm(http_port=port)
    Application.Run(form)

if __name__ == "__main__":
    main()
