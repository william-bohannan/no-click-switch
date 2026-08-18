using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NoClickSwitch;

/// <summary>
/// Detects and installs the official signed <c>PawnIO</c> driver.
/// LibreHardwareMonitor 0.9.6+ uses PawnIO instead of WinRing0, so Defender
/// does not flag it as VulnerableDriver:WinNT/Winring0.
///
/// Opening <c>\\Device\PawnIO</c> requires an elevated process. Installing the
/// driver is not enough if No Click Switch is still running as a normal user.
/// </summary>
internal static class PawnIoSetup
{
    public const string DisplayName = "PawnIO";
    public const string WebsiteUrl = "https://pawnio.eu/";
    public const string SetupDownloadUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    private static DateTime _nextDeviceProbeUtc = DateTime.MinValue;
    private static bool? _canOpenDevice;

    public static bool IsInstalled => TryGetInstalledVersion() is not null;

    /// <summary>User opted in (Settings → Addons) and the signed driver is present.</summary>
    public static bool IsEnabled =>
        AppSettingsStore.Instance.Current.AddonPawnIoEnabled && IsInstalled;

    public static string? TryGetInstalledVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                var version = key?.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(version))
                    return version.Trim();
            }
            catch
            {
                // next view
            }
        }

        return null;
    }

    public static bool IsProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this process can open the PawnIO device. Cached briefly so
    /// temperature sampling does not CreateFile on every tick.
    /// </summary>
    public static bool CanOpenDevice()
    {
        if (_canOpenDevice is bool cached && DateTime.UtcNow < _nextDeviceProbeUtc)
            return cached;

        _canOpenDevice = TryOpenDevice();
        _nextDeviceProbeUtc = DateTime.UtcNow.AddSeconds(15);
        return _canOpenDevice.Value;
    }

    public static void InvalidateDeviceProbe()
    {
        _canOpenDevice = null;
        _nextDeviceProbeUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Why LHM cannot read sensors even though PawnIO is installed, or null if it can.
    /// </summary>
    public static string? DescribeBlockedReason()
    {
        if (!IsInstalled)
            return "PawnIO driver is not installed.";
        if (CanOpenDevice())
            return null;
        if (!IsProcessElevated())
            return "PawnIO is installed, but No Click Switch is not running as administrator. The driver only accepts elevated processes.";
        return "PawnIO is installed, but the driver device could not be opened. Try reboot, or reinstall PawnIO.";
    }

    /// <summary>
    /// Start a new elevated instance of this exe. Caller should shut down
    /// the current process after a successful start so two bars do not overlap.
    /// </summary>
    public static bool TryStartElevatedProcess()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                exe = AppInstaller.InstalledExePath;
            if (!File.Exists(exe))
                return false;

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            });
            return started is not null;
        }
        catch
        {
            // UAC cancelled or start failed.
            return false;
        }
    }

    private static bool TryOpenDevice()
    {
        try
        {
            using var handle = CreateFile(
                DevicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
            return handle is { IsInvalid: false };
        }
        catch
        {
            return false;
        }
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// Download the official signed installer and launch it (UAC).
    /// Returns false if the download or launch failed.
    /// </summary>
    public static async Task<bool> StartOfficialInstallerAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppInstaller.InstallDirectory, "Tools");
        Directory.CreateDirectory(dir);
        var setupPath = Path.Combine(dir, "PawnIO_setup.exe");

        progress?.Report("Downloading official PawnIO installer…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInstaller.AppName}/{AppInstaller.VersionString}");
            using var response = await http.GetAsync(SetupDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(setupPath);
            await net.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        if (!File.Exists(setupPath) || new FileInfo(setupPath).Length < 16 * 1024)
            return false;

        progress?.Report("Starting PawnIO installer (administrator approval required)…");
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            UseShellExecute = true,
            Verb = "runas",
        });
        return started is not null;
    }

    public static void OpenWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WebsiteUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }
}
