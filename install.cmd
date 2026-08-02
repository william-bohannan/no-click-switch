@echo off
setlocal EnableExtensions
title No Click Switch installer
rem ---------------------------------------------------------------------------
rem  No Click Switch (NCS) — current-user installer (no admin, no PowerShell)
rem  https://github.com/william-bohannan/no-click-switch
rem
rem  Uses curl + tar (built into Windows 10/11) and reg.exe.
rem  Prefer this over "irm | iex" when Windows Security is strict.
rem ---------------------------------------------------------------------------

set "REPO=william-bohannan/no-click-switch"
set "APP=NoClickSwitch"
set "DISPLAY=No Click Switch"
set "INSTALL=%LOCALAPPDATA%\%APP%"
set "EXE=%INSTALL%\%APP%.exe"
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

if not exist "%INSTALL%" mkdir "%INSTALL%"
rem robocopy exit codes 0-7 mean success
robocopy "%SOURCE%" "%INSTALL%" /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /XD Update Addons
if errorlevel 8 (
  echo   Copy failed.
  pause
  exit /b 1
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
echo   Uninstall: uninstall.cmd  ^(or uninstall.ps1 / in-app Uninstall^)
echo.
endlocal
exit /b 0
