using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NoClickSwitch;

/// <summary>Filters windows against the user's exclude list (title / process name).</summary>
internal static class WindowExclude
{
    public static bool IsExcluded(IntPtr hWnd, string title, IReadOnlyList<ExcludeRule> rules)
    {
        if (rules.Count == 0 || hWnd == IntPtr.Zero)
            return false;

        string? processName = null;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                continue;

            if (rule.Kind == ExcludeMatchKind.Title)
            {
                if (title.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            // Process match — resolve once.
            processName ??= TryGetProcessName(hWnd);
            if (processName is not null
                && processName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Public process-name lookup for pinning and tab metadata.</summary>
    public static string? TryGetProcessNamePublic(IntPtr hWnd) => TryGetProcessName(hWnd);

    private static string? TryGetProcessName(IntPtr hWnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0)
                return null;

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (string.IsNullOrEmpty(name))
                return null;

            // Prefer executable file name when available.
            try
            {
                var file = process.MainModule?.ModuleName;
                if (!string.IsNullOrEmpty(file))
                    return file;
            }
            catch
            {
                // Access denied on some system processes — ProcessName is enough.
            }

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + ".exe";
        }
        catch
        {
            return TryGetProcessNameViaQuery(hWnd);
        }
    }

    private static string? TryGetProcessNameViaQuery(IntPtr hWnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0)
                return null;

            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                var sb = new StringBuilder(1024);
                var size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(handle, 0, sb, ref size))
                    return null;

                var path = sb.ToString();
                return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        StringBuilder lpExeName,
        ref uint lpdwSize);
}
