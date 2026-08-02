using System.IO;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace NoClickSwitch;

/// <summary>
/// Samples CPU/MEM load, up to two fixed disks, and CPU/GPU temperatures.
/// </summary>
internal sealed class SystemStatsReader : IDisposable
{
    private long _idlePrev;
    private long _kernelPrev;
    private long _userPrev;
    private bool _cpuPrimed;

    private Computer? _computer;
    private bool _tempsTried;

    public int CpuPercent { get; private set; }
    public int MemPercent { get; private set; }

    public DiskSample? Disk0 { get; private set; }
    public DiskSample? Disk1 { get; private set; }

    public int? CpuTempC { get; private set; }
    public int? GpuTempC { get; private set; }

    public string CpuToolTip { get; private set; } = "CPU";
    public string MemToolTip { get; private set; } = "Memory";
    public string CpuTempToolTip { get; private set; } = "CPU temperature";
    public string GpuTempToolTip { get; private set; } = "GPU temperature";

    public readonly record struct DiskSample(string Letter, int UsedPercent, string ToolTip);

    public void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleDisks();
        SampleTemperatures();
    }

    private void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return;

        var idleNow = idle.ToInt64();
        var kernelNow = kernel.ToInt64();
        var userNow = user.ToInt64();

        if (!_cpuPrimed)
        {
            _idlePrev = idleNow;
            _kernelPrev = kernelNow;
            _userPrev = userNow;
            _cpuPrimed = true;
            CpuPercent = 0;
            CpuToolTip = "CPU: measuring…";
            return;
        }

        var idleDelta = idleNow - _idlePrev;
        var kernelDelta = kernelNow - _kernelPrev;
        var userDelta = userNow - _userPrev;
        _idlePrev = idleNow;
        _kernelPrev = kernelNow;
        _userPrev = userNow;

        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            CpuPercent = 0;
            return;
        }

        var busy = Math.Max(0, total - idleDelta);
        CpuPercent = Math.Clamp((int)Math.Round(100.0 * busy / total), 0, 100);
        CpuToolTip = $"CPU: {CpuPercent}%";
    }

    private void SampleMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return;

        MemPercent = (int)Math.Clamp(status.dwMemoryLoad, 0, 100);
        var used = status.ullTotalPhys - status.ullAvailPhys;
        MemToolTip =
            $"Memory: {MemPercent}%\n" +
            $"{FormatBytes(used)} used of {FormatBytes(status.ullTotalPhys)}";
    }

    private void SampleDisks()
    {
        Disk0 = null;
        Disk1 = null;

        try
        {
            // Prefer system drive first, then other fixed drives by letter.
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            var fixedDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderBy(d => !string.Equals(
                    d.Name.TrimEnd('\\'),
                    systemRoot,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(d => d.Name)
                .Take(2)
                .ToList();

            if (fixedDrives.Count > 0)
                Disk0 = BuildDiskSample(fixedDrives[0]);
            if (fixedDrives.Count > 1)
                Disk1 = BuildDiskSample(fixedDrives[1]);
        }
        catch
        {
            // keep previous
        }
    }

    private static DiskSample BuildDiskSample(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\');
        var total = (ulong)Math.Max(0, drive.TotalSize);
        var free = (ulong)Math.Max(0, drive.TotalFreeSpace);
        if (total == 0)
            return new DiskSample(letter, 0, $"Disk {letter}");

        var used = total - free;
        var pct = Math.Clamp((int)Math.Round(100.0 * used / total), 0, 100);
        var tip =
            $"Disk {letter}: {pct}% used\n" +
            $"{FormatBytes(used)} used of {FormatBytes(total)}\n" +
            $"{FormatBytes(free)} free";
        return new DiskSample(letter, pct, tip);
    }

    private void SampleTemperatures()
    {
        EnsureComputer();
        if (_computer is null)
        {
            CpuTempC = null;
            GpuTempC = null;
            return;
        }

        try
        {
            float? cpu = null;
            float? gpu = null;

            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                VisitHardware(hardware, ref cpu, ref gpu);
            }

            CpuTempC = cpu.HasValue ? (int)Math.Round(cpu.Value) : null;
            GpuTempC = gpu.HasValue ? (int)Math.Round(gpu.Value) : null;

            CpuTempToolTip = CpuTempC is int ct
                ? $"CPU temperature: {ct}°C"
                : "CPU temperature: unavailable";
            GpuTempToolTip = GpuTempC is int gt
                ? $"GPU temperature: {gt}°C"
                : "GPU temperature: unavailable";
        }
        catch
        {
            CpuTempC = null;
            GpuTempC = null;
        }
    }

    private void VisitHardware(IHardware hardware, ref float? cpu, ref float? gpu)
    {
        foreach (var sub in hardware.SubHardware)
        {
            sub.Update();
            VisitHardware(sub, ref cpu, ref gpu);
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value)
                continue;
            if (float.IsNaN(value) || value <= 0 || value > 125)
                continue;

            var name = sensor.Name ?? "";

            if (hardware.HardwareType == HardwareType.Cpu)
            {
                // Prefer package / Tctl over single cores.
                if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Average", StringComparison.OrdinalIgnoreCase)
                    || cpu is null)
                {
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Average", StringComparison.OrdinalIgnoreCase)
                        || cpu is null
                        || value > cpu.Value)
                    {
                        // Prefer named package sensors; otherwise take max core-ish reading carefully.
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                            cpu = value;
                        else if (cpu is null)
                            cpu = value;
                    }
                }
            }
            else if (hardware.HardwareType is HardwareType.GpuNvidia
                     or HardwareType.GpuAmd
                     or HardwareType.GpuIntel)
            {
                if (name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)
                    || gpu is null)
                {
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                        || gpu is null)
                        gpu = value;
                    else if (name.Contains("Hot", StringComparison.OrdinalIgnoreCase) && gpu is null)
                        gpu = value;
                }
            }
        }
    }

    private void EnsureComputer()
    {
        if (_tempsTried)
            return;
        _tempsTried = true;

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
            };
            _computer.Open();
        }
        catch
        {
            _computer = null;
        }
    }

    public void Dispose()
    {
        try
        {
            _computer?.Close();
        }
        catch
        {
            // ignore
        }

        _computer = null;
    }

    private static string FormatBytes(ulong bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        const double tb = gb * 1024;
        var b = (double)bytes;
        if (b >= tb)
            return $"{b / tb:0.0} TB";
        if (b >= gb)
            return $"{b / gb:0.0} GB";
        if (b >= mb)
            return $"{b / mb:0.0} MB";
        if (b >= kb)
            return $"{b / kb:0.0} KB";
        return $"{bytes} B";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public long ToInt64() => ((long)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FileTime lpIdleTime,
        out FileTime lpKernelTime,
        out FileTime lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
