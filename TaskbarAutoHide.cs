using System.Runtime.InteropServices;

namespace NoClickSwitch;

/// <summary>
/// Reads / writes Windows taskbar auto-hide via the shell app-bar API.
/// Works with dual-monitor setups (primary + secondary taskbars on Win10/11).
/// </summary>
internal static class TaskbarAutoHide
{
    private const uint AbmGetState = 0x00000004;
    private const uint AbmSetState = 0x0000000A;
    private const int AbsAutohide = 0x00000001;
    private const int AbsAlwaysOnTop = 0x00000002;

    /// <summary>
    /// Last value this process intentionally set. Used so bar rebuilds do not
    /// flip the shell off while the app is still running.
    /// </summary>
    public static bool? SessionDesired { get; private set; }

    public static bool IsEnabled
    {
        get
        {
            var data = CreateAppBarData(FindPrimaryTaskbar());
            var state = (int)SHAppBarMessage(AbmGetState, ref data);
            return (state & AbsAutohide) != 0;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        SessionDesired = enabled;
        ApplyState(enabled, retries: 3);
    }

    /// <summary>
    /// Re-apply <see cref="SessionDesired"/> if the shell dropped auto-hide
    /// (common after monitor changes or explorer glitches).
    /// </summary>
    public static void ReassertDesired()
    {
        if (SessionDesired is not bool want)
            return;
        if (IsEnabled == want)
            return;
        ApplyState(want, retries: 2);
    }

    public static bool Toggle()
    {
        var next = !IsEnabled;
        SetEnabled(next);
        return IsEnabled;
    }

    private static void ApplyState(bool enabled, int retries)
    {
        for (var attempt = 0; attempt < retries; attempt++)
        {
            // Primary taskbar first (this is what ABM_SETSTATE actually controls).
            var tray = FindPrimaryTaskbar();
            WriteState(tray, enabled);

            // Nudge secondary taskbars so Win11 multi-monitor shells stay in sync.
            foreach (var secondary in FindSecondaryTaskbars())
                WriteState(secondary, enabled);

            // Also try with null hwnd — some builds only honor one of the two forms.
            if (tray != IntPtr.Zero)
                WriteState(IntPtr.Zero, enabled);

            if (IsEnabled == enabled)
                return;

            Thread.Sleep(40);
        }
    }

    private static void WriteState(IntPtr hWnd, bool enabled)
    {
        var data = CreateAppBarData(hWnd);
        var current = (int)SHAppBarMessage(AbmGetState, ref data);
        // Preserve always-on-top if the shell still reports it (pre-Win7 leftover).
        var alwaysOnTop = (current & AbsAlwaysOnTop) != 0 ? AbsAlwaysOnTop : 0;
        var next = alwaysOnTop | (enabled ? AbsAutohide : 0);
        data.lParam = new IntPtr(next);
        _ = SHAppBarMessage(AbmSetState, ref data);
    }

    private static AppBarData CreateAppBarData(IntPtr hWnd)
    {
        return new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = hWnd,
        };
    }

    private static IntPtr FindPrimaryTaskbar()
        => FindWindow("Shell_TrayWnd", null);

    private static IEnumerable<IntPtr> FindSecondaryTaskbars()
    {
        // Win10/11 secondary taskbars share class Shell_SecondaryTrayWnd.
        var found = new List<IntPtr>();
        var first = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
        var walk = first;
        while (walk != IntPtr.Zero)
        {
            found.Add(walk);
            walk = FindWindowEx(IntPtr.Zero, walk, "Shell_SecondaryTrayWnd", null);
            if (walk == first)
                break;
        }

        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);
}
