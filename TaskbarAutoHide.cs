using System.Runtime.InteropServices;

namespace SwitchedBar;

/// <summary>
/// Reads / writes Windows taskbar auto-hide via the shell app-bar API.
/// </summary>
internal static class TaskbarAutoHide
{
    private const uint AbmGetState = 0x00000004;
    private const uint AbmSetState = 0x0000000A;
    private const int AbsAutohide = 0x00000001;
    private const int AbsAlwaysOnTop = 0x00000002;

    public static bool IsEnabled
    {
        get
        {
            var data = CreateAppBarData();
            var state = (int)SHAppBarMessage(AbmGetState, ref data);
            return (state & AbsAutohide) != 0;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        var data = CreateAppBarData();
        // Preserve always-on-top bit from current state when toggling auto-hide.
        var current = (int)SHAppBarMessage(AbmGetState, ref data);
        var alwaysOnTop = (current & AbsAlwaysOnTop) != 0 ? AbsAlwaysOnTop : 0;
        var next = alwaysOnTop | (enabled ? AbsAutohide : 0);
        data.lParam = new IntPtr(next);
        _ = SHAppBarMessage(AbmSetState, ref data);
    }

    public static bool Toggle()
    {
        var next = !IsEnabled;
        SetEnabled(next);
        return IsEnabled;
    }

    private static AppBarData CreateAppBarData()
    {
        return new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
        };
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
}
