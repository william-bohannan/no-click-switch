@echo off
setlocal EnableExtensions
title No Click Switch uninstall
rem ---------------------------------------------------------------------------
rem  No Click Switch (NCS) — current-user uninstall (no admin, no PowerShell)
rem ---------------------------------------------------------------------------

set "APP=NoClickSwitch"
set "LEGACY=SwitchedBar"
set "INSTALL=%LOCALAPPDATA%\%APP%"
set "LEGACY_DIR=%LOCALAPPDATA%\%LEGACY%"

echo.
echo   Uninstalling No Click Switch...
echo.

echo   Stopping running instances...
taskkill /IM %APP%.exe /F >NUL 2>&1
taskkill /IM %LEGACY%.exe /F >NUL 2>&1
timeout /t 1 /nobreak >NUL

echo   Removing auto-start...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v %APP% /f >NUL 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v %LEGACY% /f >NUL 2>&1

echo   Removing Start Menu shortcut...
set "STARTMENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs"
del /f /q "%STARTMENU%\No Click Switch.lnk" >NUL 2>&1
del /f /q "%STARTMENU%\NoClickSwitch.lnk" >NUL 2>&1
del /f /q "%STARTMENU%\NCS.lnk" >NUL 2>&1
if exist "%STARTMENU%\No Click Switch" rmdir /s /q "%STARTMENU%\No Click Switch" >NUL 2>&1

if exist "%INSTALL%" (
  echo   Removing %INSTALL%
  rmdir /s /q "%INSTALL%" 2>NUL
  if exist "%INSTALL%" (
    echo   Folder locked; will retry after a short delay...
    timeout /t 2 /nobreak >NUL
    rmdir /s /q "%INSTALL%" 2>NUL
  )
)

if exist "%LEGACY_DIR%" (
  echo   Removing legacy %LEGACY_DIR%
  rmdir /s /q "%LEGACY_DIR%" 2>NUL
)

echo.
echo   Uninstall complete.
echo.
echo   PowerShell one-liner next time:
echo     irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/uninstall.ps1 ^| iex
echo.
endlocal
exit /b 0
