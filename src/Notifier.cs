using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GoogleFlowDesktop
{
    public static class Notifier
    {
        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        public static void PlaySystemChime(string chimeType = "asterisk")
        {
            try
            {
                uint val = 0x00000040; // MB_ICONASTERISK
                if (chimeType == "error") val = 0x00000010;
                else if (chimeType == "warning") val = 0x00000030;
                MessageBeep(val);
            }
            catch { }
        }

        public static void ShowToast(string title, string message)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    string safeTitle = (title ?? "").Replace("'", "''").Replace("\"", "`\"");
                    string safeMsg = (message ?? "").Replace("'", "''").Replace("\"", "`\"");

                    string psCode = string.Format(@"
try {{
    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    `$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
    `$textNodes = `$template.GetElementsByTagName('text')
    `$textNodes.Item(0).AppendChild(`$template.CreateTextNode('{0}')) | Out-Null
    `$textNodes.Item(1).AppendChild(`$template.CreateTextNode('{1}')) | Out-Null
    `$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Google Flow Desktop')
    `$notification = [Windows.UI.Notifications.ToastNotification]::new(`$template)
    `$notifier.Show(`$notification)
}} catch {{}}
", safeTitle, safeMsg);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -WindowStyle Hidden -Command \"" + psCode + "\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                }
                catch { }
            });
        }

        public static void NotifyBatchFinished(string batchId, int successCount, int failCount)
        {
            PlaySystemChime("asterisk");
            ShowToast("Google Flow: Пакет завершён!", string.Format("Создано: {0} медиа | Ошибок: {1}", successCount, failCount));
        }

        public static void NotifyItemCompleted(string taskId, string fileName)
        {
            PlaySystemChime("asterisk");
            ShowToast("Google Flow: Рендер готов", "Сохранено: " + fileName);
        }
    }
}
