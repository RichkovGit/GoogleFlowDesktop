"""
Google Flow Desktop - Windows Native Notifications & Sound Engine
Dispatches Toast notifications and native audio chimes on completion and alerts.
"""
import ctypes
import subprocess
import threading

def play_system_chime(chime_type: str = "asterisk"):
    """
    Plays native Windows audio chime via user32.MessageBeep.
    0x00000040: MB_ICONASTERISK (Information/Success)
    0x00000030: MB_ICONEXCLAMATION (Warning)
    0x00000010: MB_ICONHAND (Error)
    """
    try:
        types = {
            "asterisk": 0x00000040,
            "success": 0x00000040,
            "warning": 0x00000030,
            "error": 0x00000010,
            "default": 0x00000000
        }
        val = types.get(chime_type.lower(), 0x00000040)
        ctypes.windll.user32.MessageBeep(val)
    except Exception:
        pass

def show_windows_toast(title: str, message: str):
    """
    Displays a native Windows 10/11 Toast Notification in the Action Center.
    Runs asynchronously in a background thread so it never blocks the UI.
    """
    def _worker():
        try:
            safe_title = title.replace("'", "''").replace('"', '`"')
            safe_msg = message.replace("'", "''").replace('"', '`"')
            ps_code = f"""
            try {{
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
                $textNodes = $template.GetElementsByTagName('text')
                $textNodes.Item(0).AppendChild($template.CreateTextNode('{safe_title}')) | Out-Null
                $textNodes.Item(1).AppendChild($template.CreateTextNode('{safe_msg}')) | Out-Null
                $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Google Flow Desktop')
                $notification = [Windows.UI.Notifications.ToastNotification]::new($template)
                $notifier.Show($notification)
            }} catch {{}}
            """
            subprocess.run(
                ["powershell", "-NoProfile", "-WindowStyle", "Hidden", "-Command", ps_code],
                capture_output=True,
                creationflags=0x08000000 # CREATE_NO_WINDOW
            )
        except Exception:
            pass

    threading.Thread(target=_worker, daemon=True).start()

def notify_batch_finished(batch_id: str, success_count: int, fail_count: int):
    """Called on BatchFinished event."""
    play_system_chime("success")
    title = "Google Flow: Пакет генераций завершён!"
    msg = f"Успешно создано: {success_count} медиа | Ошибок: {fail_count}"
    show_windows_toast(title, msg)

def notify_item_completed(task_id: str, file_name: str):
    """Called when an individual render task completes."""
    play_system_chime("asterisk")
    title = "Google Flow: Рендер готов"
    msg = f"Сохранено: {file_name}"
    show_windows_toast(title, msg)

def notify_error(title: str, message: str):
    """Called on critical error."""
    play_system_chime("error")
    show_windows_toast(f"Google Flow: {title}", message)

if __name__ == "__main__":
    notify_batch_finished("batch-001", 4, 0)
    print("Notification dispatched successfully!")
