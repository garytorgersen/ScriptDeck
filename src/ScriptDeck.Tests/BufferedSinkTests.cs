using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.Hosting;
using Xunit;

namespace ScriptDeck.Tests
{
    public class BufferedSinkTests
    {
        [Fact]
        public void WriteOutput_Captures_With_Output_Severity()
        {
            var s = new BufferedSink();
            s.WriteOutput("hello");
            var entries = s.SnapshotEntries();
            Assert.Single(entries);
            Assert.Equal(BufferedSink.Severity.Output, entries[0].Severity);
            Assert.Equal("hello", entries[0].Text);
        }

        [Fact]
        public void All_Stream_Methods_Map_To_Correct_Severity()
        {
            var s = new BufferedSink();
            s.WriteOutput("o");
            s.WriteError("e");
            s.WriteWarning("w");
            s.WriteInfo("i");
            s.WriteVerbose("v");
            s.WriteDebug("d");
            var entries = s.SnapshotEntries();
            Assert.Equal(6, entries.Count);
            Assert.Equal(BufferedSink.Severity.Output,  entries[0].Severity);
            Assert.Equal(BufferedSink.Severity.Error,   entries[1].Severity);
            Assert.Equal(BufferedSink.Severity.Warning, entries[2].Severity);
            Assert.Equal(BufferedSink.Severity.Info,    entries[3].Severity);
            Assert.Equal(BufferedSink.Severity.Verbose, entries[4].Severity);
            Assert.Equal(BufferedSink.Severity.Debug,   entries[5].Severity);
        }

        [Fact]
        public void Empty_Or_Null_Text_Is_NoOp()
        {
            var s = new BufferedSink();
            s.WriteOutput(null);
            s.WriteOutput("");
            Assert.Empty(s.SnapshotEntries());
        }

        [Fact]
        public void Log_Adds_Timestamp_Prefix_And_Newline()
        {
            var s = new BufferedSink();
            s.Log("test message");
            var entries = s.SnapshotEntries();
            Assert.Single(entries);
            // [HH:mm:ss] - test message\r\n
            Assert.StartsWith("[", entries[0].Text);
            Assert.Contains("test message", entries[0].Text);
            Assert.EndsWith(System.Environment.NewLine, entries[0].Text);
            Assert.Equal(BufferedSink.Severity.Log, entries[0].Severity);
        }

        [Fact]
        public void SetColumns_Then_AppendRow_Roundtrips()
        {
            var s = new BufferedSink();
            s.SetColumns(new[] { "Name", "Status" });
            s.AppendRow("Spooler", "Running");
            s.AppendRow("EventLog", "Stopped");
            var cols = s.SnapshotGridColumns();
            var rows = s.SnapshotGridRows();
            Assert.Equal(new[] { "Name", "Status" }, cols);
            Assert.Equal(2, rows.Count);
            Assert.Equal("Spooler",  rows[0][0]);
            Assert.Equal("Running",  rows[0][1]);
            Assert.Equal("EventLog", rows[1][0]);
        }

        [Fact]
        public void SetColumns_Clears_Previous_Rows()
        {
            var s = new BufferedSink();
            s.SetColumns(new[] { "A" });
            s.AppendRow("x");
            s.SetColumns(new[] { "B" });
            Assert.Empty(s.SnapshotGridRows());
        }

        [Fact]
        public void SubscribeAndSnapshot_Returns_Existing_And_Captures_New()
        {
            var s = new BufferedSink();
            s.WriteOutput("first");
            s.WriteOutput("second");

            var captured = new List<BufferedSink.Entry>();
            var initial = s.SubscribeAndSnapshot(e => captured.Add(e));

            Assert.Equal(2, initial.Count);
            Assert.Equal("first",  initial[0].Text);
            Assert.Equal("second", initial[1].Text);

            // New writes after subscribe should reach the handler.
            s.WriteOutput("third");
            Assert.Single(captured);
            Assert.Equal("third", captured[0].Text);
        }

        [Fact]
        public async Task Concurrent_Appends_Do_Not_Corrupt()
        {
            // Soak test: many threads append simultaneously. Final entry
            // count should equal the total appends; order within a thread
            // is preserved (within-thread sequencing); the underlying
            // List<Entry> mustn't throw a "modified during enumeration"
            // when Snapshot races with Append.
            var s = new BufferedSink();
            const int threads = 8;
            const int perThread = 500;

            await Task.WhenAll(Enumerable.Range(0, threads).Select(t =>
                Task.Run(() =>
                {
                    for (int i = 0; i < perThread; i++)
                        s.WriteOutput($"t{t}-i{i}");
                })));

            // Final snapshot after all appends settle.
            var entries = s.SnapshotEntries();
            Assert.Equal(threads * perThread, entries.Count);
        }

        [Fact]
        public void Truncation_Emits_One_Synthetic_Warning()
        {
            // Drive past MaxEntries (50,000) and confirm:
            //   - Total entries == MaxEntries (the cap doesn't expand)
            //   - The LAST entry is the "(buffer truncated...)" warning
            //   - Only ONE truncation notice is emitted regardless of
            //     how far we overshoot.
            var s = new BufferedSink();
            const int overshoot = 51_000;
            for (int i = 0; i < overshoot; i++) s.WriteOutput("x");

            var entries = s.SnapshotEntries();
            // BufferedSink stops accepting normal writes at MaxEntries
            // (50,000) and APPENDS a single synthetic truncation warning
            // on top -- so the buffer ends at exactly 50,001 entries.
            Assert.Equal(50_001, entries.Count);
            Assert.True(s.Truncated);
            // Only one truncation marker regardless of how far we overshoot.
            int truncMarkers = entries.Count(e =>
                e.Severity == BufferedSink.Severity.Warning &&
                e.Text.Contains("buffer truncated"));
            Assert.Equal(1, truncMarkers);
        }

        [Fact]
        public void ClearOutput_Empties_Entries_And_Resets_Truncation()
        {
            var s = new BufferedSink();
            s.WriteOutput("x");
            s.ClearOutput();
            Assert.Empty(s.SnapshotEntries());
            Assert.False(s.Truncated);
        }

        [Fact]
        public void ClearGrid_Empties_Columns_And_Rows()
        {
            var s = new BufferedSink();
            s.SetColumns(new[] { "A" });
            s.AppendRow("x");
            s.ClearGrid();
            Assert.Null(s.SnapshotGridColumns());
            Assert.Empty(s.SnapshotGridRows());
        }

        [Fact]
        public void Unsubscribe_Stops_Handler()
        {
            var s = new BufferedSink();
            int count = 0;
            System.Action<BufferedSink.Entry> handler = e => count++;
            s.SubscribeAndSnapshot(handler);
            s.WriteOutput("a");
            s.Unsubscribe(handler);
            s.WriteOutput("b");
            Assert.Equal(1, count);
        }
    }
}
