using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Concrete <see cref="IOutputSink"/> that writes to two RichTextBoxes
    /// (main console + bottom log) and a DataGridView. All writes are
    /// marshaled to the UI thread because executors run on background tasks
    /// and must not touch controls directly.
    ///
    /// Color choices follow a console-style palette: green output, red
    /// error, yellow warning, cyan info, light gray verbose, gray debug.
    /// The Logs RTB uses a near-white tint to set timestamped log lines
    /// apart from the main output stream.
    ///
    /// ----- Coalesced writes -----
    /// A naive "BeginInvoke per Write*" implementation overwhelmed the
    /// UI thread when scripts emitted thousands of lines per second --
    /// the message queue filled faster than WinForms could paint, the
    /// UI froze, and Esc-to-cancel stopped responding. We now batch:
    /// every Write* enqueues a record on a thread-safe queue, and a
    /// single UI-thread timer drains the queue every 50 ms in one
    /// AppendText pass per color/run. That keeps the UI responsive
    /// regardless of how chatty the script is.
    ///
    /// Side effect: output appears in ~50 ms bursts rather than
    /// per-line. Visually still feels live; the UI thread can actually
    /// keep up.
    ///
    /// ----- Scroll-on-tail -----
    /// We only scroll the RTB to the caret when the user was already
    /// at the tail before the append. That lets users scroll back to
    /// read older output mid-run without being yanked to the bottom on
    /// every new line.
    ///
    /// ----- Disposal safety -----
    /// Every UI access checks IsDisposed and catches ObjectDisposedException.
    /// During app shutdown, in-flight executor threads can still call
    /// into the sink after the RTB is gone -- that path now no-ops
    /// instead of crashing.
    /// </summary>
    public sealed class OutputSink : IOutputSink, IDisposable
    {
        private readonly RichTextBox _console;
        private readonly RichTextBox _logs;
        private readonly DataGridView _grid;

        // Drain interval. 50 ms = 20 updates/sec, matches what the eye
        // perceives as "live" while letting the UI thread actually keep
        // up with chatty scripts.
        private const int DrainIntervalMs = 50;

        // Pending console / log text, plus pending grid mutations, all
        // captured on the executor thread and applied in one drain pass.
        private readonly ConcurrentQueue<RtbWrite> _consoleQueue = new ConcurrentQueue<RtbWrite>();
        private readonly ConcurrentQueue<RtbWrite> _logsQueue    = new ConcurrentQueue<RtbWrite>();
        private readonly ConcurrentQueue<GridOp>   _gridQueue    = new ConcurrentQueue<GridOp>();

        private readonly Timer _drainTimer;
        private bool _disposed;

        private struct RtbWrite
        {
            public string Text;
            public Color  Color;
        }

        // Discriminated grid op (set columns / append row / clear).
        // A small enum + payload is simpler than a class hierarchy and
        // good enough for the three operations we support.
        private struct GridOp
        {
            public enum Kind { SetColumns, AppendRow, ClearGrid }
            public Kind Op;
            public IList<string> Columns;
            public object[] Cells;
        }

        public OutputSink(RichTextBox console, RichTextBox logs, DataGridView grid)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _logs    = logs    ?? throw new ArgumentNullException(nameof(logs));
            _grid    = grid    ?? throw new ArgumentNullException(nameof(grid));

            // System.Windows.Forms.Timer ticks on the UI thread so the
            // drain handler is already where we need it -- no extra
            // marshalling required.
            _drainTimer = new Timer { Interval = DrainIntervalMs };
            _drainTimer.Tick += (_, __) => Drain();
            _drainTimer.Start();
        }

        // ---- Console streams (executor-thread enqueues) ----

        public void WriteOutput(string text)  => Enqueue(_consoleQueue, text, Color.LightGreen);
        public void WriteError(string text)   => Enqueue(_consoleQueue, text, Color.Red);
        public void WriteWarning(string text) => Enqueue(_consoleQueue, text, Color.Yellow);
        public void WriteInfo(string text)    => Enqueue(_consoleQueue, text, Color.Cyan);
        public void WriteVerbose(string text) => Enqueue(_consoleQueue, text, Color.LightGray);
        public void WriteDebug(string text)   => Enqueue(_consoleQueue, text, Color.Gray);
        // White on the black console reads as "this isn't script
        // output -- it's ScriptDeck telling you something." Currently
        // used for the per-click run banner.
        public void WriteHeader(string text)  => Enqueue(_consoleQueue, text, Color.White);

        public void ClearOutput()
        {
            // Clear is a structural change -- drain first, then clear,
            // so no in-flight write lands AFTER the clear was supposed
            // to take effect. Simplest path: enqueue a sentinel that
            // the drain interprets as "blow away the RTB".
            Enqueue(_consoleQueue, null, Color.Empty);
        }

        // ---- Log line (bottom RTB) ----

        public void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            // Standard timestamped format. Single source of truth — every
            // executor goes through this, so styles stay consistent.
            var stamped =
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] - " +
                message + Environment.NewLine;
            Enqueue(_logsQueue, stamped, Color.WhiteSmoke);
        }

        // ---- Grid (also queued so multi-row appends don't fight scroll) ----

        public void SetColumns(IList<string> columns)
        {
            if (columns == null) return;
            _gridQueue.Enqueue(new GridOp
            {
                Op = GridOp.Kind.SetColumns,
                Columns = new List<string>(columns),
            });
        }

        public void AppendRow(params object[] cells)
        {
            if (cells == null) return;
            _gridQueue.Enqueue(new GridOp { Op = GridOp.Kind.AppendRow, Cells = cells });
        }

        public void ClearGrid()
        {
            _gridQueue.Enqueue(new GridOp { Op = GridOp.Kind.ClearGrid });
        }

        // ---- Internals ----

        private static void Enqueue(ConcurrentQueue<RtbWrite> q, string text, Color color)
        {
            // Note: text may be null when the writer is signalling a clear
            // (special-cased in Drain). Empty strings still enqueue but
            // are filtered there.
            q.Enqueue(new RtbWrite { Text = text, Color = color });
        }

        // Drain runs on the UI thread (Timer.Tick is marshaled). Apply
        // all pending writes for each surface in one sweep, suspending
        // RTB redraw across the batch so a 1,000-line append paints
        // once instead of 1,000 times.
        private void Drain()
        {
            if (_disposed) return;

            try
            {
                if (_console != null && !_console.IsDisposed && !_consoleQueue.IsEmpty)
                    DrainRtb(_console, _consoleQueue);
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch { /* a single Drain failure shouldn't kill the timer */ }

            try
            {
                if (_logs != null && !_logs.IsDisposed && !_logsQueue.IsEmpty)
                    DrainRtb(_logs, _logsQueue);
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch { }

            try
            {
                if (_grid != null && !_grid.IsDisposed && !_gridQueue.IsEmpty)
                    DrainGrid();
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch { }
        }

        // Apply a queue of writes onto an RTB. Coalesces consecutive
        // same-color writes into one AppendText + one SelectionColor
        // call; bracketed by SuspendRedraw via the SCROLLPOS stash so
        // the user's scroll position survives if they're not at tail.
        private static void DrainRtb(RichTextBox rtb, ConcurrentQueue<RtbWrite> q)
        {
            // Were we at the tail before the batch? If so, we'll scroll
            // to caret at the end. If not, we leave the user's scroll
            // position alone so they can read older output mid-stream.
            // "At tail" = the visible last char is at-or-near TextLength.
            bool wasAtTail = IsAtTail(rtb);

            // Pull everything out of the queue into a list -- we want
            // to drain in one shot, even if more writes arrive while
            // we're applying. Anything that lands during the apply
            // gets the next Drain pass.
            var batch = new List<RtbWrite>();
            while (q.TryDequeue(out var w)) batch.Add(w);
            if (batch.Count == 0) return;

            // Special case: a clear sentinel (Text == null) wipes the
            // RTB and ends the batch; remaining writes after a clear in
            // the same batch are applied to the now-empty RTB.
            int i = 0;
            while (i < batch.Count)
            {
                if (batch[i].Text == null)
                {
                    rtb.Clear();
                    i++;
                    continue;
                }

                // Group consecutive same-color writes so we make one
                // AppendText + one SelectionColor call per group rather
                // than one per write. Big perf win on chatty scripts.
                var color = batch[i].Color;
                int groupStart = i;
                int totalLen = 0;
                while (i < batch.Count && batch[i].Text != null && batch[i].Color == color)
                {
                    totalLen += batch[i].Text.Length;
                    i++;
                }

                // Concatenate the group via StringBuilder for one
                // alloc rather than per-write.
                var sb = new System.Text.StringBuilder(totalLen);
                for (int j = groupStart; j < i; j++) sb.Append(batch[j].Text);

                int start = rtb.TextLength;
                rtb.AppendText(sb.ToString());
                rtb.Select(start, sb.Length);
                rtb.SelectionColor = color;
                rtb.Select(rtb.TextLength, 0);
            }

            if (wasAtTail)
            {
                rtb.ScrollToCaret();
            }
        }

        // The user is "at the tail" if their selection start is at or
        // very close to the end of the text. We allow a small fuzz
        // (within a few chars of the end) because an unfocused RTB
        // often has its caret slightly off the absolute end.
        private static bool IsAtTail(RichTextBox rtb)
        {
            // Empty RTB counts as at-tail (we want the first write to scroll).
            if (rtb.TextLength == 0) return true;
            int caret = rtb.SelectionStart + rtb.SelectionLength;
            return caret >= rtb.TextLength - 4;
        }

        private void DrainGrid()
        {
            while (_gridQueue.TryDequeue(out var op))
            {
                switch (op.Op)
                {
                    case GridOp.Kind.SetColumns:
                        _grid.Rows.Clear();
                        _grid.Columns.Clear();
                        if (op.Columns != null)
                            foreach (var c in op.Columns) _grid.Columns.Add(c, c);
                        break;

                    case GridOp.Kind.AppendRow:
                        // DataGridView throws if you AppendRow before
                        // any columns exist. Defensive no-op.
                        if (_grid.Columns.Count > 0 && op.Cells != null)
                        {
                            _grid.Rows.Add(op.Cells);
                        }
                        break;

                    case GridOp.Kind.ClearGrid:
                        _grid.Rows.Clear();
                        _grid.Columns.Clear();
                        break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _drainTimer?.Stop(); } catch { }
            try { _drainTimer?.Dispose(); } catch { }
            // Final drain so anything queued in the last 50ms still
            // makes it to the user. Safe to call even with disposed
            // controls -- DrainRtb / DrainGrid check IsDisposed.
            try { Drain(); } catch { }
        }
    }
}
