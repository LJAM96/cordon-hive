using System.Runtime.InteropServices;

namespace Cordon.Shared.Windows;

/// <summary>
/// Win32 Shell_NotifyIconW notification area fallback host when a taskbar overlay cannot be docked.
/// Uses a hidden message-only owner window and standard system application icon with no external assets.
/// </summary>
public sealed class TrayIconFallbackHost : IWidgetHost
{
    private const int MaxTooltipLength = 127;
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8001; // WM_APP + 1

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const int ErrorClassAlreadyExists = 1410;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiApplication = new(32512);
    private const string WindowClassName = "CordonHiveTrayFallbackHostWindow";

    private static readonly WndProcDelegate WindowProc = StaticWndProc;
    private static readonly object ClassRegistrationLock = new();
    private static bool _classRegistered;

    private readonly NotificationBadgeState _badgeState = new();
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconFallbackHost()
    {
        InitializeWindowAndIcon();
    }

    public NotificationBadgeState BadgeState => _badgeState;

    public bool IsFlyoutOpen => _badgeState.IsFlyoutOpen;

    public void UpdateBadge(int unresolvedCount, bool isConnectivityWarning)
    {
        if (_disposed)
        {
            return;
        }

        _badgeState.UnresolvedCount = unresolvedCount;
        _badgeState.IsConnectivityWarning = isConnectivityWarning;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var tip = FormatTooltip(_badgeState.UnresolvedCount, _badgeState.ShowWarningIndicator);
        var data = CreateNotifyIconData(tip);

        try
        {
            if (_iconAdded)
            {
                if (!NativeMethods.Shell_NotifyIconW(NimModify, ref data))
                {
                    _iconAdded = NativeMethods.Shell_NotifyIconW(NimAdd, ref data);
                }
            }
            else
            {
                _iconAdded = NativeMethods.Shell_NotifyIconW(NimAdd, ref data);
            }
        }
        catch
        {
            // Ignore shell communication errors in headless/service/disconnected sessions.
        }
    }

    public void ShowFlyout()
    {
        if (_disposed)
        {
            return;
        }

        _badgeState.IsFlyoutOpen = true;
    }

    public void HideFlyout()
    {
        if (_disposed)
        {
            return;
        }

        _badgeState.IsFlyoutOpen = false;
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    ~TrayIconFallbackHost()
    {
        DisposeCore();
    }

    private void InitializeWindowAndIcon()
    {
        try
        {
            var hInstance = NativeMethods.GetModuleHandleW(null);
            EnsureWindowClassRegistered(hInstance);

            _hwnd = NativeMethods.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: WindowClassName,
                lpWindowName: "Cordon Hive Tray Host",
                dwStyle: 0,
                x: 0,
                y: 0,
                nWidth: 0,
                nHeight: 0,
                hWndParent: HwndMessage,
                hMenu: IntPtr.Zero,
                hInstance: hInstance,
                lpParam: IntPtr.Zero);

            _hIcon = NativeMethods.LoadIconW(IntPtr.Zero, IdiApplication);

            if (_hwnd != IntPtr.Zero)
            {
                var tip = FormatTooltip(_badgeState.UnresolvedCount, _badgeState.ShowWarningIndicator);
                var data = CreateNotifyIconData(tip);
                _iconAdded = NativeMethods.Shell_NotifyIconW(NimAdd, ref data);
            }
        }
        catch
        {
            // Gracefully degrade when running without an interactive desktop/shell.
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _badgeState.IsFlyoutOpen = false;

        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            try
            {
                var data = CreateNotifyIconData(string.Empty);
                NativeMethods.Shell_NotifyIconW(NimDelete, ref data);
            }
            catch
            {
                // Ignore shell errors during teardown.
            }

            _iconAdded = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                NativeMethods.DestroyWindow(_hwnd);
            }
            catch
            {
                // Ignore native teardown failures across threads or disconnected sessions.
            }

            _hwnd = IntPtr.Zero;
        }

        // System shared icon from LoadIconW(NULL, IDI_APPLICATION) must not be destroyed.
        _hIcon = IntPtr.Zero;
    }

    private NOTIFYICONDATAW CreateNotifyIconData(string tooltip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = _hIcon,
        szTip = ClampTooltip(tooltip),
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private static string FormatTooltip(int unresolvedCount, bool isConnectivityWarning)
    {
        var count = Math.Max(0, unresolvedCount);
        var unit = count == 1 ? "alert" : "alerts";
        var text = isConnectivityWarning
            ? $"Cordon Hive — {count} unresolved {unit} (connection warning)"
            : $"Cordon Hive — {count} unresolved {unit}";

        return ClampTooltip(text);
    }

    private static string ClampTooltip(string tooltip) =>
        tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..MaxTooltipLength];

    private static void EnsureWindowClassRegistered(IntPtr hInstance)
    {
        if (_classRegistered)
        {
            return;
        }

        lock (ClassRegistrationLock)
        {
            if (_classRegistered)
            {
                return;
            }

            var wndClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
                hInstance = hInstance,
                lpszClassName = WindowClassName
            };

            var atom = NativeMethods.RegisterClassExW(ref wndClass);
            if (atom != 0 || Marshal.GetLastWin32Error() == ErrorClassAlreadyExists)
            {
                _classRegistered = true;
            }
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
    }
}
