using System.Runtime.InteropServices;

namespace SwiztchBar;

internal static class WindowActivator
{
    private const uint SwRestore = 9;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const int GwlStyle = -16;
    private const int WsMaximize = 0x01000000;

    /// <summary>
    /// Bring the window to the foreground and size it to the given screen rectangle
    /// (device pixels). Restores out of minimized/maximized first so the size sticks.
    /// </summary>
    public static void ActivateAndFit(IntPtr hWnd, int x, int y, int width, int height)
    {
        if (hWnd == IntPtr.Zero || width <= 0 || height <= 0)
            return;

        // Must leave maximized/minimized before SetWindowPos geometry will apply.
        if (IsIconic(hWnd) || IsZoomed(hWnd))
            ShowWindow(hWnd, SwRestore);

        // Clear maximize style if it lingered (some apps keep it after restore).
        var style = GetWindowLong(hWnd, GwlStyle);
        if ((style & WsMaximize) != 0)
            _ = SetWindowLong(hWnd, GwlStyle, style & ~WsMaximize);

        _ = SetWindowPos(
            hWnd,
            HwndTop,
            x,
            y,
            width,
            height,
            SwpShowWindow | SwpFrameChanged);

        _ = SetForegroundWindow(hWnd);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
