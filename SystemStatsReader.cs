using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace NoClickSwitch;

/// <summary>
/// Samples CPU/MEM load, up to two fixed disks, and CPU/GPU temperatures.
/// Use <see cref="Shared"/> so only one LibreHardwareMonitor <see cref="Computer"/> is opened.
/// Safe for multi-monitor (multiple bars): sampling is locked and coalesced.
/// </summary>
internal sealed class SystemStatsReader : IDisposable
{
    /// <summary>Process-wide reader (multiple bars must not each Open() LHM).</summary>
    public static SystemStatsReader Shared { get; } = new();

    private readonly object _gate = new();
    private readonly UpdateVisitor _visitor = new();

    private long _idlePrev;
    private long _kernelPrev;
    private long _userPrev;
    private bool _cpuPrimed;

    private Computer? _computer;
    private bool _tempsOpenAttempted;
    private int _tempsFailStreak;
    private DateTime _nextTempRetryUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private string _tempStatus = "CPU temperature: starting…";

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
        lock (_gate)
        {
            // Multiple bars tick on the same second — sample once, all read the same values.
            var now = DateTime.UtcNow;
            if ((now - _lastSampleUtc).TotalMilliseconds < 400)
                return;
            _lastSampleUtc = now;

            SampleCpu();
            SampleMemory();
            SampleDisks();
            SampleTemperatures();
        }
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

        float? cpu = null;
        float? gpu = null;
        string? cpuSource = null;
        string? gpuSource = null;

        if (_computer is not null)
        {
            try
            {
                // Official LHM update path (hardware + sub-hardware).
                _computer.Accept(_visitor);

                var bag = new TempBag();
                foreach (var hardware in _computer.Hardware)
                    CollectTemps(hardware, bag);

                cpu = bag.PickCpu(out cpuSource);
                gpu = bag.PickGpu(out gpuSource);

                if (cpu is not null || gpu is not null)
                    _tempsFailStreak = 0;
            }
            catch (Exception ex)
            {
                _tempStatus = "LibreHardwareMonitor error: " + ex.Message;
                // Do not tear down on every glitch — dual-bar races used to thrash Open/Close.
                _tempsFailStreak++;
                if (_tempsFailStreak >= 8)
                {
                    _tempsFailStreak = 0;
                    ResetComputer();
                }
            }
        }

        // WMI thermal-zone fallback when LHM has no CPU reading (some locked-down PCs).
        if (cpu is null)
        {
            var wmi = TryReadWmiCpuTemp(out var wmiName);
            if (wmi is not null)
            {
                cpu = wmi;
                cpuSource = wmiName;
            }
        }

        CpuTempC = cpu.HasValue ? (int)Math.Round(cpu.Value) : null;
        GpuTempC = gpu.HasValue ? (int)Math.Round(gpu.Value) : null;

        if (CpuTempC is int ct)
        {
            CpuTempToolTip = string.IsNullOrEmpty(cpuSource)
                ? $"CPU temperature: {ct}°C"
                : $"CPU temperature: {ct}°C\nSource: {cpuSource}";
            _tempStatus = CpuTempToolTip;
        }
        else
        {
            CpuTempToolTip =
                "CPU temperature: unavailable\n" +
                (_computer is null
                    ? "LibreHardwareMonitor could not open (driver/admin).\n"
                    : "No CPU package/core sensor found.\n") +
                "Tip: some laptops need a one-time admin launch so the sensor driver can install.";
            if (!string.IsNullOrEmpty(_tempStatus) && _tempStatus.Contains("error", StringComparison.OrdinalIgnoreCase))
                CpuTempToolTip += "\n" + _tempStatus;
        }

        GpuTempToolTip = GpuTempC is int gt
            ? (string.IsNullOrEmpty(gpuSource)
                ? $"GPU temperature: {gt}°C"
                : $"GPU temperature: {gt}°C\nSource: {gpuSource}")
            : "GPU temperature: unavailable (no discrete/iGPU sensor)";

        if (CpuTempC is null && GpuTempC is null)
        {
            _tempsFailStreak++;
            if (_tempsFailStreak >= 10)
            {
                _tempsFailStreak = 0;
                ResetComputer();
            }
        }
    }

    private sealed class TempBag
    {
        public float? CpuPackage;
        public string? CpuPackageName;
        public float? CpuCoreMax;
        public string? CpuCoreMaxName;
        public float? CpuCoreAverage;
        public string? CpuCoreAverageName;
        public float CpuCoreSum;
        public int CpuCoreCount;
        public float? CpuAny;
        public string? CpuAnyName;
        public float? Gpu;
        public string? GpuName;

        public float? PickCpu(out string? source)
        {
            if (CpuPackage is float p)
            {
                source = CpuPackageName;
                return p;
            }

            if (CpuCoreMax is float m)
            {
                source = CpuCoreMaxName;
                return m;
            }

            if (CpuCoreAverage is float a)
            {
                source = CpuCoreAverageName;
                return a;
            }

            if (CpuCoreCount > 0)
            {
                source = $"average of {CpuCoreCount} cores";
                return CpuCoreSum / CpuCoreCount;
            }

            if (CpuAny is float any)
            {
                source = CpuAnyName;
                return any;
            }

            source = null;
            return null;
        }

        public float? PickGpu(out string? source)
        {
            source = GpuName;
            return Gpu;
        }
    }

    private static void CollectTemps(IHardware hardware, TempBag bag)
    {
        foreach (var sub in hardware.SubHardware)
            CollectTemps(sub, bag);

        var type = hardware.HardwareType;
        var isCpuHw = type == HardwareType.Cpu;
        var isGpuHw = type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        var isBoardHw = type is HardwareType.Motherboard or HardwareType.SuperIO;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature)
                continue;
            if (sensor.Value is not float value)
                continue;
            if (float.IsNaN(value) || float.IsInfinity(value))
                continue;
            // Plausible silicon range (°C).
            if (value < 1 || value > 125)
                continue;

            var name = sensor.Name ?? "";
            // "Distance to TjMax" is remaining headroom, not a die temperature.
            if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
                   && name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                continue;

            if (isCpuHw || (isBoardHw && LooksLikeCpuSensor(name)))
            {
                if (IsPackageName(name))
                {
                    if (bag.CpuPackage is null || IsBetterPackageName(name, bag.CpuPackageName))
                    {
                        bag.CpuPackage = value;
                        bag.CpuPackageName = $"{hardware.Name} / {name}";
                    }
                }
                else if (name.Equals("Core Max", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("CPU Core Max", StringComparison.OrdinalIgnoreCase))
                {
                    bag.CpuCoreMax = value;
                    bag.CpuCoreMaxName = $"{hardware.Name} / {name}";
                }
                else if (name.Contains("Core Average", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("Average", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("CPU Core Average", StringComparison.OrdinalIgnoreCase))
                {
                    bag.CpuCoreAverage = value;
                    bag.CpuCoreAverageName = $"{hardware.Name} / {name}";
                }
                else if (IsCpuCoreSensor(name))
                {
                    bag.CpuCoreSum += value;
                    bag.CpuCoreCount++;
                    if (bag.CpuAny is null || value > bag.CpuAny)
                    {
                        bag.CpuAny = value;
                        bag.CpuAnyName = $"{hardware.Name} / {name}";
                    }
                }
                else
                {
                    // Generic die / "Temperature" on CPU hardware.
                    if (bag.CpuAny is null || value > bag.CpuAny)
                    {
                        bag.CpuAny = value;
                        bag.CpuAnyName = $"{hardware.Name} / {name}";
                    }

                    if (IsPackageName(name) || name.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        bag.CpuPackage ??= value;
                        bag.CpuPackageName ??= $"{hardware.Name} / {name}";
                    }
                }
            }
            else if (isGpuHw)
            {
                // Prefer "Core" / "GPU" named sensors; otherwise first reading wins.
                var prefer = name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("Temperature", StringComparison.OrdinalIgnoreCase);
                if (bag.Gpu is null || prefer)
                {
                    if (bag.Gpu is null
                        || name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        bag.Gpu = value;
                        bag.GpuName = $"{hardware.Name} / {name}";
                    }
                }
            }
            else if (isBoardHw)
            {
                // Last-resort board CPU probe (already handled if LooksLikeCpuSensor).
            }
        }
    }

    private static bool LooksLikeCpuSensor(string name)
        => name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CCD", StringComparison.OrdinalIgnoreCase);

    private static bool IsPackageName(string name)
        => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CCD", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CPU", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CPU Temperature", StringComparison.OrdinalIgnoreCase)
           || (name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("Core", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("Distance", StringComparison.OrdinalIgnoreCase));

    private static bool IsBetterPackageName(string candidate, string? current)
    {
        if (string.IsNullOrEmpty(current))
            return true;
        // Prefer explicit Package / Tctl over generic "CPU".
        int Rank(string n) =>
            n.Contains("Package", StringComparison.OrdinalIgnoreCase) ? 3
            : n.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || n.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ? 2
            : 1;
        return Rank(candidate) >= Rank(current);
    }

    private static bool IsCpuCoreSensor(string name)
        => name.Contains("Core", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("Average", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("Max", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("Distance", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback via Windows thermal performance counters (often a package zone).
    /// Values are already °C on modern Windows.
    /// </summary>
    private static float? TryReadWmiCpuTemp(out string source)
    {
        source = "";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            float? best = null;
            string? bestName = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (obj["Temperature"] is not { } raw)
                        continue;
                    var t = Convert.ToSingle(raw);
                    // Some systems report Kelvin-like or zero when unsupported.
                    if (t < 1 || t > 125)
                        continue;
                    // Prefer zones that mention CPU / package.
                    var prefer = name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("TZ00", StringComparison.OrdinalIgnoreCase);
                    if (best is null || prefer)
                    {
                        best = t;
                        bestName = name;
                        if (prefer)
                            break;
                    }
                }
                catch
                {
                    // next zone
                }
            }

            if (best is not null)
            {
                source = "WMI " + (bestName ?? "ThermalZone");
                return best;
            }
        }
        catch
        {
            // class missing on this SKU
        }

        // Older ACPI thermal zones: tenths of Kelvin.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    if (obj["CurrentTemperature"] is not { } raw)
                        continue;
                    var tenthsK = Convert.ToDouble(raw);
                    var c = (float)(tenthsK / 10.0 - 273.15);
                    if (c < 1 || c > 125)
                        continue;
                    source = "ACPI " + (obj["InstanceName"]?.ToString() ?? "ThermalZone");
                    return c;
                }
                catch
                {
                    // next
                }
            }
        }
        catch
        {
            // not supported
        }

        return null;
    }

    private void EnsureComputer()
    {
        if (_computer is not null)
            return;

        if (_tempsOpenAttempted && DateTime.UtcNow < _nextTempRetryUtc)
            return;

        _tempsOpenAttempted = true;
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
                IsBatteryEnabled = false,
                IsPsuEnabled = false,
            };
            _computer.Open();
            // First Accept so sensors populate before the next UI tick.
            _computer.Accept(_visitor);
            _tempsFailStreak = 0;
            _tempStatus = "LibreHardwareMonitor open";
        }
        catch (Exception ex)
        {
            _computer = null;
            _tempStatus = "LibreHardwareMonitor open failed: " + ex.Message;
            _nextTempRetryUtc = DateTime.UtcNow.AddSeconds(15);
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
        _nextTempRetryUtc = DateTime.UtcNow.AddSeconds(3);
    }

    public void Dispose()
    {
        if (!ReferenceEquals(this, Shared))
            ResetComputer();
    }

    /// <summary>Called once when the app exits.</summary>
    public static void ShutdownShared()
    {
        lock (Shared._gate)
            Shared.ResetComputer();
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
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
