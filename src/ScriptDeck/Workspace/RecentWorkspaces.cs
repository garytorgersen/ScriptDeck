using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ScriptDeck.Workspace
{
    /// <summary>
    /// Most-recently-used list of workspace file paths. Persisted as a
    /// flat JSON array at <c>%LocalAppData%\ScriptDeck\recent.json</c> so
    /// it survives across app restarts but doesn't roam with the user
    /// profile (workspaces are usually local to a machine — paths from
    /// another machine are useless and confusing).
    ///
    /// Concurrency: not thread-safe by design. The Shell calls into this
    /// from the UI thread; saving is a single small file write so a
    /// concurrent reader-writer dance isn't worth the complexity.
    ///
    /// Failure mode: every operation is best-effort. If the file is
    /// missing, malformed, or the directory can't be created, we behave
    /// as if the list were empty and don't surface the error — losing an
    /// MRU is annoying but never blocks the user from doing actual work.
    /// </summary>
    public sealed class RecentWorkspaces
    {
        // 8 slots fits comfortably in a File submenu without wrapping
        // and matches the muscle memory from VS / Notepad++.
        public const int Capacity = 8;

        private readonly string _storePath;
        private List<string> _items = new List<string>();

        public RecentWorkspaces() : this(DefaultStorePath()) { }

        // Constructor overload for tests / future override. Production
        // callers use the parameterless ctor.
        public RecentWorkspaces(string storePath)
        {
            _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
            TryLoad();
        }

        public static string DefaultStorePath()
        {
            // Same parent directory as history.db — keeps all of
            // ScriptDeck's per-user state in one folder so users (and
            // uninstall scripts) don't have to hunt for it.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptDeck");
            return Path.Combine(dir, "recent.json");
        }

        /// <summary>
        /// Snapshot of the current list, most-recent first. Stale paths
        /// (file no longer exists) are filtered OUT here so callers
        /// painting a menu don't have to repeat that work — and pruning
        /// here means the next save drops them permanently.
        /// </summary>
        public IList<string> GetLive()
        {
            var live = new List<string>();
            bool anyMissing = false;
            foreach (var p in _items)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (File.Exists(p)) live.Add(p);
                else anyMissing = true;
            }
            // Persist the prune so the dead entries don't keep showing
            // up after every restart. Best-effort.
            if (anyMissing)
            {
                _items = new List<string>(live);
                TrySave();
            }
            return live;
        }

        /// <summary>
        /// Mark <paramref name="path"/> as just-opened. Moves it to the
        /// top of the list, deduping case-insensitively (Windows paths
        /// are case-insensitive — "C:\foo" and "c:\foo" are the same
        /// workspace and shouldn't take two slots).
        /// </summary>
        public void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            // Normalize to absolute and trim trailing whitespace. We
            // don't lower-case because the on-disk casing is what we
            // want to display — comparison is the only place that needs
            // to be case-insensitive.
            string canonical;
            try { canonical = Path.GetFullPath(path.Trim()); }
            catch { return; } // unparseable path — refuse to record it

            _items.RemoveAll(p => string.Equals(p, canonical, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, canonical);
            if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
            TrySave();
        }

        public void Clear()
        {
            _items.Clear();
            TrySave();
        }

        // ---- persistence ----

        private void TryLoad()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                var json = File.ReadAllText(_storePath);
                var loaded = JsonConvert.DeserializeObject<List<string>>(json);
                if (loaded != null) _items = loaded.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            }
            catch
            {
                // Malformed file — start fresh rather than crashing.
                _items = new List<string>();
            }
        }

        private void TrySave()
        {
            try
            {
                var dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Indent for human-readability — the file is tiny and
                // users curious enough to open it appreciate the nicety.
                File.WriteAllText(_storePath, JsonConvert.SerializeObject(_items, Formatting.Indented));
            }
            catch
            {
                // Disk full / permissions / etc — silently ignore.
                // Losing the MRU is never a reason to interrupt the user.
            }
        }
    }
}
