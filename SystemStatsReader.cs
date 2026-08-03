using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
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

    private long _idlePrev;
    private long _kernelPrev;
    private long _userPrev;
    private bool _cpuPrimed;

    private Computer? _computer;
    private bool _tempsOpenAttempted;
    private int _tempsFailStreak;
    private DateTime _nextTempRetryUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private DateTime _lastGoodTempUtc = DateTime.MinValue;
    private int? _lastGoodCpuTempC;
    private int? _lastGoodGpuTempC;
    private string _tempStatus = "CPU temperature: starting...";
    private string _debugPath = "";
    private DateTime _lastDebugLogUtc = DateTime.MinValue;
    private string _lastDebugLine = "";

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
            CpuToolTip = "CPU: measuring...";
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
        var hardwareCount = 0;
        var sensorCount = 0;

        if (_computer is not null)
        {
            try
            {
                // Prefer a simple Update walk — more reliable than Accept/IVisitor on some builds.
                foreach (var hardware in _computer.Hardware)
                {
                    hardwareCount++;
                    UpdateHardwareTree(hardware);
                }

                var bag = new TempBag();
                foreach (var hardware in _computer.Hardware)
                    sensorCount += CollectTemps(hardware, bag);

                cpu = bag.PickCpu(out cpuSource);
                gpu = bag.PickGpu(out gpuSource);

                if (cpu is not null || gpu is not null)
                    _tempsFailStreak = 0;
                else
                    _tempStatus = $"LHM open; hardware={hardwareCount} tempSensors={sensorCount}; no CPU match";
            }
            catch (Exception ex)
            {
                _tempStatus = "LHM update error: " + ex.GetType().Name + ": " + ex.Message;
                _tempsFailStreak++;
                // Only reset after sustained failure — thrashing Open/Close breaks the kernel driver.
                if (_tempsFailStreak >= 30)
                {
                    _tempsFailStreak = 0;
                    ResetComputer();
                }
            }
        }
        else
        {
            _tempStatus = "LHM not open (" + _tempStatus + ")";
        }

        // WMI thermal-zone fallback when LHM has no CPU reading.
        if (cpu is null)
        {
            var wmi = TryReadWmiCpuTemp(out var wmiName);
            if (wmi is not null)
            {
                cpu = wmi;
                cpuSource = wmiName;
            }
        }

        if (cpu.HasValue)
        {
            CpuTempC = (int)Math.Round(cpu.Value);
            _lastGoodCpuTempC = CpuTempC;
            _lastGoodTempUtc = DateTime.UtcNow;
        }
        else if (_lastGoodCpuTempC is int held
                 && (DateTime.UtcNow - _lastGoodTempUtc).TotalSeconds < 45)
        {
            // Hold last good reading briefly through driver glitches.
            CpuTempC = held;
            cpuSource ??= "last good reading";
        }
        else
        {
            CpuTempC = null;
        }

        if (gpu.HasValue)
        {
            GpuTempC = (int)Math.Round(gpu.Value);
            _lastGoodGpuTempC = GpuTempC;
        }
        else if (_lastGoodGpuTempC is int heldGpu
                 && (DateTime.UtcNow - _lastGoodTempUtc).TotalSeconds < 45)
        {
            GpuTempC = heldGpu;
        }
        else
        {
            GpuTempC = null;
        }

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
                    ? "LibreHardwareMonitor could not open (sensor driver).\n"
                    : "No CPU package/core sensor found.\n") +
                _tempStatus + "\n" +
                "If this persists, restart No Click Switch from the Start menu.";
        }

        GpuTempToolTip = GpuTempC is int gt
            ? (string.IsNullOrEmpty(gpuSource)
                ? $"GPU temperature: {gt}°C"
                : $"GPU temperature: {gt}°C\nSource: {gpuSource}")
            : "GPU temperature: unavailable (no discrete/iGPU sensor)";

        if (CpuTempC is null && GpuTempC is null)
        {
            _tempsFailStreak++;
            if (_tempsFailStreak >= 30)
            {
                _tempsFailStreak = 0;
                ResetComputer();
            }
        }

        WriteDebugLog(
            $"cpu={CpuTempC?.ToString() ?? "null"} gpu={GpuTempC?.ToString() ?? "null"} " +
            $"hw={hardwareCount} sensors={sensorCount} computer={_computer is not null} " +
            $"status={_tempStatus}");
    }

    private static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            UpdateHardwareTree(sub);
    }

    private void WriteDebugLog(string line)
    {
        try
        {
            // Log on change, errors, or at most once every 30s (keep disk quiet).
            var now = DateTime.UtcNow;
            var isError = line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("null", StringComparison.OrdinalIgnoreCase);
            if (!isError
                && line == _lastDebugLine
                && (now - _lastDebugLogUtc).TotalSeconds < 30)
                return;
            _lastDebugLine = line;
            _lastDebugLogUtc = now;

            if (string.IsNullOrEmpty(_debugPath))
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppInstaller.AppName);
                Directory.CreateDirectory(dir);
                _debugPath = Path.Combine(dir, "stats-debug.log");
            }

            if (File.Exists(_debugPath) && new FileInfo(_debugPath).Length > 64 * 1024)
                File.WriteAllText(_debugPath, "");

            File.AppendAllText(
                _debugPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // never break sampling for logging
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

    /// <returns>Number of temperature sensors seen under this hardware tree.</returns>
    private static int CollectTemps(IHardware hardware, TempBag bag)
    {
        var count = 0;
        foreach (var sub in hardware.SubHardware)
            count += CollectTemps(sub, bag);

        var type = hardware.HardwareType;
        var isCpuHw = type == HardwareType.Cpu;
        var isGpuHw = type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        var isBoardHw = type is HardwareType.Motherboard or HardwareType.SuperIO;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature)
                continue;

            // Value is float?; read carefully (avoid pattern quirks).
            var raw = sensor.Value;
            if (raw is null)
                continue;
            var value = raw.Value;
            if (float.IsNaN(value) || float.IsInfinity(value))
                continue;
            if (value < 1 || value > 125)
                continue;

            count++;
            var name = sensor.Name ?? "";

            // "Distance to TjMax" is remaining headroom, not a die temperature.
            if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
                || (name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("Distance", StringComparison.OrdinalIgnoreCase)))
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
                    // Any other temp on the CPU device (die, Tctl, unnamed, ...).
                    if (bag.CpuAny is null || value > bag.CpuAny)
                    {
                        bag.CpuAny = value;
                        bag.CpuAnyName = $"{hardware.Name} / {name}";
                    }
                }
            }
            else if (isGpuHw)
            {
                var prefer = name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("Temperature", StringComparison.OrdinalIgnoreCase);
                if (bag.Gpu is null || prefer)
                {
                    bag.Gpu = value;
                    bag.GpuName = $"{hardware.Name} / {name}";
                }
            }
        }

        return count;
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
                    if (t < 1 || t > 125)
                        continue;
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
            // class missing
        }

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
            // Ensure driver sidecar is next to the executable (LHM loads process-name.sys).
            try
            {
                var baseDir = AppContext.BaseDirectory;
                WriteDebugLog($"EnsureComputer baseDir={baseDir} sys={File.Exists(Path.Combine(baseDir, "NoClickSwitch.sys"))}");
            }
            catch
            {
                // ignore
            }

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

            // Warm up sensors (first Update can be empty on some systems).
            foreach (var h in _computer.Hardware)
                UpdateHardwareTree(h);
            foreach (var h in _computer.Hardware)
                UpdateHardwareTree(h);

            _tempsFailStreak = 0;
            _tempStatus = "LibreHardwareMonitor open OK; hardware=" + _computer.Hardware.Count;
            WriteDebugLog(_tempStatus);
        }
        catch (Exception ex)
        {
            _computer = null;
            _tempStatus = "LibreHardwareMonitor open failed: " + ex.GetType().Name + ": " + ex.Message;
            _nextTempRetryUtc = DateTime.UtcNow.AddSeconds(10);
            WriteDebugLog(_tempStatus);
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
        _nextTempRetryUtc = DateTime.UtcNow.AddSeconds(2);
        WriteDebugLog("ResetComputer");
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
