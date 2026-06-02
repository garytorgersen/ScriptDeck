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
    /// Runs Python scripts via a configurable interpreter (<c>python</c> on
    /// PATH by default; per-button or per-workspace override possible).
    /// Like <see cref="CmdExecutor"/> this is one-shot-per-click: each run
    /// spawns a fresh <c>python.exe</c> process and waits for it to exit.
    ///
    /// Output handling: stdout lines prefixed with the sentinel
    /// <c>__SCRIPTDECK_JSON__</c> are parsed as routing tags (grid rows,
    /// shared-input mutations, RTB-only emissions) -- mirroring the
    /// PowerShell bootstrap helpers' <c>__ScriptDeckTarget</c> /
    /// <c>__ScriptDeckSetSharedInput</c> tag protocol but over plain
    /// stdout instead of PSObject properties. Untagged stdout lines go
    /// to the console RTB as-is; stderr always goes to the sink's Error
    /// stream.
    ///
    /// The interpreter resolution precedence is:
    ///   1. Per-button override (<c>Button.PythonInterpreter</c>)
    ///   2. Workspace default (<c>Workspace.PythonInterpreter</c>)
    ///   3. Bare <c>python</c> (resolved via the user's PATH)
    /// The dispatcher applies that precedence and stamps the chosen value
    /// on <see cref="ExecutionRequest.PythonInterpreter"/> before we see
    /// the request. If it's null/empty here, we fall through to PATH.
    /// </summary>
    public sealed class PythonExecutor : IExecutor
    {
        public string Kind => "python";

        // Per the contract with scriptdeck_bootstrap.py. Stable string;
        // any change here MUST be reflected in the bootstrap module.
        private const string JsonTagPrefix = "__SCRIPTDECK_JSON__";

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

            string interpreter = string.IsNullOrWhiteSpace(request.PythonInterpreter)
                ? "python"
                : request.PythonInterpreter.Trim();

            // Wire output routing. Whether the grid is wanted decides
            // if we honor grid-targeted JSON tags or downgrade them to
            // RTB (mirrors PowerShell's wantGrid behavior).
            bool wantGrid = request.OutputTargets != null && request.OutputTargets.Contains("grid");
            bool wantRtb  = request.OutputTargets != null && request.OutputTargets.Contains("rtb");

            bool gridColumnsSet = false;
            List<string> gridColumnOrder = null; // pinned by the first grid record

            var psi = new ProcessStartInfo
            {
                FileName  = interpreter,
                Arguments = BuildArguments(request),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WorkingDirectory       = ResolveWorkingDirectory(request),
                // Force UTF-8 on both ends. Without this, Windows
                // defaults Python's stdout encoding to whatever the
                // active code page is (cp1252 on en-US), which corrupts
                // non-ASCII print() output. PYTHONIOENCODING is the
                // documented way to force a particular encoding.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding  = new UTF8Encoding(false),
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            // Make the bootstrap module importable without requiring the
            // user to fiddle with sys.path. The module lives next to
            // ScriptDeck.exe; we prepend that directory to PYTHONPATH so
            // `import scriptdeck_bootstrap` resolves cleanly. Prepend so
            // we win over an unrelated user-installed `scriptdeck_bootstrap`
            // package if one existed.
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string existingPath = psi.EnvironmentVariables.ContainsKey("PYTHONPATH")
                    ? psi.EnvironmentVariables["PYTHONPATH"]
                    : null;
                psi.EnvironmentVariables["PYTHONPATH"] = string.IsNullOrEmpty(existingPath)
                    ? baseDir
                    : baseDir + Path.PathSeparator + existingPath;
            }
            catch { /* best effort -- a borked env shouldn't block execution */ }

            // Shared-input injection as env vars. cmd executor uses the
            // exact same mechanism; Python scripts can read these via
            // os.environ['computerName'] etc. We also expose them via
            // the bootstrap module's `inputs` dict for convenience.
            if (request.SharedInputs != null)
            {
                foreach (var kv in request.SharedInputs)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    try { psi.EnvironmentVariables[kv.Key] = kv.Value ?? string.Empty; }
                    catch { /* invalid env-var name -- skip silently */ }
                }
                // Also publish the full input set as a single JSON-encoded
                // env var so the bootstrap module can present it as a
                // typed dict without re-walking os.environ. Capped at
                // a generous size since env vars on Windows can hit a
                // 32k-per-var limit; we let it throw if exceeded.
                try
                {
                    psi.EnvironmentVariables["__SCRIPTDECK_INPUTS__"] =
                        JsonConvert.SerializeObject(request.SharedInputs);
                }
                catch { /* best effort */ }
            }

            // Publish the Static/Volatile metadata so the bootstrap
            // module's set_shared_input() can refuse to shadow a Static
            // id from inside a script (same client-side check the
            // PowerShell bootstrap does via $ScriptDeckInputs).
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
                        // Tag detection: __SCRIPTDECK_JSON__ prefix means
                        // a structured routing event from the bootstrap
                        // helpers. Anything else is plain print() output
                        // that goes to the console RTB.
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
                        if (e.Data != null) sink.WriteError(e.Data + Environment.NewLine);
                    };

                    try
                    {
                        proc.Start();
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        // ENOENT (interpreter not found on PATH / invalid
                        // override path) surfaces here as a Win32Exception.
                        // Give a clear, actionable message instead of the
                        // raw "system cannot find the file specified".
                        sink.WriteError(
                            "Python interpreter not found: '" + interpreter + "'. " +
                            "Set 'python' on PATH, or specify a full path in the button's " +
                            "Python interpreter field. (" + ex.Message + ")" +
                            Environment.NewLine);
                        return ExecutionResult.Failed("Interpreter not found", sw.Elapsed);
                    }
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    int pyPid = proc.Id;
                    using (cancellationToken.Register(() =>
                    {
                        // Kill the whole process tree -- a Python script
                        // that spawned subprocess.Popen() leaves orphans
                        // if we only kill the immediate python.exe.
                        try
                        {
                            if (!proc.HasExited)
                            {
                                KillProcessTree(pyPid);
                                try { if (!proc.HasExited) proc.Kill(); } catch { }
                            }
                        }
                        catch { /* best effort */ }
                    }))
                    {
                        await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                        // Second WaitForExit drains any buffered
                        // OutputDataReceived events still in flight when
                        // the process exited. Without it the last few
                        // lines of fast-exiting scripts vanish silently.
                        await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        return ExecutionResult.CancelledResult(sw.Elapsed);

                    return ExecutionResult.Ok(proc.ExitCode, sw.Elapsed);
                }
            }
            catch (Exception ex)
            {
                sink.WriteError($"[python] {ex.Message}{Environment.NewLine}");
                return ExecutionResult.Failed(ex.Message, sw.Elapsed);
            }
        }

        // Parse and route a __SCRIPTDECK_JSON__-tagged line. Each line is
        // a self-contained JSON object describing one event. The four
        // recognized shapes are:
        //
        //   { "__ScriptDeckTarget": "rtb",  "value": "..." }
        //     -> plain text to the RTB; no grid routing
        //   { "__ScriptDeckTarget": "grid", "row": { "col1": "v1", ... } }
        //     -> append one row; first row pins the column set
        //   { "__ScriptDeckSetSharedInput":    true, "id": "...", "value": "...", "label": "..." }
        //   { "__ScriptDeckRemoveSharedInput": true, "id": "..." }
        //     -> session-input mutations; the executor's host (Shell)
        //        is responsible for surfacing these as it does for
        //        PowerShell's tag events. We currently fire them
        //        through the sink as Info lines; a richer integration
        //        can subscribe to dedicated events if a callsite wants.
        //
        // Anything we can't parse (malformed JSON, unknown shape) is
        // surfaced to the RTB verbatim -- losing data quietly would be
        // worse than displaying ugly text.
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

            // Routing-target tag (Write-Rtb / Write-Grid equivalents).
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

                // Pin column order from the first row. Subsequent rows
                // align to those columns; new keys are dropped, missing
                // keys become blank cells. Mirrors the PowerShell
                // grid-shape behavior (first record fixes the schema).
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

            // Session-input mutation tags. The Shell wires PythonExecutor
            // up alongside its PowerShell counterparts so these events
            // should fire through the same Shell-side handlers. For now,
            // surface as Info lines so the user can see something
            // happened even if richer plumbing isn't wired yet.
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

            // Unknown shape: dump the JSON verbatim so we don't silently
            // lose the user's output.
            if (wantRtb) sink.WriteOutput(json + Environment.NewLine);
        }

        /// <summary>
        /// Fires when a Python script calls scriptdeck_bootstrap's
        /// set_shared_input(). Subscribers (Shell) marshal to the UI
        /// thread and apply the change to the live session-input dict.
        /// Same contract as <see cref="PowerShellExecutor.SharedInputSetRequested"/>.
        /// </summary>
        public static event Action<string, string, string> SharedInputSetRequested;

        /// <summary>
        /// Fires when a Python script calls scriptdeck_bootstrap's
        /// remove_shared_input(). Same contract as the PowerShell-side
        /// event.
        /// </summary>
        public static event Action<string> SharedInputRemoveRequested;

        // ---- Argument building / process management -------------------------

        // python.exe argument list: just the script path then the user
        // args. We DON'T pass -u (unbuffered) because BeginOutputReadLine
        // is line-buffered anyway; we DO ensure utf-8 via env var above.
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

        // Standard MSVCRT-compatible quoting (which is what python.exe
        // and most native Windows exes parse with). Wrap in quotes if
        // the arg has whitespace or quotes; escape embedded quotes and
        // any backslashes that immediately precede them.
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
                if (i == arg.Length)
                {
                    // Trailing backslashes get doubled because the
                    // closing quote will look like an escape otherwise.
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
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
            catch
            {
                return string.Empty;
            }
        }

        // Same taskkill /T /F dance CmdExecutor uses. .NET 4.8 lacks
        // Process.Kill(true) so we shell out for the tree-walking
        // semantics.
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
