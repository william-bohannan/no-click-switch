using System.Runtime.InteropServices;

namespace NoClickSwitch;

internal static class WindowActivator
{
    private const uint SwShowMinimized = 2;
    private const uint SwRestore = 9;
    private const uint SwShowNoActivate = 4;
    private const uint SwMinimize = 6;
    private const uint WmClose = 0x0010;
    private const uint WmExitSizeMove = 0x0232;

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
    private const int WsMinimize = 0x20000000;
    private const uint LsfwUnlock = 2;

    private const int WpfRestoreToMaximized = 0x0002;
    private const int SwShowNormal = 1;

    private const byte VkMenu = 0x12; // Alt
    private const uint KeyeventfKeyup = 0x0002;

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

    /// <summary>Minimize the window (SW_MINIMIZE).</summary>
    public static void MinimizeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return;
        ShowWindow(hWnd, SwMinimize);
    }

    /// <summary>True while the HWND is still a live window.</summary>
    public static bool IsAlive(IntPtr hWnd)
        => hWnd != IntPtr.Zero && IsWindow(hWnd);

    /// <summary>
    /// Claim foreground rights for this process without WPF Activate() (which can
    /// fire MouseLeave on tabs and cancel hover-to-switch).
    /// </summary>
    public static void ForceOurWindowForeground(IntPtr ourHwnd)
    {
        if (ourHwnd == IntPtr.Zero || !IsWindow(ourHwnd))
            return;

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
            if (fgThread == 0 || fgThread == thisThread)
                _ = SetForegroundWindow(ourHwnd);
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
    /// Primary-monitor maximized apps often ignore a bare SetWindowPos — we use
    /// SetWindowPlacement + style clear + a second SetWindowPos pass.
    /// </summary>
    public static void ActivateAndFit(IntPtr hWnd, int x, int y, int width, int height)
    {
        if (hWnd == IntPtr.Zero || width <= 0 || height <= 0 || !IsWindow(hWnd))
            return;

        RestoreToNormalFrame(hWnd);
        ApplyFrameRect(hWnd, x, y, width, height);

        // Some apps (esp. maximized on the primary) re-assert maximize on activate.
        // Apply geometry again after FG, then once more deferred.
        ForceForeground(hWnd, activate: true);
        ApplyFrameRect(hWnd, x, y, width, height);

        // Deferred re-fit: Explorer / Chromium / Office often snap back after first paint.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
                if (!IsWindow(hWnd))
                    return;
                if (!IsCloseToRect(hWnd, x, y, width, height))
                {
                    RestoreToNormalFrame(hWnd);
                    ApplyFrameRect(hWnd, x, y, width, height);
                }

                await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
                if (!IsWindow(hWnd))
                    return;
                if (!IsCloseToRect(hWnd, x, y, width, height))
                {
                    RestoreToNormalFrame(hWnd);
                    ApplyFrameRect(hWnd, x, y, width, height);
                }
            }
            catch
            {
                // best-effort
            }
        });
    }

    private static void RestoreToNormalFrame(IntPtr hWnd)
    {
        // Clear placement "restore to maximized" so the next show isn't forced max.
        try
        {
            var place = new WindowPlacement();
            place.Length = Marshal.SizeOf<WindowPlacement>();
            if (GetWindowPlacement(hWnd, ref place))
            {
                place.Flags &= ~WpfRestoreToMaximized;
                if (place.ShowCmd == (int)SwShowMinimized || IsIconic(hWnd))
                    place.ShowCmd = (int)SwRestore;
                else if (IsZoomed(hWnd) || place.ShowCmd == 3 /* SW_MAXIMIZE */)
                    place.ShowCmd = (int)SwRestore;
                else
                    place.ShowCmd = SwShowNormal;
                _ = SetWindowPlacement(hWnd, ref place);
            }
        }
        catch
        {
            // fall through
        }

        if (IsIconic(hWnd) || IsZoomed(hWnd))
            ShowWindow(hWnd, SwRestore);

        // Strip maximize/minimize style bits that block SetWindowPos geometry.
        var style = GetWindowLong(hWnd, GwlStyle);
        var cleared = style & ~WsMaximize & ~WsMinimize;
        if (cleared != style)
            _ = SetWindowLong(hWnd, GwlStyle, cleared);
    }

    private static void ApplyFrameRect(IntPtr hWnd, int x, int y, int width, int height)
    {
        // Preferred: SetWindowPlacement updates the "normal" rect apps restore to.
        try
        {
            var place = new WindowPlacement();
            place.Length = Marshal.SizeOf<WindowPlacement>();
            _ = GetWindowPlacement(hWnd, ref place);
            place.Flags &= ~WpfRestoreToMaximized;
            place.ShowCmd = SwShowNormal; // SW_SHOWNORMAL
            place.NormalPosition = new Rect
            {
                Left = x,
                Top = y,
                Right = x + width,
                Bottom = y + height,
            };
            _ = SetWindowPlacement(hWnd, ref place);
        }
        catch
        {
            // continue with SetWindowPos
        }

        _ = SetWindowPos(
            hWnd,
            HwndTop,
            x,
            y,
            width,
            height,
            SwpShowWindow | SwpFrameChanged);

        // Notify the app that a user-driven size finished (helps some frameworks).
        _ = PostMessage(hWnd, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool IsCloseToRect(IntPtr hWnd, int x, int y, int width, int height)
    {
        if (!GetWindowRect(hWnd, out var rc))
            return false;
        const int tol = 8; // DWM shadows / frame differences
        return Math.Abs(rc.Left - x) <= tol
               && Math.Abs(rc.Top - y) <= tol
               && Math.Abs((rc.Right - rc.Left) - width) <= tol * 2
               && Math.Abs((rc.Bottom - rc.Top) - height) <= tol * 2;
    }

    private static void ForceForeground(IntPtr hWnd, bool activate)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return;

        // Never ShowWindow(SW_SHOW) after a fit — some shells re-maximize on primary.
        if (IsIconic(hWnd))
            ShowWindow(hWnd, activate ? SwRestore : SwShowNoActivate);

        var fg = GetForegroundWindow();
        if (activate && fg == hWnd)
        {
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

                if (GetForegroundWindow() != hWnd)
                {
                    PulseAltKey();
                    _ = SetForegroundWindow(hWnd);
                }

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

    private static void PulseAltKey()
    {
        try
        {
            keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event(VkMenu, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
        catch
        {
            // Best-effort.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
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
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
