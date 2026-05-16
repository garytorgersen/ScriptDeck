@echo off
REM Sample CMD script for ScriptDeck. Output streams to the console RTB
REM line by line as systeminfo writes it. The first arg is the computer
REM name (substituted from the shared input).
REM
REM ScriptDeck's "computerName" shared input is normalized at click
REM time: empty / "." / "localhost" all get rewritten to the actual
REM local machine name BEFORE the script sees them. So when the user
REM has the default "." in the textbox, this script receives the
REM literal local hostname (e.g. "MYBOX"), not ".". We must compare
REM against %COMPUTERNAME% to detect that case -- a naive "is it dot?"
REM check would never match and we'd issue a remote WMI query against
REM ourselves (slow, often hangs on firewall / WMI authentication).
REM
REM IMPORTANT: we invoke systeminfo by FULL PATH (%SystemRoot%\System32\
REM systeminfo.exe). The CmdExecutor sets the current directory to the
REM script's folder, and bare "systeminfo" would resolve to THIS .cmd
REM file (current dir wins in cmd's lookup order) and recurse until the
REM setlocal stack overflows. Path-qualifying the call avoids that.
setlocal

set "SI=%SystemRoot%\System32\systeminfo.exe"
set "TARGET=%~1"

REM Local cases: empty arg, "." / "localhost" placeholders, OR the
REM normalized value -- which is the actual local hostname.
if "%TARGET%"==""           goto LOCAL
if /i "%TARGET%"=="."         goto LOCAL
if /i "%TARGET%"=="localhost" goto LOCAL
if /i "%TARGET%"=="%COMPUTERNAME%" goto LOCAL

REM Remote: /S targets the named machine. Requires WMI access +
REM credentials on the target; will be slow or fail with helpful
REM error text routed to the executor's error stream.
"%SI%" /S "%TARGET%"
goto END

:LOCAL
"%SI%"

:END
endlocal
