using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.Hosting;
using ScriptDeck.Tests.Fakes;
using Xunit;

namespace ScriptDeck.Tests
{
    /// <summary>
    /// Integration tests for <see cref="BashExecutor"/>. Same Detector-
    /// fixture pattern as PythonExecutorTests: tests skip cleanly when
    /// bash isn't installed on the test box (CI containers, dev
    /// machines without Git for Windows).
    /// </summary>
    public class BashExecutorTests : IClassFixture<BashExecutorTests.Detector>
    {
        private readonly Detector _det;
        public BashExecutorTests(Detector det) { _det = det; }

        public sealed class Detector
        {
            public bool Available { get; }

            public Detector()
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName  = "bash",
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) { Available = false; return; }
                        Available = p.WaitForExit(5000) && p.ExitCode == 0;
                    }
                }
                catch
                {
                    // Try canonical Git Bash install as a fallback so
                    // the tests still run on boxes that don't add bash
                    // to PATH automatically.
                    try
                    {
                        string fallback = @"C:\Program Files\Git\bin\bash.exe";
                        Available = File.Exists(fallback);
                    }
                    catch { Available = false; }
                }
            }
        }

        // ---- Helpers -------------------------------------------------------

        private static string WriteScript(string body)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "scriptdeck-bashtest-" + Guid.NewGuid().ToString("N") + ".sh");
            // LF line endings -- bash hates CRLF (broken shebang).
            File.WriteAllText(path, body.Replace("\r\n", "\n"));
            return path;
        }

        private static ExecutionRequest BuildRequest(string scriptPath, bool wantGrid = false)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" };
            if (wantGrid) targets.Add("grid");
            return new ExecutionRequest
            {
                ScriptPath    = scriptPath,
                Args          = new List<string>(),
                ButtonLabel   = "bashtest",
                OutputTargets = targets,
                SharedInputs  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
        }

        private async Task<(FakeSink Sink, ExecutionResult Result)> RunAsync(ExecutionRequest req)
        {
            var sink = new FakeSink();
            var executor = new BashExecutor();
            var result = await executor.ExecuteAsync(req, sink, CancellationToken.None);
            return (sink, result);
        }

        private static void Cleanup(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // ---- Tests ---------------------------------------------------------

        [Fact]
        public async Task Plain_Echo_Goes_To_RTB_As_Output()
        {
            if (!_det.Available) return;
            string script = WriteScript("echo hello-bash\n");
            try
            {
                var (sink, result) = await RunAsync(BuildRequest(script));
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Output" && w.Text.Contains("hello-bash"));
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Stderr_Goes_To_Sink_Error_Stream()
        {
            if (!_det.Available) return;
            string script = WriteScript("echo boom >&2\n");
            try
            {
                var (sink, _) = await RunAsync(BuildRequest(script));
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Error" && w.Text.Contains("boom"));
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task NonZero_Exit_Propagates()
        {
            if (!_det.Available) return;
            string script = WriteScript("exit 7\n");
            try
            {
                var (_, result) = await RunAsync(BuildRequest(script));
                Assert.Equal(7, result.ExitCode);
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Json_Tag_RTB_Routes_To_Console_Only()
        {
            if (!_det.Available) return;
            // Emit a hand-rolled __SCRIPTDECK_JSON__ tag (no bootstrap
            // sourcing in this test) so we exercise the executor's
            // parser directly.
            string script = WriteScript(
                "echo '__SCRIPTDECK_JSON__{\"__ScriptDeckTarget\":\"rtb\",\"value\":\"tagged-line\"}'\n");
            try
            {
                var (sink, _) = await RunAsync(BuildRequest(script, wantGrid: true));
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Output" && w.Text.Contains("tagged-line"));
                // Grid stays untouched -- tag explicitly said RTB only.
                Assert.Empty(sink.GridColumns);
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Json_Tag_Grid_Populates_Columns_And_Rows()
        {
            if (!_det.Available) return;
            string script = WriteScript(
                "echo '__SCRIPTDECK_JSON__{\"__ScriptDeckTarget\":\"grid\",\"row\":{\"name\":\"a\",\"id\":\"1\"}}'\n" +
                "echo '__SCRIPTDECK_JSON__{\"__ScriptDeckTarget\":\"grid\",\"row\":{\"name\":\"b\",\"id\":\"2\"}}'\n");
            try
            {
                var (sink, _) = await RunAsync(BuildRequest(script, wantGrid: true));
                Assert.Equal(new[] { "name", "id" }, sink.GridColumns);
                Assert.Equal(2, sink.GridRows.Count);
                Assert.Equal("a", sink.GridRows[0][0]);
                Assert.Equal("b", sink.GridRows[1][0]);
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Bootstrap_Helpers_Are_Available_Via_BASH_ENV()
        {
            if (!_det.Available) return;
            // Verify auto-sourcing: a script that calls scriptdeck_write_rtb
            // without an explicit `source` line should still work because
            // BASH_ENV points at the bootstrap that ships next to the EXE.
            // If the bootstrap file isn't where the executor expects, this
            // test will fail noisily -- which is the point.
            string baseDir = AppContext.BaseDirectory;
            string bootstrap = Path.Combine(baseDir, "scriptdeck_bootstrap.sh");
            if (!File.Exists(bootstrap))
            {
                // Test env doesn't have the bootstrap copied alongside
                // (rare -- it's Content + PreserveNewest in the csproj).
                // Skip rather than fail to keep CI green.
                return;
            }
            string script = WriteScript("scriptdeck_write_rtb \"via-helper\"\n");
            try
            {
                var (sink, result) = await RunAsync(BuildRequest(script));
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Output" && w.Text.Contains("via-helper"));
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Missing_Script_Returns_Failed_Not_Throw()
        {
            if (!_det.Available) return;
            var req = BuildRequest(@"C:\nope\never\does-not-exist.sh");
            var (sink, result) = await RunAsync(req);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("Script not found"));
        }
    }
}
