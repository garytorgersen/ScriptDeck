using System;

using System.IO;
using Newtonsoft.Json;

namespace ScriptDeck.Workspace
{
    /// <summary>
    /// Reads/writes <see cref="Workspace"/> documents to disk as JSON.
    ///
    /// Single source of truth for workspace persistence. All schema-version
    /// gating, default-value backfill, and ScriptsRoot resolution happens
    /// here — callers get a fully-populated, validated <see cref="Workspace"/>.
    /// </summary>
    public static class WorkspaceLoader
    {
        // Bumped when the schema acquires breaking changes. The loader
        // refuses files whose Version exceeds this so a workspace from a
        // newer build doesn't silently lose fields when opened by an
        // older ScriptDeck.
        public const int CurrentSchemaVersion = 1;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            // Tolerate humans editing the JSON: ignore unknown fields rather
            // than throwing, and preserve null-vs-missing semantics so we
            // can backfill defaults below.
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.Indented,
        };

        public static Workspace Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Workspace path is required.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Workspace file not found.", path);

            var json = File.ReadAllText(path);
            var ws = JsonConvert.DeserializeObject<Workspace>(json, Settings)
                     ?? throw new InvalidDataException("Workspace JSON deserialized to null.");

            if (ws.Version > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Workspace schema v{ws.Version} is newer than this build (v{CurrentSchemaVersion}). " +
                    "Update ScriptDeck or open this workspace in a newer build.");
            }

            // Default ScriptsRoot to the directory holding the workspace
            // file. Relative ScriptPath values then resolve naturally for
            // users who keep their scripts next to the workspace.
            if (string.IsNullOrWhiteSpace(ws.ScriptsRoot))
                ws.ScriptsRoot = Path.GetDirectoryName(Path.GetFullPath(path));

            // Belt-and-suspenders: a JSON file with `"buttons": null` would
            // otherwise NRE on the first foreach. Guarantee non-null
            // collections everywhere callers iterate.
            ws.SharedInputs ??= new System.Collections.Generic.List<SharedInput>();
            ws.Tabs         ??= new System.Collections.Generic.List<Tab>();
            ws.Menus        ??= new System.Collections.Generic.List<MenuDefinition>();
            foreach (var t in ws.Tabs)
            {
                t.Buttons ??= new System.Collections.Generic.List<Button>();
                t.Groups  ??= new System.Collections.Generic.List<ButtonGroup>();
            }
            foreach (var m in ws.Menus)
                m.Items   ??= new System.Collections.Generic.List<Button>();

            return ws;
        }

        public static void Save(Workspace workspace, string path)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Workspace path is required.", nameof(path));

            workspace.Version = CurrentSchemaVersion;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Atomic-replace pattern: write fresh content to a temp file,
            // then swap it into place with File.Replace. File.Replace is
            // a single-call OS-level atomic rename on NTFS -- the user's
            // workspace file is never absent, even if the process is
            // killed mid-write. The .bak file is the previous version
            // (cleaned up on success below).
            //
            // First-save case: when `path` doesn't exist yet, File.Replace
            // throws because there's nothing to replace. Fall through to
            // a plain File.Move -- the temp -> final move is the closest
            // we get to atomicity for the brand-new case (and on net48
            // there's no File.Move overload that overwrites, so we'd be
            // back to delete-then-move otherwise).
            var tmp = path + ".tmp";
            var bak = path + ".bak";

            File.WriteAllText(tmp, JsonConvert.SerializeObject(workspace, Settings));

            if (File.Exists(path))
            {
                // Replace performs the swap in one OS call. The destination
                // is always present afterwards; the .bak holds the prior
                // version until we delete it.
                File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                try { if (File.Exists(bak)) File.Delete(bak); }
                catch { /* best-effort cleanup; .bak is fine to leave behind */ }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
