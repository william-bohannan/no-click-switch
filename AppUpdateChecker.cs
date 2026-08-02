using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Threading;

namespace NoClickSwitch;

/// <summary>
/// Checks GitHub Releases for a newer version than the running app.
/// </summary>
internal static class AppUpdateChecker
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();
    private static DateTime _lastCheckUtc = DateTime.MinValue;
    private static bool _checking;

    /// <summary>Latest remote version (no leading v), or null if none / unknown.</summary>
    public static string? AvailableVersion { get; private set; }

    /// <summary>Release tag (e.g. v1.2.0).</summary>
    public static string? AvailableTag { get; private set; }

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

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
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
        // https://api.github.com/repos/william-bohannan/no-click-switch/releases/latest
        var url = "https://api.github.com/repos/william-bohannan/no-click-switch/releases/latest";
        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return;

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl))
            return;

        var tag = tagEl.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(tag))
            return;

        var version = NormalizeVersion(tag);
        if (string.IsNullOrEmpty(version))
            return;

        lock (Gate)
        {
            AvailableTag = tag.StartsWith('v') || tag.StartsWith('V') ? tag : "v" + tag;
            AvailableVersion = version;
        }
    }

    public static string? NormalizeVersion(string tagOrVersion)
    {
        var s = tagOrVersion.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        // strip pre-release / build metadata for comparison base
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
        // System.Version wants at least Major.Minor
        var parts = v.Split('.');
        if (parts.Length == 1)
            return v + ".0";
        return v;
    }

    /// <summary>
    /// Runs the official install.ps1 (downloads latest release, installs to LocalAppData, restarts).
    /// Current process is stopped by the installer.
    /// </summary>
    public static bool StartUpgrade()
    {
        try
        {
            // Same one-liner as README install — always pulls latest main install.ps1 + latest release zip.
            const string cmd =
                "irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.ps1 | iex";

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
