#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstall No Click Switch (NCS) for the current user.

.EXAMPLE
  # One-liner (recommended)
  irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/uninstall.ps1 | iex
#>
$ErrorActionPreference = "Stop"

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
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$LegacyAppName = "SwitchedBar"
$LegacyInstallDir = Join-Path $env:LOCALAPPDATA $LegacyAppName

Write-Host ""
Write-Host "  $DisplayName ($ShortName) uninstaller" -ForegroundColor White
Write-Host "  https://github.com/$Repo" -ForegroundColor DarkGray
Write-Host ""

# Stop running instance.
Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "=> Stopping $($_.ProcessName) (PID $($_.Id))..." -ForegroundColor Cyan
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

# Remove auto-start.
try {
    if (Get-ItemProperty -Path $RunKeyPath -Name $AppName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKeyPath -Name $AppName -ErrorAction SilentlyContinue
        Write-Host "=> Removed auto-start registry entry" -ForegroundColor Cyan
    }
}
catch {
    # best-effort
}

# Remove Start Menu shortcut(s).
$startMenuCandidates = @(
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$DisplayName.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$ShortName.lnk")
)
foreach ($lnk in $startMenuCandidates) {
    if (Test-Path -LiteralPath $lnk) {
        Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
        Write-Host "=> Removed Start Menu shortcut" -ForegroundColor Cyan
    }
}
$startMenuFolder = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$DisplayName"
if (Test-Path -LiteralPath $startMenuFolder) {
    Remove-Item -LiteralPath $startMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "=> Removed Start Menu folder" -ForegroundColor Cyan
}

# Remove install folder.
if (Test-Path -LiteralPath $InstallDir) {
    Write-Host "=> Removing $InstallDir" -ForegroundColor Cyan
    try {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    catch {
        # Files may still be locked; schedule delete after a short delay.
        $cmd = "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q `"$InstallDir`""
        Start-Process -FilePath "cmd.exe" -ArgumentList $cmd -WindowStyle Hidden
        Write-Host "   Scheduled folder delete (files were locked)" -ForegroundColor DarkGray
    }
}
else {
    Write-Host "=> Install folder not found (already removed)" -ForegroundColor DarkGray
}

# Also clean a legacy Switched Bar install if present.
Get-Process -Name $LegacyAppName -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
try {
    if (Get-ItemProperty -Path $RunKeyPath -Name $LegacyAppName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKeyPath -Name $LegacyAppName -ErrorAction SilentlyContinue
        Write-Host "=> Removed legacy Switched Bar auto-start" -ForegroundColor Cyan
    }
}
catch { }
if (Test-Path -LiteralPath $LegacyInstallDir) {
    Write-Host "=> Removing legacy $LegacyInstallDir" -ForegroundColor Cyan
    Remove-Item -LiteralPath $LegacyInstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "  Uninstalled." -ForegroundColor Green
Write-Host ""
