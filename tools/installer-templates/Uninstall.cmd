@echo off
setlocal EnableExtensions

REM ===========================================================
REM   ScriptDeck uninstaller
REM
REM   Removes the install folder, desktop shortcut, and Start
REM   Menu shortcut. Preserves user data under
REM   %LocalAppData%\ScriptDeck (history.db, recent.json, custom
REM   workspaces saved there) so an upgrade-by-reinstall keeps
REM   your stuff.
REM ===========================================================

title ScriptDeck Uninstaller
echo.
echo   ScriptDeck Uninstaller
echo   ----------------------
echo.

set "INSTALL_DIR=%LocalAppData%\Programs\ScriptDeck"
set "STARTMENU=%AppData%\Microsoft\Windows\Start Menu\Programs"

if not exist "%INSTALL_DIR%" (
    echo ScriptDeck doesn't appear to be installed at:
    echo   %INSTALL_DIR%
    echo Nothing to remove.
    pause
    exit /b 0
)

echo This will remove:
echo   %INSTALL_DIR%
echo   %USERPROFILE%\Desktop\ScriptDeck.lnk
echo   %STARTMENU%\ScriptDeck.lnk
echo.
echo User data (run history, recent workspaces) under
echo %LocalAppData%\ScriptDeck will be PRESERVED.
echo.
echo Continue? [Y/N]
set /p OK=^>^>^>
if /i not "%OK%"=="Y" exit /b 1

if exist "%USERPROFILE%\Desktop\ScriptDeck.lnk" del /q "%USERPROFILE%\Desktop\ScriptDeck.lnk"
if exist "%STARTMENU%\ScriptDeck.lnk" del /q "%STARTMENU%\ScriptDeck.lnk"
rmdir /s /q "%INSTALL_DIR%"
echo.
echo Done.
pause
exit /b 0
