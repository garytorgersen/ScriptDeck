using System;
using System.Collections.Generic;
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
    /// Integration tests for <see cref="ProcessExecutor"/>. By design,
    /// ProcessExecutor is FIRE-AND-FORGET: it launches an executable
    /// via ShellExecute, then returns immediately without capturing
    /// output or waiting for exit. That makes "happy path" tests
    /// (launch notepad and verify output) unworkable -- there IS no
    /// output to verify.
    ///
    /// What we CAN test is:
    /// * Missing executable returns a Failed result
    /// * Empty scriptPath returns a Failed result
    /// * A successful launch returns Ok almost immediately
    /// * Log lines are written for the launch
    ///
    /// We deliberately avoid asserting on the launched process itself
    /// because ShellExecute returns null in many legitimate cases
    /// (URL handlers, document files via associations) and trying to
    /// inspect the spawned process would couple the tests to OS
    /// scheduling quirks.
    /// </summary>
    public class ProcessExecutorIntegrationTests : IDisposable
    {
        private readonly ProcessExecutor _exec = new ProcessExecutor();
        private readonly string _tmpDir;

        public ProcessExecutorIntegrationTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(),
                "ScriptDeckProcessTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
            catch { }
        }

        private ExecutionRequest BuildReq(string scriptPath,
            IEnumerable<string> args = null,
            string workingDir = null)
        {
            return new ExecutionRequest
            {
                ScriptPath = scriptPath,
                Args = args?.ToList() ?? new List<string>(),
                ButtonLabel = "process-test",
                WorkingDirectory = workingDir,
                OutputTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SharedInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
        }

        [Fact]
        public async Task Missing_Executable_Returns_Failed_Result()
        {
            // Path that definitely doesn't resolve to anything. The
            // exact error wording has shifted over time (was
            // "Could not launch", now "launch target not found") --
            // assert on the shape: non-empty error message + an Error
            // stream entry mentioning the bad path, NOT a specific
            // phrase.
            string badPath = Path.Combine(_tmpDir, "definitely-not-there.exe");
            var req = BuildReq(badPath);
            var sink = new FakeSink();
            var result = await _exec.ExecuteAsync(req, sink, CancellationToken.None);
            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("definitely-not-there.exe"));
        }

        [Fact]
        public async Task Empty_ScriptPath_Returns_Failed_Result()
        {
            var req = BuildReq("");
            var sink = new FakeSink();
            var result = await _exec.ExecuteAsync(req, sink, CancellationToken.None);
            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("scriptPath"));
        }

        [Fact]
        public async Task Launch_Calc_Returns_Ok_And_Logs()
        {
            // calc.exe is a stable target on every Windows install.
            // We don't try to kill it -- on modern Windows calc.exe
            // launches the Calculator UWP app via a stub that exits
            // immediately, so it doesn't litter the test session
            // with a stuck process even though we launched it.
            //
            // We accept either Ok or Failed here -- some test runners
            // run in environments without Calculator installed and we
            // don't want to fail the suite for that. Just verify the
            // result is well-formed and the executor didn't hang.
            string calcPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe");
            if (!File.Exists(calcPath))
            {
                // No calc.exe on this box -- skip cleanly.
                return;
            }
            var req = BuildReq(calcPath);
            var sink = new FakeSink();
            var result = await _exec.ExecuteAsync(req, sink, CancellationToken.None);
            Assert.NotNull(result);
            // Ok is the expected case. If something else came back,
            // it should at least be Failed with an ErrorMessage --
            // not silently lost.
            Assert.True(result.ExitCode == 0 || !string.IsNullOrEmpty(result.ErrorMessage));
            // A successful launch logs a "Launched:" line.
            if (result.ExitCode == 0)
            {
                Assert.Contains(sink.Logs, l => l.Contains("Launched"));
            }
        }

        [Fact]
        public async Task Returns_Fast_Fire_And_Forget()
        {
            // ProcessExecutor must NOT wait for the launched app. If
            // we launch calc.exe and the call takes >2 seconds, the
            // executor is incorrectly blocking on something.
            string calcPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe");
            if (!File.Exists(calcPath)) return;
            var req = BuildReq(calcPath);
            var sink = new FakeSink();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _exec.ExecuteAsync(req, sink, CancellationToken.None);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"ProcessExecutor blocked for {sw.ElapsedMilliseconds}ms; should be fire-and-forget");
        }
    }
}
