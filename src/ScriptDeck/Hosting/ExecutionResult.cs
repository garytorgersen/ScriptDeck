using System;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Summary of a finished execution. Goes into the run-history store
    /// (Phase 6) and drives the post-run log line in the bottom RTB.
    ///
    /// Streamed output already reached the sink while the executor was
    /// running — this struct is for the *outcome*, not the payload.
    /// </summary>
    public sealed class ExecutionResult
    {
        public int ExitCode { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// If the executor itself threw (process couldn't start, runspace
        /// faulted, etc.) the message lands here. A non-zero <see cref="ExitCode"/>
        /// from the script is NOT an error from the executor's POV.
        /// </summary>
        public string ErrorMessage { get; set; }

        public static ExecutionResult Ok(int exitCode, TimeSpan duration) =>
            new ExecutionResult { ExitCode = exitCode, Duration = duration };

        public static ExecutionResult Failed(string message, TimeSpan duration) =>
            new ExecutionResult { ExitCode = -1, ErrorMessage = message, Duration = duration };

        public static ExecutionResult CancelledResult(TimeSpan duration) =>
            new ExecutionResult { Cancelled = true, ExitCode = -1, Duration = duration };
    }
}
