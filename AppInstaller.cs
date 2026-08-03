using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace NoClickSwitch;

/// <summary>
/// Installs No Click Switch for the current user with auto-start on login
/// (HKCU Run key + files under LocalAppData). Not a Windows Service —
/// UI apps must run in the user session.
/// </summary>
internal static class AppInstaller
{
    /// <summary>Executable / install folder / Run-key name (no spaces).</summary>
    public const string AppName = "NoClickSwitch";

    /// <summary>Short product name.</summary>
    public const string ShortName = "NCS";

    /// <summary>Full product display name.</summary>
    public const string DisplayName = "No Click Switch";

    public const string GitHubUrl = "https://github.com/william-bohannan/no-click-switch";
    public const string WebsiteUrl = "https://noclickswitch.com";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, $"{AppName}.exe");

    public static string VersionString
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip any +git suffix from informational versions.
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }

            var v = asm.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static bool IsInstalled
    {
        get
        {
            if (!File.Exists(InstalledExePath))
                return false;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(AppName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // Compare paths (quoted or unquoted).
            var normalized = value.Trim().Trim('"');
            return string.Equals(
                Path.GetFullPath(normalized),
                Path.GetFullPath(InstalledExePath),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsRunningFromInstallLocation()
    {
        try
        {
            var current = Path.GetFullPath(Environment.ProcessPath ?? AppContext.BaseDirectory);
            var installed = Path.GetFullPath(InstalledExePath);
            return string.Equals(current, installed, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       Path.GetDirectoryName(current),
                       Path.GetFullPath(InstallDirectory),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Install()
    {
        Directory.CreateDirectory(InstallDirectory);

        var sourceDir = AppContext.BaseDirectory;
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            // Skip debug symbols noise if present; copy everything else needed to run.
            var dest = Path.Combine(InstallDirectory, name);
            File.Copy(file, dest, overwrite: true);
        }

        // Multi-file self-contained layouts may include runtimes/ and other subfolders.
        // Skip Update/ (staging) and Addons/ (local helpers) if present next to a dev build.
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            if (name is "Update" or "Addons")
                continue;
            CopyDirectory(dir, Path.Combine(InstallDirectory, name));
        }

        if (!File.Exists(InstalledExePath))
            throw new InvalidOperationException($"Install failed: {InstalledExePath} not found after copy.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Could not open HKCU Run key.");
        key.SetValue(AppName, $"\"{InstalledExePath}\"");

        InstallStartMenuShortcut();
    }

    public static void Uninstall()
    {
        // Remove auto-start first.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch
        {
            // continue cleanup
        }

        RemoveStartMenuShortcut();

        if (!Directory.Exists(InstallDirectory))
            return;

        if (IsRunningFromInstallLocation())
        {
            // Schedule folder delete after this process exits.
            var dir = InstallDirectory.Replace("\"", "\\\"");
            var cmd =
                $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDirectory}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return;
        }

        try
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }
        catch
        {
            // Best-effort if files locked.
            var cmd =
                $"/c ping 127.0.0.1 -n 2 >nul & rmdir /s /q \"{InstallDirectory}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Current-user Start Menu entry so the app can be relaunched after a crash.
    /// </summary>
    public static string StartMenuShortcutPath
    {
        get
        {
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            return Path.Combine(programs, $"{DisplayName}.lnk");
        }
    }

    public static void InstallStartMenuShortcut()
    {
        try
        {
            var lnk = StartMenuShortcutPath;
            var dir = Path.GetDirectoryName(lnk);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create WScript.Shell.");
            var shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = InstalledExePath;
            shortcut.WorkingDirectory = InstallDirectory;
            shortcut.WindowStyle = 1;
            shortcut.Description = $"{DisplayName} ({ShortName})";
            shortcut.IconLocation = $"{InstalledExePath},0";
            shortcut.Save();
        }
        catch
        {
            // Best-effort — auto-start still works without a Start Menu icon.
        }
    }

    public static void RemoveStartMenuShortcut()
    {
        try
        {
            var lnk = StartMenuShortcutPath;
            if (File.Exists(lnk))
                File.Delete(lnk);

            // Older / alternate names.
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            foreach (var name in new[] { $"{AppName}.lnk", $"{ShortName}.lnk" })
            {
                var alt = Path.Combine(programs, name);
                if (File.Exists(alt))
                    File.Delete(alt);
            }

            var folder = Path.Combine(programs, DisplayName);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
