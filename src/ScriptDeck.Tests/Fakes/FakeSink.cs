using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ScriptDeck.Hosting;

namespace ScriptDeck.Tests.Fakes
{
    /// <summary>
    /// IOutputSink that records every call into thread-safe lists. Tests
    /// assert against these collections instead of poking at WinForms
    /// controls. All Write* calls capture the (severity, text) pair.
    /// </summary>
    public sealed class FakeSink : IOutputSink
    {
        public ConcurrentBag<(string Severity, string Text)> Writes { get; }
            = new ConcurrentBag<(string, string)>();

        public ConcurrentBag<string> Logs { get; } = new ConcurrentBag<string>();

        public IList<string> GridColumns { get; private set; } = new List<string>();
        public List<object[]> GridRows { get; } = new List<object[]>();

        public void WriteOutput(string text)  => Writes.Add(("Output",  text));
        public void WriteError(string text)   => Writes.Add(("Error",   text));
        public void WriteWarning(string text) => Writes.Add(("Warning", text));
        public void WriteInfo(string text)    => Writes.Add(("Info",    text));
        public void WriteVerbose(string text) => Writes.Add(("Verbose", text));
        public void WriteDebug(string text)   => Writes.Add(("Debug",   text));

        public void Log(string message) => Logs.Add(message);

        public void SetColumns(IList<string> columns)
        {
            GridColumns = columns == null ? new List<string>() : new List<string>(columns);
            GridRows.Clear();
        }

        public void AppendRow(params object[] cells)
        {
            GridRows.Add(cells ?? Array.Empty<object>());
        }

        public void ClearOutput() { /* no-op for tests */ }
        public void ClearGrid()
        {
            GridColumns = new List<string>();
            GridRows.Clear();
        }
    }
}
