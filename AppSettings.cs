using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoClickSwitch;

public enum BarMode
{
    Standard,
    Compact,
}

public enum ThemeMode
{
    Light,
    Dark,
    System,
}

public enum ExcludeMatchKind
{
    Title,
    Process,
}

/// <summary>Where to place NCS bars across displays.</summary>
public enum MonitorBarMode
{
    /// <summary>Single bar on the primary monitor (shows all windows).</summary>
    PrimaryOnly,

    /// <summary>One bar per monitor (each lists windows on that display).</summary>
    AllMonitors,
}

/// <summary>One exclusion rule: hide windows whose title or process name matches.</summary>
public sealed class ExcludeRule
{
    public ExcludeMatchKind Kind { get; set; } = ExcludeMatchKind.Title;

    /// <summary>Case-insensitive substring match.</summary>
    public string Pattern { get; set; } = "";
}

/// <summary>User preferences persisted under %LocalAppData%\NoClickSwitch\settings.json.</summary>
public sealed class AppSettings
{
    public BarMode Mode { get; set; } = BarMode.Compact;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Accent as #RRGGBB (used for hover/active highlights).</summary>
    public string AccentColor { get; set; } = "#2F7FD1";

    /// <summary>Bar tint opacity 0.40–1.00 (wash over Mica/Acrylic or the desktop).</summary>
    public double Opacity { get; set; } = 0.85;

    /// <summary>
    /// 0 = solid; 1–49 = Mica (Win11) / acrylic blur (Win10); 50–100 = Acrylic.
    /// </summary>
    public double BlurStrength { get; set; } = 40;

    /// <summary>Milliseconds to wait on tab hover before activating (0 = instant).</summary>
    public int HoverDelayMs { get; set; } = 75;

    /// <summary>Tab width in em (1em ≈ 16 DIP). Default 5 → 80 DIP.</summary>
    public double TabWidthEm { get; set; } = 5.0;

    /// <summary>When true, the bar collapses off-screen until the pointer hits the top edge.</summary>
    public bool BarAutoHide { get; set; } = false;

    public bool ShowLoadStat { get; set; } = true;
    public bool ShowDiskStat { get; set; } = true;
    public bool ShowTempStat { get; set; } = true;
    public bool ShowClock { get; set; } = true;

    /// <summary>Order of stat blocks: Load, Disk, Temp (Clock stays rightmost).</summary>
    public List<string> StatsOrder { get; set; } = new() { "Load", "Disk", "Temp" };

    public List<ExcludeRule> ExcludeList { get; set; } = new();

    /// <summary>
    /// Pinned favorites (process names without path, e.g. "chrome", "Code").
    /// Matched case-insensitively; pinned tabs stay at the front of the strip.
    /// </summary>
    public List<string> PinnedProcesses { get; set; } = new();

    /// <summary>Primary only vs one bar per monitor.</summary>
    public MonitorBarMode MonitorMode { get; set; } = MonitorBarMode.AllMonitors;

    /// <summary>Show notify-icon in the system tray.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Register Ctrl+Alt+1..0 (and optional Win+1..0) to jump to tab N.
    /// Win+number often loses to the shell taskbar binding.
    /// </summary>
    public bool EnableTabHotkeys { get; set; } = true;

    /// <summary>Also register Win+1..0 (may conflict with Windows taskbar).</summary>
    public bool EnableWinNumberHotkeys { get; set; } = false;

    /// <summary>Show Flameshot icon on the bar when the app is installed.</summary>
    public bool AddonFlameshotShowOnBar { get; set; } = true;

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Clamp()
    {
        Opacity = Math.Clamp(Opacity, 0.40, 1.0);
        BlurStrength = Math.Clamp(BlurStrength, 0, 100);
        HoverDelayMs = Math.Clamp(HoverDelayMs, 0, 2000);
        TabWidthEm = Math.Clamp(TabWidthEm, 3.0, 12.0);

        if (string.IsNullOrWhiteSpace(AccentColor) || !AccentColor.StartsWith('#') || AccentColor.Length is not (7 or 9))
            AccentColor = "#2F7FD1";

        StatsOrder ??= new List<string> { "Load", "Disk", "Temp" };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Load", "Disk", "Temp" };
        StatsOrder = StatsOrder
            .Where(s => allowed.Contains(s))
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in new[] { "Load", "Disk", "Temp" })
        {
            if (!StatsOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                StatsOrder.Add(key);
        }

        ExcludeList ??= new List<ExcludeRule>();
        ExcludeList = ExcludeList
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new ExcludeRule
            {
                Kind = r.Kind,
                Pattern = r.Pattern.Trim(),
            })
            .ToList();

        PinnedProcesses ??= new List<string>();
        PinnedProcesses = PinnedProcesses
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsProcessPinned(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || PinnedProcesses.Count == 0)
            return false;
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return PinnedProcesses.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Loads / saves <see cref="AppSettings"/> and notifies listeners of changes.</summary>
internal sealed class AppSettingsStore
{
    private static readonly Lazy<AppSettingsStore> Lazy = new(() => new AppSettingsStore());
    public static AppSettingsStore Instance => Lazy.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private AppSettings _current;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public string FilePath { get; }

    public event EventHandler? Changed;

    private AppSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInstaller.AppName);
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "settings.json");
        _current = LoadFromDisk();
    }

    public AppSettings Snapshot()
    {
        lock (_gate)
            return _current.Clone();
    }

    /// <summary>Replace settings, persist, and notify.</summary>
    public void Replace(AppSettings settings)
    {
        settings.Clamp();
        lock (_gate)
            _current = settings.Clone();
        SaveToDisk();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mutate a clone, then replace (saves + notifies).</summary>
    public void Update(Action<AppSettings> mutate)
    {
        var next = Snapshot();
        mutate(next);
        Replace(next);
    }

    private AppSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            loaded.Clamp();
            return loaded;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveToDisk()
    {
        try
        {
            AppSettings copy;
            lock (_gate)
                copy = _current.Clone();

            copy.Clamp();
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(copy, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
