using System;
using System.IO;
using System.Linq;
using ScriptDeck.Workspace;
using Xunit;

namespace ScriptDeck.Tests
{
    /// <summary>
    /// RecentWorkspaces uses a real JSON file under LocalAppData by default,
    /// but the testable ctor takes a path. Each test gets a fresh tempdir
    /// + storefile so they can't tread on each other.
    /// </summary>
    public class RecentWorkspacesTests : IDisposable
    {
        private readonly string _tmpDir;
        private readonly string _storePath;

        public RecentWorkspacesTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "ScriptDeckTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
            _storePath = Path.Combine(_tmpDir, "recent.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
            catch { }
        }

        // Helper: create a real workspace-shaped file so GetLive's
        // file-existence check counts it as live.
        private string CreateExistingFile(string name = null)
        {
            string p = Path.Combine(_tmpDir, name ?? (Guid.NewGuid().ToString("N") + ".json"));
            File.WriteAllText(p, "{}");
            return p;
        }

        [Fact]
        public void Add_Then_GetLive_Returns_The_Path()
        {
            var rw = new RecentWorkspaces(_storePath);
            var f = CreateExistingFile();
            rw.Add(f);
            Assert.Equal(new[] { Path.GetFullPath(f) }, rw.GetLive());
        }

        [Fact]
        public void Add_Most_Recent_First()
        {
            var rw = new RecentWorkspaces(_storePath);
            var a = CreateExistingFile("a.json");
            var b = CreateExistingFile("b.json");
            rw.Add(a);
            rw.Add(b);
            Assert.Equal(new[] { Path.GetFullPath(b), Path.GetFullPath(a) }, rw.GetLive());
        }

        [Fact]
        public void Add_Dedupes_Case_Insensitively()
        {
            var rw = new RecentWorkspaces(_storePath);
            var a = CreateExistingFile("a.json");
            rw.Add(a);
            rw.Add(a.ToUpperInvariant());
            // Same path twice with different casing should occupy ONE slot.
            Assert.Single(rw.GetLive());
        }

        [Fact]
        public void Add_Caps_At_Capacity()
        {
            var rw = new RecentWorkspaces(_storePath);
            var files = Enumerable.Range(0, RecentWorkspaces.Capacity + 5)
                .Select(i => CreateExistingFile($"f{i}.json"))
                .ToArray();
            foreach (var f in files) rw.Add(f);
            // The list keeps the last <Capacity> additions, most-recent first.
            var live = rw.GetLive();
            Assert.Equal(RecentWorkspaces.Capacity, live.Count);
            // The most recently added one is at the top.
            Assert.Equal(Path.GetFullPath(files.Last()), live[0]);
        }

        [Fact]
        public void Add_Whitespace_Or_Null_Is_NoOp()
        {
            var rw = new RecentWorkspaces(_storePath);
            rw.Add(null);
            rw.Add("");
            rw.Add("   ");
            Assert.Empty(rw.GetLive());
        }

        [Fact]
        public void Add_Unparseable_Path_Is_Silently_Refused()
        {
            var rw = new RecentWorkspaces(_storePath);
            // Invalid path chars trip Path.GetFullPath. Behavior should
            // be "swallow and continue" -- never crash on a hand-edited
            // recent.json or a weird Add call.
            rw.Add("C:\\<>:\"|?*");  // contains illegal chars
            Assert.Empty(rw.GetLive());
        }

        [Fact]
        public void GetLive_Prunes_Missing_Files()
        {
            var rw = new RecentWorkspaces(_storePath);
            var keep = CreateExistingFile("keep.json");
            var ghost = Path.Combine(_tmpDir, "ghost.json"); // never created
            rw.Add(keep);
            rw.Add(ghost);
            // The ghost file doesn't exist -- GetLive should drop it AND
            // persist the prune so it doesn't keep showing up next time.
            var live = rw.GetLive();
            Assert.Single(live);
            Assert.Equal(Path.GetFullPath(keep), live[0]);
        }

        [Fact]
        public void Clear_Empties_The_List()
        {
            var rw = new RecentWorkspaces(_storePath);
            rw.Add(CreateExistingFile("a.json"));
            rw.Add(CreateExistingFile("b.json"));
            rw.Clear();
            Assert.Empty(rw.GetLive());
        }

        [Fact]
        public void Survives_Missing_Store_File()
        {
            // No store file yet -- ctor should not throw, list is empty.
            Assert.False(File.Exists(_storePath));
            var rw = new RecentWorkspaces(_storePath);
            Assert.Empty(rw.GetLive());
        }

        [Fact]
        public void Survives_Malformed_Store_File()
        {
            // Hand-corrupt the file then try to load. Should reset to
            // empty rather than throw.
            File.WriteAllText(_storePath, "this is not json at all { ] }");
            var rw = new RecentWorkspaces(_storePath);
            Assert.Empty(rw.GetLive());
        }

        [Fact]
        public void Persists_Across_Instances()
        {
            var rw1 = new RecentWorkspaces(_storePath);
            var f = CreateExistingFile();
            rw1.Add(f);
            // Construct a SECOND instance pointing at the same file --
            // simulates restart -- and verify the entry persisted.
            var rw2 = new RecentWorkspaces(_storePath);
            Assert.Equal(new[] { Path.GetFullPath(f) }, rw2.GetLive());
        }

        [Fact]
        public void Add_Re_Adds_Existing_To_Top()
        {
            var rw = new RecentWorkspaces(_storePath);
            var a = CreateExistingFile("a.json");
            var b = CreateExistingFile("b.json");
            rw.Add(a);
            rw.Add(b);
            rw.Add(a);
            Assert.Equal(new[] { Path.GetFullPath(a), Path.GetFullPath(b) }, rw.GetLive());
        }
    }
}
