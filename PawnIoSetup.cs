using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;

namespace NoClickSwitch;

/// <summary>
/// Detects and installs the official signed <c>PawnIO</c> driver.
/// LibreHardwareMonitor 0.9.6+ uses PawnIO instead of WinRing0, so Defender
/// does not flag it as VulnerableDriver:WinNT/Winring0.
/// </summary>
internal static class PawnIoSetup
{
    public const string DisplayName = "PawnIO";
    public const string WebsiteUrl = "https://pawnio.eu/";
    public const string SetupDownloadUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    public static bool IsInstalled => TryGetInstalledVersion() is not null;

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
