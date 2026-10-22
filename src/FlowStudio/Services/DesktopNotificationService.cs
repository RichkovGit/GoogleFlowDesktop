using System.Runtime.InteropServices;

namespace FlowStudio.Services;

/// <summary>Native Windows notification-area balloons; Windows owns notification visibility and quiet hours.</summary>
public sealed class DesktopNotificationService : IDisposable
{
    const uint Identifier = 0xF10A;
    readonly IntPtr owner;
    readonly object sync = new();
    bool installed, disposed;
    public DesktopNotificationService(IntPtr ownerWindow) => owner = ownerWindow != IntPtr.Zero ? ownerWindow :
        throw new ArgumentException("Для уведомлений требуется открытое окно приложения.");

    public bool Show(string title, string message)
    {
        lock (sync)
        {
            if (disposed) return false;
            var data = Data();
            if (!installed)
            {
                data.Flags = 0x2 | 0x4; // NIF_ICON | NIF_TIP.
                installed = ShellNotifyIcon(0, ref data); // NIM_ADD.
                if (!installed) return false;
            }
            data.Flags = 0x10; // NIF_INFO.
            data.InfoTitle = Clean(title, 63);
            data.Info = Clean(message, 255);
            data.InfoFlags = 1; // NIIF_INFO. No focus stealing and no custom executable.
            data.TimeoutOrVersion = 10000;
            return ShellNotifyIcon(1, ref data); // NIM_MODIFY.
        }
    }

    NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = owner, Id = Identifier,
        Icon = LoadIcon(IntPtr.Zero, new IntPtr(32516)), Tip = "Flow Studio", Info = "", InfoTitle = ""
    };
    static string Clean(string text, int maxLength) => new((text ?? "").Where(c => !char.IsControl(c) || c == '\n').Take(maxLength).ToArray());
    public void Dispose()
    {
        lock(sync)
        {
            if(disposed) return;
            if(installed) { var data=Data(); _=ShellNotifyIcon(2,ref data); }
            installed=false;disposed=true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShellNotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW", CharSet = CharSet.Unicode)] static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
}
