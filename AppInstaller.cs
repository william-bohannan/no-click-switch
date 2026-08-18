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
            // Never copy kernel drivers. Older builds extracted WinRing0 as *.sys
            // and Defender quarantines it as VulnerableDriver:WinNT/Winring0.
            if (name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                continue;
            var dest = Path.Combine(InstallDirectory, name);
            File.Copy(file, dest, overwrite: true);
        }

        // Multi-file self-contained layouts may include runtimes/ and other subfolders.
        // Skip Update/ (staging) and Addons/ (local helpers) if present next to a dev build.
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            if (name is "Update" or "Addons" or "Tools")
                continue;
            CopyDirectory(dir, Path.Combine(InstallDirectory, name));
        }

        if (!File.Exists(InstalledExePath))
            throw new InvalidOperationException($"Install failed: {InstalledExePath} not found after copy.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Could not open HKCU Run key.");
        key.SetValue(AppName, $"\"{InstalledExePath}\"");

        InstallStartMenuShortcut();

        // PawnIO needs an elevated process; swap Run-key start for a highest-privilege
        // logon task when the addon is already on and this install is elevated.
        if (AppSettingsStore.Instance.Current.AddonPawnIoEnabled && PawnIoSetup.IsProcessElevated())
            SyncLogonStart(elevate: true);
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

        TryRemoveElevatedLogonTask();

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

    /// <summary>
    /// PawnIO only answers elevated processes. When the addon is on, replace the
    /// HKCU Run key with a logon scheduled task that runs with highest privileges
    /// so CPU temps survive reboot without a UAC prompt every time.
    /// </summary>
    public static void SyncLogonStart(bool elevate)
    {
        if (elevate)
        {
            if (!PawnIoSetup.IsProcessElevated())
                return;
            if (TryCreateElevatedLogonTask())
                TryRemoveRunKey();
            return;
        }

        TryRemoveElevatedLogonTask();
        TrySetRunKey();
    }

    private const string LogonTaskName = AppName;

    private static bool TryCreateElevatedLogonTask()
    {
        var exe = File.Exists(InstalledExePath)
            ? InstalledExePath
            : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return false;

        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Schedule.Service unavailable.");
            dynamic service = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not create Schedule.Service.");
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic def = service.NewTask(0);
            def.RegistrationInfo.Description =
                $"{DisplayName} elevated logon start (PawnIO CPU temperatures)";
            def.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
            def.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.AllowHardTerminate = false;
            def.Settings.ExecutionTimeLimit = "PT0S";
            def.Settings.MultipleInstances = 2; // IgnoreNew
            def.Settings.StartWhenAvailable = true;
            def.Settings.StopIfGoingOnBatteries = false;
            dynamic trigger = def.Triggers.Create(9); // TASK_TRIGGER_LOGON
            trigger.Enabled = true;
            dynamic action = def.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = exe;
            action.WorkingDirectory = Path.GetDirectoryName(exe) ?? InstallDirectory;
            folder.RegisterTaskDefinition(
                LogonTaskName,
                def,
                6, // TASK_CREATE_OR_UPDATE
                null,
                null,
                3); // TASK_LOGON_INTERACTIVE_TOKEN
            return ElevatedLogonTaskExists();
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryRemoveElevatedLogonTask()
    {
        if (!ElevatedLogonTaskExists())
            return true;

        if (RunSchtasks($"/Delete /TN \"{LogonTaskName}\" /F", waitMs: 10000) == 0)
            return true;

        if (PawnIoSetup.IsProcessElevated())
            return !ElevatedLogonTaskExists();

        // Unelevated process cannot delete a highest-privilege task.
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{LogonTaskName}\" /F",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            });
            if (started is null)
                return false;
            started.WaitForExit(15000);
            return !ElevatedLogonTaskExists();
        }
        catch
        {
            return false;
        }
    }

    private static bool ElevatedLogonTaskExists()
        => RunSchtasks($"/Query /TN \"{LogonTaskName}\"", waitMs: 8000) == 0;

    private static void TrySetRunKey()
    {
        try
        {
            var exe = File.Exists(InstalledExePath) ? InstalledExePath : Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(AppName, $"\"{exe}\"");
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryRemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch
        {
            // best-effort
        }
    }

    private static int RunSchtasks(string arguments, int waitMs)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null)
                return -1;
            if (!proc.WaitForExit(waitMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return -1;
            }

            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
