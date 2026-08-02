#Requires -Version 5.1
<#
.SYNOPSIS
  Install Switched Bar for the current user (no admin).

.DESCRIPTION
  Downloads the latest release (or builds from source), installs to
  %LocalAppData%\SwitchedBar, registers auto-start on login, and launches the app.

.EXAMPLE
  # One-liner from the web (recommended)
  irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/install.ps1 | iex

.EXAMPLE
  # From Command Prompt
  powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/install.ps1 | iex"

.EXAMPLE
  # Install a specific release tag
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/install.ps1))) -Version v1.0.0

.EXAMPLE
  # Install without launching
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/install.ps1))) -NoStart
#>
param(
    [string]$Version = "latest",
    [switch]$NoStart,
    [switch]$ForceBuild
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Repo = "william-bohannan/switchedbar"
$AppName = "SwitchedBar"
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$ExePath = Join-Path $InstallDir "$AppName.exe"
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$UserAgent = "SwitchedBar-Installer"

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

    $headers = @{ "User-Agent" = $UserAgent; "Accept" = "application/vnd.github+json" }
    if ($Tag -eq "latest") {
        $uri = "https://api.github.com/repos/$Repo/releases/latest"
    }
    else {
        $normalized = if ($Tag.StartsWith("v")) { $Tag } else { "v$Tag" }
        $uri = "https://api.github.com/repos/$Repo/releases/tags/$normalized"
    }

    $release = Invoke-RestMethod -Uri $uri -Headers $headers
    $asset = $release.assets |
        Where-Object { $_.name -match '(?i)win-x64.*\.zip$|SwitchedBar.*\.zip$' } |
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

function Install-FromZip {
    param([string]$ZipPath)

    if (Test-Path $InstallDir) {
        Write-Info "Removing previous install at $InstallDir"
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("SwitchedBar-extract-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractDir -Force

        # Support both flat zips and a single top-level folder.
        $source = $extractDir
        $children = Get-ChildItem -LiteralPath $extractDir -Force
        if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
            $source = $children[0].FullName
        }

        Copy-Item -Path (Join-Path $source "*") -Destination $InstallDir -Recurse -Force
    }
    finally {
        Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }

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
"@
    }

    $srcZip = Join-Path ([System.IO.Path]::GetTempPath()) "SwitchedBar-src.zip"
    $srcDir = Join-Path ([System.IO.Path]::GetTempPath()) ("SwitchedBar-src-" + [guid]::NewGuid().ToString("N"))
    $publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("SwitchedBar-pub-" + [guid]::NewGuid().ToString("N"))

    Write-Info "Downloading source from main..."
    Invoke-WebRequest -Uri "https://github.com/$Repo/archive/refs/heads/main.zip" -OutFile $srcZip -UseBasicParsing -Headers @{ "User-Agent" = $UserAgent }

    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
    Expand-Archive -LiteralPath $srcZip -DestinationPath $srcDir -Force
    $projectRoot = Get-ChildItem -LiteralPath $srcDir -Directory | Select-Object -First 1
    if (-not $projectRoot) { throw "Could not locate source folder after download." }

    $csproj = Join-Path $projectRoot.FullName "SwitchedBar.csproj"
    if (-not (Test-Path -LiteralPath $csproj)) {
        throw "SwitchedBar.csproj not found in source archive."
    }

    Write-Info "Publishing self-contained win-x64 build (this may take a minute)..."
    & dotnet publish $csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    if (Test-Path $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $InstallDir -Recurse -Force

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

# --- main ---
Write-Host ""
Write-Host "  Switched Bar installer" -ForegroundColor White
Write-Host "  https://github.com/$Repo" -ForegroundColor DarkGray

Stop-AppProcesses

$installedFrom = $null
if (-not $ForceBuild) {
    try {
        Write-Step "Fetching release metadata ($Version)..."
        $asset = Get-ReleaseAsset -Tag $Version
        Write-Ok "Found $($asset.Tag) — $($asset.Name)"

        Write-Step "Downloading..."
        $zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "SwitchedBar-install.zip"
        Invoke-WebRequest -Uri $asset.DownloadUrl -OutFile $zipPath -UseBasicParsing -Headers @{ "User-Agent" = $UserAgent }

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

if (-not $NoStart) {
    Write-Step "Starting Switched Bar..."
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
