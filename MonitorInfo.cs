using System.Runtime.InteropServices;
using System.Windows;

namespace NoClickSwitch;

/// <summary>One display: bounds in device pixels (Win32) and DIP for WPF layout.</summary>
public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public required bool IsPrimary { get; init; }

    /// <summary>Monitor rectangle in physical pixels (including taskbar area).</summary>
    public required Rect BoundsPx { get; init; }

    /// <summary>Work area in physical pixels (excludes taskbar).</summary>
    public required Rect WorkAreaPx { get; init; }

    /// <summary>Work area top edge in DIP for placing the bar.</summary>
    public required Rect WorkAreaDip { get; init; }

    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        var scale = GetPrimaryDipScale();

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var info = new MonitorInfoNative
            {
                cbSize = Marshal.SizeOf<MonitorInfoNative>(),
            };
            if (!GetMonitorInfo(hMon, ref info))
                return true;

            var boundsPx = new Rect(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);
            var workPx = new Rect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);

            // Convert with primary DPI as a reasonable default; per-monitor DPI
            // is applied when the bar's PresentationSource is available.
            var workDip = new Rect(
                workPx.X / scale,
                workPx.Y / scale,
                workPx.Width / scale,
                workPx.Height / scale);

            list.Add(new MonitorInfo
            {
                DeviceName = info.szDevice?.TrimEnd('\0') ?? "",
                IsPrimary = (info.dwFlags & 1) != 0, // MONITORINFOF_PRIMARY
                BoundsPx = boundsPx,
                WorkAreaPx = workPx,
                WorkAreaDip = workDip,
            });
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            // Fallback: primary only via SystemParameters.
            var wa = SystemParameters.WorkArea;
            list.Add(new MonitorInfo
            {
                DeviceName = "Primary",
                IsPrimary = true,
                BoundsPx = new Rect(0, 0, SystemParameters.PrimaryScreenWidth * scale, SystemParameters.PrimaryScreenHeight * scale),
                WorkAreaPx = new Rect(wa.X * scale, wa.Y * scale, wa.Width * scale, wa.Height * scale),
                WorkAreaDip = wa,
            });
        }

        // Primary first, then left-to-right.
        return list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.BoundsPx.X)
            .ThenBy(m => m.BoundsPx.Y)
            .ToList();
    }

    public static MonitorInfo GetPrimary()
        => GetAll().FirstOrDefault(m => m.IsPrimary) ?? GetAll()[0];

    /// <summary>True if the window's center lies on this monitor (device pixels).</summary>
    public bool ContainsWindowCenter(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out var rc))
            return false;

        var cx = (rc.Left + rc.Right) / 2.0;
        var cy = (rc.Top + rc.Bottom) / 2.0;
        // Inclusive on all edges so windows snapped to the right/bottom border count.
        // WPF Rect.Contains is right/bottom exclusive and can drop edge windows.
        var b = BoundsPx;
        return cx >= b.Left && cx <= b.Right && cy >= b.Top && cy <= b.Bottom;
    }

    private static double GetPrimaryDipScale()
    {
        try
        {
            using var src = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
            var m = src.CompositionTarget?.TransformToDevice;
            if (m is { } matrix && matrix.M11 > 0)
                return matrix.M11;
        }
        catch
        {
            // ignore
        }

        return 1.0;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoNative
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoNative lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative lpRect);
}
