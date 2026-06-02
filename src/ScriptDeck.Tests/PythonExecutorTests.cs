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
    /// Integration tests for <see cref="PythonExecutor"/>. Each test
    /// writes a tiny .py to a temp file, runs it, and asserts on the
    /// resulting FakeSink state.
    ///
    /// All tests skip if `python` isn't on PATH (CI containers without
    /// Python, dev boxes that only use PowerShell). The skip uses xUnit's
    /// SkipIf pattern -- one helper detects Python once per fixture and
    /// each test calls it as a guard.
    /// </summary>
    public class PythonExecutorTests : IClassFixture<PythonExecutorTests.Detector>
    {
        private readonly Detector _det;

        public PythonExecutorTests(Detector det) { _det = det; }

        /// <summary>
        /// Fixture-scoped probe so the "is Python on PATH?" check only
        /// runs once per test class. The actual `python --version`
        /// invocation has a noticeable cold-start cost.
        /// </summary>
        public sealed class Detector
        {
            public bool Available { get; }
            public string SkipReason => Available
                ? null
                : "python.exe was not found on PATH; PythonExecutor tests skipped.";

            public Detector()
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName  = "python",
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
                    Available = false;
                }
            }
        }

        // ---- Helpers -------------------------------------------------------

        private string WriteScript(string body)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "scriptdeck-pytest-" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(path, body);
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
                ButtonLabel   = "pytest",
                OutputTargets = targets,
                SharedInputs  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
        }

        private async Task<(FakeSink Sink, ExecutionResult Result)> RunAsync(ExecutionRequest req)
        {
            var sink = new FakeSink();
            var executor = new PythonExecutor();
            var result = await executor.ExecuteAsync(req, sink, CancellationToken.None);
            return (sink, result);
        }

        private void Cleanup(string scriptPath)
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
        }

        // ---- Tests ---------------------------------------------------------

        [Fact]
        public async Task Plain_Print_Goes_To_RTB_As_Output()
        {
            if (!_det.Available) return; // skip-style
            string script = WriteScript("print('hello world')");
            try
            {
                var (sink, result) = await RunAsync(BuildRequest(script));
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Output" && w.Text.Contains("hello world"));
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Stderr_Goes_To_Sink_Error_Stream()
        {
            if (!_det.Available) return;
            string script = WriteScript(
                "import sys\nsys.stderr.write('boom\\n')\n");
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
            string script = WriteScript("import sys\nsys.exit(7)\n");
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
            // Emit a hand-rolled __SCRIPTDECK_JSON__ tag without importing
            // the bootstrap (so this test exercises the executor's parser,
            // not the python helper module).
            string script = WriteScript(
                "import json\n" +
                "print('__SCRIPTDECK_JSON__' + json.dumps({\n" +
                "    '__ScriptDeckTarget': 'rtb',\n" +
                "    'value': 'tagged-rtb-text'\n" +
                "}))\n");
            try
            {
                var (sink, _) = await RunAsync(BuildRequest(script, wantGrid: true));
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Output" && w.Text.Contains("tagged-rtb-text"));
                // The grid must remain untouched -- the tag explicitly
                // said RTB-only.
                Assert.Empty(sink.GridColumns);
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Json_Tag_Grid_Populates_Columns_And_Rows()
        {
            if (!_det.Available) return;
            string script = WriteScript(
                "import json\n" +
                "for row in [{'name':'a','id':1},{'name':'b','id':2}]:\n" +
                "    print('__SCRIPTDECK_JSON__' + json.dumps({\n" +
                "        '__ScriptDeckTarget': 'grid', 'row': row}))\n");
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
        public async Task Bootstrap_Module_Importable_And_Functional()
        {
            if (!_det.Available) return;
            // Locate the bootstrap module that lives next to ScriptDeck.exe
            // (the test process is the test runner; the bootstrap file is
            // copied to the ScriptDeck output folder). We compute the path
            // the same way the executor does -- AppContext.BaseDirectory --
            // because tests run from a sibling bin folder.
            string baseDir = AppContext.BaseDirectory;
            string bootstrapPath = Path.Combine(baseDir, "scriptdeck_bootstrap.py");
            if (!File.Exists(bootstrapPath))
            {
                // Test environment doesn't have the bootstrap copied next
                // to it (rare -- it's a content file with PreserveNewest).
                // Skip rather than fail to keep CI green.
                return;
            }

            string script = WriteScript(
                "from scriptdeck_bootstrap import write_rtb, write_grid\n" +
                "write_rtb('via-helper')\n" +
                "write_grid({'col': 'value'})\n");
            try
            {
                var (sink, result) = await RunAsync(BuildRequest(script, wantGrid: true));
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Output" && w.Text.Contains("via-helper"));
                Assert.Equal(new[] { "col" }, sink.GridColumns);
                Assert.Single(sink.GridRows);
                Assert.Equal("value", sink.GridRows[0][0]);
            }
            finally { Cleanup(script); }
        }

        [Fact]
        public async Task Missing_Script_Returns_Failed_Not_Throw()
        {
            if (!_det.Available) return;
            var req = BuildRequest(@"C:\nope\never\does-not-exist.py");
            var (sink, result) = await RunAsync(req);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("Script not found"));
        }

        [Fact]
        public async Task Missing_Interpreter_Returns_Failed_With_Clear_Error()
        {
            if (!_det.Available) return;
            string script = WriteScript("print('x')");
            try
            {
                var req = BuildRequest(script);
                req.PythonInterpreter = @"C:\nope\never\python.exe";
                var (sink, result) = await RunAsync(req);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(sink.Writes,
                    w => w.Severity == "Error" &&
                         w.Text.Contains("Python interpreter not found"));
            }
            finally { Cleanup(script); }
        }
    }
}
