@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================
REM   ScriptDeck installer
REM
REM   Installs to %LocalAppData%\Programs\ScriptDeck and drops a
REM   desktop + Start Menu shortcut. Checks for the .NET Framework
REM   4.8 runtime first; if missing, offers to launch Microsoft's
REM   download page so the user can install it.
REM
REM   This installer NEVER requires admin. It writes only to the
REM   current user's profile -- no UAC prompt, no Program Files
REM   write attempts, no machine-wide changes.
REM ===========================================================

title ScriptDeck Installer
echo.
echo   ScriptDeck Installer
echo   --------------------
echo.

REM --- Prerequisite: .NET Framework 4.8 ---
REM 528040 is the Release value for .NET 4.8 (4.8.1 is 533320). We
REM accept anything >= 528040 because newer versions are backwards
REM compatible. Read via PowerShell because reg.exe has subtle
REM quoting quirks across Windows versions.

set "NET_OK="
for /f "delims=" %%R in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "$r = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release; if ($r -ge 528040) { 'OK' } else { 'MISSING' }"') do set "NET_OK=%%R"

if /i not "%NET_OK%"=="OK" goto NET_MISSING

echo [1/3] .NET Framework 4.8 found.

REM --- Copy app files ---
set "INSTALL_DIR=%LocalAppData%\Programs\ScriptDeck"
echo [2/3] Installing to %INSTALL_DIR%
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
xcopy /E /Y /I /Q "%~dp0App\*" "%INSTALL_DIR%\" >nul
if errorlevel 1 (
    echo.
    echo ERROR: Failed to copy files to "%INSTALL_DIR%".
    echo Make sure you have write access to %LocalAppData%.
    echo.
    pause
    exit /b 1
)

REM --- Shortcuts ---
echo [3/3] Creating shortcuts...
set "SHORTCUT_VBS=%TEMP%\ScriptDeck-shortcut.vbs"

REM Desktop shortcut.
(
    echo Set s = WScript.CreateObject^("WScript.Shell"^)
    echo Set sc = s.CreateShortcut^("%USERPROFILE%\Desktop\ScriptDeck.lnk"^)
    echo sc.TargetPath = "%INSTALL_DIR%\ScriptDeck.exe"
    echo sc.WorkingDirectory = "%INSTALL_DIR%"
    echo sc.IconLocation = "%INSTALL_DIR%\ScriptDeck.exe, 0"
    echo sc.Description = "ScriptDeck button-driven script launcher"
    echo sc.Save
) > "%SHORTCUT_VBS%"
cscript //nologo "%SHORTCUT_VBS%" >nul 2>&1

REM Start Menu shortcut.
set "STARTMENU=%AppData%\Microsoft\Windows\Start Menu\Programs"
if not exist "%STARTMENU%" mkdir "%STARTMENU%"
(
    echo Set s = WScript.CreateObject^("WScript.Shell"^)
    echo Set sc = s.CreateShortcut^("%STARTMENU%\ScriptDeck.lnk"^)
    echo sc.TargetPath = "%INSTALL_DIR%\ScriptDeck.exe"
    echo sc.WorkingDirectory = "%INSTALL_DIR%"
    echo sc.IconLocation = "%INSTALL_DIR%\ScriptDeck.exe, 0"
    echo sc.Description = "ScriptDeck button-driven script launcher"
    echo sc.Save
) > "%SHORTCUT_VBS%"
cscript //nologo "%SHORTCUT_VBS%" >nul 2>&1
del /q "%SHORTCUT_VBS%" 2>nul

echo.
echo   ScriptDeck installed successfully.
echo   Location: %INSTALL_DIR%
echo   Shortcut: Desktop and Start Menu
echo.
echo   Launch ScriptDeck now? [Y/N]
set /p LAUNCH=^>^>^>
if /i "%LAUNCH%"=="Y" start "" "%INSTALL_DIR%\ScriptDeck.exe"
exit /b 0


:NET_MISSING
echo .NET Framework 4.8 is REQUIRED but not installed on this machine.
echo.
echo .NET 4.8 ships built-in with Windows 10 (May 2019 update) and
echo Windows 11. If you're on an older Windows or have manually
echo uninstalled it, you'll need to install the runtime first.
echo.
echo Open Microsoft's download page now? [Y/N]
set /p DLNOW=^>^>^>
if /i "%DLNOW%"=="Y" (
    start "" "https://dotnet.microsoft.com/download/dotnet-framework/net48"
    echo.
    echo After installing .NET Framework 4.8, re-run this installer.
)
echo.
pause
exit /b 1
