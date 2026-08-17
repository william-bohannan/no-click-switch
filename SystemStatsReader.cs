using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace NoClickSwitch;

/// <summary>
/// Samples CPU/MEM load, up to two fixed disks, and CPU/GPU temperatures.
/// Use <see cref="Shared"/> so only one sampler runs for every bar.
///
/// Temperature sources, in order:
/// 1. LibreHardwareMonitor 0.9.6+ via the signed <c>PawnIO</c> driver (no WinRing0).
/// 2. Windows WMI / ACPI thermal zones (works on many laptops, not this desktop).
/// 3. nvidia-smi for NVIDIA GPUs.
///
/// We never extract WinRing0 / NoClickSwitch.sys — Defender quarantines that as
/// VulnerableDriver:WinNT/Winring0.
/// </summary>
internal sealed class SystemStatsReader : IDisposable
{
    /// <summary>Process-wide reader (multiple bars must not each open WMI / LHM).</summary>
    public static SystemStatsReader Shared { get; } = new();

    private readonly object _gate = new();
    /// <summary>Separate from load/disk so UI sampling never waits on WMI or LHM.</summary>
    private readonly object _tempGate = new();

    private Computer? _computer;
    private bool _lhmOpenAttempted;
    private DateTime _nextLhmRetryUtc = DateTime.MinValue;

    private long _idlePrev;
    private long _kernelPrev;
    private long _userPrev;
    private bool _cpuPrimed;

    private DateTime _lastSampleUtc = DateTime.MinValue;
    private DateTime _lastTempSampleUtc = DateTime.MinValue;
    private DateTime _lastGoodTempUtc = DateTime.MinValue;
    private int? _lastGoodCpuTempC;
    private int? _lastGoodGpuTempC;
    private string _tempStatus = "CPU temperature: starting...";
    private string _debugPath = "";
    private DateTime _lastDebugLogUtc = DateTime.MinValue;
    private string _lastDebugLine = "";
    private int _tempSampleBusy; // 0 = idle, 1 = background sample running

    private string? _nvidiaSmiPath;
    private bool _nvidiaSmiProbed;
    private DateTime _nextNvidiaProbeUtc = DateTime.MinValue;
    private DateTime _nextNvidiaReadUtc = DateTime.MinValue;
    private float? _cachedNvidiaTempC;

    /// <summary>How often to poll temperature sources (WMI can hitch). Load/disk stay at Sample rate.</summary>
    private static readonly TimeSpan TempSampleInterval = TimeSpan.FromSeconds(2.5);

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

            Perf.Time("Stats.CpuMemDisk", () =>
            {
                SampleCpu();
                SampleMemory();
                SampleDisks();
            }, warnMs: 5);
        }

        // WMI / nvidia-smi can block — never run them on the UI thread.
        RequestTemperatureSampleAsync();
    }

    private void RequestTemperatureSampleAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTempSampleUtc) < TempSampleInterval)
            return;
        if (Interlocked.CompareExchange(ref _tempSampleBusy, 1, 0) != 0)
            return;

        _lastTempSampleUtc = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (_tempGate)
                    Perf.Time("Stats.Temperatures", SampleTemperatures, warnMs: 20);
            }
            catch
            {
                // best-effort
            }
            finally
            {
                Interlocked.Exchange(ref _tempSampleBusy, 0);
            }
        });
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

    /// <summary>Re-open LHM after the user installs PawnIO without restarting NCS.</summary>
    public void RetryHardwareMonitor()
    {
        lock (_tempGate)
        {
            _lhmOpenAttempted = false;
            _nextLhmRetryUtc = DateTime.MinValue;
            try { _computer?.Close(); } catch { /* ignore */ }
            _computer = null;
        }
    }

    private void SampleTemperatures()
    {
        TryDeleteLegacyWinRing0();

        float? cpu = null;
        float? gpu = null;
        string? cpuSource = null;
        string? gpuSource = null;

        EnsureComputer();
        var lhm = TryReadLhmTemps();
        if (lhm.Cpu is not null)
        {
            cpu = lhm.Cpu;
            cpuSource = lhm.CpuSource;
        }

        if (lhm.Gpu is not null)
        {
            gpu = lhm.Gpu;
            gpuSource = lhm.GpuSource;
        }

        if (cpu is null || gpu is null)
        {
            var wmi = TryReadWmiTemps();
            if (cpu is null && wmi.Cpu is not null)
            {
                cpu = wmi.Cpu;
                cpuSource = wmi.CpuSource;
            }

            if (gpu is null && wmi.Gpu is not null)
            {
                gpu = wmi.Gpu;
                gpuSource = wmi.GpuSource;
            }
        }

        if (gpu is null)
        {
            var nv = TryReadNvidiaSmiTemp(out var nvSource);
            if (nv is not null)
            {
                gpu = nv;
                gpuSource = nvSource;
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
            // Hold last good reading briefly through transient WMI misses.
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
            if (_lastGoodTempUtc == DateTime.MinValue)
                _lastGoodTempUtc = DateTime.UtcNow;
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
                (PawnIoSetup.IsInstalled
                    ? "PawnIO is installed but no CPU sensor was found.\n"
                    : "This PC does not expose a Windows thermal zone.\n" +
                      "Install PawnIO (signed driver — Settings → Stats)\n" +
                      "to read the CPU package temperature safely.\n" +
                      "We do not use WinRing0 (Defender blocks it).\n") +
                _tempStatus;
        }

        GpuTempToolTip = GpuTempC is int gt
            ? (string.IsNullOrEmpty(gpuSource)
                ? $"GPU temperature: {gt}°C"
                : $"GPU temperature: {gt}°C\nSource: {gpuSource}")
            : "GPU temperature: unavailable\n" +
              "No thermal zone or nvidia-smi reading.";

        WriteDebugLog(
            $"cpu={CpuTempC?.ToString() ?? "null"} gpu={GpuTempC?.ToString() ?? "null"} " +
            $"status={_tempStatus}");
    }

    private readonly record struct WmiTemps(float? Cpu, string? CpuSource, float? Gpu, string? GpuSource);

    private void EnsureComputer()
    {
        if (_computer is not null)
            return;
        if (_lhmOpenAttempted && DateTime.UtcNow < _nextLhmRetryUtc)
            return;

        _lhmOpenAttempted = true;
        if (!PawnIoSetup.IsInstalled)
        {
            _tempStatus = "PawnIO not installed";
            _nextLhmRetryUtc = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = false,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
                IsBatteryEnabled = false,
                IsPsuEnabled = false,
            };
            _computer.Open();

            foreach (var h in _computer.Hardware)
            {
                if (IsTempRelevant(h.HardwareType))
                    UpdateHardwareTree(h);
            }

            _tempStatus = "LHM/PawnIO open; hardware=" + _computer.Hardware.Count;
            WriteDebugLog(_tempStatus);
        }
        catch (Exception ex)
        {
            try { _computer?.Close(); } catch { /* ignore */ }
            _computer = null;
            _tempStatus = "LHM/PawnIO open failed: " + ex.GetType().Name + ": " + ex.Message;
            _nextLhmRetryUtc = DateTime.UtcNow.AddSeconds(30);
            WriteDebugLog(_tempStatus);
        }
    }

    private WmiTemps TryReadLhmTemps()
    {
        if (_computer is null)
            return default;

        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (!IsTempRelevant(hardware.HardwareType))
                    continue;
                UpdateHardwareTree(hardware);
            }

            var bag = new TempBag();
            var sensorCount = 0;
            foreach (var hardware in _computer.Hardware)
            {
                if (!IsTempRelevant(hardware.HardwareType))
                    continue;
                sensorCount += CollectTemps(hardware, bag);
            }

            var cpu = bag.PickCpu(out var cpuSource);
            var gpu = bag.PickGpu(out var gpuSource);
            if (cpu is null && gpu is null)
                _tempStatus = $"LHM/PawnIO open; sensors={sensorCount}; no CPU/GPU match";
            return new WmiTemps(cpu, cpuSource is null ? null : "LHM " + cpuSource,
                gpu, gpuSource is null ? null : "LHM " + gpuSource);
        }
        catch (Exception ex)
        {
            _tempStatus = "LHM update error: " + ex.GetType().Name;
            WriteDebugLog(_tempStatus + " " + ex.Message);
            return default;
        }
    }

    private static bool IsTempRelevant(HardwareType type)
        => type is HardwareType.Cpu
            or HardwareType.GpuNvidia
            or HardwareType.GpuAmd
            or HardwareType.GpuIntel
            or HardwareType.Motherboard
            or HardwareType.SuperIO;

    private static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            UpdateHardwareTree(sub);
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
                else if (bag.CpuAny is null || value > bag.CpuAny)
                {
                    bag.CpuAny = value;
                    bag.CpuAnyName = $"{hardware.Name} / {name}";
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

    private WmiTemps TryReadWmiTemps()
    {
        float? cpu = null;
        string? cpuSource = null;
        float? gpu = null;
        string? gpuSource = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (obj["Temperature"] is not { } raw)
                        continue;
                    if (!TryNormalizeTempC(Convert.ToDouble(raw), out var c))
                        continue;

                    var looksGpu = LooksLikeGpuZone(name);
                    var looksCpu = LooksLikeCpuZone(name);
                    if (looksGpu)
                    {
                        if (gpu is null || c > gpu)
                        {
                            gpu = c;
                            gpuSource = "WMI " + name;
                        }
                    }
                    else if (looksCpu || cpu is null)
                    {
                        // Prefer named CPU zones; otherwise keep the hottest remaining zone as CPU.
                        if (cpu is null || looksCpu || c > cpu)
                        {
                            cpu = c;
                            cpuSource = "WMI " + (string.IsNullOrWhiteSpace(name) ? "ThermalZone" : name);
                            if (looksCpu)
                            {
                                // keep scanning for GPU zones
                            }
                        }
                    }
                }
                catch
                {
                    // next zone
                }
            }
        }
        catch
        {
            // class missing
        }

        if (cpu is null)
        {
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
                        if (!TryNormalizeTempC(tenthsK, out var c, tenthsKelvinHint: true))
                            continue;
                        var name = obj["InstanceName"]?.ToString() ?? "ThermalZone";
                        if (LooksLikeGpuZone(name))
                        {
                            gpu ??= c;
                            gpuSource ??= "ACPI " + name;
                        }
                        else
                        {
                            cpu = c;
                            cpuSource = "ACPI " + name;
                            break;
                        }
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
        }

        if (cpu is null && gpu is null)
            _tempStatus = "No Windows thermal-zone reading";
        else if (cpu is null)
            _tempStatus = "WMI GPU only; no CPU thermal zone";

        return new WmiTemps(cpu, cpuSource, gpu, gpuSource);
    }

    private static bool LooksLikeCpuZone(string name)
        => name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("TZ00", StringComparison.OrdinalIgnoreCase)
           || name.Contains("TZ0", StringComparison.OrdinalIgnoreCase)
           || name.Contains("ACPI\\ThermalZone\\TZ", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Processor", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeGpuZone(string name)
        => name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
           || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
           || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows reports thermal-zone values as °C, Kelvin, or tenths of a kelvin.
    /// </summary>
    private static bool TryNormalizeTempC(double raw, out float celsius, bool tenthsKelvinHint = false)
    {
        celsius = 0;
        double c;
        if (tenthsKelvinHint || raw > 400)
            c = raw / 10.0 - 273.15;
        else if (raw >= 200)
            c = raw - 273.15;
        else
            c = raw;

        if (c < 1 || c > 125)
            return false;
        celsius = (float)c;
        return true;
    }

    private float? TryReadNvidiaSmiTemp(out string source)
    {
        source = "";
        var exe = ResolveNvidiaSmiPath();
        if (exe is null)
            return null;

        if (DateTime.UtcNow < _nextNvidiaReadUtc && _cachedNvidiaTempC is not null)
        {
            source = "nvidia-smi";
            return _cachedNvidiaTempC;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null)
                return null;

            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            var text = proc.StandardOutput.ReadToEnd();
            if (proc.ExitCode != 0)
                return null;

            float? best = null;
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!float.TryParse(line.Trim(), out var t))
                    continue;
                if (t < 1 || t > 125)
                    continue;
                if (best is null || t > best)
                    best = t;
            }

            if (best is null)
                return null;

            _cachedNvidiaTempC = best;
            _nextNvidiaReadUtc = DateTime.UtcNow.AddSeconds(10);
            source = "nvidia-smi";
            return best;
        }
        catch
        {
            _nvidiaSmiPath = null;
            _cachedNvidiaTempC = null;
            _nextNvidiaProbeUtc = DateTime.UtcNow.AddMinutes(10);
            return null;
        }
    }

    private string? ResolveNvidiaSmiPath()
    {
        if (_nvidiaSmiPath is not null && File.Exists(_nvidiaSmiPath))
            return _nvidiaSmiPath;

        if (_nvidiaSmiProbed && DateTime.UtcNow < _nextNvidiaProbeUtc)
            return _nvidiaSmiPath;

        _nvidiaSmiProbed = true;
        _nextNvidiaProbeUtc = DateTime.UtcNow.AddMinutes(5);

        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                _nvidiaSmiPath = path;
                return path;
            }
        }

        _nvidiaSmiPath = null;
        return null;
    }

    /// <summary>
    /// Older builds extracted WinRing0 next to the exe as NoClickSwitch.sys.
    /// Delete leftovers so Defender does not keep rediscovering them.
    /// Never recreate the file.
    /// </summary>
    private static void TryDeleteLegacyWinRing0()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, AppInstaller.InstallDirectory })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                var sys = Path.Combine(dir, "NoClickSwitch.sys");
                if (File.Exists(sys))
                    File.Delete(sys);
            }
            catch
            {
                // Defender may already have it locked or quarantined.
            }
        }
    }

    private void WriteDebugLog(string line)
    {
        try
        {
            // Log on change, errors, or at most once every 30s (keep disk quiet).
            var now = DateTime.UtcNow;
            var isError = line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("null", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
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

    public void Dispose()
    {
        // Shared lives for the process; nothing to close (no kernel driver).
    }

    /// <summary>Called once when the app exits.</summary>
    public static void ShutdownShared()
    {
        lock (Shared._tempGate)
        {
            try { Shared._computer?.Close(); } catch { /* ignore */ }
            Shared._computer = null;
        }
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
