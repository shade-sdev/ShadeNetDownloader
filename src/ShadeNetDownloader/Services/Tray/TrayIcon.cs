using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ShadeNetDownloader.Services.Tray;

/// <summary>
/// ShadeNet's icon in the notification area, on the plain Win32 <c>Shell_NotifyIcon</c> API (no extra packages): an icon, a
/// tooltip, click events, and balloon notifications. Events arrive on a hidden message-only window; the icon re-adds itself
/// when Explorer restarts (<c>TaskbarCreated</c>).
/// </summary>
public sealed class TrayIcon : ITrayNotifier, IDisposable
{
    const int CallbackMessage = 0x8000 + 77;   // WM_APP + 77

    // version-4 callback events (LOWORD(lParam))
    const int WM_CONTEXTMENU = 0x007B;
    const int NIN_SELECT = 0x0400;
    const int NIN_KEYSELECT = 0x0401;
    const int NIN_BALLOONUSERCLICK = 0x0405;

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_INFO = 0x01, NIIF_NOSOUND = 0x10, NIIF_LARGE_ICON = 0x20;

    readonly HwndSource _window;
    readonly uint _taskbarCreated;
    IntPtr _icon;
    string _tooltip = "ShadeNet Downloader";
    bool _visible;
    object? _balloonTag;

    public TrayIcon(string iconPath)
    {
        // a message-only window: it exists only to receive the icon's messages
        _window = new HwndSource(new HwndSourceParameters("ShadeNetTray") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        _icon = File.Exists(iconPath) ? LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : IntPtr.Zero;
    }

    /// <summary>Shows another icon (the app icon the user chose); a file that cannot be read leaves the one shown.</summary>
    public void SetIcon(string iconPath)
    {
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        var icon = File.Exists(iconPath) ? LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : IntPtr.Zero;
        if (icon == IntPtr.Zero) return;
        var old = _icon;
        _icon = icon;
        if (_visible) Notify(NIM_MODIFY, NIF_ICON);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>The user clicked the icon (or pressed Enter on it): open the window.</summary>
    public event Action? Clicked;

    /// <summary>The user right-clicked the icon; the point is in screen pixels.</summary>
    public event Action<int, int>? ContextMenuRequested;

    public event Action<object?>? BalloonClicked;

    public bool IsVisible => _visible;

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            var text = value.Length > 120 ? value[..120] : value;
            if (text == _tooltip) return;
            _tooltip = text;
            if (_visible) Notify(NIM_MODIFY, NIF_TIP | NIF_SHOWTIP);
        }
    }

    public void Show()
    {
        if (_visible) return;
        _visible = Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (_visible) Notify(NIM_SETVERSION, 0, version: 4);
    }

    public void Hide()
    {
        if (!_visible) return;
        Notify(NIM_DELETE, 0);
        _visible = false;
    }

    public void ShowBalloon(string title, string text, bool silent, object? tag)
    {
        if (!_visible) return;
        _balloonTag = tag;
        var flags = NIIF_INFO | NIIF_LARGE_ICON | (silent ? NIIF_NOSOUND : 0);
        Notify(NIM_MODIFY, NIF_INFO, info: (Clip(title, 63), Clip(text, 255), flags));
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    bool Notify(uint message, uint flags, uint version = 0, (string Title, string Text, uint Flags)? info = null)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = info?.Text ?? "",
            szInfoTitle = info?.Title ?? "",
            dwInfoFlags = info?.Flags ?? 0,
            uTimeoutOrVersion = version,
        };
        return Shell_NotifyIcon(message, ref data);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // version 4: the event is in LOWORD(lParam), the anchor point in (LOWORD, HIWORD)(wParam)
            var evt = (int)((long)lParam & 0xFFFF);
            var x = (short)((long)wParam & 0xFFFF);
            var y = (short)(((long)wParam >> 16) & 0xFFFF);

            switch (evt)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Clicked?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(x, y);
                    break;
                case NIN_BALLOONUSERCLICK:
                    BalloonClicked?.Invoke(_balloonTag);
                    break;
            }

            handled = true;
        }
        else if (msg == _taskbarCreated && _visible)
        {
            // Explorer restarted and lost every icon: put ours back
            _visible = false;
            Show();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Hide();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
