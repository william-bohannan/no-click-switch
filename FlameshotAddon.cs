using System.Diagnostics;
using System.IO;
using System.Text;

namespace NoClickSwitch;

/// <summary>
/// Optional Flameshot screenshot addon: detect install, launch GUI, install via winget.
/// </summary>
internal static class FlameshotAddon
{
    public const string DisplayName = "Flameshot";
    public const string WingetId = "Flameshot.Flameshot";
    public const string WebsiteUrl = "https://flameshot.org/";
    public const string DocsInstallUrl = "https://flameshot.org/docs/installation/installation-windows/";

    /// <summary>True if flameshot.exe can be resolved on this machine.</summary>
    public static bool IsInstalled => TryGetExePath() is not null;

    /// <summary>Bar icon should show when installed and the user has not disabled it.</summary>
    public static bool ShouldShowOnBar
    {
        get
        {
            var s = AppSettingsStore.Instance.Current;
            return s.AddonFlameshotShowOnBar && IsInstalled;
        }
    }

    private static string? _cachedExe;
    private static DateTime _cacheUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    /// <summary>Clear cached path (after install / refresh).</summary>
    public static void InvalidateCache()
    {
        _cachedExe = null;
        _cacheUtc = DateTime.MinValue;
    }

    public static string? TryGetExePath()
    {
        if (_cachedExe is not null && DateTime.UtcNow - _cacheUtc < CacheTtl)
            return File.Exists(_cachedExe) ? _cachedExe : null;

        var found = ResolveExePath();
        _cachedExe = found;
        _cacheUtc = DateTime.UtcNow;
        return found;
    }

    private static string? ResolveExePath()
    {
        // winget / MSI install layout (Windows): ...\Flameshot\bin\flameshot.exe
        var installRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Flameshot"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Flameshot"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flameshot"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Flameshot"),
        };

        foreach (var root in installRoots)
        {
            var hit = FindExeUnder(root, maxDepth: 3);
            if (hit is not null)
                return hit;
        }

        // Explicit well-known relative paths (fast path).
        foreach (var root in installRoots)
        {
            foreach (var rel in new[] { "bin\\flameshot.exe", "flameshot.exe", "bin\\flameshot-cli.exe" })
            {
                var p = Path.Combine(root, rel);
                if (File.Exists(p))
                    return PreferGuiExe(p);
            }
        }

        // PATH
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "flameshot.exe");
                    if (File.Exists(p))
                        return p;
                }
                catch
                {
                    // ignore bad PATH entries
                }
            }
        }
        catch
        {
            // ignore
        }

        // where.exe (uses PATH + App Paths)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "flameshot",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if ((t.EndsWith("flameshot.exe", StringComparison.OrdinalIgnoreCase)
                         || t.EndsWith("flameshot-cli.exe", StringComparison.OrdinalIgnoreCase))
                        && File.Exists(t))
                        return PreferGuiExe(t);
                }
            }
        }
        catch
        {
            // ignore
        }

        // Uninstall registry → InstallLocation
        var fromReg = TryFromUninstallRegistry();
        if (fromReg is not null)
            return fromReg;

        // WinGet package layout: only scan *Flameshot* package folders.
        try
        {
            var wingetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetRoot))
            {
                foreach (var pkg in Directory.EnumerateDirectories(wingetRoot, "*Flameshot*"))
                {
                    var hit = FindExeUnder(pkg, maxDepth: 4);
                    if (hit is not null)
                        return hit;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Prefer GUI exe over CLI if both exist in the same folder.</summary>
    private static string PreferGuiExe(string path)
    {
        if (path.EndsWith("flameshot-cli.exe", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                var gui = Path.Combine(dir, "flameshot.exe");
                if (File.Exists(gui))
                    return gui;
            }
        }

        return path;
    }

    private static string? FindExeUnder(string root, int maxDepth)
    {
        if (!Directory.Exists(root))
            return null;

        try
        {
            // Prefer flameshot.exe over flameshot-cli.exe.
            string? cli = null;
            foreach (var file in EnumerateFilesShallow(root, maxDepth))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("flameshot.exe", StringComparison.OrdinalIgnoreCase))
                    return file;
                if (cli is null && name.Equals("flameshot-cli.exe", StringComparison.OrdinalIgnoreCase))
                    cli = file;
            }

            return cli;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFilesShallow(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
                yield return f;

            if (depth >= maxDepth)
                continue;

            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var s in subs)
                queue.Enqueue((s, depth + 1));
        }
    }

    private static string? TryFromUninstallRegistry()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            foreach (var root in roots)
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    if (key is null)
                        continue;

                    foreach (var name in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(name);
                            if (sub is null)
                                continue;

                            var display = sub.GetValue("DisplayName") as string ?? "";
                            if (!display.Contains("Flameshot", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var loc = sub.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(loc))
                            {
                                var hit = FindExeUnder(loc.Trim().TrimEnd('\\'), maxDepth: 3);
                                if (hit is not null)
                                    return hit;
                            }

                            var icon = sub.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrWhiteSpace(icon))
                            {
                                var path = icon.Split(',')[0].Trim().Trim('"');
                                if (File.Exists(path)
                                    && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                    return PreferGuiExe(path);
                            }
                        }
                        catch
                        {
                            // next key
                        }
                    }
                }
                catch
                {
                    // next hive/root
                }
            }
        }

        return null;
    }

    /// <summary>Open Flameshot capture UI (<c>flameshot gui</c>).</summary>
    public static bool LaunchGui()
    {
        var exe = TryGetExePath();
        if (exe is null)
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "gui",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Install Flameshot via winget (or Chocolatey) using a local <c>.cmd</c> — no PowerShell.
    /// Returns false if the helper could not start.
    /// </summary>
    public static bool StartInstallWithWinget()
    {
        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("setlocal EnableExtensions");
        bat.AppendLine("title No Click Switch - install Flameshot");
        bat.AppendLine("echo.");
        bat.AppendLine("echo   Installing Flameshot (screenshot tool)");
        bat.AppendLine("echo.");
        bat.AppendLine("where winget >NUL 2>&1");
        bat.AppendLine("if %ERRORLEVEL%==0 (");
        bat.AppendLine("  echo   Using winget...");
        bat.AppendLine($"  winget install -e --id {WingetId} --accept-package-agreements --accept-source-agreements");
        bat.AppendLine("  if errorlevel 1 goto fail");
        bat.AppendLine("  goto ok");
        bat.AppendLine(")");
        bat.AppendLine("where choco >NUL 2>&1");
        bat.AppendLine("if %ERRORLEVEL%==0 (");
        bat.AppendLine("  echo   winget not found; using Chocolatey...");
        bat.AppendLine("  choco install flameshot -y");
        bat.AppendLine("  if errorlevel 1 goto fail");
        bat.AppendLine("  goto ok");
        bat.AppendLine(")");
        bat.AppendLine("echo   winget/choco not found. Opening install docs...");
        bat.AppendLine($"start \"\" \"{DocsInstallUrl}\"");
        bat.AppendLine("echo   Install winget or Flameshot manually, then close this window.");
        bat.AppendLine("pause");
        bat.AppendLine("exit /b 1");
        bat.AppendLine(":fail");
        bat.AppendLine("echo   Install failed.");
        bat.AppendLine("pause");
        bat.AppendLine("exit /b 1");
        bat.AppendLine(":ok");
        bat.AppendLine("echo.");
        bat.AppendLine("echo   Flameshot install finished. You can close this window.");
        bat.AppendLine("pause");
        bat.AppendLine("exit /b 0");
        return StartLocalCmd("Flameshot-Install.cmd", bat.ToString());
    }

    /// <summary>
    /// Uninstall Flameshot via winget (or Chocolatey) using a local <c>.cmd</c> — no PowerShell.
    /// </summary>
    public static bool StartUninstallWithWinget()
    {
        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("setlocal EnableExtensions");
        bat.AppendLine("title No Click Switch - uninstall Flameshot");
        bat.AppendLine("echo.");
        bat.AppendLine("echo   Uninstalling Flameshot");
        bat.AppendLine("echo.");
        bat.AppendLine("where winget >NUL 2>&1");
        bat.AppendLine("if %ERRORLEVEL%==0 (");
        bat.AppendLine("  echo   Using winget...");
        bat.AppendLine($"  winget uninstall -e --id {WingetId} --accept-source-agreements");
        bat.AppendLine("  if errorlevel 1 (");
        bat.AppendLine("    echo   Retrying by name...");
        bat.AppendLine("    winget uninstall --name Flameshot --accept-source-agreements");
        bat.AppendLine("    if errorlevel 1 goto fail");
        bat.AppendLine("  )");
        bat.AppendLine("  goto ok");
        bat.AppendLine(")");
        bat.AppendLine("where choco >NUL 2>&1");
        bat.AppendLine("if %ERRORLEVEL%==0 (");
        bat.AppendLine("  echo   winget not found; using Chocolatey...");
        bat.AppendLine("  choco uninstall flameshot -y");
        bat.AppendLine("  if errorlevel 1 goto fail");
        bat.AppendLine("  goto ok");
        bat.AppendLine(")");
        bat.AppendLine("echo   winget/choco not found.");
        bat.AppendLine("echo   Remove Flameshot from Settings - Apps, or run:");
        bat.AppendLine($"echo     winget uninstall {WingetId}");
        bat.AppendLine("pause");
        bat.AppendLine("exit /b 1");
        bat.AppendLine(":fail");
        bat.AppendLine("echo   Uninstall failed.");
        bat.AppendLine("pause");
        bat.AppendLine("exit /b 1");
        bat.AppendLine(":ok");
        bat.AppendLine("echo.");
        bat.AppendLine("echo   Flameshot uninstall finished. You can close this window.");
        bat.AppendLine("pause");
        bat.AppendLine("exit /b 0");
        return StartLocalCmd("Flameshot-Uninstall.cmd", bat.ToString());
    }

    private static bool StartLocalCmd(string fileName, string content)
    {
        try
        {
            // Local .cmd only — no PowerShell / EncodedCommand (common Defender signals).
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInstaller.AppName,
                "Addons");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            var header =
                "@rem No Click Switch — Flameshot addon helper\r\n" +
                "@rem Local script written by NoClickSwitch. Not downloaded from the internet.\r\n";
            File.WriteAllText(path, header + content, Encoding.ASCII);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string StatusText =>
        IsInstalled
            ? $"Installed ({TryGetExePath()})"
            : "Not installed";
}
