using System;
using System.Threading;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// One unit of work on the background queue. Created when a
    /// `runInBackground` button is clicked, lives until dismissed from
    /// the Jobs tab. Carries everything the worker needs to run, plus
    /// everything the UI needs to render the row.
    ///
    /// State transitions are linear:
    ///
    ///     Queued -> Running -> Completed
    ///                       -> Failed
    ///                       -> Cancelled
    ///     Queued -> Cancelled    (cancelled while still queued)
    ///
    /// The job manager mutates Status under its own lock; readers (the
    /// UI) snapshot the property and accept that it may already be
    /// stale by the time they paint. The StatusChanged event fires
    /// after each transition so the UI can refresh on demand.
    /// </summary>
    public sealed class Job
    {
        public enum JobStatus
        {
            Queued,
            Running,
            Completed,
            Failed,
            Cancelled,
        }

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>What the user clicked. Used as the job's display name.</summary>
        public string ButtonLabel { get; set; }

        /// <summary>Stable button id (slug). Optional; null when the button has none.</summary>
        public string ButtonId { get; set; }

        /// <summary>"powershell" / "cmd" / "process". Same string as the foreground path uses.</summary>
        public string ExecutorKind { get; set; }

        /// <summary>The fully-resolved execution request. The worker hands this to the executor as-is.</summary>
        public ExecutionRequest Request { get; set; }

        /// <summary>Captures all output the script emits. The Jobs tab UI replays this onto an RTB.</summary>
        public BufferedSink Sink { get; set; }

        /// <summary>Per-job cancellation source. Cancel() the source to stop just this job.</summary>
        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();

        public JobStatus Status { get; internal set; } = JobStatus.Queued;

        /// <summary>Set when the worker picks the job up. Null while still queued.</summary>
        public DateTime? StartedAtUtc { get; internal set; }

        /// <summary>Set when the worker finishes (regardless of outcome).</summary>
        public DateTime? CompletedAtUtc { get; internal set; }

        /// <summary>Process / pipeline exit code, when applicable.</summary>
        public int? ExitCode { get; internal set; }

        /// <summary>Failure message, when Status == Failed.</summary>
        public string ErrorMessage { get; internal set; }

        /// <summary>
        /// Why a Cancelled job was stopped. Set by BackgroundJobQueue.Cancel
        /// to one of "User" (the user clicked Cancel Job), "Shutdown"
        /// (app closing), or any caller-supplied reason. Null for jobs
        /// that weren't cancelled.
        /// </summary>
        public string CancelReason { get; internal set; }

        /// <summary>
        /// Fired after every Status transition. Subscribers should treat
        /// this as a hint to refresh — they MAY be on a non-UI thread,
        /// so anything that touches WinForms controls must marshal back
        /// to the UI thread itself.
        /// </summary>
        public event Action<Job> StatusChanged;

        // Internal raise — only the queue / worker should call this.
        internal void RaiseStatusChanged()
        {
            try { StatusChanged?.Invoke(this); }
            catch { /* listeners shouldn't throw, but if they do, don't bring down the worker */ }
        }

        /// <summary>Friendly elapsed string for the Jobs grid. Live-updates while running.</summary>
        public TimeSpan Elapsed
        {
            get
            {
                if (StartedAtUtc == null) return TimeSpan.Zero;
                var end = CompletedAtUtc ?? DateTime.UtcNow;
                return end - StartedAtUtc.Value;
            }
        }
    }
}
