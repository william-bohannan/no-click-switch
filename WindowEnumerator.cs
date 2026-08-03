using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NoClickSwitch;

/// <summary>
/// Enumerates visible top-level windows suitable for task-switcher style tabs
/// (one tab per open window). Icons and process names are cached — re-fetching
/// icons every tick was a major source of UI jank on multi-monitor setups.
/// </summary>
internal static class WindowEnumerator
{
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const uint GwOwner = 4;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<IntPtr, BitmapSource?> IconCache = new();
    private static readonly Dictionary<IntPtr, string> ProcessNameCache = new();
    private static List<WindowEntry>? _allCache;
    private static DateTime _allCacheUtc = DateTime.MinValue;
    private static int _allCacheExcludeHash;
    private const int AllCacheTtlMs = 400;

    public static IReadOnlyList<WindowEntry> GetOpenWindows(
        IntPtr excludeHwnd,
        IReadOnlyList<ExcludeRule>? excludeRules = null,
        MonitorInfo? onMonitor = null,
        IReadOnlyCollection<IntPtr>? excludeHwnds = null)
    {
        return Perf.Time("WindowEnum.GetOpenWindows", () =>
        {
            var rules = excludeRules ?? Array.Empty<ExcludeRule>();
            var excludeHash = HashExcludes(excludeHwnd, excludeHwnds, rules);

            // Dual bars refresh nearly together — share one full enum for a few hundred ms.
            List<WindowEntry> all;
            lock (CacheGate)
            {
                if (_allCache is not null
                    && _allCacheExcludeHash == excludeHash
                    && (DateTime.UtcNow - _allCacheUtc).TotalMilliseconds < AllCacheTtlMs)
                {
                    all = _allCache;
                }
                else
                {
                    all = EnumerateAll(excludeHwnd, rules, excludeHwnds);
                    _allCache = all;
                    _allCacheUtc = DateTime.UtcNow;
                    _allCacheExcludeHash = excludeHash;
                    PruneCaches(all);
                }
            }

            if (onMonitor is null)
                return all;

            // Per-monitor filter (cheap — no re-enum, no icon fetch).
            var filtered = new List<WindowEntry>(all.Count);
            foreach (var w in all)
            {
                if (onMonitor.ContainsWindowCenter(w.Handle))
                    filtered.Add(w);
            }

            return filtered;
        }, warnMs: 12);
    }

    private static int HashExcludes(
        IntPtr excludeHwnd,
        IReadOnlyCollection<IntPtr>? excludeHwnds,
        IReadOnlyList<ExcludeRule> rules)
    {
        unchecked
        {
            var h = excludeHwnd.GetHashCode() * 397;
            if (excludeHwnds is not null)
            {
                foreach (var e in excludeHwnds)
                    h = (h * 31) + e.GetHashCode();
            }

            h = (h * 31) + rules.Count;
            foreach (var r in rules)
                h = (h * 31) + (r.Pattern?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
            return h;
        }
    }

    private static List<WindowEntry> EnumerateAll(
        IntPtr excludeHwnd,
        IReadOnlyList<ExcludeRule> rules,
        IReadOnlyCollection<IntPtr>? excludeHwnds)
    {
        var results = new List<WindowEntry>(32);
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

            var processName = GetCachedProcessName(hWnd);

            results.Add(new WindowEntry
            {
                Handle = hWnd,
                Title = title,
                Icon = GetCachedIcon(hWnd),
                ProcessName = processName,
            });

            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static void PruneCaches(List<WindowEntry> live)
    {
        var liveSet = new HashSet<IntPtr>(live.Count);
        foreach (var w in live)
            liveSet.Add(w.Handle);

        // Drop icons/names for windows that are gone (avoid unbounded growth).
        if (IconCache.Count > liveSet.Count + 16)
        {
            var dead = IconCache.Keys.Where(k => !liveSet.Contains(k)).ToList();
            foreach (var k in dead)
                IconCache.Remove(k);
        }

        if (ProcessNameCache.Count > liveSet.Count + 16)
        {
            var dead = ProcessNameCache.Keys.Where(k => !liveSet.Contains(k)).ToList();
            foreach (var k in dead)
                ProcessNameCache.Remove(k);
        }
    }

    private static string GetCachedProcessName(IntPtr hWnd)
    {
        lock (CacheGate)
        {
            if (ProcessNameCache.TryGetValue(hWnd, out var name))
                return name;
        }

        var resolved = WindowExclude.TryGetProcessNamePublic(hWnd) ?? "";
        lock (CacheGate)
            ProcessNameCache[hWnd] = resolved;
        return resolved;
    }

    private static BitmapSource? GetCachedIcon(IntPtr hWnd)
    {
        lock (CacheGate)
        {
            if (IconCache.TryGetValue(hWnd, out var cached))
                return cached;
        }

        var icon = FetchIcon(hWnd);
        lock (CacheGate)
            IconCache[hWnd] = icon;
        return icon;
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

        if (hasToolWindow && !hasAppWindow)
            return false;

        var owner = GetWindow(hWnd, GwOwner);
        if (owner != IntPtr.Zero && !hasAppWindow)
            return false;

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

    private static BitmapSource? FetchIcon(IntPtr hWnd)
    {
        try
        {
            // Prefer class small icon / small icon — fewer round-trips than big→small→small2.
            var hIcon = GetClassLongPtr(hWnd, GclHiconsm);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WmGeticon, IconSmall, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WmGeticon, IconSmall2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hWnd, GclHicon);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WmGeticon, IconBig, IntPtr.Zero);
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
