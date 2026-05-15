@echo off
REM Sample CMD script for ScriptDeck. Output streams to the console RTB
REM line by line as systeminfo writes it. The first arg is the computer
REM name (substituted from the shared input).
REM
REM IMPORTANT: we invoke systeminfo by FULL PATH (%SystemRoot%\System32\
REM systeminfo.exe). The CmdExecutor sets the current directory to the
REM script's folder, and bare "systeminfo" would resolve to THIS .cmd
REM file (current dir wins in cmd's lookup order) and recurse until the
REM setlocal stack overflows. Path-qualifying the call avoids that.
setlocal

set "SI=%SystemRoot%\System32\systeminfo.exe"

if "%~1"=="" (
  "%SI%"
) else if "%~1"=="." (
  "%SI%"
) else (
  "%SI%" /S "%~1"
)

endlocal
