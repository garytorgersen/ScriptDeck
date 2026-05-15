using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Runs .cmd / .bat batch scripts via cmd.exe, with stdout/stderr
    /// captured into the sink. Cancellation kills the cmd.exe process.
    ///
    /// We always go through cmd.exe even when args don't need shell
    /// features — batch files require a command interpreter to run, and
    /// .Process.Start on a .cmd directly defers to cmd.exe anyway.
    /// </summary>
    public sealed class CmdExecutor : IExecutor
    {
        public string Kind => "cmd";

        public async Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request, IOutputSink sink, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            var sw = Stopwatch.StartNew();

            if (string.IsNullOrEmpty(request.ScriptPath) || !File.Exists(request.ScriptPath))
            {
                sink.WriteError($"Script not found: {request.ScriptPath}{Environment.NewLine}");
                return ExecutionResult.Failed("Script not found", sw.Elapsed);
            }

            // cmd.exe writes in the active OEM code page (typically CP437
            // on en-US, CP850 on Western European locales, etc.), NOT
            // UTF-8. Forcing UTF-8 here corrupts non-ASCII output. We
            // resolve the OEM page from the current UI culture; if that
            // fails (rare -- a deeply nonstandard locale), fall back to
            // the system default ANSI code page.
            Encoding consoleEncoding;
            try
            {
                int oemCp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                consoleEncoding = Encoding.GetEncoding(oemCp);
            }
            catch
            {
                consoleEncoding = Encoding.Default;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = BuildCmdArguments(request),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ResolveWorkingDirectory(request),
                StandardOutputEncoding = consoleEncoding,
                StandardErrorEncoding = consoleEncoding,
            };

            // Publish shared-input values to the child cmd.exe as
            // environment variables, so a batch script can reference them
            // as %computerName% (or whatever the input id was). This is
            // the cmd-side equivalent of the runspace-variable injection
            // PowerShellExecutor does — same idea, different ABI.
            //
            // Env-var names with dashes / dots / spaces aren't legal on
            // Windows; we let ProcessStartInfo throw on those and just
            // skip (caller saw the warning from PowerShellExecutor first
            // if both executors are wired up).
            if (request.SharedInputs != null)
            {
                foreach (var kv in request.SharedInputs)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    try { psi.EnvironmentVariables[kv.Key] = kv.Value ?? string.Empty; }
                    catch { /* invalid name — skip silently */ }
                }
            }

            try
            {
                using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    // Line-buffered async reads. Both streams race for the
                    // sink, but the sink is thread-safe (BeginInvoke per
                    // append) so interleaving is safe — and matches what
                    // the user sees in a real cmd window.
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null) sink.WriteOutput(e.Data + Environment.NewLine);
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) sink.WriteError(e.Data + Environment.NewLine);
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // Capture the cmd.exe PID so the cancellation handler
                    // (which can run on a thread that's lost the local
                    // var to scope teardown) has a stable reference.
                    int cmdPid = proc.Id;

                    using (cancellationToken.Register(() =>
                    {
                        // Kill the entire process tree, not just cmd.exe.
                        // .NET Framework 4.8 doesn't have Process.Kill(true),
                        // so shell out to taskkill /T /F which walks the
                        // process tree via the OS job-object tracking and
                        // terminates every descendant.
                        try
                        {
                            if (!proc.HasExited)
                            {
                                KillProcessTree(cmdPid);
                                // Belt-and-suspenders: also try the direct
                                // Kill in case taskkill failed for any
                                // reason (UAC, AV interference, etc.).
                                try { if (!proc.HasExited) proc.Kill(); } catch { }
                            }
                        }
                        catch { /* best effort */ }
                    }))
                    {
                        // Task.Run wraps the blocking WaitForExit so we
                        // don't tie up the dispatcher's continuation thread.
                        await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);

                        // Critical: a SECOND WaitForExit (no timeout)
                        // forces the runtime to drain any remaining
                        // OutputDataReceived / ErrorDataReceived events
                        // that were buffered when the process exited.
                        // Without this, the last batch of stdout/stderr
                        // is silently lost on fast-exiting processes.
                        await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        return ExecutionResult.CancelledResult(sw.Elapsed);

                    return ExecutionResult.Ok(proc.ExitCode, sw.Elapsed);
                }
            }
            catch (Exception ex)
            {
                sink.WriteError($"[cmd] {ex.Message}{Environment.NewLine}");
                return ExecutionResult.Failed(ex.Message, sw.Elapsed);
            }
        }

        // cmd.exe's /c argument parsing has a documented quirk: if the
        // entire command is wrapped in an outer pair of quotes, those
        // quotes are stripped and the remainder is parsed as the command.
        // That lets us safely pass paths-with-spaces:
        //     cmd.exe /c ""C:\My Scripts\thing.cmd" arg1 "arg with space""
        // Without the outer wrap, cmd would split at the first space.
        private static string BuildCmdArguments(ExecutionRequest req)
        {
            var inner = new StringBuilder();
            inner.Append('"').Append(req.ScriptPath).Append('"');
            if (req.Args != null)
            {
                foreach (var a in req.Args)
                {
                    inner.Append(' ').Append(QuoteArg(a ?? string.Empty));
                }
            }
            return "/c \"" + inner + "\"";
        }

        // Conservative argument quoting: wrap in quotes if the arg
        // contains a space or any cmd metacharacter. Embedded quotes are
        // doubled (cmd.exe convention, not Windows-CRT).
        private static string QuoteArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            bool needsQuote = arg.Any(c => c == ' ' || c == '\t' || c == '&' || c == '|' ||
                                            c == '<' || c == '>' || c == '^' || c == '"');
            if (!needsQuote) return arg;
            return "\"" + arg.Replace("\"", "\"\"") + "\"";
        }

        private static string ResolveWorkingDirectory(ExecutionRequest req)
        {
            if (!string.IsNullOrEmpty(req.WorkingDirectory)) return req.WorkingDirectory;
            // Resolve to an absolute path so a relative ScriptPath (which
            // shouldn't normally happen, but is possible if a workspace
            // hand-edits) doesn't inherit the parent process's CWD --
            // that would be wherever ScriptDeck.exe was launched from,
            // which is rarely what the user expects.
            try
            {
                var full = Path.GetFullPath(req.ScriptPath ?? string.Empty);
                return Path.GetDirectoryName(full) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // Terminate a process and every descendant. Net48's Process.Kill
        // only stops the immediate process -- if the user's batch script
        // launched ping.exe -t or similar, those children orphan after
        // an Esc cancellation. taskkill /T walks the OS process-tree
        // tracking and signals all of them; /F forces termination
        // (cmd.exe ignores polite shutdown signals when it has children
        // mid-pipeline).
        //
        // Runs synchronously with a short timeout so cancellation feels
        // instant. Failures are swallowed -- this is a best-effort
        // last resort; if taskkill itself can't run, the caller falls
        // back to proc.Kill() in the catch above.
        private static void KillProcessTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/T /F /PID " + pid.ToString(CultureInfo.InvariantCulture),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var killer = Process.Start(psi))
                {
                    if (killer != null)
                    {
                        // 2 seconds is more than enough on a healthy box
                        // and short enough to keep the cancel UX snappy.
                        if (!killer.WaitForExit(2000))
                        {
                            try { killer.Kill(); } catch { }
                        }
                    }
                }
            }
            catch
            {
                // Swallow -- the caller has its own fallback Kill().
            }
        }
    }
}
