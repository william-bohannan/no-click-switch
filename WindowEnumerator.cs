using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NoClickSwitch;

/// <summary>
/// Enumerates visible top-level windows suitable for task-switcher style tabs
/// (one tab per open window).
/// </summary>
internal static class WindowEnumerator
{
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const uint GwOwner = 4;

    public static IReadOnlyList<WindowEntry> GetOpenWindows(
        IntPtr excludeHwnd,
        IReadOnlyList<ExcludeRule>? excludeRules = null,
        MonitorInfo? onMonitor = null,
        IReadOnlyCollection<IntPtr>? excludeHwnds = null)
    {
        var results = new List<WindowEntry>();
        var rules = excludeRules ?? Array.Empty<ExcludeRule>();
        HashSet<IntPtr>? extraExclude = null;
        if (excludeHwnds is { Count: > 0 })
            extraExclude = new HashSet<IntPtr>(excludeHwnds);

        EnumWindows((hWnd, _) =>
        {
            if (extraExclude is not null && extraExclude.Contains(hWnd))
                return true;

            if (!IsCandidate(hWnd, excludeHwnd))
                return true;

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            if (WindowExclude.IsExcluded(hWnd, title, rules))
                return true;

            // Per-monitor bars: only windows whose center sits on this display.
            if (onMonitor is not null && !onMonitor.ContainsWindowCenter(hWnd))
                return true;

            var processName = WindowExclude.TryGetProcessNamePublic(hWnd);

            results.Add(new WindowEntry
            {
                Handle = hWnd,
                Title = title,
                Icon = TryGetIcon(hWnd),
                ProcessName = processName,
            });

            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static bool IsCandidate(IntPtr hWnd, IntPtr excludeHwnd)
    {
        if (hWnd == excludeHwnd)
            return false;

        if (!IsWindowVisible(hWnd))
            return false;

        var exStyle = GetWindowLong(hWnd, GwlExstyle);
        var hasAppWindow = (exStyle & WsExAppwindow) != 0;
        var hasToolWindow = (exStyle & WsExToolwindow) != 0;

        // Tool windows stay off the taskbar unless they opt into APPWINDOW.
        if (hasToolWindow && !hasAppWindow)
            return false;

        // Owned windows (dialogs) are skipped unless they request APPWINDOW.
        var owner = GetWindow(hWnd, GwOwner);
        if (owner != IntPtr.Zero && !hasAppWindow)
            return false;

        // Skip cloaked UWP shells (invisible but still "visible" to Win32).
        if (IsCloaked(hWnd))
            return false;

        return true;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        const int dwmwaCloaked = 14;
        if (DwmGetWindowAttribute(hWnd, dwmwaCloaked, out var cloaked, sizeof(int)) != 0)
            return false;
        return cloaked != 0;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
            return string.Empty;

        var sb = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static BitmapSource? TryGetIcon(IntPtr hWnd)
    {
        try
        {
            var hIcon = SendMessage(hWnd, WmGeticon, IconBig, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WmGeticon, IconSmall, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WmGeticon, IconSmall2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hWnd, GclHicon);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hWnd, GclHiconsm);
            if (hIcon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private const uint WmGeticon = 0x007F;
    private static readonly IntPtr IconSmall = (IntPtr)0;
    private static readonly IntPtr IconBig = (IntPtr)1;
    private static readonly IntPtr IconSmall2 = (IntPtr)2;
    private const int GclHicon = -14;
    private const int GclHiconsm = -34;

    private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetClassLongPtr64(hWnd, nIndex)
            : new IntPtr(unchecked((int)GetClassLong32(hWnd, nIndex)));
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLong")]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
