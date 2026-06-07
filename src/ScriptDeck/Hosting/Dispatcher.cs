using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.History;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Routes button-click execution requests to the matching <see cref="IExecutor"/>,
    /// enforces the one-at-a-time rule, and surfaces busy-state changes to the UI.
    ///
    /// Concurrency policy (matches the user's stated intent):
    ///   - At most one execution active at a time.
    ///   - A second click while busy is rejected with a warning to the
    ///     sink (no queueing yet — that's a future enhancement).
    ///   - The active run is cancellable via <see cref="CancelActive"/>.
    /// </summary>
    public sealed class Dispatcher : IDisposable
    {
        // Width of the post-run visual separator written to the console.
        // Sized to a typical 80-col window minus a margin so it reads as
        // a clear divider without forcing wrap on narrow displays.
        private const int SeparatorWidth = 60;

        private readonly IOutputSink _sink;
        private readonly Dictionary<string, IExecutor> _executors;
        // Optional — null disables history recording. We don't fail the
        // ctor if the caller didn't pass one, since headless tests and
        // future "incognito" modes have legitimate reasons to opt out.
        private readonly RunHistory _history;
        // Background path (optional). Null when ctor was called without
        // backgroundExecutors. Owns its own worker task + FIFO queue.
        private readonly BackgroundJobQueue _bgQueue;
        // Distinct disposable instances across foreground + background.
        // HashSet so a shared cmd/process executor isn't double-disposed.
        private readonly HashSet<IDisposable> _disposalSet;

        // Reentrancy gate. CompareExchange to enter, Exchange-back to leave.
        // 0 = idle, 1 = busy. Volatile-read for the IsBusy property.
        private int _busy;

        // CTS for the active run. Replaced (and disposed) every execute.
        // Held under _ctsGate so CancelActive sees a stable reference.
        private readonly object _ctsGate = new object();
        private CancellationTokenSource _activeCts;
        private string _activeLabel;

        /// <summary>Raised on busy-state transitions. Always fires, including on errors.</summary>
        public event EventHandler BusyChanged;

        public bool IsBusy => Volatile.Read(ref _busy) != 0;

        /// <summary>Label of the currently running button, or null if idle.</summary>
        public string ActiveLabel
        {
            get { lock (_ctsGate) return _activeLabel; }
        }

        public Dispatcher(IOutputSink sink, IEnumerable<IExecutor> executors, RunHistory history = null)
            : this(sink, executors, backgroundExecutors: null, history) { }

        /// <summary>
        /// Two-track constructor. <paramref name="executors"/> drive the
        /// foreground single-flight path; <paramref name="backgroundExecutors"/>,
        /// when supplied, set up the background queue (single worker,
        /// FIFO). Pass null/empty backgroundExecutors to disable the
        /// background path -- requests with RunInBackground=true will
        /// then return null.
        ///
        /// Cmd / Process executor instances are stateless and can be
        /// shared between the two collections without conflict.
        /// PowerShell executors should NOT be shared -- give the
        /// foreground and background paths their own runspaces so a
        /// long-running background job doesn't serialize behind a
        /// foreground click.
        /// </summary>
        public Dispatcher(
            IOutputSink sink,
            IEnumerable<IExecutor> executors,
            IEnumerable<IExecutor> backgroundExecutors,
            RunHistory history = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            if (executors == null) throw new ArgumentNullException(nameof(executors));

            _executors = new Dictionary<string, IExecutor>(StringComparer.OrdinalIgnoreCase);
            // Track every disposable instance handed in -- across both
            // collections -- in a HashSet so a shared cmd/process
            // executor doesn't get disposed twice on shutdown.
            _disposalSet = new HashSet<IDisposable>();
            foreach (var ex in executors)
            {
                if (ex == null || string.IsNullOrEmpty(ex.Kind)) continue;
                _executors[ex.Kind] = ex;
                if (ex is IDisposable d) _disposalSet.Add(d);
            }
            _history = history;

            if (backgroundExecutors != null)
            {
                var bg = new List<IExecutor>();
                foreach (var ex in backgroundExecutors)
                {
                    if (ex == null || string.IsNullOrEmpty(ex.Kind)) continue;
                    bg.Add(ex);
                    if (ex is IDisposable d) _disposalSet.Add(d);
                }
                if (bg.Count > 0)
                {
                    _bgQueue = new BackgroundJobQueue(bg, history, sink);
                }
            }
        }

        /// <summary>
        /// The background queue, or null when no background executors
        /// were configured. The Shell wires its Jobs tab to this
        /// instance's events.
        /// </summary>
        public BackgroundJobQueue BackgroundQueue { get { return _bgQueue; } }

        /// <summary>
        /// Reset the foreground and background PowerShell runspaces.
        /// Wipes all global state from previous scripts -- variables,
        /// imported modules, anything `$global:`-prefixed -- and
        /// reloads the bootstrap so edits to it take effect. Used when
        /// the user loads a different workspace, since cross-workspace
        /// state leakage was an explicit "we want each workspace to
        /// start clean" design call.
        ///
        /// Cmd / Process executors are stateless per-invocation and
        /// don't need (or have) a Reset.
        /// </summary>
        public void ResetExecutors()
        {
            foreach (var ex in _executors.Values)
            {
                if (ex is PowerShellExecutor ps)
                {
                    try { ps.Reset(); } catch { /* best effort */ }
                }
            }
            if (_bgQueue != null) _bgQueue.ResetExecutors();
        }

        /// <summary>
        /// Build a Job for the given request, attach a fresh BufferedSink,
        /// and submit to the background queue. Returns the Job so the
        /// caller can wire its StatusChanged event into a UI row. Returns
        /// null when no background queue is configured.
        /// </summary>
        public Job EnqueueBackground(ExecutionRequest request, string executorKind)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_bgQueue == null) return null;

            var job = new Job
            {
                ButtonLabel = request.ButtonLabel,
                ButtonId = request.ButtonId,
                ExecutorKind = executorKind,
                Request = request,
                Sink = new BufferedSink(),
            };
            _bgQueue.Submit(job);
            return job;
        }

        /// <summary>
        /// Run the request through the executor whose <see cref="IExecutor.Kind"/>
        /// matches <paramref name="executorKind"/>. Returns <c>null</c> if a
        /// run is already active (rejected) or the kind is unknown.
        ///
        /// <paramref name="overrideSink"/> lets a caller (typically the
        /// Script Editor's Run Test) redirect output to its own pane
        /// instead of the main console. The dispatcher's internal log
        /// lines still go to the main sink — they're audit/status output,
        /// not test output, and it's helpful to see "Running test..." in
        /// the main log even when the test results land in a dialog.
        /// </summary>
        public async Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request,
            string executorKind,
            IOutputSink overrideSink = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // Sink resolution: per-call override wins, otherwise the
            // dispatcher's own sink. Computed up front so EVERY message
            // emitted along this dispatch path -- including pre-execute
            // rejections -- lands in the caller's chosen sink (e.g. the
            // Script Editor's local pane) rather than leaking to the
            // main console.
            IOutputSink runSink = overrideSink ?? _sink;

            if (string.IsNullOrEmpty(executorKind) ||
                !_executors.TryGetValue(executorKind, out var executor))
            {
                runSink.WriteError(
                    $"Unknown executor '{executorKind}'. Configured kinds: " +
                    string.Join(", ", _executors.Keys) + Environment.NewLine);
                return null;
            }

            // Single-flight gate. The user's UI should also disable buttons
            // when busy, but this is the source of truth — handlers that
            // miss the visual cue still can't double-fire.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                runSink.WriteWarning(
                    $"Already running '{ActiveLabel}'. Cancel first or wait." + Environment.NewLine);
                return null;
            }

            // Seed the active label BEFORE the gate is reachable as "busy"
            // by anyone observing IsBusy. The CompareExchange above is the
            // first place the busy-state becomes visible; any reader that
            // reaches ActiveLabel between that moment and the lock-acquire
            // below would otherwise see null. Set the field under the lock
            // up front, then create the CTS and complete state setup.
            CancellationTokenSource cts = new CancellationTokenSource();
            lock (_ctsGate)
            {
                _activeLabel = request.ButtonLabel;
                _activeCts = cts;
            }
            RaiseBusyChanged();

            // Capture wall-clock start so the history row reflects when the
            // user clicked, not when the executor's internal Stopwatch started
            // (those are usually within microseconds, but the history is the
            // user's audit trail and "click time" is what they remember).
            DateTime startedUtc = DateTime.UtcNow;

            // Initialized to null so the compiler can prove definite-assignment
            // in the finally below — if the catch clause itself throws (e.g.
            // sink.WriteError fails), control goes straight to finally with
            // no assignment from try or catch. RecordToHistory tolerates null.
            ExecutionResult result = null;
            try
            {
                // Wipe stale grid content from the previous run so a
                // command that produces no structured output starts
                // with an empty grid -- without this, the previous
                // command's rows linger and look like they belong to
                // the new run. The grid is a single shared surface
                // with a 1:1 click->result relationship; users who
                // want to keep a result around can use the popout
                // (toolbar glyph or right-click -> Open in new window).
                //
                // Console RTB is NOT cleared between runs -- a scroll-
                // back log of outputs is useful, and the **** divider
                // already gives visual boundaries.
                try { runSink.ClearGrid(); } catch { /* sink failure mustn't abort dispatch */ }

                // Per-click banner in the main console RTB. White on
                // black so it visibly stands apart from the script's
                // green output. Format: "Running: <label> [<executor>]
                // - <script path>" followed by a blank line that
                // separates the banner from the script's first write.
                try
                {
                    string scriptDisplay = !string.IsNullOrEmpty(request.ScriptPath)
                        ? "  -  " + request.ScriptPath
                        : string.Empty;
                    runSink.WriteHeader(
                        "Running: " + (request.ButtonLabel ?? "(unnamed)") +
                        "  [" + executorKind + "]" + scriptDisplay +
                        Environment.NewLine + Environment.NewLine);
                }
                catch { /* sink failure mustn't abort dispatch */ }

                runSink.Log($"Running: {request.ButtonLabel} ({executorKind})");
                result = await executor.ExecuteAsync(request, runSink, cts.Token).ConfigureAwait(false);

                // Post-run summary line. "Done" / "Cancelled" / "Error" so
                // the user can scan the bottom RTB for outcomes.
                if (result == null)
                {
                    runSink.Log($"Done: {request.ButtonLabel} (no result)");
                }
                else if (result.Cancelled)
                {
                    runSink.Log($"Cancelled: {request.ButtonLabel} after {FormatDuration(result.Duration)}");
                }
                else if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    runSink.Log($"Error: {request.ButtonLabel} \u2014 {result.ErrorMessage}");
                }
                else
                {
                    runSink.Log($"Done: {request.ButtonLabel} (exit {result.ExitCode}, {FormatDuration(result.Duration)})");
                }
            }
            catch (Exception ex)
            {
                // Defensive: an executor SHOULD return a Failed result
                // rather than throw, but if one does we don't want the
                // dispatcher to leave the busy gate stuck on.
                runSink.WriteError($"[dispatcher] {ex.Message}{Environment.NewLine}");
                result = ExecutionResult.Failed(ex.Message, TimeSpan.Zero);
            }
            finally
            {
                // Record into history BEFORE releasing the busy gate so a
                // fast double-click can't race a half-recorded row into the
                // store. RunHistory.Record is internally best-effort but
                // wrap defensively — history bookkeeping must never escape
                // the dispatcher and crash the app. Test runs from the
                // editor opt out via SkipHistory so users iterating on a
                // script don't fill the audit trail with scratch runs.
                if (!request.SkipHistory)
                {
                    try { RecordToHistory(request, executorKind, startedUtc, result); }
                    catch { /* swallow */ }
                }

                // Visual separator between commands in the console RTB.
                // Two blank lines, a row of asterisks, then two MORE
                // blank lines below -- gives the eye an unambiguous
                // boundary AND a quiet zone before the next run starts
                // writing, so back-to-back clicks don't visually crowd
                // their output up against the asterisks. Goes ONLY to
                // the output stream (not the logs RTB or the grid) so
                // it doesn't pollute structured outputs. Best-effort:
                // a sink failure here mustn't escape the finally and
                // mask the original result.
                try
                {
                    runSink.WriteOutput(
                        Environment.NewLine + Environment.NewLine +
                        new string('*', SeparatorWidth) + Environment.NewLine +
                        Environment.NewLine + Environment.NewLine);
                }
                catch { /* swallow */ }

                lock (_ctsGate)
                {
                    if (_activeCts == cts)
                    {
                        _activeCts = null;
                        _activeLabel = null;
                    }
                }
                cts.Dispose();
                Interlocked.Exchange(ref _busy, 0);
                RaiseBusyChanged();
            }

            return result;
        }

        /// <summary>
        /// Translate the live request + result into a <see cref="RunRecord"/>
        /// and hand it to the history store. No-op when no store was wired.
        ///
        /// Status mapping is the only piece of judgment here:
        ///   - null result        → "Failed"   (executor returned nothing — abnormal)
        ///   - Cancelled flag     → "Cancelled"
        ///   - non-null ErrorMessage → "Failed"
        ///   - everything else    → "Ok"        (including non-zero exit codes;
        ///                                       see RunRecord for the rationale)
        /// </summary>
        private void RecordToHistory(
            ExecutionRequest req, string executorKind, DateTime startedUtc, ExecutionResult result)
        {
            if (_history == null) return;

            string status;
            int? exitCode;
            string error;
            TimeSpan duration;

            if (result == null)
            {
                status = RunStatus.Failed;
                exitCode = null;
                error = "Executor returned no result.";
                duration = TimeSpan.Zero;
            }
            else if (result.Cancelled)
            {
                status = RunStatus.Cancelled;
                exitCode = result.ExitCode;
                error = null;
                duration = result.Duration;
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                status = RunStatus.Failed;
                exitCode = result.ExitCode;
                error = result.ErrorMessage;
                duration = result.Duration;
            }
            else
            {
                status = RunStatus.Ok;
                exitCode = result.ExitCode;
                error = null;
                duration = result.Duration;
            }

            _history.Record(new RunRecord
            {
                StartedAtUtc     = startedUtc,
                Duration         = duration,
                WorkspacePath    = req.WorkspacePath,
                WorkspaceName    = req.WorkspaceName,
                ButtonId         = req.ButtonId,
                ButtonLabel      = req.ButtonLabel,
                Executor         = executorKind,
                ScriptPath       = req.ScriptPath,
                Args             = req.Args != null ? new List<string>(req.Args) : new List<string>(),
                WorkingDirectory = req.WorkingDirectory,
                ExitCode         = exitCode,
                Status           = status,
                ErrorMessage     = error,
            });
        }

        public void CancelActive()
        {
            CancellationTokenSource cts;
            lock (_ctsGate) cts = _activeCts;
            if (cts == null) return;
            try { cts.Cancel(); } catch { /* already disposed */ }
        }

        public void Dispose()
        {
            CancelActive();
            // Tear down the background queue first -- it cancels the
            // active background job and joins the worker task. Doing
            // this BEFORE disposing the runspace-owning executors
            // avoids a "runspace closed mid-Invoke" race.
            try { _bgQueue?.Dispose(); } catch { /* swallow */ }

            // Dedupes shared instances (cmd/process between fg and bg).
            if (_disposalSet != null)
            {
                foreach (var d in _disposalSet)
                {
                    try { d.Dispose(); } catch { /* swallow on shutdown */ }
                }
            }
            // History is the dispatcher's collaborator, but Shell holds the
            // canonical reference. Dispose only if we *own* it, which we
            // never do — Shell will dispose. We deliberately do NOT call
            // _history?.Dispose() here so a dispatcher Reset/recreate
            // doesn't tear down the user's history connection pool.
        }

        private void RaiseBusyChanged()
        {
            try { BusyChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* listeners shouldn't throw, but if they do, don't propagate */ }
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0} ms";
            if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:F2} s";
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        }
    }
}
