using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Runs Bash scripts via a configurable interpreter. Like
    /// <see cref="PythonExecutor"/> this is one-shot-per-click: each
    /// run spawns a fresh bash process and waits for it to exit.
    ///
    /// "Bash on Windows" is fragmented across at least four
    /// implementations (Git Bash, WSL, MSYS2, Cygwin), each with
    /// subtly different path semantics. We don't try to abstract over
    /// them at the executor level; we just spawn whatever interpreter
    /// the workspace / button configures. The shipped
    /// <c>scriptdeck_bootstrap.sh</c> (sourced via BASH_ENV) provides
    /// path-translation helpers that hide the differences from user
    /// scripts.
    ///
    /// Interpreter resolution precedence:
    ///   1. Per-button override (<c>Button.BashInterpreter</c>)
    ///   2. Workspace default (<c>Workspace.BashInterpreter</c>)
    ///   3. Bare <c>bash</c> on PATH
    ///   4. Canonical Git Bash install paths
    ///      (<c>C:\Program Files\Git\bin\bash.exe</c>, then x86)
    ///
    /// Output protocol mirrors PythonExecutor's: stdout lines prefixed
    /// with <c>__SCRIPTDECK_JSON__</c> are parsed as routing tags
    /// (grid/rtb/set-shared-input/remove-shared-input); untagged lines
    /// go to the console RTB; stderr always routes to the sink's Error
    /// stream.
    ///
    /// Forces UTF-8 (LANG=C.UTF-8) so non-ASCII output renders
    /// correctly across Git Bash / WSL / MSYS2 / Cygwin.
    /// </summary>
    public sealed class BashExecutor : IExecutor
    {
        public string Kind => "bash";

        private const string JsonTagPrefix = "__SCRIPTDECK_JSON__";

        // Canonical Git Bash install paths. Probed when no explicit
        // interpreter is configured AND bare "bash" isn't on PATH.
        // Order matters: 64-bit first, then x86 legacy.
        private static readonly string[] GitBashFallbacks =
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        };

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

            string interpreter = ResolveInterpreter(request);
            if (string.IsNullOrEmpty(interpreter))
            {
                sink.WriteError(
                    "Bash interpreter not found. Configure a path on the workspace " +
                    "(bashInterpreter) or per-button override, or install Git for " +
                    "Windows so bash.exe is reachable." + Environment.NewLine);
                return ExecutionResult.Failed("Bash not found", sw.Elapsed);
            }

            bool wantGrid = request.OutputTargets != null && request.OutputTargets.Contains("grid");
            bool wantRtb  = request.OutputTargets != null && request.OutputTargets.Contains("rtb");

            bool gridColumnsSet = false;
            List<string> gridColumnOrder = null;

            // Latch for the "WSL shim points at a broken distro" case.
            // The shim emits a recognizable error line on stderr; we
            // detect it and post a clearer hint after the process
            // exits so users know what to do about it.
            bool wslRelayFailureSeen = false;

            var psi = new ProcessStartInfo
            {
                FileName  = interpreter,
                Arguments = BuildArguments(request),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WorkingDirectory       = ResolveWorkingDirectory(request),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding  = new UTF8Encoding(false),
            };

            // Force UTF-8 locale so print / echo of non-ASCII strings
            // renders correctly. C.UTF-8 is widely available on
            // Git Bash, WSL, MSYS2, and Cygwin.
            psi.EnvironmentVariables["LANG"]     = "C.UTF-8";
            psi.EnvironmentVariables["LC_ALL"]   = "C.UTF-8";

            // Auto-source scriptdeck_bootstrap.sh (which ships next to
            // ScriptDeck.exe) so user scripts get scriptdeck_path /
            // scriptdeck_to_unix_path / scriptdeck_write_grid_row /
            // etc. for free. Bash sources BASH_ENV when running
            // non-interactively (our subprocess case).
            try
            {
                string bootstrapPath = Path.Combine(
                    AppContext.BaseDirectory, "scriptdeck_bootstrap.sh");
                if (File.Exists(bootstrapPath))
                {
                    // BASH_ENV is consulted as a Windows-style path by
                    // Git Bash + MSYS2 + Cygwin. WSL bash expects a
                    // unix path -- but our bootstrap script itself is
                    // a Windows file and WSL can't see it via /mnt/c
                    // without a translation. For WSL users we leave
                    // BASH_ENV unset; they need to source the helpers
                    // manually from a path that's visible to their
                    // distro.
                    if (!IsWslInvocation(interpreter))
                        psi.EnvironmentVariables["BASH_ENV"] = bootstrapPath;
                }
            }
            catch { /* best effort */ }

            // Shared inputs as env vars (Git Bash sees $computerName,
            // matching cmd / Python). Also publish a JSON dict for
            // the bash bootstrap helpers + the Static-id set for
            // client-side dup-detection in set_shared_input.
            if (request.SharedInputs != null)
            {
                foreach (var kv in request.SharedInputs)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    try { psi.EnvironmentVariables[kv.Key] = kv.Value ?? string.Empty; }
                    catch { /* illegal env-var name -- skip silently */ }
                }
                try
                {
                    psi.EnvironmentVariables["__SCRIPTDECK_INPUTS__"] =
                        JsonConvert.SerializeObject(request.SharedInputs);
                }
                catch { /* best effort */ }
            }
            try
            {
                psi.EnvironmentVariables["__SCRIPTDECK_STATIC_IDS__"] =
                    JsonConvert.SerializeObject(
                        request.StaticInputIds?.ToList() ?? new List<string>());
            }
            catch { /* best effort */ }

            try
            {
                using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        if (e.Data.StartsWith(JsonTagPrefix, StringComparison.Ordinal))
                        {
                            HandleTaggedLine(
                                e.Data.Substring(JsonTagPrefix.Length),
                                sink,
                                wantGrid, wantRtb,
                                ref gridColumnsSet, ref gridColumnOrder);
                        }
                        else if (wantRtb)
                        {
                            sink.WriteOutput(e.Data + Environment.NewLine);
                        }
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        // Detect the WSL shim's "no /bin/bash in distro"
                        // error. The recognizable fragments are
                        // "CreateProcessCommon" (WSL relay internals)
                        // or "execvpe(/bin/bash) failed". Match either.
                        if (!wslRelayFailureSeen &&
                            (e.Data.IndexOf("CreateProcessCommon",   StringComparison.Ordinal) >= 0 ||
                             e.Data.IndexOf("execvpe(/bin/bash)",    StringComparison.Ordinal) >= 0))
                        {
                            wslRelayFailureSeen = true;
                        }
                        sink.WriteError(e.Data + Environment.NewLine);
                    };

                    try
                    {
                        proc.Start();
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        sink.WriteError(
                            "Bash interpreter not launchable: '" + interpreter + "'. " +
                            "(" + ex.Message + ")" + Environment.NewLine);
                        return ExecutionResult.Failed("Interpreter not launchable", sw.Elapsed);
                    }
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    int pid = proc.Id;
                    using (cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                KillProcessTree(pid);
                                try { if (!proc.HasExited) proc.Kill(); } catch { }
                            }
                        }
                        catch { /* best effort */ }
                    }))
                    {
                        await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                        // Second WaitForExit drains buffered Output /
                        // Error events that fired between exit and our
                        // first wait returning. Without this the final
                        // few lines of fast-exiting scripts can vanish.
                        await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        return ExecutionResult.CancelledResult(sw.Elapsed);

                    // Surface the WSL-shim diagnostic AFTER the raw
                    // error so the user can correlate but also see
                    // what to do. Only fires when (a) we detected the
                    // recognizable WSL relay message AND (b) the
                    // interpreter we picked was actually the WSL shim
                    // (avoids false positives for legitimate WSL
                    // overrides where the message might appear for a
                    // different reason).
                    if (wslRelayFailureSeen && IsWslShim(interpreter))
                    {
                        sink.WriteWarning(
                            "[hint] The above error came from the Windows WSL bash shim " +
                            "(" + interpreter + "), which forwards into your default " +
                            "WSL distro -- that distro doesn't appear to have /bin/bash. " +
                            "Install Git for Windows (gives you C:\\Program Files\\Git\\bin\\bash.exe), " +
                            "fix the WSL distro, or set the workspace's bashInterpreter to " +
                            "a specific bash you want to use (e.g. wsl.exe -d Ubuntu bash)." +
                            Environment.NewLine);
                    }

                    return ExecutionResult.Ok(proc.ExitCode, sw.Elapsed);
                }
            }
            catch (Exception ex)
            {
                sink.WriteError($"[bash] {ex.Message}{Environment.NewLine}");
                return ExecutionResult.Failed(ex.Message, sw.Elapsed);
            }
        }

        // Tag-line parser. Same shape as PythonExecutor.HandleTaggedLine;
        // could be extracted into a shared TagLineRouter someday if the
        // pattern proliferates. For now keep them parallel + readable.
        private static void HandleTaggedLine(
            string json,
            IOutputSink sink,
            bool wantGrid, bool wantRtb,
            ref bool gridColumnsSet,
            ref List<string> gridColumnOrder)
        {
            JObject obj;
            try { obj = JObject.Parse(json); }
            catch
            {
                if (wantRtb) sink.WriteOutput(json + Environment.NewLine);
                return;
            }

            var target = obj["__ScriptDeckTarget"]?.ToString()?.ToLowerInvariant();
            if (target == "rtb")
            {
                if (wantRtb)
                {
                    string text = obj["value"]?.ToString() ?? string.Empty;
                    sink.WriteOutput(text + Environment.NewLine);
                }
                return;
            }
            if (target == "grid")
            {
                if (!wantGrid) return;
                var row = obj["row"] as JObject;
                if (row == null) return;
                if (!gridColumnsSet)
                {
                    gridColumnOrder = row.Properties().Select(p => p.Name).ToList();
                    sink.SetColumns(gridColumnOrder);
                    gridColumnsSet = true;
                }
                var cells = gridColumnOrder
                    .Select(c => (object)(row[c]?.ToString() ?? string.Empty))
                    .ToArray();
                sink.AppendRow(cells);
                return;
            }

            if (obj["__ScriptDeckSetSharedInput"]?.Type == JTokenType.Boolean
                && obj["__ScriptDeckSetSharedInput"].ToObject<bool>())
            {
                string id    = obj["id"]?.ToString();
                string value = obj["value"]?.ToString();
                string label = obj["label"]?.ToString();
                SharedInputSetRequested?.Invoke(id, value, label);
                return;
            }
            if (obj["__ScriptDeckRemoveSharedInput"]?.Type == JTokenType.Boolean
                && obj["__ScriptDeckRemoveSharedInput"].ToObject<bool>())
            {
                string id = obj["id"]?.ToString();
                SharedInputRemoveRequested?.Invoke(id);
                return;
            }

            if (wantRtb) sink.WriteOutput(json + Environment.NewLine);
        }

        /// <summary>
        /// Same contract as <see cref="PythonExecutor.SharedInputSetRequested"/>.
        /// </summary>
        public static event Action<string, string, string> SharedInputSetRequested;

        /// <summary>
        /// Same contract as <see cref="PythonExecutor.SharedInputRemoveRequested"/>.
        /// </summary>
        public static event Action<string> SharedInputRemoveRequested;

        // ---- Interpreter / arg / process helpers ----------------------------

        private static string ResolveInterpreter(ExecutionRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.BashInterpreter))
                return req.BashInterpreter.Trim();

            // Canonical Git Bash paths FIRST -- before bare "bash" on
            // PATH. Why: Windows 10/11 with WSL installed adds
            // C:\Windows\System32\bash.exe to PATH, which is a shim
            // that forwards into the default WSL distro. If that
            // distro doesn't have /bin/bash (broken / minimal /
            // never-installed) the shim returns:
            //   "<3>WSL (9 - Relay) ERROR: CreateProcessCommon:800:
            //    execvpe(/bin/bash) failed: No such file or directory"
            // Git Bash is what the vast majority of users actually
            // want when they say "bash". WSL stays accessible via
            // the BashInterpreter override (e.g. "wsl.exe -d Ubuntu
            // bash") for users who specifically want that path.
            foreach (var fb in GitBashFallbacks)
            {
                if (File.Exists(fb)) return fb;
            }

            // Falls through to bare "bash" on PATH -- which catches
            // MSYS2 / Cygwin / a hand-installed bash, and yes the
            // WSL shim if nothing else exists. Better to try the
            // possibly-broken shim than to error out with no
            // interpreter at all.
            string onPath = ResolveExeOnPath("bash.exe") ?? ResolveExeOnPath("bash");
            return string.IsNullOrEmpty(onPath) ? null : onPath;
        }

        private static string ResolveExeOnPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* invalid PATH entry */ }
            }
            return null;
        }

        // Crude detection: if the interpreter is wsl.exe or a "wsl ..."
        // command, BASH_ENV won't reach the bash inside the distro
        // (different filesystem). We skip auto-sourcing in that case.
        // Also true for the System32 bash.exe shim which is a thin
        // WSL forwarder (see IsWslShim).
        private static bool IsWslInvocation(string interpreter)
        {
            return !string.IsNullOrEmpty(interpreter)
                && (interpreter.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase)
                    || interpreter.EndsWith("wsl",   StringComparison.OrdinalIgnoreCase)
                    || IsWslShim(interpreter));
        }

        // True when the path is Windows' own bash.exe shim under
        // System32 -- that file isn't a real bash, it's a launcher
        // that forwards into the default WSL distro. When the distro
        // doesn't have /bin/bash, the shim returns an unmistakable
        // "<3>WSL (9 - Relay) ERROR: CreateProcessCommon" message.
        private static bool IsWslShim(string interpreter)
        {
            if (string.IsNullOrEmpty(interpreter)) return false;
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string shim   = Path.Combine(sysDir, "bash.exe");
                return string.Equals(
                    Path.GetFullPath(interpreter),
                    Path.GetFullPath(shim),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // bash.exe argument list: just the script path then user args.
        // Quoting follows bash's expectations (POSIX-ish single-quote
        // style isn't applicable when bash itself isn't parsing -- we
        // hand args via ProcessStartInfo which builds a Windows
        // command line; bash sees them as $1..$n correctly).
        private static string BuildArguments(ExecutionRequest req)
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(req.ScriptPath).Append('"');
            if (req.Args != null)
            {
                foreach (var a in req.Args)
                {
                    sb.Append(' ').Append(QuoteArg(a ?? string.Empty));
                }
            }
            return sb.ToString();
        }

        private static string QuoteArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            bool needsQuote = arg.Any(c => c == ' ' || c == '\t' || c == '"');
            if (!needsQuote) return arg;
            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
                if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
                if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1);
                else               sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ResolveWorkingDirectory(ExecutionRequest req)
        {
            if (!string.IsNullOrEmpty(req.WorkingDirectory)) return req.WorkingDirectory;
            try
            {
                var full = Path.GetFullPath(req.ScriptPath ?? string.Empty);
                return Path.GetDirectoryName(full) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName  = "taskkill.exe",
                    Arguments = "/T /F /PID " + pid.ToString(CultureInfo.InvariantCulture),
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using (var killer = Process.Start(psi))
                {
                    if (killer != null && !killer.WaitForExit(2000))
                    {
                        try { killer.Kill(); } catch { }
                    }
                }
            }
            catch { /* best effort */ }
        }
    }
}
