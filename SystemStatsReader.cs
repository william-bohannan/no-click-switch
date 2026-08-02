using System.IO;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace NoClickSwitch;

/// <summary>
/// Samples CPU/MEM load, up to two fixed disks, and CPU/GPU temperatures.
/// Use <see cref="Shared"/> so only one LibreHardwareMonitor <see cref="Computer"/> is opened.
/// </summary>
internal sealed class SystemStatsReader : IDisposable
{
    /// <summary>Process-wide reader (multiple bars must not each Open() LHM).</summary>
    public static SystemStatsReader Shared { get; } = new();

    private long _idlePrev;
    private long _kernelPrev;
    private long _userPrev;
    private bool _cpuPrimed;

    private Computer? _computer;
    private bool _tempsOpenAttempted;
    private int _tempsFailStreak;
    private DateTime _nextTempRetryUtc = DateTime.MinValue;

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
            CpuTempToolTip = "CPU temperature: sensor unavailable";
            GpuTempToolTip = "GPU temperature: sensor unavailable";
            return;
        }

        try
        {
            float? cpuPackage = null;
            float? cpuAny = null;
            var cpuCoreSum = 0f;
            var cpuCoreCount = 0;
            float? gpu = null;

            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                CollectTemps(hardware, ref cpuPackage, ref cpuAny, ref cpuCoreSum, ref cpuCoreCount, ref gpu);
            }

            float? cpu = cpuPackage;
            if (cpu is null && cpuCoreCount > 0)
                cpu = cpuCoreSum / cpuCoreCount;
            if (cpu is null)
                cpu = cpuAny;

            CpuTempC = cpu.HasValue ? (int)Math.Round(cpu.Value) : null;
            GpuTempC = gpu.HasValue ? (int)Math.Round(gpu.Value) : null;

            CpuTempToolTip = CpuTempC is int ct
                ? $"CPU temperature: {ct}°C"
                : "CPU temperature: unavailable (no sensor found)";
            GpuTempToolTip = GpuTempC is int gt
                ? $"GPU temperature: {gt}°C"
                : "GPU temperature: unavailable";

            if (CpuTempC is null && GpuTempC is null)
            {
                _tempsFailStreak++;
                // After repeated empty reads, reopen LHM (driver init can lag at startup).
                if (_tempsFailStreak >= 5)
                {
                    _tempsFailStreak = 0;
                    ResetComputer();
                }
            }
            else
            {
                _tempsFailStreak = 0;
            }
        }
        catch
        {
            CpuTempC = null;
            GpuTempC = null;
            ResetComputer();
        }
    }

    private static void CollectTemps(
        IHardware hardware,
        ref float? cpuPackage,
        ref float? cpuAny,
        ref float cpuCoreSum,
        ref int cpuCoreCount,
        ref float? gpu)
    {
        foreach (var sub in hardware.SubHardware)
        {
            sub.Update();
            CollectTemps(sub, ref cpuPackage, ref cpuAny, ref cpuCoreSum, ref cpuCoreCount, ref gpu);
        }

        var type = hardware.HardwareType;
        var isCpuHw = type == HardwareType.Cpu;
        var isGpuHw = type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        var isBoardHw = type is HardwareType.Motherboard or HardwareType.SuperIO;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value)
                continue;
            if (float.IsNaN(value) || float.IsInfinity(value))
                continue;
            // Plausible silicon range (°C). Allow slightly below ambient for weird chips.
            if (value < 1 || value > 125)
                continue;

            var name = sensor.Name ?? "";

            if (isCpuHw || (isBoardHw && LooksLikeCpuSensor(name)))
            {
                if (IsCpuPackageSensor(name))
                {
                    // Prefer package / Tctl over whatever we had.
                    if (cpuPackage is null || IsPreferredPackageName(name, cpuPackageName: true))
                        cpuPackage = value;
                    else if (value > cpuPackage)
                        cpuPackage = value;
                }
                else if (IsCpuCoreSensor(name))
                {
                    cpuCoreSum += value;
                    cpuCoreCount++;
                    cpuAny ??= value;
                    if (value > (cpuAny ?? 0))
                        cpuAny = value;
                }
                else
                {
                    // Generic CPU die / temperature sensor.
                    cpuAny ??= value;
                    if (IsGenericCpuTempName(name))
                        cpuPackage ??= value;
                }
            }
            else if (isGpuHw)
            {
                if (name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                    || gpu is null)
                {
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) || gpu is null)
                        gpu = value;
                }
            }
        }
    }

    private static bool LooksLikeCpuSensor(string name)
        => name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CCD", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuPackageSensor(string name)
        => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CCD", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Average", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CPU", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CPU Temperature", StringComparison.OrdinalIgnoreCase)
           || (name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("Core", StringComparison.OrdinalIgnoreCase));

    private static bool IsPreferredPackageName(string name, bool cpuPackageName)
        => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuCoreSensor(string name)
        => name.Contains("Core", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("Package", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericCpuTempName(string name)
        => name.Contains("Temperature", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Temp", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Die", StringComparison.OrdinalIgnoreCase);

    private void EnsureComputer()
    {
        if (_computer is not null)
            return;

        if (_tempsOpenAttempted && DateTime.UtcNow < _nextTempRetryUtc)
            return;

        _tempsOpenAttempted = true;
        try
        {
            // Motherboard/SuperIO often exposes CPU temp when CPU package is missing.
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
            };
            _computer.Open();
            _tempsFailStreak = 0;
        }
        catch
        {
            _computer = null;
            _nextTempRetryUtc = DateTime.UtcNow.AddSeconds(30);
        }
    }

    private void ResetComputer()
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
        _tempsOpenAttempted = false;
        _nextTempRetryUtc = DateTime.UtcNow.AddSeconds(5);
    }

    public void Dispose()
    {
        // Shared instance lives for the process; only close on explicit app shutdown.
        if (!ReferenceEquals(this, Shared))
            ResetComputer();
    }

    /// <summary>Called once when the app exits.</summary>
    public static void ShutdownShared()
    {
        Shared.ResetComputer();
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
