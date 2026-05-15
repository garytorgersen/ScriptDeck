using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScriptDeck.History;
using Xunit;

namespace ScriptDeck.Tests
{
    /// <summary>
    /// Integration tests against a real SQLite database. Each test
    /// instance gets its own temp dir / DB file so tests can't trample
    /// each other and so the user's actual history.db is never touched.
    /// </summary>
    public class RunHistoryTests : IDisposable
    {
        // Force the SQLitePCLRaw bundle's runtime initializer to fire.
        // On net48 in a test runner, the module-init-via-assembly-load
        // path that production code relies on doesn't always trigger
        // -- the symptom is "TypeInitializationException: The type
        // initializer for 'Microsoft.Data.Sqlite.SqliteConnection'
        // threw an exception." Calling Batteries.Init() explicitly is
        // a one-time no-op when it already ran, so this is safe even
        // in environments where the auto-init worked.
        static RunHistoryTests()
        {
            try { SQLitePCL.Batteries.Init(); } catch { /* already init'd */ }
        }

        private readonly string _tmpDir;
        private readonly string _dbPath;

        public RunHistoryTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(),
                "ScriptDeckHistoryTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
            _dbPath = Path.Combine(_tmpDir, "history.db");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
            catch { }
        }

        // Factory for a sample record with a few overridable fields.
        // Returns the populated record so tests can also keep a
        // reference for cross-checking.
        private static RunRecord MakeRecord(
            string status = "Ok",
            string buttonLabel = "Test",
            int? exitCode = 0,
            DateTime? startedAt = null,
            TimeSpan? duration = null,
            string error = null)
        {
            return new RunRecord
            {
                StartedAtUtc     = startedAt ?? DateTime.UtcNow,
                Duration         = duration  ?? TimeSpan.FromMilliseconds(123),
                WorkspaceName    = "TestWorkspace",
                WorkspacePath    = @"C:\fake\test.json",
                ButtonId         = "btn-1",
                ButtonLabel      = buttonLabel,
                Executor         = "powershell",
                ScriptPath       = @"C:\fake\test.ps1",
                Args             = new List<string> { "-X", "1" },
                WorkingDirectory = @"C:\fake",
                ExitCode         = exitCode,
                Status           = status,
                ErrorMessage     = error,
            };
        }

        [Fact]
        public void Open_Creates_Database_File()
        {
            using var hist = new RunHistory(_dbPath);
            Assert.False(hist.Disabled, hist.DisabledReason);
            Assert.True(File.Exists(_dbPath),
                "Expected SQLite DB file to be created on first open");
        }

        [Fact]
        public void Sqlite_Provider_Initializes_Cleanly()
        {
            // Pins the test environment: SQLitePCLRaw must find its
            // native e_sqlite3.dll, the BCL System.Runtime.CompilerServices.Unsafe
            // assembly load must resolve, and Microsoft.Data.Sqlite's
            // type initializer must complete. If any of those break
            // (shadow copy, binding redirect, native dll missing) this
            // test surfaces the actual reason via the inner-exception
            // chain instead of the bare "TypeInitializationException"
            // you get from any other Sqlite-touching test.
            try
            {
                SQLitePCL.Batteries.Init();
                using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
                c.Open();
            }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder();
                for (var e = ex; e != null; e = e.InnerException)
                    sb.AppendLine(e.GetType().FullName + ": " + e.Message);
                Assert.Fail("Sqlite init failed:\n" + sb);
            }
        }

        [Fact]
        public void Record_Then_GetRecent_Roundtrips_All_Fields()
        {
            using var hist = new RunHistory(_dbPath);
            var record = MakeRecord(buttonLabel: "Test Run", exitCode: 7);
            hist.Record(record);

            var rows = hist.GetRecent(10);
            Assert.Single(rows);
            var r = rows[0];
            Assert.Equal("Test Run",      r.ButtonLabel);
            Assert.Equal("powershell",    r.Executor);
            Assert.Equal("Ok",            r.Status);
            Assert.Equal(7,               r.ExitCode);
            Assert.Equal(new[] { "-X", "1" }, r.Args);
            Assert.Equal("TestWorkspace", r.WorkspaceName);
        }

        [Fact]
        public void GetRecent_Orders_By_Most_Recent_First()
        {
            using var hist = new RunHistory(_dbPath);
            var t0 = DateTime.UtcNow;
            // Insert in reverse chronological order to confirm the
            // ORDER BY drives output, not insertion order.
            hist.Record(MakeRecord(buttonLabel: "oldest",  startedAt: t0.AddSeconds(-30)));
            hist.Record(MakeRecord(buttonLabel: "middle",  startedAt: t0.AddSeconds(-15)));
            hist.Record(MakeRecord(buttonLabel: "newest",  startedAt: t0));
            var rows = hist.GetRecent(10);
            Assert.Equal(3, rows.Count);
            Assert.Equal("newest", rows[0].ButtonLabel);
            Assert.Equal("middle", rows[1].ButtonLabel);
            Assert.Equal("oldest", rows[2].ButtonLabel);
        }

        [Fact]
        public void GetRecent_Honors_Limit()
        {
            using var hist = new RunHistory(_dbPath);
            for (int i = 0; i < 10; i++)
                hist.Record(MakeRecord(buttonLabel: "run-" + i));
            var rows = hist.GetRecent(3);
            Assert.Equal(3, rows.Count);
        }

        [Fact]
        public void Record_Null_Is_NoOp()
        {
            using var hist = new RunHistory(_dbPath);
            hist.Record(null);          // must not throw, must not write a row
            Assert.Empty(hist.GetRecent(10));
        }

        [Fact]
        public void Record_Failed_Status_Carries_ErrorMessage()
        {
            using var hist = new RunHistory(_dbPath);
            hist.Record(MakeRecord(status: "Failed", exitCode: 99,
                error: "Something went wrong"));
            var r = hist.GetRecent(1)[0];
            Assert.Equal("Failed",                r.Status);
            Assert.Equal(99,                      r.ExitCode);
            Assert.Equal("Something went wrong",  r.ErrorMessage);
        }

        [Fact]
        public void Record_Cancelled_Status_Roundtrips()
        {
            using var hist = new RunHistory(_dbPath);
            hist.Record(MakeRecord(status: "Cancelled", exitCode: null));
            var r = hist.GetRecent(1)[0];
            Assert.Equal("Cancelled", r.Status);
            Assert.Null(r.ExitCode);
        }

        [Fact]
        public void Record_Preserves_Args_With_Special_Chars()
        {
            using var hist = new RunHistory(_dbPath);
            var args = new List<string> { "with space", "with \"quote\"", "C:\\path\\back\\slashes" };
            var record = MakeRecord();
            record.Args = args;
            hist.Record(record);
            var r = hist.GetRecent(1)[0];
            Assert.Equal(args, r.Args);
        }

        [Fact]
        public void Clear_Empties_The_Table_And_Returns_Count()
        {
            using var hist = new RunHistory(_dbPath);
            for (int i = 0; i < 5; i++) hist.Record(MakeRecord());
            Assert.Equal(5, hist.GetRecent(100).Count);
            int deleted = hist.Clear();
            Assert.Equal(5, deleted);
            Assert.Empty(hist.GetRecent(10));
        }

        [Fact]
        public void Disposed_Store_Refuses_New_Records()
        {
            var hist = new RunHistory(_dbPath);
            hist.Record(MakeRecord());
            hist.Dispose();
            // After dispose, Record should silently no-op rather than
            // throw. The user might still have an in-flight task that
            // tries to Record on shutdown.
            hist.Record(MakeRecord(buttonLabel: "after-dispose"));
            // Reopening to confirm the post-dispose record was NOT written.
            using var hist2 = new RunHistory(_dbPath);
            var rows = hist2.GetRecent(10);
            Assert.Single(rows);
            Assert.NotEqual("after-dispose", rows[0].ButtonLabel);
        }

        [Fact]
        public void Persists_Across_Instances()
        {
            // Open, write, dispose; reopen, read.
            using (var hist = new RunHistory(_dbPath))
            {
                hist.Record(MakeRecord(buttonLabel: "persisted"));
            }
            using (var hist2 = new RunHistory(_dbPath))
            {
                var rows = hist2.GetRecent(10);
                Assert.Single(rows);
                Assert.Equal("persisted", rows[0].ButtonLabel);
            }
        }

        [Fact]
        public void Disabled_When_Path_Is_Unreachable()
        {
            // A directory that we can't create (the path crosses a
            // non-existent drive). Should NOT throw -- should Disabled.
            string badPath = @"Q:\nonexistent\drive\here\history.db";
            using var hist = new RunHistory(badPath);
            Assert.True(hist.Disabled);
            Assert.False(string.IsNullOrEmpty(hist.DisabledReason));
            // A disabled store accepts Record/GetRecent without throwing.
            hist.Record(MakeRecord());
            Assert.Empty(hist.GetRecent(10));
        }

        [Fact]
        public async Task Concurrent_Records_Are_Serialized_Safely()
        {
            // The dispatcher is single-flight so this isn't a production
            // path, but the instance lock is supposed to make Record
            // safe under contention regardless. Stress with many threads
            // recording at once and confirm we end up with the right
            // total row count.
            using var hist = new RunHistory(_dbPath);
            const int threads = 4;
            const int perThread = 20;
            await Task.WhenAll(Enumerable.Range(0, threads).Select(t =>
                Task.Run(() =>
                {
                    for (int i = 0; i < perThread; i++)
                        hist.Record(MakeRecord(buttonLabel: $"t{t}-{i}"));
                })));
            Assert.Equal(threads * perThread, hist.GetRecent(1000).Count);
        }

        [Fact]
        public void Database_Path_Reported_Even_When_Disabled()
        {
            string badPath = @"Q:\bad\path.db";
            using var hist = new RunHistory(badPath);
            Assert.Equal(badPath, hist.DatabasePath);
        }
    }
}
