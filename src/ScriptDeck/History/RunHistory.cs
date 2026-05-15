using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace ScriptDeck.History
{
    /// <summary>
    /// SQLite-backed run-history store. One row per finished execution
    /// (Ok / Failed / Cancelled all recorded — the user wants an honest
    /// record, including the cancellations they triggered).
    ///
    /// Database location: <c>%LocalAppData%\ScriptDeck\history.db</c>. We
    /// deliberately do NOT put it next to the workspace — multiple
    /// workspaces share one history, and per-machine LocalAppData is the
    /// idiomatic place for per-user state that shouldn't sync.
    ///
    /// Concurrency: the dispatcher is single-flight, so writes are
    /// serialized at the call-site level. The instance lock is belt-and-
    /// suspenders against future callers (e.g. a "clear history" button)
    /// stepping on a Record from a finishing run.
    ///
    /// Failure mode: if SQLite can't open or the schema migration fails,
    /// the store flags itself as <see cref="Disabled"/> and silently
    /// no-ops. A history feature should never block the user from
    /// running scripts — losing a record is annoying, losing the ability
    /// to launch is unacceptable.
    /// </summary>
    public sealed class RunHistory : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _connectionString;
        // One persistent connection for the lifetime of the store.
        // Microsoft.Data.Sqlite connections aren't thread-safe, but
        // every public method acquires _gate before touching this so
        // serialization is enforced at the call-site level. Keeping
        // the connection open eliminates ~5-10 ms per Record from
        // open/setup/close churn -- noticeable on rapid testing.
        // Null when Disabled (init failed) or after Dispose.
        private SqliteConnection _connection;
        private bool _disposed;

        /// <summary>True if the store hit a fatal init error and is no-opping.</summary>
        public bool Disabled { get; private set; }

        /// <summary>The on-disk path of the database (informational; stable for the life of the instance).</summary>
        public string DatabasePath { get; }

        /// <summary>If init failed, the error message. Surface in the UI for diagnostics.</summary>
        public string DisabledReason { get; private set; }

        public RunHistory()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptDeck",
                "history.db"))
        { }

        /// <summary>
        /// Path-override constructor for tests. Production code calls the
        /// parameterless ctor which uses <c>%LocalAppData%\ScriptDeck\history.db</c>;
        /// tests pass a temp path so they don't trample the user's real
        /// history database. Internal so production callers can't
        /// accidentally bypass the LocalAppData convention.
        /// </summary>
        internal RunHistory(string databasePath)
        {
            DatabasePath = databasePath;
            string dir = Path.GetDirectoryName(databasePath);

            // Mode=ReadWriteCreate: open or create. Cache=Shared keeps us from
            // re-opening a connection on every call (we still open per-Record
            // because Microsoft.Data.Sqlite's connection isn't thread-safe).
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            try
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _connection = new SqliteConnection(_connectionString);
                _connection.Open();
                EnsureSchema();
            }
            catch (Exception ex)
            {
                Disabled = true;
                DisabledReason = ex.Message;
                try { _connection?.Dispose(); } catch { }
                _connection = null;
            }
        }

        private void EnsureSchema()
        {
            using (var cmd = _connection.CreateCommand())
            {
                    // started_at stored as ISO-8601 UTC text. SQLite has no
                    // first-class datetime type; ISO sorts correctly as TEXT
                    // and round-trips through DateTime.Parse cleanly.
                    //
                    // args_json stores the full args list as a JSON array so
                    // we don't need a side table. Most arg lists are tiny;
                    // the JSON overhead is negligible and keeps queries simple.
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS runs (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at        TEXT NOT NULL,
    duration_ms       INTEGER NOT NULL,
    workspace_path    TEXT,
    workspace_name    TEXT,
    button_id         TEXT,
    button_label      TEXT,
    executor          TEXT,
    script_path       TEXT,
    args_json         TEXT,
    working_directory TEXT,
    exit_code         INTEGER,
    status            TEXT NOT NULL,
    error_message     TEXT
);
CREATE INDEX IF NOT EXISTS ix_runs_started_at ON runs (started_at DESC);
";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Record a finished run. Swallows all SQLite errors (logs nothing —
        /// the dispatcher already logs the run; a history failure shouldn't
        /// double-write to the user's console).
        /// </summary>
        public void Record(RunRecord row)
        {
            if (row == null) return;
            if (Disabled || _disposed) return;

            lock (_gate)
            {
                if (_connection == null) return;
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT INTO runs
    (started_at, duration_ms, workspace_path, workspace_name,
     button_id, button_label, executor, script_path, args_json,
     working_directory, exit_code, status, error_message)
VALUES
    (@started_at, @duration_ms, @workspace_path, @workspace_name,
     @button_id, @button_label, @executor, @script_path, @args_json,
     @working_directory, @exit_code, @status, @error_message);
";
                        cmd.Parameters.AddWithValue("@started_at",
                            row.StartedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                        cmd.Parameters.AddWithValue("@duration_ms", (long)row.Duration.TotalMilliseconds);
                        cmd.Parameters.AddWithValue("@workspace_path", (object)row.WorkspacePath ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@workspace_name", (object)row.WorkspaceName ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@button_id", (object)row.ButtonId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@button_label", (object)row.ButtonLabel ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@executor", (object)row.Executor ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@script_path", (object)row.ScriptPath ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@args_json",
                            JsonConvert.SerializeObject(row.Args ?? new List<string>()));
                        cmd.Parameters.AddWithValue("@working_directory", (object)row.WorkingDirectory ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@exit_code",
                            row.ExitCode.HasValue ? (object)row.ExitCode.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@status", row.Status ?? "Unknown");
                        cmd.Parameters.AddWithValue("@error_message", (object)row.ErrorMessage ?? DBNull.Value);

                        cmd.ExecuteNonQuery();
                    }
                }
                catch
                {
                    // Best-effort. We don't want to take down the run path
                    // because a write to history failed. The next successful
                    // Record will continue from where we are; the failed row
                    // is simply lost.
                }
            }
        }

        /// <summary>
        /// Most recent runs, newest first. <paramref name="limit"/> caps at
        /// 1000 to keep the dialog responsive — pagination's a Phase 8
        /// concern if it ever matters.
        /// </summary>
        public IList<RunRecord> GetRecent(int limit)
        {
            var result = new List<RunRecord>();
            if (Disabled || _disposed) return result;
            if (limit <= 0) return result;
            if (limit > 1000) limit = 1000;

            lock (_gate)
            {
                if (_connection == null) return result;
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
SELECT id, started_at, duration_ms, workspace_path, workspace_name,
       button_id, button_label, executor, script_path, args_json,
       working_directory, exit_code, status, error_message
FROM runs
ORDER BY id DESC
LIMIT @limit;
";
                        cmd.Parameters.AddWithValue("@limit", limit);
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read())
                            {
                                result.Add(ReadRow(rdr));
                            }
                        }
                    }
                }
                catch
                {
                    // Same philosophy as Record — return whatever we have.
                }
            }
            return result;
        }

        /// <summary>Delete every row. Used by the "Clear" button in the history dialog.</summary>
        public int Clear()
        {
            if (Disabled || _disposed) return 0;
            lock (_gate)
            {
                if (_connection == null) return 0;
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        // DELETE then VACUUM — VACUUM reclaims the file
                        // size so the db file doesn't grow unboundedly
                        // across many clears. Cheap on a tiny db.
                        cmd.CommandText = "DELETE FROM runs;";
                        int n = cmd.ExecuteNonQuery();
                        cmd.CommandText = "VACUUM;";
                        cmd.ExecuteNonQuery();
                        return n;
                    }
                }
                catch { return 0; }
            }
        }

        private static RunRecord ReadRow(SqliteDataReader r)
        {
            // ISO-8601 round-trip: stored as "o" format, parsed with
            // RoundtripKind so the resulting DateTime keeps Utc kind.
            DateTime startedUtc;
            try
            {
                startedUtc = DateTime.Parse(
                    r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (startedUtc.Kind == DateTimeKind.Unspecified)
                    startedUtc = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc);
            }
            catch
            {
                startedUtc = DateTime.UtcNow;
            }

            IList<string> args = new List<string>();
            if (!r.IsDBNull(9))
            {
                var json = r.GetString(9);
                try
                {
                    args = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
                }
                catch
                {
                    // Forward-compat: if the column has a non-JSON-array
                    // value (a future schema change?), fall back to a
                    // single-element list so the user can still see something.
                    args = new List<string> { json };
                }
            }

            return new RunRecord
            {
                Id               = r.GetInt64(0),
                StartedAtUtc     = startedUtc,
                Duration         = TimeSpan.FromMilliseconds(r.GetInt64(2)),
                WorkspacePath    = r.IsDBNull(3) ? null : r.GetString(3),
                WorkspaceName    = r.IsDBNull(4) ? null : r.GetString(4),
                ButtonId         = r.IsDBNull(5) ? null : r.GetString(5),
                ButtonLabel      = r.IsDBNull(6) ? null : r.GetString(6),
                Executor         = r.IsDBNull(7) ? null : r.GetString(7),
                ScriptPath       = r.IsDBNull(8) ? null : r.GetString(8),
                Args             = args,
                WorkingDirectory = r.IsDBNull(10) ? null : r.GetString(10),
                ExitCode         = r.IsDBNull(11) ? (int?)null : (int)r.GetInt64(11),
                Status           = r.IsDBNull(12) ? null : r.GetString(12),
                ErrorMessage     = r.IsDBNull(13) ? null : r.GetString(13),
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Close the persistent connection under the gate so we
            // don't race a final Record from the dispatcher's finally
            // block. Then drop the pool to release file handles --
            // the db file can be moved / deleted immediately after.
            lock (_gate)
            {
                try { _connection?.Dispose(); } catch { }
                _connection = null;
            }
            try { SqliteConnection.ClearAllPools(); } catch { }
        }
    }
}
