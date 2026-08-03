@echo off
setlocal EnableExtensions
title No Click Switch installer
rem ---------------------------------------------------------------------------
rem  No Click Switch (NCS) — current-user installer (no admin, no PowerShell)
rem  https://github.com/william-bohannan/no-click-switch
rem
rem  Uses curl + tar (built into Windows 10/11) and reg.exe.
rem  Preferred one-liner (PowerShell):
rem    irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.ps1 | iex
rem ---------------------------------------------------------------------------

set "REPO=william-bohannan/no-click-switch"
set "APP=NoClickSwitch"
set "DISPLAY=No Click Switch"
set "INSTALL=%LOCALAPPDATA%\%APP%"
set "EXE=%INSTALL%\%APP%.exe"
set "SETTINGS=%INSTALL%\settings.json"
set "SETTINGS_BAK=%TEMP%\ncs-settings-backup.json"
set "ZIP=%TEMP%\NoClickSwitch-win-x64.zip"
set "EXTRACT=%TEMP%\NoClickSwitch-extract-cmd"
set "URL=https://github.com/%REPO%/releases/latest/download/NoClickSwitch-win-x64.zip"
set "UA=NoClickSwitch-Installer"

echo.
echo   %DISPLAY% installer
echo   https://github.com/%REPO%
echo.

where curl.exe >NUL 2>&1
if errorlevel 1 (
  echo   curl.exe not found. Download the release zip in your browser:
  echo   https://github.com/%REPO%/releases/latest
  echo   Extract into: %INSTALL%
  start "" "https://github.com/%REPO%/releases/latest"
  pause
  exit /b 1
)

echo   Stopping running instances...
taskkill /IM %APP%.exe /F >NUL 2>&1
timeout /t 1 /nobreak >NUL

rem Preserve user settings across reinstall.
if exist "%SETTINGS_BAK%" del /f /q "%SETTINGS_BAK%" >NUL 2>&1
if exist "%SETTINGS%" (
  echo   Preserving settings.json...
  copy /y "%SETTINGS%" "%SETTINGS_BAK%" >NUL
)

echo   Downloading latest release...
curl.exe -L --fail --retry 3 -A "%UA%" -o "%ZIP%" "%URL%"
if errorlevel 1 (
  echo   Download failed. Opening Releases page...
  start "" "https://github.com/%REPO%/releases/latest"
  pause
  exit /b 1
)

echo   Installing to %INSTALL%
if exist "%EXTRACT%" rmdir /s /q "%EXTRACT%"
mkdir "%EXTRACT%" >NUL 2>&1
tar -xf "%ZIP%" -C "%EXTRACT%"
if errorlevel 1 (
  echo   Extract failed. Is tar available?
  pause
  exit /b 1
)

rem Flat zip or single top-level folder.
set "SOURCE=%EXTRACT%"
if not exist "%EXTRACT%\%APP%.exe" (
  for /d %%D in ("%EXTRACT%\*") do (
    if exist "%%D\%APP%.exe" set "SOURCE=%%D"
  )
)

rem Clean install dir, then copy (avoid stale files from older builds).
if exist "%INSTALL%" rmdir /s /q "%INSTALL%" 2>NUL
if exist "%INSTALL%" (
  rem Folder may still be locked; fall back to overwrite copy.
  echo   Note: could not fully clear install folder; overwriting files...
) else (
  mkdir "%INSTALL%" >NUL 2>&1
)

rem robocopy exit codes 0-7 mean success
robocopy "%SOURCE%" "%INSTALL%" /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /XD Update Addons
if errorlevel 8 (
  echo   Copy failed.
  pause
  exit /b 1
)

if exist "%SETTINGS_BAK%" (
  echo   Restoring settings.json...
  copy /y "%SETTINGS_BAK%" "%SETTINGS%" >NUL
  del /f /q "%SETTINGS_BAK%" >NUL 2>&1
)

if not exist "%EXE%" (
  echo   Install failed: %EXE% not found after extract.
  pause
  exit /b 1
)

echo   Configuring auto-start on login...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v %APP% /t REG_SZ /d "\"%EXE%\"" /f >NUL

echo   Starting %DISPLAY%...
start "" "%EXE%"

del /f /q "%ZIP%" >NUL 2>&1
rmdir /s /q "%EXTRACT%" >NUL 2>&1

echo.
echo   Installed successfully.
echo   Location: %INSTALL%
echo.
echo   Uninstall ^(PowerShell^):
echo     irm https://raw.githubusercontent.com/%REPO%/main/uninstall.ps1 ^| iex
echo.
endlocal
exit /b 0
