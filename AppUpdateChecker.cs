using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace NoClickSwitch;

/// <summary>
/// Checks GitHub Releases for a newer version and applies updates without
/// remote script execution (avoids Defender flagging <c>irm | iex</c>).
/// </summary>
internal static class AppUpdateChecker
{
    private const string RepoApiLatest =
        "https://api.github.com/repos/william-bohannan/no-click-switch/releases/latest";

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(15));
    private static readonly object Gate = new();
    private static DateTime _lastCheckUtc = DateTime.MinValue;
    private static bool _checking;

    /// <summary>Latest remote version (no leading v), or null if none / unknown.</summary>
    public static string? AvailableVersion { get; private set; }

    /// <summary>Release tag (e.g. v1.2.0).</summary>
    public static string? AvailableTag { get; private set; }

    /// <summary>Direct browser download URL for the win-x64 zip asset.</summary>
    public static string? AvailableDownloadUrl { get; private set; }

    public static string? AvailableAssetName { get; private set; }

    public static bool IsUpdateAvailable
    {
        get
        {
            lock (Gate)
                return !string.IsNullOrEmpty(AvailableVersion)
                       && IsNewerThanCurrent(AvailableVersion);
        }
    }

    public static event EventHandler? Changed;

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInstaller.AppName}/{AppInstaller.VersionString}");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>
    /// Fire-and-forget check (throttled). Raises <see cref="Changed"/> on the UI thread when done.
    /// </summary>
    public static void CheckInBackground(bool force = false)
    {
        lock (Gate)
        {
            if (_checking)
                return;
            if (!force && DateTime.UtcNow - _lastCheckUtc < TimeSpan.FromMinutes(30))
                return;
            _checking = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync().ConfigureAwait(false);
            }
            catch
            {
                // offline / rate limit — leave previous result
            }
            finally
            {
                lock (Gate)
                {
                    _checking = false;
                    _lastCheckUtc = DateTime.UtcNow;
                }

                try
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher is not null)
                        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () => Changed?.Invoke(null, EventArgs.Empty));
                    else
                        Changed?.Invoke(null, EventArgs.Empty);
                }
                catch
                {
                    // ignore
                }
            }
        });
    }

    public static async Task CheckAsync()
    {
        using var response = await Http.GetAsync(RepoApiLatest).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return;

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagEl))
            return;

        var tag = tagEl.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(tag))
            return;

        var version = NormalizeVersion(tag);
        if (string.IsNullOrEmpty(version))
            return;

        string? downloadUrl = null;
        string? assetName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                var preferred = name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("NoClickSwitch", StringComparison.OrdinalIgnoreCase);
                if (!preferred && downloadUrl is not null)
                    continue;

                if (asset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    downloadUrl = urlEl.GetString();
                    assetName = name;
                    if (preferred)
                        break;
                }
            }
        }

        lock (Gate)
        {
            AvailableTag = tag.StartsWith('v') || tag.StartsWith('V') ? tag : "v" + tag;
            AvailableVersion = version;
            AvailableDownloadUrl = downloadUrl;
            AvailableAssetName = assetName;
        }
    }

    public static string? NormalizeVersion(string tagOrVersion)
    {
        var s = tagOrVersion.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];
        var dash = s.IndexOf('-');
        if (dash >= 0)
            s = s[..dash];

        return Version.TryParse(PadVersion(s), out _) ? s : null;
    }

    public static bool IsNewerThanCurrent(string remoteVersion)
    {
        var local = NormalizeVersion(AppInstaller.VersionString) ?? "0.0.0";
        if (!Version.TryParse(PadVersion(local), out var a))
            return false;
        if (!Version.TryParse(PadVersion(remoteVersion), out var b))
            return false;
        return b > a;
    }

    private static string PadVersion(string v)
    {
        var parts = v.Split('.');
        if (parts.Length == 1)
            return v + ".0";
        return v;
    }

    /// <summary>
    /// Download the GitHub release zip in-process, then run a local PowerShell <c>-File</c>
    /// helper (not remote, not encoded) to copy files after this process exits.
    /// </summary>
    public static async Task StartUpgradeAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Checking latest release…");
        if (string.IsNullOrWhiteSpace(AvailableDownloadUrl))
            await CheckAsync().ConfigureAwait(false);

        string? downloadUrl;
        string? tag;
        lock (Gate)
        {
            downloadUrl = AvailableDownloadUrl;
            tag = AvailableTag;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException(
                "No download URL found for the latest release. " +
                "Open the Releases page on GitHub and install manually.");

        var tempRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInstaller.AppName,
            "Update");
        Directory.CreateDirectory(tempRoot);

        var zipPath = Path.Combine(tempRoot, "NoClickSwitch-win-x64.zip");
        var extractDir = Path.Combine(tempRoot, "extract");
        var applyPs1 = Path.Combine(tempRoot, "Apply-NoClickSwitchUpdate.ps1");

        progress?.Report($"Downloading {tag ?? "latest"} from GitHub…");
        using (var dl = CreateClient(TimeSpan.FromMinutes(15)))
        {
            using var response = await dl.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            await net.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        progress?.Report("Extracting package…");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var sourceDir = extractDir;
        var children = Directory.GetFileSystemEntries(extractDir);
        if (children.Length == 1 && Directory.Exists(children[0]))
            sourceDir = children[0];

        var stagedExe = Path.Combine(sourceDir, $"{AppInstaller.AppName}.exe");
        if (!File.Exists(stagedExe))
            throw new InvalidOperationException(
                $"The download did not contain {AppInstaller.AppName}.exe.");

        var installDir = AppInstaller.InstallDirectory;
        var installedExe = AppInstaller.InstalledExePath;
        var pid = Environment.ProcessId;

        progress?.Report("Starting local update helper…");

        // Clear, readable local script — no encoding, no network, product-branded.
        var ps = new StringBuilder();
        ps.AppendLine("#Requires -Version 5.1");
        ps.AppendLine("#");
        ps.AppendLine("# No Click Switch (NCS) — local update helper");
        ps.AppendLine("# Written by NoClickSwitch.exe during Upgrade.");
        ps.AppendLine("# This script does NOT download anything. It only copies files");
        ps.AppendLine("# already fetched from https://github.com/william-bohannan/no-click-switch");
        ps.AppendLine("#");
        ps.AppendLine($"$ErrorActionPreference = 'Stop'");
        ps.AppendLine($"$PidToWait = {pid}");
        ps.AppendLine($"$Source = '{EscapePs(sourceDir)}'");
        ps.AppendLine($"$Dest = '{EscapePs(installDir)}'");
        ps.AppendLine($"$Exe = '{EscapePs(installedExe)}'");
        ps.AppendLine("");
        ps.AppendLine("Write-Host ''");
        ps.AppendLine("Write-Host '  No Click Switch — applying update' -ForegroundColor Cyan");
        ps.AppendLine("Write-Host '  https://github.com/william-bohannan/no-click-switch' -ForegroundColor DarkGray");
        ps.AppendLine("Write-Host ''");
        ps.AppendLine("Write-Host '  Waiting for the old process to exit...' -ForegroundColor DarkGray");
        ps.AppendLine("try { Wait-Process -Id $PidToWait -Timeout 60 -ErrorAction SilentlyContinue } catch { }");
        ps.AppendLine("Start-Sleep -Seconds 1");
        ps.AppendLine("");
        ps.AppendLine("Write-Host \"  Installing to $Dest\" -ForegroundColor DarkGray");
        ps.AppendLine("New-Item -ItemType Directory -Force -Path $Dest | Out-Null");
        ps.AppendLine("Copy-Item -Path (Join-Path $Source '*') -Destination $Dest -Recurse -Force");
        ps.AppendLine("");
        ps.AppendLine("if (-not (Test-Path -LiteralPath $Exe)) {");
        ps.AppendLine("  Write-Host '  Update failed: executable missing after copy.' -ForegroundColor Red");
        ps.AppendLine("  Read-Host 'Press Enter to close'");
        ps.AppendLine("  exit 1");
        ps.AppendLine("}");
        ps.AppendLine("");
        ps.AppendLine("# Auto-start on login (current user only)");
        ps.AppendLine("$runKey = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run'");
        ps.AppendLine("New-Item -Path $runKey -Force | Out-Null");
        ps.AppendLine("Set-ItemProperty -Path $runKey -Name 'NoClickSwitch' -Value ('\"' + $Exe + '\"')");
        ps.AppendLine("");
        ps.AppendLine("Write-Host '  Starting No Click Switch...' -ForegroundColor Green");
        ps.AppendLine("Start-Process -FilePath $Exe");
        ps.AppendLine("Write-Host '  Update complete.' -ForegroundColor Green");
        ps.AppendLine("Start-Sleep -Seconds 2");

        await File.WriteAllTextAsync(applyPs1, ps.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct)
            .ConfigureAwait(false);

        // -File (not -EncodedCommand): clearer to the user and less "script malware"-like.
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{applyPs1}\"",
            WorkingDirectory = tempRoot,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        });

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                BarCoordinator.Instance.Shutdown();
            }
            catch
            {
                // best-effort
            }

            System.Windows.Application.Current.Shutdown();
        });
    }

    private static string EscapePs(string path)
        => path.Replace("'", "''");
}
