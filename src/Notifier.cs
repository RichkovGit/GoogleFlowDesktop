using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>
    /// Native Windows notifications. Uses a hidden NotifyIcon balloon instead of
    /// shelling out to PowerShell (faster, no window flash, works offline).
    /// </summary>
    public class Notifier : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        private NotifyIcon icon;
        private DateTime lastToast = DateTime.MinValue;

        public void Attach(Icon appIcon)
        {
            if (icon != null) return;
            icon = new NotifyIcon();
            icon.Icon = appIcon != null ? appIcon : SystemIcons.Application;
            icon.Text = "Google Flow Desktop";
            icon.Visible = true;
        }

        public void PlayChime(string kind)
        {
            try
            {
                uint val = 0x00000040; // MB_ICONASTERISK
                if (kind == "error") val = 0x00000010;
                else if (kind == "warning") val = 0x00000030;
                MessageBeep(val);
            }
            catch { }
        }

        public void ShowToast(string title, string message)
        {
            ShowToast(title, message, false);
        }

        public void ShowToast(string title, string message, bool force)
        {
            if (icon == null) return;
            if (!force && (DateTime.Now - lastToast).TotalSeconds < 4.0) return;
            lastToast = DateTime.Now;

            try
            {
                icon.BalloonTipTitle = title;
                icon.BalloonTipText = message;
                icon.BalloonTipIcon = ToolTipIcon.Info;
                icon.ShowBalloonTip(5000);
            }
            catch { }
        }

        public void NotifySnapshotSaved(string fileName)
        {
            PlayChime("asterisk");
            ShowToast("Google Flow: снимок сохранён", fileName, true);
        }

        public void Dispose()
        {
            if (icon != null)
            {
                try
                {
                    icon.Visible = false;
                    icon.Dispose();
                }
                catch { }
                icon = null;
            }
        }
    }
}
