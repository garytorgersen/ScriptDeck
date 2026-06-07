using System.Collections.Generic;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// The sink every executor writes to. Decouples the executor implementations
    /// (PowerShell / Cmd / Process) from the WinForms controls in <see cref="Shell"/>,
    /// which both makes them testable and lets a future "headless run" feature
    /// (e.g. unattended workspace execution) drop in a different sink.
    ///
    /// Convention: text methods include their own trailing newline if the caller
    /// wants one. The sink does not append newlines on its own — that lets
    /// progress dots and inline "[OK]" markers behave naturally.
    /// </summary>
    public interface IOutputSink
    {
        // Console-style streams. Color routing lives in the concrete sink.
        void WriteOutput(string text);
        void WriteError(string text);
        void WriteWarning(string text);
        void WriteInfo(string text);
        void WriteVerbose(string text);
        void WriteDebug(string text);

        // Rendered in white. Used by the dispatcher to print a
        // per-click "Running: <button> [<executor>]" banner that
        // stands out from the script's green/yellow/red output. The
        // caller decides on newlines (header line + blank separator
        // before the executor's first write is the convention).
        void WriteHeader(string text);

        // Persistent log line in the bottom RTB. `[timestamp] - <message>`
        // formatting is the sink's responsibility, not the caller's, so all
        // log lines look identical regardless of which executor wrote them.
        void Log(string message);

        // Grid output. SetColumns is called once per result set (clears prior
        // columns + rows); AppendRow then streams rows in. Executors that
        // produce structured output (PS PSObjects, parsed CSV) call these;
        // pure text executors leave the grid alone.
        void SetColumns(IList<string> columns);
        void AppendRow(params object[] cells);

        void ClearOutput();
        void ClearGrid();
    }
}
