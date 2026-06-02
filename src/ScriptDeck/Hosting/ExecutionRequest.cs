using System.Collections.Generic;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// What to run. Built by the dispatcher from a <c>Button</c> config plus
    /// the current shared-input values; passed to whichever <see cref="IExecutor"/>
    /// matches the button's kind.
    ///
    /// Token substitution (<c>{{computerName}}</c>, etc.) is applied to
    /// <see cref="Args"/> and <see cref="WorkingDirectory"/> by the dispatcher
    /// BEFORE this object is handed to an executor — executors never see
    /// raw template strings.
    /// </summary>
    public sealed class ExecutionRequest
    {
        /// <summary>Absolute path to the .ps1 / .cmd / .exe file to run.</summary>
        public string ScriptPath { get; set; }

        /// <summary>Already-substituted argument values, in order.</summary>
        public IList<string> Args { get; set; } = new List<string>();

        /// <summary>Working directory for the process. Null = workspace dir.</summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// Friendly button label (used for log lines like
        /// <c>"[timestamp] - Running: Get NIC Info"</c>).
        /// </summary>
        public string ButtonLabel { get; set; }

        /// <summary>
        /// Where to send output: any combination of "rtb" and "grid".
        /// "rtb" = console RichTextBox; "grid" = DataGridView. Pure-text
        /// executors ignore "grid" if they have no structured records.
        /// </summary>
        public ISet<string> OutputTargets { get; set; } =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "rtb" };

        /// <summary>
        /// PowerShell-only RTB rendering format for structured records.
        /// See <see cref="ScriptDeck.Workspace.Button.RtbFormat"/> for
        /// the supported values. Null/empty = "default" (obj.ToString()).
        /// </summary>
        public string RtbFormat { get; set; }

        /// <summary>
        /// Python-only: full path to the python interpreter to spawn.
        /// The dispatcher applies the precedence
        /// <c>Button.PythonInterpreter -> Workspace.PythonInterpreter ->
        /// (null)</c> and stamps the resolved value here. Null or empty
        /// means "use bare 'python' from PATH". Has no effect for
        /// non-Python executors.
        /// </summary>
        public string PythonInterpreter { get; set; }

        /// <summary>
        /// When true, dispatch routes the request through the background
        /// job queue (one worker, FIFO) instead of the foreground
        /// single-flight gate. Foreground and background can run
        /// simultaneously -- at most one of each.
        /// </summary>
        public bool RunInBackground { get; set; }

        // ---- Phase 6 history metadata ----
        // These are not used by executors — they ride along on the
        // request so the dispatcher has everything it needs to write a
        // history row without us having to plumb a parallel "context"
        // object through every call site.

        /// <summary>Stable button id (slug). Optional — null/empty when the button has none.</summary>
        public string ButtonId { get; set; }

        /// <summary>Workspace display name at the moment of dispatch.</summary>
        public string WorkspaceName { get; set; }

        /// <summary>Absolute path to the workspace JSON file at the moment of dispatch.</summary>
        public string WorkspacePath { get; set; }

        /// <summary>
        /// When true, the dispatcher skips writing a row to the run-history
        /// store on completion. Used by the Script Editor's "Run Test"
        /// path: a user iterating on a script shouldn't pollute their
        /// audit trail with dozens of half-finished iterations. Defaults
        /// to false so normal button clicks always record.
        /// </summary>
        public bool SkipHistory { get; set; }

        // ---- Shared-input injection ----

        /// <summary>
        /// Snapshot of every shared input's id -> resolved string value
        /// at click time, AFTER any per-input normalization rules (e.g.
        /// the "computerName" rule that fills empty / "." / "localhost"
        /// with the local machine name). The PowerShell executor
        /// publishes these as runspace variables (so a script can use
        /// <c>$computerName</c> directly without a <c>param()</c> block);
        /// the cmd / process executors publish them as environment
        /// variables (<c>%computerName%</c> / <c>$env:computerName</c>).
        ///
        /// Tokens like <c>{{computerName}}</c> in <see cref="Args"/> have
        /// already been substituted by the dispatcher before the request
        /// reaches an executor — this dictionary is the SAME values used
        /// for that substitution, exposed in raw form so executors can
        /// publish them under their natural names too.
        /// </summary>
        public IDictionary<string, string> SharedInputs { get; set; } =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The subset of <see cref="SharedInputs"/> ids that came from
        /// the workspace JSON (i.e. are Static). The rest are
        /// session-scoped (Volatile). Used by the bootstrap helpers'
        /// duplicate-detection (Set-SharedInput refuses to shadow a
        /// Static id) and by PowerShellExecutor to publish a
        /// <c>$ScriptDeckInputs</c> metadata hashtable into the runspace.
        /// Empty when the caller didn't distinguish (e.g. test fixtures);
        /// in that case every id is treated as Volatile and the
        /// no-duplicates rule simply doesn't fire.
        /// </summary>
        public System.Collections.Generic.ISet<string> StaticInputIds { get; set; } =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    }
}
