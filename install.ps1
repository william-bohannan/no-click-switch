#Requires -Version 5.1
<#
.SYNOPSIS
  Install No Click Switch (NCS) for the current user (no admin).

.DESCRIPTION
  Downloads the latest self-contained release, installs to
  %LocalAppData%\NoClickSwitch, registers auto-start on login, and launches the app.
  Preserves settings.json across reinstalls. Removes a previous Switched Bar install if present.

.EXAMPLE
  # One-liner (recommended)
  irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.ps1 | iex

.EXAMPLE
  # Local / pinned version
  .\install.ps1
  .\install.ps1 -Version v1.1.6
  .\install.ps1 -NoStart
  .\install.ps1 -ForceBuild
#>
param(
    [string]$Version = "latest",
    [switch]$NoStart,
    [switch]$ForceBuild
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Older Windows PowerShell defaults can omit TLS 1.2.
try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
catch { }

$Repo = "william-bohannan/no-click-switch"
$AppName = "NoClickSwitch"
$ShortName = "NCS"
$DisplayName = "No Click Switch"
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$ExePath = Join-Path $InstallDir "$AppName.exe"
$SettingsName = "settings.json"
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$UserAgent = "NoClickSwitch-Installer"
$LegacyAppName = "SwitchedBar"
$LegacyInstallDir = Join-Path $env:LOCALAPPDATA $LegacyAppName
$LatestZipUrl = "https://github.com/$Repo/releases/latest/download/NoClickSwitch-win-x64.zip"

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "=> $Message" -ForegroundColor Cyan
}

function Write-Ok([string]$Message) {
    Write-Host "   $Message" -ForegroundColor Green
}

function Write-Info([string]$Message) {
    Write-Host "   $Message" -ForegroundColor DarkGray
}

function Stop-AppProcesses {
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Info "Stopping running $($_.ProcessName) (PID $($_.Id))..."
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 400
}

function Get-ReleaseAsset {
    param([string]$Tag)

    # Fast path: no GitHub API needed for "latest".
    if ($Tag -eq "latest") {
        return [pscustomobject]@{
            Tag         = "latest"
            Name        = "NoClickSwitch-win-x64.zip"
            DownloadUrl = $LatestZipUrl
        }
    }

    $headers = @{ "User-Agent" = $UserAgent; "Accept" = "application/vnd.github+json" }
    $normalized = if ($Tag.StartsWith("v")) { $Tag } else { "v$Tag" }
    $uri = "https://api.github.com/repos/$Repo/releases/tags/$normalized"
    $release = Invoke-RestMethod -Uri $uri -Headers $headers
    $asset = $release.assets |
        Where-Object { $_.name -match '(?i)NoClickSwitch.*\.zip$|win-x64.*\.zip$' } |
        Select-Object -First 1
    if (-not $asset) {
        $asset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
    }
    if (-not $asset) {
        throw "Release $($release.tag_name) has no .zip asset."
    }

    [pscustomobject]@{
        Tag         = $release.tag_name
        Name        = $asset.name
        DownloadUrl = $asset.browser_download_url
    }
}

function Save-UserSettings {
    $settingsPath = Join-Path $InstallDir $SettingsName
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return $null
    }
    $backup = Join-Path ([System.IO.Path]::GetTempPath()) ("ncs-settings-" + [guid]::NewGuid().ToString("N") + ".json")
    Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
    Write-Info "Preserving existing $SettingsName"
    return $backup
}

function Restore-UserSettings {
    param([string]$BackupPath)
    if (-not $BackupPath -or -not (Test-Path -LiteralPath $BackupPath)) {
        return
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -LiteralPath $BackupPath -Destination (Join-Path $InstallDir $SettingsName) -Force
    Remove-Item -LiteralPath $BackupPath -Force -ErrorAction SilentlyContinue
    Write-Ok "Restored $SettingsName"
}

function Install-FromZip {
    param([string]$ZipPath)

    $settingsBackup = Save-UserSettings

    if (Test-Path -LiteralPath $InstallDir) {
        Write-Info "Removing previous install at $InstallDir"
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("NoClickSwitch-extract-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractDir -Force

        # Support both flat zips and a single top-level folder.
        $source = $extractDir
        $children = @(Get-ChildItem -LiteralPath $extractDir -Force)
        if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
            $source = $children[0].FullName
        }

        Copy-Item -Path (Join-Path $source "*") -Destination $InstallDir -Recurse -Force
    }
    finally {
        Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Restore-UserSettings -BackupPath $settingsBackup

    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "Install failed: $ExePath not found after extract."
    }
}

function Install-FromSourceBuild {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw @"
No GitHub release zip was found and the .NET SDK is not installed.

Options:
  1) Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
  2) Wait for / use a published release, then re-run this script

  irm https://raw.githubusercontent.com/$Repo/main/install.ps1 | iex
"@
    }

    $settingsBackup = Save-UserSettings

    $srcZip = Join-Path ([System.IO.Path]::GetTempPath()) "NoClickSwitch-src.zip"
    $srcDir = Join-Path ([System.IO.Path]::GetTempPath()) ("NoClickSwitch-src-" + [guid]::NewGuid().ToString("N"))
    $publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("NoClickSwitch-pub-" + [guid]::NewGuid().ToString("N"))

    Write-Info "Downloading source from main..."
    Invoke-WebRequest -Uri "https://github.com/$Repo/archive/refs/heads/main.zip" -OutFile $srcZip -UseBasicParsing -Headers @{ "User-Agent" = $UserAgent }

    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
    Expand-Archive -LiteralPath $srcZip -DestinationPath $srcDir -Force
    $projectRoot = Get-ChildItem -LiteralPath $srcDir -Directory | Select-Object -First 1
    if (-not $projectRoot) { throw "Could not locate source folder after download." }

    $csproj = Join-Path $projectRoot.FullName "NoClickSwitch.csproj"
    if (-not (Test-Path -LiteralPath $csproj)) {
        throw "NoClickSwitch.csproj not found in source archive."
    }

    Write-Info "Publishing self-contained win-x64 multi-file build (this may take a minute)..."
    & dotnet publish $csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $InstallDir -Recurse -Force

    Restore-UserSettings -BackupPath $settingsBackup

    Remove-Item -LiteralPath $srcZip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $srcDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "Install failed: $ExePath not found after build."
    }
}

function Set-AutoStart {
    New-Item -Path $RunKeyPath -Force | Out-Null
    Set-ItemProperty -Path $RunKeyPath -Name $AppName -Value "`"$ExePath`""
    Write-Ok "Auto-start on login enabled (current user)"
}

function Install-StartMenuShortcut {
    # Current-user Start Menu so you can relaunch after a crash (no admin).
    # Filename includes NCS so Start Search matches both "No Click Switch" and "ncs".
    $programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    New-Item -ItemType Directory -Path $programs -Force | Out-Null
    $appId = "william-bohannan.NoClickSwitch"
    $shortcutName = "$DisplayName ($ShortName)"
    $lnkPath = Join-Path $programs "$shortcutName.lnk"
    try {
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnkPath)
        $sc.TargetPath = $ExePath
        $sc.WorkingDirectory = $InstallDir
        $sc.WindowStyle = 1
        $sc.Description = "$DisplayName ($ShortName) - always-on-top window switcher"
        $ico = Join-Path $InstallDir "$AppName.ico"
        if (-not (Test-Path -LiteralPath $ico)) {
            $ico = $ExePath
        }
        $sc.IconLocation = "$ico,0"
        $sc.Save()
        Set-ShortcutAppUserModelId -LnkPath $lnkPath -AppId $appId -DisplayName $shortcutName
        foreach ($stale in @("$DisplayName.lnk", "$ShortName.lnk", "$AppName.lnk")) {
            $old = Join-Path $programs $stale
            if ((Test-Path -LiteralPath $old) -and ($old -ne $lnkPath)) {
                Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
            }
        }
        Write-Ok "Start Menu shortcut: $shortcutName"
    }
    catch {
        Write-Info "Start Menu shortcut skipped: $($_.Exception.Message)"
    }
}

function Set-ShortcutAppUserModelId {
    param([string]$LnkPath, [string]$AppId, [string]$DisplayName)
    # WScript.Shell cannot set AppUserModelID; without it, Windows 11 Search hides the app.
    if (-not ("NcsInstallLnk" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public static class NcsInstallLnk {
  [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
  private class ShellLink { }
  [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
  private interface IShellLinkW {
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
  }
  [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
  private interface IPropertyStore {
    [PreserveSig] int GetCount(out uint cProps);
    [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
    [PreserveSig] int Commit();
  }
  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  private struct PropertyKey { public Guid fmtid; public uint pid; }
  [StructLayout(LayoutKind.Sequential)]
  private struct PropVariant {
    public ushort vt; public ushort wReserved1; public ushort wReserved2; public ushort wReserved3;
    public IntPtr pointerValue; public IntPtr extra;
  }
  [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pvar);
  [DllImport("shell32.dll")] static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
  public static void Write(string lnkPath, string appId, string target, string workDir, string desc, string displayName) {
    var sl = (IShellLinkW)new ShellLink();
    sl.SetPath(target);
    sl.SetWorkingDirectory(workDir);
    sl.SetDescription(desc);
    sl.SetIconLocation(target, 0);
    sl.SetShowCmd(1);
    var store = (IPropertyStore)sl;
    Set(store, new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }, appId);
    Set(store, new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 2 }, "\"" + target + "\"");
    Set(store, new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 3 }, target + ",0");
    Set(store, new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 4 }, displayName);
    Set(store, new PropertyKey { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 5 }, "NCS;NoClickSwitch;No Click Switch");
    store.Commit();
    if (File.Exists(lnkPath)) File.Delete(lnkPath);
    ((IPersistFile)sl).Save(lnkPath, true);
    SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
  }
  static void Set(IPropertyStore store, PropertyKey key, string value) {
    var pv = new PropVariant { vt = 31, pointerValue = Marshal.StringToCoTaskMemUni(value) };
    store.SetValue(ref key, ref pv);
    PropVariantClear(ref pv);
  }
}
"@
    }
    try {
        [NcsInstallLnk]::Write($LnkPath, $AppId, $ExePath, $InstallDir, "$DisplayName ($ShortName)", $DisplayName)
    }
    catch {
        Write-Info "AppUserModelID not stamped on $(Split-Path $LnkPath -Leaf): $($_.Exception.Message)"
    }
}

function Register-AppIdentity {
    $uninstall = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
    New-Item -Path $uninstall -Force | Out-Null
    Set-ItemProperty -Path $uninstall -Name DisplayName -Value $DisplayName
    Set-ItemProperty -Path $uninstall -Name Publisher -Value "william-bohannan"
    Set-ItemProperty -Path $uninstall -Name InstallLocation -Value $InstallDir
    Set-ItemProperty -Path $uninstall -Name DisplayIcon -Value "$ExePath,0"
    Set-ItemProperty -Path $uninstall -Name UninstallString -Value "`"$ExePath`" --uninstall"
    Set-ItemProperty -Path $uninstall -Name QuietUninstallString -Value "`"$ExePath`" --uninstall"
    Set-ItemProperty -Path $uninstall -Name HelpLink -Value "https://github.com/$Repo"
    Set-ItemProperty -Path $uninstall -Name URLInfoAbout -Value "https://noclickswitch.com"
    Set-ItemProperty -Path $uninstall -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstall -Name NoRepair -Value 1 -Type DWord

    foreach ($name in @("$AppName.exe", "ncs.exe")) {
        $p = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$name"
        New-Item -Path $p -Force | Out-Null
        New-ItemProperty -Path $p -Name "(default)" -Value $ExePath -PropertyType String -Force | Out-Null
        Set-ItemProperty -Path $p -Name Path -Value $InstallDir
    }

    $appClass = "HKCU:\Software\Classes\Applications\$AppName.exe"
    New-Item -Path $appClass -Force | Out-Null
    Set-ItemProperty -Path $appClass -Name FriendlyAppName -Value $DisplayName
    Set-ItemProperty -Path $appClass -Name AppUserModelID -Value "william-bohannan.NoClickSwitch"
    Write-Ok "Registered for Start Search (ncs / $DisplayName)"
}

function Remove-LegacyInstall {
    Get-Process -Name $LegacyAppName -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Info "Stopping legacy $($_.ProcessName) (PID $($_.Id))..."
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    try {
        if (Get-ItemProperty -Path $RunKeyPath -Name $LegacyAppName -ErrorAction SilentlyContinue) {
            Remove-ItemProperty -Path $RunKeyPath -Name $LegacyAppName -ErrorAction SilentlyContinue
            Write-Info "Removed legacy Switched Bar auto-start"
        }
    }
    catch { }
    if (Test-Path -LiteralPath $LegacyInstallDir) {
        Write-Info "Removing legacy install at $LegacyInstallDir"
        Remove-Item -LiteralPath $LegacyInstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- main ---
Write-Host ""
Write-Host "  $DisplayName ($ShortName) installer" -ForegroundColor White
Write-Host "  https://github.com/$Repo" -ForegroundColor DarkGray
Write-Host "  https://noclickswitch.com" -ForegroundColor DarkGray

Stop-AppProcesses
Remove-LegacyInstall

$installedFrom = $null
if (-not $ForceBuild) {
    try {
        Write-Step "Resolving release ($Version)..."
        $asset = Get-ReleaseAsset -Tag $Version
        Write-Ok "Using $($asset.Name) ($($asset.Tag))"

        Write-Step "Downloading..."
        $zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "NoClickSwitch-install.zip"
        Invoke-WebRequest -Uri $asset.DownloadUrl -OutFile $zipPath -UseBasicParsing -Headers @{ "User-Agent" = $UserAgent }

        # Basic sanity check (empty / HTML error page).
        $zipItem = Get-Item -LiteralPath $zipPath
        if ($zipItem.Length -lt 1MB) {
            throw "Download looks too small ($($zipItem.Length) bytes). Release asset may be missing."
        }

        Write-Step "Installing to $InstallDir"
        Install-FromZip -ZipPath $zipPath
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        $installedFrom = $asset.Tag
    }
    catch {
        Write-Info "Release download unavailable: $($_.Exception.Message)"
        Write-Step "Falling back to build-from-source..."
        Install-FromSourceBuild
        $installedFrom = "source (main)"
    }
}
else {
    Write-Step "Building from source (-ForceBuild)..."
    Install-FromSourceBuild
    $installedFrom = "source (main)"
}

Write-Step "Configuring auto-start..."
Set-AutoStart

Write-Step "Adding Start Menu shortcut..."
Install-StartMenuShortcut

Write-Step "Registering app for Start Search..."
Register-AppIdentity

if (-not $NoStart) {
    Write-Step "Starting $DisplayName ($ShortName)..."
    Start-Process -FilePath $ExePath
    Write-Ok "Launched"
}

Write-Host ""
Write-Host "  Installed successfully ($installedFrom)" -ForegroundColor Green
Write-Host "  Location: $InstallDir" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Uninstall:" -ForegroundColor DarkGray
Write-Host "    irm https://raw.githubusercontent.com/$Repo/main/uninstall.ps1 | iex" -ForegroundColor DarkGray
Write-Host ""
