using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.History;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Owns the background job pipeline: a FIFO queue plus a single
    /// long-running worker task that pulls jobs and runs them one at
    /// a time. Foreground execution is unaffected -- this lives
    /// alongside the dispatcher's foreground gate.
    ///
    /// Today: concurrency is hard-coded to 1 (single worker). If users
    /// later request "let me run K background jobs in parallel," this
    /// becomes a SemaphoreSlim(K) gate around the executor invocation
    /// with no other surgery -- the queue, job model, and per-job
    /// sinks are all already isolation-clean.
    ///
    /// State management:
    ///  * `_pending` is a thread-safe FIFO of submitted jobs.
    ///  * `_signal` releases the worker when something is added.
    ///  * `_active` (under _gate) is the currently-running job, if any.
    ///  * `_history` is shared with the foreground dispatcher so
    ///    background runs land in the same audit trail.
    /// </summary>
    public sealed class BackgroundJobQueue : IDisposable
    {
        private readonly Dictionary<string, IExecutor> _executors;
        private readonly RunHistory _history;
        // Optional override for status / error log lines. The job
        // manager doesn't write OUTPUT to this sink (that goes to the
        // job's BufferedSink) -- only meta-events ("Queued: X",
        // "Started: X", etc.) so the user can follow background
        // activity from the main Logs RTB without having to switch
        // to the Jobs tab.
        private readonly IOutputSink _metaSink;

        private readonly BlockingCollection<Job> _pending =
            new BlockingCollection<Job>(new ConcurrentQueue<Job>());
        private readonly object _gate = new object();
        private Job _active;

        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private Task _workerTask;
        private bool _disposed;

        /// <summary>Fired when a job is submitted (still Queued at this point).</summary>
        public event Action<Job> JobAdded;
        /// <summary>Fired on every job status transition. Marshalling to UI is the listener's job.</summary>
        public event Action<Job> JobStatusChanged;

        public BackgroundJobQueue(IEnumerable<IExecutor> executors, RunHistory history, IOutputSink metaSink)
        {
            if (executors == null) throw new ArgumentNullException(nameof(executors));
            _executors = new Dictionary<string, IExecutor>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in executors)
            {
                if (e == null || string.IsNullOrEmpty(e.Kind)) continue;
                _executors[e.Kind] = e;
            }
            _history = history;
            _metaSink = metaSink;

            _workerTask = Task.Factory.StartNew(
                WorkerLoop,
                _shutdownCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Snapshot the currently-running job, if any. Used by the UI
        /// for the "show output of the active job" path. Returns null
        /// when the queue is idle.
        /// </summary>
        public Job ActiveJob
        {
            get { lock (_gate) return _active; }
        }

        /// <summary>
        /// Reset every PowerShell executor we own. Called from the
        /// dispatcher when a workspace switch demands fresh runspace
        /// state on both foreground and background paths. Cmd / Process
        /// executors are stateless and skipped.
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
        }

        /// <summary>Enqueue a job. Returns immediately; the worker will pick it up FIFO.</summary>
        public void Submit(Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (_disposed) throw new ObjectDisposedException(nameof(BackgroundJobQueue));

            _pending.Add(job);
            try { JobAdded?.Invoke(job); } catch { /* swallow */ }
            try { _metaSink?.Log("Queued: " + (job.ButtonLabel ?? "(unnamed)")); } catch { /* swallow */ }
        }

        /// <summary>
        /// Cancel a single job. If it's currently running, signals its
        /// CTS (the executor honors it). If it's still queued, marks
        /// it Cancelled in place; the worker will skip it when it
        /// reaches it. <paramref name="reason"/> is stamped onto the
        /// job so the UI can show "Cancelled (User)" vs "Cancelled
        /// (Shutdown)" etc. -- defaults to "User" since that's what a
        /// click on the Cancel Job button means.
        /// </summary>
        public void Cancel(Job job, string reason = "User")
        {
            if (job == null) return;
            // Stamp the reason BEFORE we trigger cancellation so the
            // status-changed handler sees it in the same transition.
            job.CancelReason = reason;
            try { job.Cts.Cancel(); } catch { /* already disposed */ }

            // Queued -> Cancelled transition is handled by the worker
            // when it dequeues; we don't try to remove from the queue
            // here because BlockingCollection doesn't support pluck.
            lock (_gate)
            {
                if (job.Status == Job.JobStatus.Queued)
                {
                    job.Status = Job.JobStatus.Cancelled;
                    job.CompletedAtUtc = DateTime.UtcNow;
                    job.RaiseStatusChanged();
                    SafeRaise(JobStatusChanged, job);
                }
            }
        }

        // ---- worker ----

        private async Task WorkerLoop()
        {
            try
            {
                foreach (var job in _pending.GetConsumingEnumerable(_shutdownCts.Token))
                {
                    // Per-job try/catch so a crash inside RunOne (or in
                    // anything RunOne touches that escapes its own
                    // try/catch -- e.g. a sink throwing during status
                    // logging) doesn't kill the worker permanently.
                    // The queue must keep draining or every subsequent
                    // background submission sits Queued forever.
                    try
                    {
                        await RunOne(job).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; } // shutdown bubbles up
                    catch (Exception ex)
                    {
                        // Mark the job Failed so the UI doesn't show it
                        // stuck in Running, then surface to the meta sink
                        // and keep iterating.
                        try
                        {
                            job.Status = Job.JobStatus.Failed;
                            job.ErrorMessage = "Worker exception: " + ex.Message;
                            job.CompletedAtUtc ??= DateTime.UtcNow;
                            job.RaiseStatusChanged();
                            SafeRaise(JobStatusChanged, job);
                        }
                        catch { /* best effort */ }
                        try { _metaSink?.WriteError("[jobs] job failed unexpectedly: " + ex.Message + Environment.NewLine); }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                // The outer loop itself died -- shouldn't happen because
                // GetConsumingEnumerable normally only throws on
                // cancellation. Log and exit; queue won't drain after this.
                try { _metaSink?.WriteError("[jobs] worker crashed: " + ex.Message + Environment.NewLine); } catch { }
            }
        }

        private async Task RunOne(Job job)
        {
            // If the job was cancelled while queued, skip it.
            lock (_gate)
            {
                if (job.Status == Job.JobStatus.Cancelled) return;
                _active = job;
            }

            // Promote to Running.
            job.Status = Job.JobStatus.Running;
            job.StartedAtUtc = DateTime.UtcNow;
            job.RaiseStatusChanged();
            SafeRaise(JobStatusChanged, job);
            try { _metaSink?.Log("Started (background): " + (job.ButtonLabel ?? "(unnamed)")); } catch { }

            ExecutionResult result = null;
            string executorKind = job.ExecutorKind ?? string.Empty;

            try
            {
                if (!_executors.TryGetValue(executorKind, out var exec))
                {
                    job.Sink?.WriteError(
                        "Unknown executor '" + executorKind + "' (background path). Known: " +
                        string.Join(", ", _executors.Keys) + Environment.NewLine);
                    job.Status = Job.JobStatus.Failed;
                    job.ErrorMessage = "Unknown executor: " + executorKind;
                }
                else
                {
                    result = await exec.ExecuteAsync(job.Request, job.Sink, job.Cts.Token).ConfigureAwait(false);

                    if (result == null)
                    {
                        job.Status = Job.JobStatus.Failed;
                        job.ErrorMessage = "Executor returned no result.";
                    }
                    else if (result.Cancelled)
                    {
                        job.Status = Job.JobStatus.Cancelled;
                        job.ExitCode = result.ExitCode;
                    }
                    else if (!string.IsNullOrEmpty(result.ErrorMessage))
                    {
                        job.Status = Job.JobStatus.Failed;
                        job.ExitCode = result.ExitCode;
                        job.ErrorMessage = result.ErrorMessage;
                    }
                    else
                    {
                        job.Status = Job.JobStatus.Completed;
                        job.ExitCode = result.ExitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                job.Status = Job.JobStatus.Failed;
                job.ErrorMessage = ex.Message;
                try { job.Sink?.WriteError("[background] " + ex.Message + Environment.NewLine); } catch { }
            }
            finally
            {
                job.CompletedAtUtc = DateTime.UtcNow;
                lock (_gate) { if (ReferenceEquals(_active, job)) _active = null; }

                // History row. Best-effort -- a history failure should
                // never escape the worker.
                try { RecordHistory(job, executorKind, result); }
                catch { /* swallow */ }

                job.RaiseStatusChanged();
                SafeRaise(JobStatusChanged, job);

                // Meta-log to the main Logs RTB so the user sees the
                // outcome even if they never open the Jobs tab.
                try
                {
                    string outcome;
                    switch (job.Status)
                    {
                        case Job.JobStatus.Completed:
                            outcome = "Done (background): " + job.ButtonLabel + " (exit " + job.ExitCode + ")";
                            break;
                        case Job.JobStatus.Cancelled:
                            outcome = "Cancelled (background): " + job.ButtonLabel;
                            break;
                        default:
                            outcome = "Error (background): " + job.ButtonLabel + " -- " + job.ErrorMessage;
                            break;
                    }
                    _metaSink?.Log(outcome);
                }
                catch { }
            }
        }

        private void RecordHistory(Job job, string executorKind, ExecutionResult result)
        {
            if (_history == null) return;

            string status;
            int? exitCode;
            string error;
            TimeSpan duration = job.Elapsed;

            switch (job.Status)
            {
                case Job.JobStatus.Completed:
                    status = RunStatus.Ok;
                    exitCode = job.ExitCode;
                    error = null;
                    break;
                case Job.JobStatus.Cancelled:
                    status = RunStatus.Cancelled;
                    exitCode = job.ExitCode;
                    error = null;
                    break;
                default:
                    status = RunStatus.Failed;
                    exitCode = job.ExitCode;
                    error = job.ErrorMessage;
                    break;
            }

            _history.Record(new RunRecord
            {
                StartedAtUtc     = job.StartedAtUtc ?? DateTime.UtcNow,
                Duration         = duration,
                WorkspacePath    = job.Request?.WorkspacePath,
                WorkspaceName    = job.Request?.WorkspaceName,
                ButtonId         = job.ButtonId,
                ButtonLabel      = job.ButtonLabel,
                Executor         = executorKind,
                ScriptPath       = job.Request?.ScriptPath,
                Args             = job.Request?.Args != null ? new List<string>(job.Request.Args) : new List<string>(),
                WorkingDirectory = job.Request?.WorkingDirectory,
                ExitCode         = exitCode,
                Status           = status,
                ErrorMessage     = error,
            });
        }

        private static void SafeRaise(Action<Job> handler, Job job)
        {
            if (handler == null) return;
            try { handler(job); } catch { /* swallow */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Stop accepting new submissions and wake the worker.
            try { _pending.CompleteAdding(); } catch { }
            try { _shutdownCts.Cancel(); } catch { }

            // Cancel whatever's running so we don't wait forever.
            // Tag the reason so the UI / history can distinguish a
            // user-initiated cancel from a shutdown-initiated one.
            lock (_gate)
            {
                if (_active != null)
                {
                    _active.CancelReason = "Shutdown";
                    try { _active.Cts.Cancel(); } catch { }
                }
            }

            try { _workerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* swallow */ }

            try { _pending.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
        }
    }
}
