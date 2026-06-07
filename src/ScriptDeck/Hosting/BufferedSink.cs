using System;
using System.Collections.Generic;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// IOutputSink implementation that captures every call into an
    /// in-memory list of typed entries. The Jobs tab UI replays these
    /// onto a RichTextBox, preserving the original severity (and
    /// therefore the colors). Thread-safe: executor threads append,
    /// the UI thread reads -- locks are short and uncontended.
    ///
    /// Grid output is captured as a simple "columns + rows" pair. The
    /// Jobs tab doesn't render a grid (the user explicitly said it
    /// doesn't need one), so for now the captured grid data is
    /// available for inspection but not displayed. Easy to surface
    /// later via a "Send to main grid" action without changing this
    /// type.
    ///
    /// Logs (Sink.Log) are captured separately since they correspond
    /// to a different RTB in the foreground UI; here we just append
    /// them with a [LOG] prefix so the user can see them inline.
    /// </summary>
    public sealed class BufferedSink : IOutputSink
    {
        public enum Severity { Output, Error, Warning, Info, Verbose, Debug, Log, Header }

        public sealed class Entry
        {
            public Severity Severity { get; set; }
            public string Text { get; set; }
            public DateTime AtUtc { get; set; }
        }

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private List<string> _gridColumns;
        private readonly List<object[]> _gridRows = new List<object[]>();

        // Bounds the buffer to prevent runaway memory on a misbehaving
        // script. Roughly 5 MB at 100 chars/line average. The replay UI
        // shows a "(truncated to N entries)" notice when this hits.
        private const int MaxEntries = 50_000;
        private bool _truncated;

        /// <summary>Fires after every append. Subscribers should marshal to the UI thread.</summary>
        public event Action<Entry> EntryAppended;

        public bool Truncated { get { lock (_gate) return _truncated; } }

        /// <summary>Snapshot of all entries to date. Cheap copy under lock.</summary>
        public IList<Entry> SnapshotEntries()
        {
            lock (_gate) return _entries.ToArray();
        }

        /// <summary>
        /// Atomic "give me everything you have AND start sending me new
        /// entries as they arrive." Eliminates the snapshot-then-subscribe
        /// race where an entry between the two calls would be missed
        /// (or, in the wrong order, double-counted).
        ///
        /// Implementation: we hold the lock across both the snapshot
        /// and the subscription so any Append racing with us is forced
        /// to either (a) land in the snapshot we return, or (b) wait
        /// until our subscriber is wired up. Callers should NOT call
        /// Append from inside <paramref name="onAppend"/> -- that would
        /// deadlock.
        /// </summary>
        public IList<Entry> SubscribeAndSnapshot(Action<Entry> onAppend)
        {
            if (onAppend == null) throw new ArgumentNullException(nameof(onAppend));
            lock (_gate)
            {
                var initial = _entries.ToArray();
                EntryAppended += onAppend;
                return initial;
            }
        }

        /// <summary>Inverse of <see cref="SubscribeAndSnapshot"/>; remove a previously-attached handler.</summary>
        public void Unsubscribe(Action<Entry> onAppend)
        {
            if (onAppend == null) return;
            lock (_gate) { EntryAppended -= onAppend; }
        }

        public IList<string> SnapshotGridColumns()
        {
            lock (_gate) return _gridColumns?.ToArray();
        }

        public IList<object[]> SnapshotGridRows()
        {
            lock (_gate) return _gridRows.ToArray();
        }

        // ---- IOutputSink ----

        public void WriteOutput(string text)  => Append(Severity.Output,  text);
        public void WriteError(string text)   => Append(Severity.Error,   text);
        public void WriteWarning(string text) => Append(Severity.Warning, text);
        public void WriteInfo(string text)    => Append(Severity.Info,    text);
        public void WriteVerbose(string text) => Append(Severity.Verbose, text);
        public void WriteDebug(string text)   => Append(Severity.Debug,   text);
        public void WriteHeader(string text)  => Append(Severity.Header,  text);

        public void Log(string message)
        {
            // Match foreground OutputSink's [HH:mm:ss] - prefix so logs
            // read identically across paths.
            string stamped = "[" + DateTime.Now.ToString("HH:mm:ss") + "] - " + message + Environment.NewLine;
            Append(Severity.Log, stamped);
        }

        public void SetColumns(IList<string> columns)
        {
            lock (_gate)
            {
                _gridColumns = columns == null ? new List<string>() : new List<string>(columns);
                _gridRows.Clear();
            }
        }

        public void AppendRow(params object[] cells)
        {
            lock (_gate)
            {
                _gridRows.Add(cells ?? Array.Empty<object>());
            }
        }

        public void ClearOutput()
        {
            lock (_gate)
            {
                _entries.Clear();
                _truncated = false;
            }
        }

        public void ClearGrid()
        {
            lock (_gate)
            {
                _gridColumns = null;
                _gridRows.Clear();
            }
        }

        // ---- internals ----

        private void Append(Severity sev, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Hold the lock across the event raise so SubscribeAndSnapshot
            // can guarantee no double-counting: a handler added between
            // snapshot and subscribe-completion would otherwise see an
            // entry that's both in the snapshot AND fired through the
            // event. Subscribers (the Jobs-tab UI) all do BeginInvoke
            // and return immediately, so the held-lock period is
            // microseconds even on chatty scripts.
            lock (_gate)
            {
                if (_entries.Count >= MaxEntries)
                {
                    if (!_truncated)
                    {
                        _truncated = true;
                        // Emit one synthetic entry so the user knows.
                        // Subsequent overflowing writes are silently
                        // dropped -- the script is misbehaving.
                        _entries.Add(new Entry
                        {
                            Severity = Severity.Warning,
                            Text = $"(buffer truncated at {MaxEntries:N0} entries; further output discarded)" + Environment.NewLine,
                            AtUtc = DateTime.UtcNow,
                        });
                    }
                    return;
                }
                var e = new Entry { Severity = sev, Text = text, AtUtc = DateTime.UtcNow };
                _entries.Add(e);
                try { EntryAppended?.Invoke(e); }
                catch { /* swallow */ }
            }
        }
    }
}
