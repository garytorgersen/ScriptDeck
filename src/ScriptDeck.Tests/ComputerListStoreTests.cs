using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScriptDeck.Hosting;
using Xunit;

namespace ScriptDeck.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ComputerListStore"/>. Each test creates
    /// its own temp file path so they're safe to run in parallel and
    /// don't touch the real %LocalAppData%\ScriptDeck\computers.json.
    /// </summary>
    public class ComputerListStoreTests : IDisposable
    {
        // Per-test scratch path. xUnit instantiates a fresh class
        // instance per test, so each test gets its own temp file.
        private readonly string _tmpPath;

        public ComputerListStoreTests()
        {
            _tmpPath = Path.Combine(
                Path.GetTempPath(),
                "scriptdeck-computers-" + Guid.NewGuid().ToString("N") + ".json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { /* swallow */ }
        }

        [Fact]
        public void Missing_File_Loads_As_Empty_List()
        {
            var store = new ComputerListStore(_tmpPath);
            Assert.Empty(store.GetAll());
        }

        [Fact]
        public void Add_Then_Save_Roundtrips_Through_Json()
        {
            var store = new ComputerListStore(_tmpPath);
            Assert.True(store.Add("web01"));
            Assert.True(store.Add("db02"));
            Assert.True(store.Add("laptop-gtw"));
            store.Save();

            // Reload via a fresh instance to prove the data round-tripped
            // through actual disk JSON, not just in-memory state.
            var reloaded = new ComputerListStore(_tmpPath);
            var all = reloaded.GetAll();
            Assert.Equal(3, all.Count);
            // GetAll returns sorted alphabetically (case-insensitive).
            Assert.Equal("db02",       all[0]);
            Assert.Equal("laptop-gtw", all[1]);
            Assert.Equal("web01",      all[2]);
        }

        [Fact]
        public void Saved_File_Uses_Versioned_Envelope_Shape()
        {
            var store = new ComputerListStore(_tmpPath);
            store.Add("alpha");
            store.Save();

            // The on-disk shape must be { version, computers: [...] }
            // so future schema evolution can branch on version.
            var raw = File.ReadAllText(_tmpPath);
            var obj = JObject.Parse(raw);
            Assert.NotNull(obj["version"]);
            Assert.Equal(1, (int)obj["version"]);
            var arr = obj["computers"] as JArray;
            Assert.NotNull(arr);
            Assert.Single(arr);
            Assert.Equal("alpha", (string)arr[0]);
        }

        [Fact]
        public void Add_Refuses_Case_Insensitive_Duplicate()
        {
            var store = new ComputerListStore(_tmpPath);
            Assert.True(store.Add("Server01"));
            Assert.False(store.Add("server01"));
            Assert.False(store.Add("SERVER01"));
            Assert.Single(store.GetAll());
        }

        [Fact]
        public void Add_Trims_Whitespace_And_Ignores_Blank()
        {
            var store = new ComputerListStore(_tmpPath);
            Assert.True(store.Add("  trimmed  "));
            Assert.False(store.Add("   "));
            Assert.False(store.Add(""));
            Assert.False(store.Add(null));
            var all = store.GetAll();
            Assert.Single(all);
            Assert.Equal("trimmed", all[0]);
        }

        [Fact]
        public void Remove_Is_Case_Insensitive_And_Returns_True_Only_When_Something_Removed()
        {
            var store = new ComputerListStore(_tmpPath);
            store.Add("HostA");
            store.Add("HostB");
            Assert.True(store.Remove("hosta"));
            Assert.False(store.Remove("hosta")); // already gone
            Assert.Single(store.GetAll());
            Assert.Equal("HostB", store.GetAll()[0]);
        }

        [Fact]
        public void Replace_Dedupes_And_Trims()
        {
            var store = new ComputerListStore(_tmpPath);
            store.Replace(new[] { "a", "B", "a", "  c  ", "", null, "B" });
            var all = store.GetAll();
            Assert.Equal(3, all.Count);
            // a + B + c, sorted case-insensitively
            Assert.Contains("a", all);
            Assert.Contains("B", all);
            Assert.Contains("c", all);
        }

        [Fact]
        public void Import_Strips_Comments_And_Blank_Lines_And_Trims()
        {
            string importPath = _tmpPath + ".import.txt";
            try
            {
                File.WriteAllText(importPath, string.Join(Environment.NewLine, new[]
                {
                    "# Servers",
                    "web01",
                    "  db02  ",   // trim
                    "",            // blank
                    "# Workstations",
                    "laptop-gtw",
                    "# trailing comment",
                }));

                var store = new ComputerListStore(_tmpPath);
                var (added, dupes) = store.ImportFromTextFile(importPath);
                Assert.Equal(3, added);
                Assert.Equal(0, dupes);

                var all = store.GetAll();
                Assert.Equal(3, all.Count);
                Assert.Contains("web01",      all);
                Assert.Contains("db02",       all);
                Assert.Contains("laptop-gtw", all);
            }
            finally
            {
                try { if (File.Exists(importPath)) File.Delete(importPath); } catch { /* swallow */ }
            }
        }

        [Fact]
        public void Import_Counts_Duplicates_Separately()
        {
            string importPath = _tmpPath + ".import.txt";
            try
            {
                File.WriteAllText(importPath,
                    "alpha" + Environment.NewLine +
                    "beta"  + Environment.NewLine);

                var store = new ComputerListStore(_tmpPath);
                store.Add("alpha"); // seed a dupe

                var (added, dupes) = store.ImportFromTextFile(importPath);
                Assert.Equal(1, added);   // just beta
                Assert.Equal(1, dupes);   // alpha was already there
                Assert.Equal(2, store.GetAll().Count);
            }
            finally
            {
                try { if (File.Exists(importPath)) File.Delete(importPath); } catch { /* swallow */ }
            }
        }

        [Fact]
        public void Save_Fires_Changed_Once_Per_Call()
        {
            var store = new ComputerListStore(_tmpPath);
            store.Add("a");

            int fires = 0;
            store.Changed += () => fires++;

            store.Save();
            store.Save();
            store.Save();
            Assert.Equal(3, fires);
        }

        [Fact]
        public void Malformed_File_Loads_As_Empty_Not_Throw()
        {
            // Junk bytes -- malformed JSON. Load must tolerate it
            // gracefully (start fresh) rather than crash the app.
            File.WriteAllText(_tmpPath, "this is not json {{{{");
            var store = new ComputerListStore(_tmpPath);
            Assert.Empty(store.GetAll());
        }

        [Fact]
        public void Load_Tolerates_Legacy_Bare_Array_Shape()
        {
            // Hand-edited or pattern-matched-from-recent.json: a bare
            // [..] array instead of the envelope. Should still load.
            File.WriteAllText(_tmpPath,
                JsonConvert.SerializeObject(new[] { "host1", "host2" }));
            var store = new ComputerListStore(_tmpPath);
            var all = store.GetAll();
            Assert.Equal(2, all.Count);
            Assert.Contains("host1", all);
            Assert.Contains("host2", all);
        }
    }
}
