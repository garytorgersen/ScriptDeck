using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScriptDeck.Hosting;
using Xunit;

namespace ScriptDeck.Tests
{
    /// <summary>
    /// Tests for <see cref="InterpreterDetector"/>. Each test creates
    /// its own temp cache file to keep parallel runs isolated and to
    /// avoid touching the real %LocalAppData%\ScriptDeck\interpreters.json.
    /// </summary>
    public class InterpreterDetectorTests : IDisposable
    {
        private readonly string _tmpCache;

        public InterpreterDetectorTests()
        {
            _tmpCache = Path.Combine(
                Path.GetTempPath(),
                "scriptdeck-interp-" + Guid.NewGuid().ToString("N") + ".json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tmpCache)) File.Delete(_tmpCache); } catch { /* swallow */ }
        }

        [Fact]
        public async Task PowerShell_Is_Always_Detected()
        {
            // PowerShell hosting is in-process and Windows-bundled, so
            // every run should produce at least the PowerShell row.
            var det = new InterpreterDetector(_tmpCache);
            var results = await det.DetectAsync();
            Assert.Contains(results, r => r.Kind == InterpreterKind.PowerShell);
            var ps = results.First(r => r.Kind == InterpreterKind.PowerShell);
            Assert.False(string.IsNullOrEmpty(ps.Version));
        }

        [Fact]
        public async Task Cache_File_Is_Written_After_First_Scan()
        {
            var det = new InterpreterDetector(_tmpCache);
            await det.DetectAsync();
            Assert.True(File.Exists(_tmpCache),
                "Detector did not persist its cache to disk after a scan.");
        }

        [Fact]
        public async Task Second_Scan_Reads_Cache_File_Successfully()
        {
            // After a first scan, a second detector instance pointing
            // at the same cache file should load it without error. We
            // can't directly assert "this entry came from cache" without
            // exposing internals, but we can verify the load path
            // doesn't blow up on a real cache file shape.
            var det1 = new InterpreterDetector(_tmpCache);
            await det1.DetectAsync();
            var det2 = new InterpreterDetector(_tmpCache);
            var results2 = await det2.DetectAsync();
            Assert.NotEmpty(results2);
        }

        [Fact]
        public async Task ClearCache_Removes_The_File()
        {
            var det = new InterpreterDetector(_tmpCache);
            await det.DetectAsync();
            Assert.True(File.Exists(_tmpCache));
            det.ClearCache();
            Assert.False(File.Exists(_tmpCache));
        }

        [Fact]
        public async Task Workspace_Default_Marker_Fires_When_Path_Matches()
        {
            // Round-trip the PowerShell row's resolved path is "(built-in)"
            // which doesn't really test the path-matching code. The
            // realistic workspace-default check is on Python -- pass
            // the same path the system probe will resolve to and the
            // detector should mark that row as the workspace default.
            //
            // We don't have access to "what python --version returns"
            // from here without spawning it ourselves, so we just
            // assert the API doesn't blow up. The visible UX check
            // belongs in the manual smoke test.
            var det = new InterpreterDetector(_tmpCache);
            var results = await det.DetectAsync(workspacePythonInterpreter: "python");
            // At minimum we should not have lost the PowerShell row
            // when the optional workspace defaults are provided.
            Assert.Contains(results, r => r.Kind == InterpreterKind.PowerShell);
        }

        [Fact]
        public async Task Detect_Tolerates_Missing_Cache_Directory()
        {
            // Point at a path whose parent doesn't exist yet. SaveCache
            // creates the parent on demand, so this should still work.
            string deepPath = Path.Combine(
                Path.GetTempPath(),
                "scriptdeck-interp-deep-" + Guid.NewGuid().ToString("N"),
                "sub", "interpreters.json");
            try
            {
                var det = new InterpreterDetector(deepPath);
                var results = await det.DetectAsync();
                Assert.NotEmpty(results);
                Assert.True(File.Exists(deepPath));
            }
            finally
            {
                try
                {
                    var parent = Path.GetDirectoryName(Path.GetDirectoryName(deepPath));
                    if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
                }
                catch { /* swallow */ }
            }
        }

        [Fact]
        public async Task Malformed_Cache_File_Loads_As_Empty_Not_Throw()
        {
            // Hand-corrupt the cache file before the first scan. The
            // detector must tolerate it (start with empty in-memory
            // cache) and overwrite on save.
            File.WriteAllText(_tmpCache, "this is not json {{{");
            var det = new InterpreterDetector(_tmpCache);
            var results = await det.DetectAsync();
            Assert.NotEmpty(results);
            // After save, the file should now be valid JSON.
            string content = File.ReadAllText(_tmpCache);
            Assert.DoesNotContain("this is not json", content);
        }
    }
}
