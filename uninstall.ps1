#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstall Switched Bar for the current user.

.EXAMPLE
  irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/uninstall.ps1 | iex
#>
$ErrorActionPreference = "Stop"

$Repo = "william-bohannan/switchedbar"
$AppName = "SwitchedBar"
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

Write-Host ""
Write-Host "  Switched Bar uninstaller" -ForegroundColor White
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

Write-Host ""
Write-Host "  Uninstalled." -ForegroundColor Green
Write-Host ""
