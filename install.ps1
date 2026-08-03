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
    $programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    New-Item -ItemType Directory -Path $programs -Force | Out-Null
    $lnkPath = Join-Path $programs "$DisplayName.lnk"
    try {
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnkPath)
        $sc.TargetPath = $ExePath
        $sc.WorkingDirectory = $InstallDir
        $sc.WindowStyle = 1
        $sc.Description = "$DisplayName ($ShortName) — always-on-top window switcher"
        $ico = Join-Path $InstallDir "$AppName.ico"
        if (-not (Test-Path -LiteralPath $ico)) {
            $ico = $ExePath
        }
        $sc.IconLocation = "$ico,0"
        $sc.Save()
        Write-Ok "Start Menu shortcut: $DisplayName"
    }
    catch {
        Write-Info "Start Menu shortcut skipped: $($_.Exception.Message)"
    }
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
