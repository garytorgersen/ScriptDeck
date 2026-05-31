using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Persistent list of computer names the user manages via
    /// Tools -> Manage Computers. Surfaces as the dropdown source for
    /// any workspace shared input whose <c>normalize</c> rule is
    /// <c>computerName</c>.
    ///
    /// Stored as JSON at <c>%LocalAppData%\ScriptDeck\computers.json</c>,
    /// matching the per-user pattern used by recent.json / history.db.
    /// The list is per-user (different operators on the same box manage
    /// different fleets) and per-machine (paths and hostnames don't
    /// roam meaningfully).
    ///
    /// Shape on disk:
    /// <code>
    /// { "version": 1, "computers": [ "web01", "db02", ... ] }
    /// </code>
    /// The envelope's <c>version</c> field is a forward-compatibility
    /// hook -- future schema changes (per-entry metadata, tags, etc.)
    /// bump the version and the loader can branch.
    ///
    /// Failure mode: every operation is best-effort. Missing file =
    /// empty list. Malformed file = empty list + a single warning the
    /// caller can surface. Write failure on Save (write-protected
    /// install? full disk?) throws -- the dialog catches and shows
    /// the OS error so the user knows their changes weren't persisted.
    /// </summary>
    public sealed class ComputerListStore
    {
        private const int CurrentVersion = 1;

        private readonly string _storePath;
        private List<string> _items = new List<string>();

        /// <summary>
        /// Fires when Save successfully persists a change. The
        /// WorkspaceRenderer subscribes so live ComboBoxes refresh
        /// their DataSource without the user reopening the workspace.
        /// </summary>
        public event Action Changed;

        public ComputerListStore() : this(DefaultStorePath()) { }

        // Constructor overload for tests / future override. Production
        // callers use the parameterless ctor.
        public ComputerListStore(string storePath)
        {
            _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
            TryLoad();
        }

        public static string DefaultStorePath()
        {
            // Same parent directory as history.db / recent.json -- keeps
            // all of ScriptDeck's per-user state in one folder so users
            // (and uninstall scripts) don't have to hunt for it.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptDeck");
            return Path.Combine(dir, "computers.json");
        }

        /// <summary>
        /// Snapshot of the current list. Sorted alphabetically (case-
        /// insensitive) because the dialog and the dropdown both want
        /// it that way, and sorting once here is cheaper than sorting
        /// at every render.
        /// </summary>
        public IList<string> GetAll()
        {
            return _items
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Case-insensitive membership check. Used by Add to refuse
        /// duplicates without surprising the caller.
        /// </summary>
        public bool Contains(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            return _items.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Adds an entry. Returns true if added, false if it was a
        /// duplicate (case-insensitive) or blank. Does NOT persist --
        /// the caller batches Add/Remove operations and ends with one
        /// <see cref="Save"/> call (matches the dialog's "edit + apply
        /// on Save" workflow and minimizes disk writes).
        /// </summary>
        public bool Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            if (Contains(trimmed)) return false;
            _items.Add(trimmed);
            return true;
        }

        /// <summary>
        /// Removes an entry by case-insensitive match. Returns true if
        /// something was removed.
        /// </summary>
        public bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            int before = _items.Count;
            _items.RemoveAll(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
            return _items.Count < before;
        }

        /// <summary>
        /// Replaces the entire list. Used by the dialog's Cancel path
        /// to revert without touching disk, and by tests to seed state.
        /// </summary>
        public void Replace(IEnumerable<string> entries)
        {
            _items = (entries ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Reads a text file (one entry per line; lines starting with
        /// '#' and blank lines are ignored; whitespace trimmed) and
        /// merges into the current list. Returns (added, duplicates)
        /// so the dialog can show "Imported N new entries; M already
        /// existed". Does NOT persist -- caller must Save.
        /// </summary>
        public (int Added, int Duplicates) ImportFromTextFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return (0, 0);
            int added = 0, dupes = 0;
            // ReadAllLines is fine -- a fleet list of even tens of
            // thousands of lines is < 1 MB. Streaming buys nothing.
            foreach (var raw in File.ReadAllLines(path))
            {
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (Add(line)) added++;
                else dupes++;
            }
            return (added, dupes);
        }

        /// <summary>
        /// Persists the current list to disk and fires <see cref="Changed"/>.
        /// Throws on IO failure -- callers (the Manage Computers dialog)
        /// catch and present the OS error directly so the user knows
        /// their edits didn't stick.
        /// </summary>
        public void Save()
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var envelope = new StoreEnvelope
            {
                Version = CurrentVersion,
                Computers = _items
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            File.WriteAllText(_storePath,
                JsonConvert.SerializeObject(envelope, Formatting.Indented));

            try { Changed?.Invoke(); } catch { /* subscriber failure mustn't break Save */ }
        }

        // ---- persistence ----

        private void TryLoad()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                var json = File.ReadAllText(_storePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                // Tolerate two shapes for robustness:
                //   - Current: { version, computers: [...] }
                //   - Legacy/hand-edited: bare array [...] (matches the
                //     RecentWorkspaces format people might pattern-match)
                json = json.TrimStart();
                if (json.StartsWith("["))
                {
                    var arr = JsonConvert.DeserializeObject<List<string>>(json);
                    if (arr != null)
                        Replace(arr);
                }
                else
                {
                    var env = JsonConvert.DeserializeObject<StoreEnvelope>(json);
                    if (env?.Computers != null)
                        Replace(env.Computers);
                }
            }
            catch
            {
                // Malformed file -- start fresh rather than crashing the
                // app. The user can Manage Computers to repopulate.
                _items = new List<string>();
            }
        }

        // JSON envelope wrapper. Internal POCO -- the public API exposes
        // List<string> directly via GetAll(), keeping the version field
        // an on-disk concern. JsonProperty attributes force lowercase
        // keys on disk so the file is friendly to hand-editing and
        // stable against any future change to Newtonsoft's default
        // casing or a project-wide ContractResolver swap.
        private sealed class StoreEnvelope
        {
            [JsonProperty("version")]
            public int Version { get; set; }
            [JsonProperty("computers")]
            public List<string> Computers { get; set; }
        }
    }
}
