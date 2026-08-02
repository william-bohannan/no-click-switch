using System.Runtime.InteropServices;

namespace SwitchedBar;

internal static class WindowActivator
{
    private const uint SwRestore = 9;
    private const uint SwShow = 5;
    private const uint SwShowNoActivate = 4;
    private const uint WmClose = 0x0010;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int GwlStyle = -16;
    private const int WsMaximize = 0x01000000;
    private const uint LsfwUnlock = 2;

    /// <summary>
    /// Raise the window in z-order and make it the foreground window.
    /// Works even when our top bar was not clicked first (AttachThreadInput).
    /// </summary>
    public static void BringToFront(IntPtr hWnd)
    {
        ForceForeground(hWnd, activate: true);
    }

    /// <summary>
    /// Ask the window to close (WM_CLOSE). Apps can still prompt to save, etc.
    /// </summary>
    public static void CloseWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return;

        _ = PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>True while the HWND is still a live window.</summary>
    public static bool IsAlive(IntPtr hWnd)
        => hWnd != IntPtr.Zero && IsWindow(hWnd);

    /// <summary>
    /// Force this process's window into the foreground so later activations succeed.
    /// </summary>
    public static void ForceOurWindowForeground(IntPtr ourHwnd)
    {
        if (ourHwnd == IntPtr.Zero || !IsWindow(ourHwnd))
            return;

        // Our bar is topmost; just take foreground rights without z-order fights.
        var fg = GetForegroundWindow();
        if (fg == ourHwnd)
            return;

        var thisThread = GetCurrentThreadId();
        var fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0u;
        var attached = false;
        try
        {
            if (fgThread != 0 && fgThread != thisThread)
                attached = AttachThreadInput(thisThread, fgThread, true);

            _ = LockSetForegroundWindow(LsfwUnlock);
            _ = AllowSetForegroundWindow(-1); // ASFW_ANY
            _ = SetForegroundWindow(ourHwnd);
            _ = SetActiveWindow(ourHwnd);
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(thisThread, fgThread, false);
        }
    }

    /// <summary>
    /// Bring the window to the foreground and size it to the given screen rectangle
    /// (device pixels). Restores out of minimized/maximized first so the size sticks.
    /// </summary>
    public static void ActivateAndFit(IntPtr hWnd, int x, int y, int width, int height)
    {
        if (hWnd == IntPtr.Zero || width <= 0 || height <= 0 || !IsWindow(hWnd))
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

        ForceForeground(hWnd, activate: true);
    }

    private static void ForceForeground(IntPtr hWnd, bool activate)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return;

        if (IsIconic(hWnd))
            ShowWindow(hWnd, activate ? SwRestore : SwShowNoActivate);
        else if (activate)
            ShowWindow(hWnd, SwShow);

        var fg = GetForegroundWindow();
        if (activate && fg == hWnd)
        {
            // Still raise in case it is covered by non-foreground peers.
            _ = SetWindowPos(hWnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            return;
        }

        var thisThread = GetCurrentThreadId();
        var fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0u;
        var targetThread = GetWindowThreadProcessId(hWnd, out _);

        var attachedFg = false;
        var attachedTarget = false;
        try
        {
            // Join input queues so SetForegroundWindow is allowed.
            if (fgThread != 0 && fgThread != thisThread)
                attachedFg = AttachThreadInput(thisThread, fgThread, true);
            if (targetThread != 0 && targetThread != thisThread && targetThread != fgThread)
                attachedTarget = AttachThreadInput(thisThread, targetThread, true);

            _ = LockSetForegroundWindow(LsfwUnlock);
            _ = AllowSetForegroundWindow(-1);

            _ = BringWindowToTop(hWnd);
            _ = SetWindowPos(
                hWnd,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpShowWindow | (activate ? 0u : SwpNoActivate));

            if (activate)
            {
                _ = SetForegroundWindow(hWnd);
                _ = SetActiveWindow(hWnd);

                // Last-resort topmost flicker if Windows still blocked us.
                if (GetForegroundWindow() != hWnd)
                {
                    _ = SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                    _ = SetWindowPos(hWnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                    _ = SetForegroundWindow(hWnd);
                }
            }
        }
        finally
        {
            if (attachedTarget)
                _ = AttachThreadInput(thisThread, targetThread, false);
            if (attachedFg)
                _ = AttachThreadInput(thisThread, fgThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool LockSetForegroundWindow(uint uLockCode);

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
