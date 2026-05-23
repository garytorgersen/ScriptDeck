using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Runs PowerShell 5.1 scripts (.ps1) inside a long-lived runspace.
    ///
    /// Lifecycle: one runspace per executor instance, opened in the
    /// constructor, reused across every button click, closed on Dispose.
    /// Reusing the runspace means scripts can rely on shared module
    /// imports / variables across invocations -- expensive imports
    /// (Az, ActiveDirectory, etc.) pay the load cost once at first
    /// use and stay warm for the rest of the session.
    ///
    /// Output routing follows the request's <see cref="ExecutionRequest.OutputTargets"/>:
    ///   "rtb"  — every PSObject is ToString'd and written to the console
    ///   "grid" — structured PSObjects (non-primitive BaseObject) populate
    ///            the grid using property names as columns
    /// Both can be combined; primitives (strings/ints/etc) only ever land
    /// in the RTB regardless of the "grid" flag.
    /// </summary>
    public sealed class PowerShellExecutor : IExecutor, IDisposable
    {
        /// <summary>
        /// Cap on records buffered for table/json/csv RTB rendering. The
        /// grid still gets every record; this only bounds in-memory text
        /// retention so a script emitting millions of objects doesn't
        /// blow up the RTB. 2000 is the legacy default; raise for huge
        /// reports, lower for tighter memory limits.
        /// </summary>
        public static int MaxBufferedRtbRecords { get; set; } = 2000;

        // _runspace is replaced wholesale by Reset() so loading a new
        // workspace can wipe globals/modules/dot-sourced helpers from
        // the previous one. Guarded by _gate to keep the swap atomic
        // with respect to in-flight pipelines.
        private Runspace _runspace;
        private readonly object _gate = new object();
        private PowerShell _current;
        private bool _disposed;

        public string Kind => "powershell";

        // Events fired when a script invokes the Set-SharedInput /
        // Remove-SharedInput bootstrap helpers. The helpers emit
        // PSObjects with magic __ScriptDeck* properties; the DataAdded
        // handler intercepts those and routes here instead of letting
        // them flow into the normal output streams. Shell subscribes
        // to these to mutate its session-input dictionary. Handlers
        // run on the executor's background thread; subscribers must
        // marshal to the UI thread themselves.
        public event Action<string, string, string> SharedInputSetRequested;
        public event Action<string> SharedInputRemoveRequested;

        public PowerShellExecutor()
        {
            OpenFreshRunspace();
        }

        // Open a brand-new runspace and dot-source the bootstrap into
        // it. Called from the constructor and from Reset() -- the work
        // is identical either way.
        private void OpenFreshRunspace()
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            // STA so scripts that touch COM/WinForms/WPF objects work.
            // We don't use UseCurrentThread -- ScriptDeck runs on
            // background tasks and PS gets its own STA worker thread.
            iss.ApartmentState = ApartmentState.STA;
            _runspace = RunspaceFactory.CreateRunspace(iss);
            _runspace.Open();

            // Dot-source the bootstrap script (if it ships next to the
            // exe) so helpers like Test-IsLocalTarget are available in
            // every script the user runs. Best-effort: a missing or
            // broken bootstrap shouldn't take down the runspace — we
            // just lose the helpers, and scripts can still run.
            // Reset the latch so Reset() re-surfaces any new bootstrap
            // warnings on the first invocation in the new runspace.
            _bootstrapWarning = null;
            _bootstrapWarningSurfaced = false;
            TryRunBootstrap();
        }

        /// <summary>
        /// Tear down the current runspace and open a fresh one. Used
        /// when loading a new workspace so globals, imported modules,
        /// and any `$global:` state from the previous workspace's
        /// scripts are wiped. The bootstrap is reloaded into the new
        /// runspace so any edits the user made between sessions take
        /// effect.
        ///
        /// Cancels any in-flight pipeline (no graceful shutdown -- a
        /// workspace switch is an explicit user action and the user
        /// has already accepted "drop everything"). Safe to call
        /// repeatedly; safe to call when the executor is idle.
        /// </summary>
        public void Reset()
        {
            if (_disposed) return;
            lock (_gate)
            {
                // Stop and discard any in-flight pipeline so the runspace
                // disposal below doesn't fight a live Invoke.
                try { _current?.Stop(); } catch { /* best effort */ }
                _current = null;

                try { _runspace?.Close(); } catch { /* best effort */ }
                try { _runspace?.Dispose(); } catch { /* best effort */ }
                _runspace = null;

                OpenFreshRunspace();
            }
        }

        // Captured bootstrap-load problem, if any. Stamped here at runspace
        // open and surfaced to the sink on the FIRST script invocation
        // -- by then there's a real sink to report to. Without this,
        // a syntax error in ScriptDeck.Bootstrap.ps1 silently disables
        // every helper for the whole session and users see only cryptic
        // "Test-IsLocalTarget : The term ... is not recognized" errors.
        private string _bootstrapWarning;
        private bool _bootstrapWarningSurfaced;

        private void TryRunBootstrap()
        {
            // The exe folder is the canonical "next to ScriptDeck.exe"
            // location. AppContext.BaseDirectory is friendlier than
            // AppDomain on .NET Framework (consistent across hosts).
            string baseDir = AppContext.BaseDirectory;
            string bootstrapPath = Path.Combine(baseDir, "ScriptDeck.Bootstrap.ps1");
            if (!File.Exists(bootstrapPath))
            {
                _bootstrapWarning =
                    "ScriptDeck.Bootstrap.ps1 not found next to ScriptDeck.exe. " +
                    "Helpers like Test-IsLocalTarget / Write-Rtb / Write-Grid won't be available.";
                return;
            }

            try
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    // The leading "." (dot-source) loads functions into
                    // the runspace's global scope, which is what we want
                    // — every subsequent script sees them.
                    ps.AddScript(". '" + bootstrapPath.Replace("'", "''") + "'");
                    ps.Invoke();

                    // The dot-source itself doesn't throw on PowerShell
                    // syntax errors -- they accumulate in Streams.Error.
                    // Capture them so users get a clear message instead
                    // of mysterious "command not recognized" later.
                    if (ps.HadErrors && ps.Streams.Error.Count > 0)
                    {
                        var first = ps.Streams.Error[0];
                        _bootstrapWarning =
                            "ScriptDeck.Bootstrap.ps1 reported errors loading: " +
                            first.ToString() + " Helpers may not be available.";
                    }
                }
            }
            catch (Exception ex)
            {
                _bootstrapWarning =
                    "Failed to load ScriptDeck.Bootstrap.ps1: " + ex.Message +
                    ". Helpers (Test-IsLocalTarget, Write-Rtb, Write-Grid) won't be available.";
            }
        }

        // Emit any deferred bootstrap warning to the FIRST sink we see.
        // We can't dump it during construction (no sink yet) and we don't
        // want to surface it again on every invocation, hence the latch.
        private void SurfaceBootstrapWarningOnce(IOutputSink sink)
        {
            if (_bootstrapWarningSurfaced || _bootstrapWarning == null) return;
            _bootstrapWarningSurfaced = true;
            try { sink.WriteWarning("[bootstrap] " + _bootstrapWarning + Environment.NewLine); }
            catch { /* sink can't accept warnings? give up */ }
        }

        public Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request, IOutputSink sink, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            return Task.Run(() => RunInternal(request, sink, cancellationToken), cancellationToken);
        }

        private ExecutionResult RunInternal(ExecutionRequest req, IOutputSink sink, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            // First-invocation latch: surface any deferred bootstrap-load
            // warning so users see a real diagnostic (instead of cryptic
            // "command not recognized" later when a script calls into a
            // helper that didn't load).
            SurfaceBootstrapWarningOnce(sink);

            if (string.IsNullOrEmpty(req.ScriptPath) || !File.Exists(req.ScriptPath))
            {
                sink.WriteError($"PowerShell script not found: {req.ScriptPath}{Environment.NewLine}");
                return ExecutionResult.Failed("Script not found", sw.Elapsed);
            }

            // Publish shared-input values as runspace variables BEFORE
            // creating the pipeline. Scripts can then reference them as
            // bare $variables (e.g. $computerName) without a param() block.
            // Set on the runspace's session state proxy so they live at
            // global scope and persist across this invocation; the next
            // run overwrites them with that click's values.
            //
            // Names that aren't valid PowerShell identifiers (or that
            // collide with engine-reserved names) are skipped with a
            // single warning to the sink — better to log once and keep
            // running than to fail every dispatch on a misnamed input.
            ApplySharedInputVariables(req.SharedInputs, sink);
            // Publish the static/volatile metadata so bootstrap helpers
            // (Set-SharedInput / Remove-SharedInput) can do client-side
            // duplicate detection without round-tripping to Shell. The
            // hashtable is rebuilt every dispatch -- it reflects the
            // CURRENT inputs, not a frozen snapshot.
            PublishScriptDeckInputsMetadata(req.SharedInputs, req.StaticInputIds);

            var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // Use the file path as a command — PS treats .ps1 paths as
            // invocable commands, and the PSCommand API handles quoting
            // for us so we never build a string-cat'd command line.
            ps.AddCommand(req.ScriptPath);

            // Args are a flat list, but PS distinguishes positional args
            // (AddArgument) from named parameters (AddParameter). The
            // workspace JSON lets users write `["-ComputerName", "."]`
            // expecting a name+value pair — so we parse `-Name Value`
            // pairs here and route them correctly. Without this, the
            // first AddArgument binds the literal "-ComputerName" as the
            // first positional value of $ComputerName, and the next arg
            // errors with "no positional parameter accepts 'localhost'".
            var argsList = req.Args ?? new List<string>();
            for (int i = 0; i < argsList.Count; i++)
            {
                var a = argsList[i] ?? string.Empty;
                if (LooksLikeParameterName(a))
                {
                    // Strip the leading dash; PS expects the bare name.
                    var name = a.Substring(1);
                    // -Switch (no value): the next arg is also a parameter
                    // name, OR there is no next arg. In both cases bind as
                    // a switch with no value.
                    if (i + 1 < argsList.Count && !LooksLikeParameterName(argsList[i + 1]))
                    {
                        ps.AddParameter(name, argsList[i + 1] ?? string.Empty);
                        i++;
                    }
                    else
                    {
                        ps.AddParameter(name);
                    }
                }
                else
                {
                    ps.AddArgument(a);
                }
            }

            bool wantRtb  = req.OutputTargets != null && req.OutputTargets.Contains("rtb");
            bool wantGrid = req.OutputTargets != null && req.OutputTargets.Contains("grid");

            // RTB rendering format. "default" / "list" stream as records
            // arrive; "table" / "json" / "csv" buffer until the script
            // completes (column widths and document structure need the
            // full record set). Cap at MaxBufferedRtbRecords to keep the
            // RTB readable; anything bigger should flow through the grid.
            string rtbFormat = (req.RtbFormat ?? "default").Trim().ToLowerInvariant();
            bool bufferRtb = wantRtb && (rtbFormat == "table" || rtbFormat == "json" || rtbFormat == "csv");
            int maxBufferedRtbRecords = MaxBufferedRtbRecords;
            var rtbBuffer = bufferRtb ? new List<PSObject>(capacity: 64) : null;
            bool rtbTruncated = false;

            // Output stream gets special handling: structured records
            // populate the grid; primitives go to RTB only. (There used
            // to be an "Extended Grid Data" toggle that also fed
            // primitives into a single-"Value" grid column AND retained
            // PS* engine metadata as columns -- removed in favor of the
            // cleaner default behavior, since the toggle couldn't actually
            // expand properties past a script-side Select-Object. Scripts
            // that want full-property grid output should emit unprojected
            // objects via Write-Grid and route the curated view to the
            // RTB via Write-Rtb.)
            var output = new PSDataCollection<PSObject>();
            bool gridColumnsSet = false;

            output.DataAdded += (_, ev) =>
            {
                var obj = output[ev.Index];
                if (obj == null) return;

                // Session-input mutation tags (set by Set-SharedInput /
                // Remove-SharedInput bootstrap helpers). These NEVER
                // render -- we intercept, fire the event, and return.
                // Tagged objects don't reach the RTB, the grid, or any
                // format helper.
                if (TryHandleSharedInputTag(obj)) return;

                bool isStructured = IsStructured(obj);

                // Explicit per-object routing tag (set by Write-Rtb /
                // Write-Grid in the bootstrap module). Untagged objects
                // keep the existing both-destinations behavior; tagged
                // ones go to the chosen destination only.
                string routeTag = (obj.Properties["__ScriptDeckTarget"]?.Value as string)?.ToLowerInvariant();
                bool routeRtbOnly  = routeTag == "rtb";
                bool routeGridOnly = routeTag == "grid";
                bool sendToGrid = wantGrid && !routeRtbOnly;
                bool sendToRtb  = wantRtb  && !routeGridOnly;

                if (sendToGrid && isStructured)
                {
                    // Curated user-property view: PS* engine metadata and
                    // __* routing tags filtered out. This is the only
                    // grid-render path now.
                    var props = GetUserProperties(obj);
                    if (!gridColumnsSet && props.Count > 0)
                    {
                        sink.SetColumns(props.Select(p => p.Name).ToList());
                        gridColumnsSet = true;
                    }
                    if (gridColumnsSet)
                        sink.AppendRow(props.Select(p => SafeReadProperty(p)).ToArray());
                }

                if (!sendToRtb) return;

                if (bufferRtb)
                {
                    // Primitives can't contribute to a table's columns or
                    // a json document's record set, so buffering them
                    // would just lose them at flush time. Stream them
                    // inline instead -- a Write-Output "hello" still
                    // shows up in the RTB even when the surrounding
                    // structured records are deferred for table/json
                    // rendering.
                    if (!isStructured)
                    {
                        sink.WriteOutput((obj.BaseObject?.ToString() ?? string.Empty) + Environment.NewLine);
                    }
                    else if (rtbBuffer.Count < maxBufferedRtbRecords)
                    {
                        rtbBuffer.Add(obj);
                    }
                    else
                    {
                        rtbTruncated = true;
                    }
                }
                else if (rtbFormat == "list")
                {
                    sink.WriteOutput(FormatAsList(obj));
                }
                else
                {
                    sink.WriteOutput(obj.ToString() + Environment.NewLine);
                }
            };

            // Other streams: route by severity to the matching sink color.
            ps.Streams.Information.DataAdded += (_, ev) =>
                sink.WriteInfo(ps.Streams.Information[ev.Index].MessageData?.ToString() + Environment.NewLine);
            ps.Streams.Warning.DataAdded += (_, ev) =>
                sink.WriteWarning(ps.Streams.Warning[ev.Index].Message + Environment.NewLine);
            ps.Streams.Error.DataAdded += (_, ev) =>
                sink.WriteError(ps.Streams.Error[ev.Index].ToString() + Environment.NewLine);
            ps.Streams.Verbose.DataAdded += (_, ev) =>
                sink.WriteVerbose(ps.Streams.Verbose[ev.Index].Message + Environment.NewLine);
            ps.Streams.Debug.DataAdded += (_, ev) =>
                sink.WriteDebug(ps.Streams.Debug[ev.Index].Message + Environment.NewLine);

            lock (_gate) { _current = ps; }

            try
            {
                using (ct.Register(() => { try { ps.Stop(); } catch { /* best effort */ } }))
                {
                    try
                    {
                        // Single-generic overload: Invoke<TOut>(IEnumerable, PSDataCollection<TOut>).
                        // The two-generic version requires PSInvocationSettings, which we
                        // don't need — defaults are fine for a one-shot synchronous run.
                        ps.Invoke<PSObject>(null, output);
                    }
                    catch (PipelineStoppedException)
                    {
                        // Expected when ct.Cancel triggered ps.Stop.
                        if (ct.IsCancellationRequested)
                            return ExecutionResult.CancelledResult(sw.Elapsed);
                        throw;
                    }
                }
                // Non-terminating errors don't throw; they accumulate in
                // ps.Streams.Error and surface via DataAdded above. The
                // exit code we report is 0 for "ran to completion" — let
                // the user judge severity from the streamed errors.

                // Flush any buffered RTB output. Table/Json/Csv formats
                // need the full record set before they can render
                // (column widths, document closure, header derivation),
                // so we held them aside in DataAdded — emit the result
                // here in one shot.
                if (bufferRtb && rtbBuffer.Count > 0)
                {
                    string rendered;
                    switch (rtbFormat)
                    {
                        case "table": rendered = FormatAsTable(rtbBuffer); break;
                        case "json":  rendered = FormatAsJson(rtbBuffer);  break;
                        case "csv":   rendered = FormatAsCsv(rtbBuffer);   break;
                        default:      rendered = string.Empty;             break;
                    }
                    if (!string.IsNullOrEmpty(rendered)) sink.WriteOutput(rendered);
                    if (rtbTruncated)
                        sink.WriteWarning(
                            $"(rtb output truncated to first {maxBufferedRtbRecords} record(s); use the grid for the full set){Environment.NewLine}");
                }

                return ExecutionResult.Ok(0, sw.Elapsed);
            }
            catch (Exception ex)
            {
                sink.WriteError($"[powershell] {ex.Message}{Environment.NewLine}");
                return ExecutionResult.Failed(ex.Message, sw.Elapsed);
            }
            finally
            {
                lock (_gate) { if (_current == ps) _current = null; }
                ps.Dispose();
            }
        }

        // ---- shared-input injection ----

        /// <summary>
        /// PowerShell engine names that scripts already rely on, plus
        /// preference variables that misbehave when blindly overwritten
        /// with a string. We refuse to inject under any of these names —
        /// the user's "Host" textbox shouldn't clobber <c>$Host</c>.
        ///
        /// Comparison is case-insensitive (PowerShell variable names are).
        /// </summary>
        private static readonly HashSet<string> ReservedVariableNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "_", "args", "ConsoleFileName", "Error", "ErrorActionPreference",
                "Event", "EventArgs", "EventSubscriber", "ExecutionContext",
                "false", "ForEach", "Home", "Host", "Input", "LASTEXITCODE",
                "Matches", "MyInvocation", "NestedPromptLevel", "null",
                "OutputEncoding", "PID", "PROFILE", "PSBoundParameters",
                "PSCmdlet", "PSCommandPath", "PSCulture", "PSDebugContext",
                "PSHOME", "PSItem", "PSScriptRoot", "PSSenderInfo",
                "PSUICulture", "PSVersionTable", "PWD", "Sender",
                "ShellId", "StackTrace", "this", "true",
                "VerbosePreference", "WarningPreference", "DebugPreference",
                "WhatIfPreference", "ConfirmPreference",
                "InformationPreference", "ProgressPreference",
            };

        // Pattern for valid PowerShell variable identifiers. PS allows
        // letters, digits, underscores; first char must be letter or
        // underscore. Variables CAN have other chars when wrapped in
        // braces (`${weird name}`) but we don't try to support that —
        // shared-input ids should be ordinary identifiers.
        private static readonly System.Text.RegularExpressions.Regex
            ValidIdentifier = new System.Text.RegularExpressions.Regex(
                @"^[A-Za-z_][A-Za-z0-9_]*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Track which bad names we've already complained about so the
        // user gets one warning per name per session, not one per click.
        // Keyed on the raw input id; concurrent access is guarded by
        // _gate (already used elsewhere in this class).
        private readonly HashSet<string> _warnedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void ApplySharedInputVariables(
            IDictionary<string, string> inputs, IOutputSink sink)
        {
            if (inputs == null || inputs.Count == 0) return;
            var proxy = _runspace.SessionStateProxy;
            foreach (var kv in inputs)
            {
                string name = kv.Key;
                string value = kv.Value ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                if (!ValidIdentifier.IsMatch(name))
                {
                    WarnOnce(sink, name,
                        $"Shared input '{name}' is not a valid PowerShell variable name; skipping injection.");
                    continue;
                }
                if (ReservedVariableNames.Contains(name))
                {
                    WarnOnce(sink, name,
                        $"Shared input '{name}' collides with a reserved PowerShell name; skipping injection.");
                    continue;
                }

                try { proxy.SetVariable(name, value); }
                catch (Exception ex)
                {
                    WarnOnce(sink, name,
                        $"Could not publish shared input '{name}' as a variable: {ex.Message}");
                }
            }
        }

        // Inject a global $ScriptDeckInputs hashtable into the runspace
        // so bootstrap helpers can answer "is this id Static?" without
        // round-tripping to Shell. Shape:
        //   @{
        //       Static   = @('computerName', 'companyName')
        //       Volatile = @('authToken')
        //   }
        // Rebuilt each dispatch. Static = ids that came from workspace JSON;
        // Volatile = everything else (script-set + user-added at runtime).
        // Caller must have already populated $proxy with the variable
        // values themselves -- this only adds the metadata index.
        private void PublishScriptDeckInputsMetadata(
            System.Collections.Generic.IDictionary<string, string> allInputs,
            System.Collections.Generic.ISet<string> staticIds)
        {
            try
            {
                var staticList   = new System.Collections.ArrayList();
                var volatileList = new System.Collections.ArrayList();
                if (allInputs != null)
                {
                    foreach (var id in allInputs.Keys)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        if (staticIds != null && staticIds.Contains(id))
                            staticList.Add(id);
                        else
                            volatileList.Add(id);
                    }
                }
                var meta = new System.Collections.Hashtable(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    { "Static",   staticList.ToArray() },
                    { "Volatile", volatileList.ToArray() },
                };
                _runspace.SessionStateProxy.SetVariable("ScriptDeckInputs", meta);
            }
            catch
            {
                // Metadata is a convenience -- if we can't publish it,
                // the bootstrap helpers fall back to "tag and let Shell
                // sort it out" semantics (a Set on a Static id still
                // gets rejected, just with a less direct error).
            }
        }

        private void WarnOnce(IOutputSink sink, string key, string message)
        {
            lock (_gate)
            {
                if (!_warnedNames.Add(key)) return;
            }
            try { sink.WriteWarning(message + Environment.NewLine); }
            catch { /* sink errors aren't worth crashing on */ }
        }

        // Check if `obj` is a tagged session-input mutation. If yes,
        // dispatch the event and return true (caller should skip all
        // other rendering). Returns false for plain output objects.
        //
        // The contract with the bootstrap helpers:
        //   Set-SharedInput emits:
        //     PSObject { __ScriptDeckSetSharedInput=$true; Id=...; Value=...; Label=... }
        //   Remove-SharedInput emits:
        //     PSObject { __ScriptDeckRemoveSharedInput=$true; Id=... }
        // The booleans are sentinels so an unrelated PSCustomObject that
        // happens to have an Id property doesn't get misrouted.
        private bool TryHandleSharedInputTag(System.Management.Automation.PSObject obj)
        {
            try
            {
                var setSentinel = obj.Properties["__ScriptDeckSetSharedInput"]?.Value;
                if (setSentinel is bool b && b)
                {
                    string id    = obj.Properties["Id"]?.Value as string;
                    string value = obj.Properties["Value"]?.Value?.ToString();
                    string label = obj.Properties["Label"]?.Value as string;
                    if (!string.IsNullOrEmpty(id))
                    {
                        try { SharedInputSetRequested?.Invoke(id, value ?? string.Empty, label); }
                        catch { /* listener exceptions don't escape the executor */ }
                    }
                    return true;
                }

                var removeSentinel = obj.Properties["__ScriptDeckRemoveSharedInput"]?.Value;
                if (removeSentinel is bool rb && rb)
                {
                    string id = obj.Properties["Id"]?.Value as string;
                    if (!string.IsNullOrEmpty(id))
                    {
                        try { SharedInputRemoveRequested?.Invoke(id); }
                        catch { /* listener exceptions don't escape the executor */ }
                    }
                    return true;
                }
            }
            catch
            {
                // A misshapen tagged object shouldn't break the pipeline.
                // Fall through and let normal output handling deal with it.
            }
            return false;
        }

        // ---- helpers ----

        /// <summary>
        /// Heuristic: does this arg look like a PowerShell parameter name
        /// (e.g. "-ComputerName")? We require leading dash + a letter or
        /// underscore so values like "-1" or "-" are treated as bare
        /// positional args.
        ///
        /// PowerShell-style long options use a single dash. Bash-style
        /// "--Name" is intentionally NOT detected — it would be passed
        /// through as a positional, which is what someone explicitly
        /// typing "--" presumably wants.
        /// </summary>
        private static bool LooksLikeParameterName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length < 2) return false;
            if (s[0] != '-') return false;
            return char.IsLetter(s[1]) || s[1] == '_';
        }

        private static bool IsStructured(PSObject obj)
        {
            var b = obj.BaseObject;
            if (b == null) return false;
            if (b is string) return false;
            var t = b.GetType();
            // Treat primitives + DateTime + decimal as scalar values.
            if (t.IsPrimitive) return false;
            if (t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid)) return false;
            return true;
        }

        /// <summary>
        /// Properties minus the PS-engine-injected metadata (PSChildName,
        /// PSPath, PSDrive, PSParentPath, PSProvider) and ScriptDeck's
        /// internal routing tags (the `__`-prefixed properties added by
        /// Write-Rtb / Write-Grid). Those clutter the grid for
        /// filesystem-style outputs (Get-ChildItem, etc.) and have no
        /// value to a casual viewer.
        /// </summary>
        private static IList<PSPropertyInfo> GetUserProperties(PSObject obj)
        {
            return obj.Properties
                .Where(p => !p.Name.StartsWith("PS", StringComparison.Ordinal))
                .Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>
        /// Reads a property's value defensively. Some PSPropertyInfo
        /// kinds (PSScriptProperty backed by a getter that throws,
        /// CodeProperty over a stale type) can throw on .Value — and in
        /// extended mode we surface lots of them, so a single bad
        /// property mustn't poison the whole row. Failures render as
        /// "(error: ...)" in the cell so the user can still see the
        /// shape of the data.
        /// </summary>
        private static object SafeReadProperty(PSPropertyInfo p)
        {
            try { return FormatCell(p.Value); }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        // ---- RTB format helpers ----
        // Each takes one (FormatAsList) or many (FormatAsTable/Json/Csv)
        // PSObjects and returns a string ready to dump into the RTB.
        // None of these throw — bad properties become empty cells, invalid
        // serialization paths fall back to ToString — so a misbehaving
        // record never poisons the rest of the output.

        private static string FormatAsList(PSObject obj)
        {
            if (!IsStructured(obj))
                return (obj.BaseObject?.ToString() ?? string.Empty) + Environment.NewLine;

            var props = GetUserProperties(obj);
            if (props.Count == 0) return obj.ToString() + Environment.NewLine;

            int padTo = 0;
            foreach (var p in props) if (p.Name.Length > padTo) padTo = p.Name.Length;

            var sb = new System.Text.StringBuilder(props.Count * 32);
            foreach (var p in props)
            {
                sb.Append(p.Name.PadRight(padTo));
                sb.Append(" : ");
                var v = SafeReadProperty(p);
                sb.AppendLine(v?.ToString() ?? string.Empty);
            }
            sb.AppendLine(); // blank line between records, like Format-List
            return sb.ToString();
        }

        // Compute the union of property names across the buffered records
        // in first-seen order. Preserves the natural reading order from
        // the script (first object's properties stay leftmost). Empty
        // when the records are all primitives.
        private static List<string> ColumnUnion(IList<PSObject> objs)
        {
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in objs)
            {
                if (o == null || !IsStructured(o)) continue;
                foreach (var p in GetUserProperties(o))
                    if (seen.Add(p.Name)) columns.Add(p.Name);
            }
            return columns;
        }

        private static string FormatAsTable(IList<PSObject> objs)
        {
            var columns = ColumnUnion(objs);
            if (columns.Count == 0)
            {
                // All-primitive set — fall back to one ToString per line
                // so the user still sees something sensible instead of
                // an empty pane.
                var fallback = new System.Text.StringBuilder();
                foreach (var o in objs) fallback.AppendLine(o?.BaseObject?.ToString() ?? string.Empty);
                return fallback.ToString();
            }

            // Pre-extract values + measure widths in a single pass so
            // we don't read each property twice.
            var widths = new int[columns.Count];
            for (int i = 0; i < columns.Count; i++) widths[i] = columns[i].Length;

            var rows = new List<string[]>(objs.Count);
            foreach (var o in objs)
            {
                var row = new string[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    string v = string.Empty;
                    if (o != null)
                    {
                        var p = o.Properties[columns[i]];
                        if (p != null) v = SafeReadProperty(p)?.ToString() ?? string.Empty;
                    }
                    row[i] = v;
                    if (v.Length > widths[i]) widths[i] = v.Length;
                }
                rows.Add(row);
            }

            const string sep = "  ";
            var sb = new System.Text.StringBuilder();

            // Header + dash separator. PowerShell's Format-Table uses a
            // dashed underline; we copy the convention so output looks
            // familiar.
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(columns[i].PadRight(widths[i]));
            }
            sb.AppendLine();
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(new string('-', widths[i]));
            }
            sb.AppendLine();

            foreach (var row in rows)
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    if (i > 0) sb.Append(sep);
                    sb.Append(row[i].PadRight(widths[i]));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string FormatAsJson(IList<PSObject> objs)
        {
            var list = new List<object>(objs.Count);
            foreach (var o in objs)
            {
                if (o == null) { list.Add(null); continue; }
                if (!IsStructured(o)) { list.Add(o.BaseObject); continue; }
                var d = new Dictionary<string, object>();
                foreach (var p in GetUserProperties(o))
                {
                    object v;
                    try { v = p.Value; } catch { v = null; }
                    // Newtonsoft serializes most CLR types fine; the
                    // exceptions are types with circular references or
                    // throwing getters. Fall back to ToString in those
                    // cases so the document stays valid JSON.
                    if (v != null && !IsJsonFriendly(v))
                    {
                        try { v = v.ToString(); } catch { v = null; }
                    }
                    d[p.Name] = v;
                }
                list.Add(d);
            }
            try
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented)
                    + Environment.NewLine;
            }
            catch (Exception ex)
            {
                return $"(json serialization failed: {ex.Message}){Environment.NewLine}";
            }
        }

        private static bool IsJsonFriendly(object v)
        {
            if (v == null) return true;
            var t = v.GetType();
            if (t.IsPrimitive) return true;
            if (v is string || v is decimal || v is DateTime || v is DateTimeOffset
                || v is Guid || v is TimeSpan) return true;
            // Collections and dictionaries are fine; Newtonsoft handles them.
            if (v is System.Collections.IDictionary) return true;
            if (v is System.Collections.IEnumerable) return true;
            return false;
        }

        private static string FormatAsCsv(IList<PSObject> objs)
        {
            var columns = ColumnUnion(objs);
            var sb = new System.Text.StringBuilder();

            if (columns.Count == 0)
            {
                // Single-column CSV of primitive ToStrings — keeps the
                // file shape valid for spreadsheets even when the script
                // emitted only strings/ints.
                sb.AppendLine("Value");
                foreach (var o in objs) sb.AppendLine(EscapeCsv(o?.BaseObject?.ToString() ?? string.Empty));
                return sb.ToString();
            }

            // Header
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(EscapeCsv(columns[i]));
            }
            sb.AppendLine();

            // Rows
            foreach (var o in objs)
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    string v = string.Empty;
                    if (o != null)
                    {
                        var p = o.Properties[columns[i]];
                        if (p != null) v = SafeReadProperty(p)?.ToString() ?? string.Empty;
                    }
                    sb.Append(EscapeCsv(v));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // RFC-4180 quoting: wrap in double-quotes when the value contains
        // a comma, double-quote, or any line break; double internal quotes.
        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            bool needsQuotes = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ',' || c == '"' || c == '\n' || c == '\r') { needsQuotes = true; break; }
            }
            if (!needsQuotes) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // ---- existing cell formatter (used by the grid) ----

        private static object FormatCell(object value)
        {
            if (value == null) return string.Empty;
            // Arrays/collections in a property would show as
            // "System.String[]" via default ToString — flatten to a
            // comma-joined string instead.
            if (value is System.Collections.IEnumerable e && !(value is string))
            {
                var parts = new List<string>();
                foreach (var item in e) parts.Add(item?.ToString() ?? string.Empty);
                return string.Join(", ", parts);
            }
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                lock (_gate)
                {
                    try { _current?.Stop(); } catch { }
                    _current = null;
                }
                _runspace?.Close();
                _runspace?.Dispose();
            }
            catch { /* swallow on shutdown */ }
        }
    }
}
