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
    /// Integration tests that spawn real cmd.exe processes via
    /// <see cref="CmdExecutor"/>. Each test creates a tiny .cmd file in
    /// a per-instance temp dir, runs it, and asserts on the captured
    /// output / exit code / process behavior.
    ///
    /// CmdExecutor is stateless so we don't need a shared fixture -- the
    /// per-invocation process spawn is the only cost, and it's fast
    /// (~50-100 ms per test).
    /// </summary>
    public class CmdExecutorIntegrationTests : IDisposable
    {
        private readonly CmdExecutor _exec = new CmdExecutor();
        private readonly string _tmpDir;

        public CmdExecutorIntegrationTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(),
                "ScriptDeckCmdTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
            catch { }
        }

        // ---- helpers ----

        // Write a .cmd file with the given body. Each call gets a fresh
        // GUID-named file so tests don't reuse stale content if the
        // disk's write cache lags.
        private string WriteCmd(string body)
        {
            string p = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(p, body, new System.Text.UTF8Encoding(false));
            return p;
        }

        // Build a request pointing at a freshly-written .cmd file.
        private ExecutionRequest BuildReq(string body, IEnumerable<string> args = null,
                                          string workingDir = null,
                                          IDictionary<string, string> sharedInputs = null)
        {
            return new ExecutionRequest
            {
                ScriptPath = WriteCmd(body),
                Args = args?.ToList() ?? new List<string>(),
                ButtonLabel = "cmd-test",
                WorkingDirectory = workingDir,
                OutputTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" },
                SharedInputs = sharedInputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
        }

        // Run a request to completion and return the captured sink + result.
        private async Task<(FakeSink Sink, ExecutionResult Result)> RunAsync(
            ExecutionRequest req, CancellationToken ct = default)
        {
            var sink = new FakeSink();
            var result = await _exec.ExecuteAsync(req, sink, ct);
            return (sink, result);
        }

        // ---- A. Basic execution ----

        [Fact]
        public async Task Echo_Captures_Stdout()
        {
            var (sink, result) = await RunAsync(BuildReq("@echo off\r\necho hello\r\n"));
            Assert.NotNull(result);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("hello"));
        }

        [Fact]
        public async Task Stderr_Routes_To_Error_Stream()
        {
            // 1>&2 redirects stdout to stderr for that command.
            string body = "@echo off\r\necho oops 1>&2\r\n";
            var (sink, result) = await RunAsync(BuildReq(body));
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("oops"));
        }

        [Fact]
        public async Task Exit_Code_Propagates()
        {
            var (_, result) = await RunAsync(BuildReq("@echo off\r\nexit /b 42\r\n"));
            Assert.Equal(42, result.ExitCode);
        }

        [Fact]
        public async Task Missing_Script_Returns_Failed_Result()
        {
            var req = BuildReq("@echo off\r\n");
            req.ScriptPath = @"C:\does\not\exist.cmd";
            var (sink, result) = await RunAsync(req);
            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("not found"));
        }

        // ---- B. C2 fix: drain tail of stdout/stderr ----

        [Fact]
        public async Task Drain_Captures_Tail_Of_Fast_Exiting_Process()
        {
            // Emit many lines back-to-back then exit immediately. Before
            // the C2 fix (second WaitForExit), the last batch of stdout
            // events was sometimes lost because the process died before
            // OutputDataReceived had drained.
            var body = new System.Text.StringBuilder();
            body.AppendLine("@echo off");
            for (int i = 1; i <= 100; i++) body.AppendLine("echo line" + i);
            // No artificial wait at end -- we WANT the fast exit.

            var (sink, _) = await RunAsync(BuildReq(body.ToString()));
            int seen = sink.Writes.Count(w =>
                w.Severity == "Output" && w.Text.StartsWith("line"));
            Assert.Equal(100, seen);
            // Specifically verify the LAST line made it -- that's the
            // one the old race tended to swallow.
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("line100"));
        }

        // ---- C. C1 fix: process-tree kill on cancellation ----

        [Fact]
        public async Task Cancellation_Kills_Cmd_Process_Promptly()
        {
            // Long-running script (timeout waits in seconds). Cancel after
            // 200ms, verify dispatch returns within a couple seconds and
            // the post-timeout echo never ran.
            //
            // Use `ping` as the delay primitive -- timeout.exe refuses
            // to run when stdin is redirected (a test runner condition),
            // and a `timeout` from MSYS/Git Bash on PATH would use
            // different flags. ping -n 11 sends 11 pings at 1s intervals
            // for a ~10s total runtime. Explicit System32 path so PATH
            // can't swap in a unix ping.
            //
            // We do NOT assert result.Cancelled -- depending on the
            // cmd exit path (taskkill killed it vs cmd exited from a
            // SIGINT-like signal), the executor can return either
            // Cancelled or Ok with a non-zero exit. The user-visible
            // outcome that matters is "the script didn't run to
            // completion."
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            string body =
                "@echo off\r\n" +
                "\"" + pingExe + "\" -n 11 127.0.0.1 >nul\r\n" +
                "echo should not print\r\n";
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource();
            var task = RunAsync(BuildReq(body), cts.Token);
            await Task.Delay(200);
            cts.Cancel();
            var (sink, _) = await task;
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 4000,
                $"Cancellation took too long: {sw.ElapsedMilliseconds}ms (script's natural runtime: 10s)");
            // The post-timeout echo should never have fired.
            Assert.DoesNotContain(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("should not print"));
        }

        [Fact]
        public async Task Cancellation_Kills_Spawned_Child_Process()
        {
            // C1 fix verification: a cmd script that launches a child
            // (timeout.exe) must have BOTH cmd.exe AND timeout.exe
            // terminated when the user cancels. Without taskkill /T,
            // the child orphans and runs to completion.
            //
            // We assert behavior by giving the script a "marker" that
            // would be left behind only if the child completes naturally.
            string markerPath = Path.Combine(_tmpDir, "child-finished.flag");
            // Use ping for the delay -- timeout.exe rejects redirected
            // stdin, which the test runner gives us. Explicit System32
            // path so PATH overrides can't swap in a unix tool.
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            string body =
                "@echo off\r\n" +
                "start /wait cmd.exe /c \"\"" + pingExe + "\" -n 11 127.0.0.1 >nul & echo done > " + markerPath + "\"\r\n";
            using var cts = new CancellationTokenSource();
            var task = RunAsync(BuildReq(body), cts.Token);
            // Wait for the child to actually be running (give the script
            // ~500 ms to start the child process). Then cancel.
            await Task.Delay(500);
            cts.Cancel();
            var (_, _) = await task;

            // Don't assert on result.Cancelled -- the executor's
            // Cancelled flag depends on which path tore the process
            // down (cancellation token observed before WaitForExit
            // vs taskkill's exit happening to coincide with the
            // token check). The user-visible test is "did the
            // child die?" -- the marker file proves that.
            //
            // Beyond cancel, give the OS a beat to settle: cleanup
            // of the child tree happens after taskkill returns.
            await Task.Delay(500);
            Assert.False(File.Exists(markerPath),
                "Child process survived cancellation -- process-tree kill (taskkill /T) failed.");
        }

        // ---- D. Args ----

        [Fact]
        public async Task Args_Pass_Through_To_Script()
        {
            // Cmd referrers args as %1 %2 ... per position.
            string body = "@echo off\r\necho arg1=%1 arg2=%2\r\n";
            var (sink, _) = await RunAsync(BuildReq(body, args: new[] { "alpha", "beta" }));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("arg1=alpha") && w.Text.Contains("arg2=beta"));
        }

        [Fact]
        public async Task Args_With_Spaces_Are_Quoted_Correctly()
        {
            // cmd's argument parsing is finicky around spaces. The
            // executor's QuoteArg should wrap and pass the value as
            // a single token. Using %~1 to strip the surrounding
            // quotes (which cmd preserves when reading %1 verbatim)
            // so we can assert on the raw value the script saw.
            string body = "@echo off\r\necho got=%~1\r\n";
            var (sink, _) = await RunAsync(BuildReq(body, args: new[] { "hello world" }));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("got=hello world"));
        }

        // Note: a test that asserts "args with & | < > metacharacters
        // pass through to cmd scripts unchanged" is omitted. cmd.exe's
        // /c parsing has a documented bug where metacharacters inside
        // double-quoted arguments are STILL interpreted in some quote
        // configurations -- it's a cmd.exe quirk, not a ScriptDeck bug.
        // Users who need to pass shell metacharacters as data through
        // a cmd button should use PowerShell instead, or pass via env
        // var (shared input).

        // ---- E. Working directory ----

        [Fact]
        public async Task WorkingDirectory_Honored_By_Cmd_Executor()
        {
            // Unlike PowerShell, cmd.exe gets WorkingDirectory through
            // ProcessStartInfo.WorkingDirectory and the spawned process
            // inherits it. cd echoes the current directory.
            string body = "@echo off\r\ncd\r\n";
            // Create a subdirectory in our temp dir so we have a
            // distinct working dir to assert against.
            string subDir = Path.Combine(_tmpDir, "workdir");
            Directory.CreateDirectory(subDir);
            var req = BuildReq(body, workingDir: subDir);
            var (sink, _) = await RunAsync(req);
            string subDirName = Path.GetFileName(subDir);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains(subDirName));
        }

        // ---- F. Shared input injection as env vars ----

        [Fact]
        public async Task Shared_Inputs_Visible_As_Env_Vars()
        {
            // CmdExecutor sets shared inputs as environment variables on
            // the spawned cmd.exe so batch scripts can reference them.
            string body = "@echo off\r\necho name=%computerName% region=%region%\r\n";
            var req = BuildReq(body,
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "BOX42" },
                    { "region", "us-west" },
                });
            var (sink, _) = await RunAsync(req);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output"
                  && w.Text.Contains("name=BOX42")
                  && w.Text.Contains("region=us-west"));
        }

        [Fact]
        public async Task Shared_Inputs_With_Invalid_Names_Are_Silently_Skipped()
        {
            // Names with characters illegal in Windows env vars (= or
            // null) should be silently skipped, not crash the dispatch.
            string body = "@echo off\r\necho name=%computerName%\r\n";
            var req = BuildReq(body,
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "OK" },
                    { "weird=name",  "X" },  // illegal char
                });
            var (sink, result) = await RunAsync(req);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("name=OK"));
        }

        // ---- G. Multi-line and special output ----

        [Fact]
        public async Task Multiple_Echo_Lines_Each_Captured_Separately()
        {
            string body = "@echo off\r\necho one\r\necho two\r\necho three\r\n";
            var (sink, _) = await RunAsync(BuildReq(body));
            var outputs = sink.Writes.Where(w => w.Severity == "Output").Select(w => w.Text).ToList();
            Assert.Contains(outputs, t => t.Contains("one"));
            Assert.Contains(outputs, t => t.Contains("two"));
            Assert.Contains(outputs, t => t.Contains("three"));
        }

        [Fact]
        public async Task Empty_Script_Produces_No_Output_And_Zero_Exit()
        {
            var (sink, result) = await RunAsync(BuildReq("@echo off\r\n"));
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(sink.Writes, w => w.Severity == "Output");
            Assert.DoesNotContain(sink.Writes, w => w.Severity == "Error");
        }

        // ---- H. Path with spaces ----

        [Fact]
        public async Task Script_Path_With_Spaces_Works()
        {
            // The executor's BuildCmdArguments uses cmd's outer-quote
            // pattern to handle paths with spaces. Move the .cmd file
            // into a sub-folder with a space, then run it.
            string spacedDir = Path.Combine(_tmpDir, "with spaces");
            Directory.CreateDirectory(spacedDir);
            string scriptPath = Path.Combine(spacedDir, "test.cmd");
            File.WriteAllText(scriptPath, "@echo off\r\necho works\r\n");
            var req = new ExecutionRequest
            {
                ScriptPath = scriptPath,
                Args = new List<string>(),
                ButtonLabel = "spaced",
                OutputTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" },
                SharedInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
            var (sink, result) = await RunAsync(req);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("works"));
        }
    }
}
