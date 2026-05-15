using System;
using System.Collections.Generic;

namespace ScriptDeck.History
{
    /// <summary>
    /// One row of run history. Mirrors the columns in <c>runs</c> exactly so the
    /// SQLite store can hydrate these in a single SELECT without a separate
    /// mapping layer. Times are stored UTC; the UI renders local.
    ///
    /// Status is the post-run verdict: "Ok", "Failed", or "Cancelled". A
    /// non-zero <see cref="ExitCode"/> with status "Ok" means the script ran to
    /// completion and the *script* signalled failure — that's the script's
    /// concern, not the executor's, and we deliberately don't relabel it.
    /// </summary>
    /// <summary>
    /// Stable string constants for the <see cref="RunRecord.Status"/> column.
    /// Centralized to keep typos out of the audit trail -- a stray
    /// "OK" vs "Ok" used to silently corrupt the column. Use these
    /// everywhere the status is set; the SQLite column stays free-string
    /// so future statuses can land without a schema migration.
    /// </summary>
    public static class RunStatus
    {
        public const string Ok = "Ok";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";
    }

    public sealed class RunRecord
    {
        public long Id { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public TimeSpan Duration { get; set; }

        public string WorkspacePath { get; set; }
        public string WorkspaceName { get; set; }

        public string ButtonId { get; set; }
        public string ButtonLabel { get; set; }

        public string Executor { get; set; }
        public string ScriptPath { get; set; }

        /// <summary>Args as they were passed to the executor (post token-resolve).</summary>
        public IList<string> Args { get; set; } = new List<string>();

        public string WorkingDirectory { get; set; }

        public int? ExitCode { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
    }
}
