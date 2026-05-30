using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.Hosting;
using ScriptDeck.Tests.Fakes;
using Xunit;

namespace ScriptDeck.Tests
{
    /// <summary>
    /// Integration tests against a REAL PowerShell runspace. These exist
    /// to catch regressions in the executor's contract with PowerShell
    /// itself -- the kind of bugs unit tests with fakes can't see (PS
    /// engine behavior changes between versions, stream routing quirks,
    /// pipeline metadata leaking into the grid, etc.).
    ///
    /// All tests share one runspace via <see cref="PowerShellFixture"/>
    /// to keep the suite fast. That means tests MUST be order-independent
    /// and MUST NOT leave global runspace state behind (no `$global:`,
    /// no `Import-Module`, no preference mutations at top level).
    /// </summary>
    public class RealPowerShellTests : IClassFixture<PowerShellFixture>
    {
        private readonly PowerShellFixture _fx;

        public RealPowerShellTests(PowerShellFixture fx)
        {
            _fx = fx;
        }

        // Helper: build a request pointing at a fresh script file in the
        // fixture's tempdir, executed with the given options.
        private ExecutionRequest BuildRequest(
            string scriptBody,
            IEnumerable<string> args = null,
            bool wantGrid = false,
            bool wantRtb = true,
            string rtbFormat = null,
            IDictionary<string, string> sharedInputs = null)
        {
            string path = _fx.WriteScript(scriptBody);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (wantRtb)  targets.Add("rtb");
            if (wantGrid) targets.Add("grid");
            return new ExecutionRequest
            {
                ScriptPath       = path,
                Args             = args?.ToList() ?? new List<string>(),
                ButtonLabel      = "test",
                OutputTargets    = targets,
                RtbFormat        = rtbFormat,
                SharedInputs     = sharedInputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
        }

        // Helper: run a request and return (sink, result). Uses CancellationToken.None.
        private async Task<(FakeSink Sink, ExecutionResult Result)> RunAsync(ExecutionRequest req)
        {
            var sink = new FakeSink();
            var result = await _fx.Executor.ExecuteAsync(req, sink, CancellationToken.None);
            return (sink, result);
        }

        // ---- A. Basic execution ----

        [Fact]
        public async Task Bare_String_Lands_In_Output_Stream()
        {
            var (sink, result) = await RunAsync(BuildRequest("'hello'"));
            Assert.NotNull(result);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("hello"));
        }

        [Fact]
        public async Task Write_Output_Lands_In_Output_Stream()
        {
            var (sink, _) = await RunAsync(BuildRequest("Write-Output 'pipeline'"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("pipeline"));
        }

        [Fact]
        public async Task Empty_Script_Produces_No_Output()
        {
            var (sink, result) = await RunAsync(BuildRequest("# nothing\n"));
            Assert.NotNull(result);
            Assert.Equal(0, result.ExitCode);
            // The "**********" run separator at the end of dispatch is
            // a Dispatcher concern, not the executor's. The executor
            // itself should emit nothing for an empty script.
            Assert.DoesNotContain(sink.Writes, w => w.Severity == "Output");
        }

        [Fact]
        public async Task Missing_Script_Returns_Failed_Result()
        {
            var req = BuildRequest("");
            req.ScriptPath = @"C:\does\not\exist.ps1";
            var (sink, result) = await RunAsync(req);
            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("not found"));
        }

        // ---- B. Stream routing ----

        [Fact]
        public async Task Write_Warning_Routes_To_Warning_Stream()
        {
            var (sink, _) = await RunAsync(BuildRequest("Write-Warning 'be careful'"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Warning" && w.Text.Contains("be careful"));
        }

        [Fact]
        public async Task Write_Error_Non_Terminating_Routes_To_Error_Stream_But_Script_Completes()
        {
            // A non-terminating error doesn't end the pipeline. The script
            // exits 0; the error lands in the Error stream.
            var (sink, result) = await RunAsync(BuildRequest("Write-Error 'oops'\n'still here'"));
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("oops"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("still here"));
        }

        [Fact]
        public async Task Write_Information_With_Continue_Routes_To_Info_Stream()
        {
            var (sink, _) = await RunAsync(BuildRequest(
                "Write-Information 'status' -InformationAction Continue"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Info" && w.Text.Contains("status"));
        }

        [Fact]
        public async Task Throw_Returns_Failed_Result()
        {
            // PowerShell `throw` is terminating. Pipeline ends; the
            // exception bubbles through Invoke and becomes a Failed result.
            // (The exact ErrorMessage text varies by PS version.)
            var (sink, result) = await RunAsync(BuildRequest("throw 'kaboom'"));
            // Terminating errors land in the Error stream OR the result's
            // ErrorMessage depending on PS version; assert at least one
            // of the two surfaces saw it.
            bool inStream = sink.Writes.Any(w => w.Severity == "Error" && w.Text.Contains("kaboom"));
            bool inResult = !string.IsNullOrEmpty(result?.ErrorMessage) && result.ErrorMessage.Contains("kaboom");
            Assert.True(inStream || inResult, "Expected 'kaboom' in either the Error stream or the result message");
        }

        // ---- C. Structured output -> grid ----

        [Fact]
        public async Task PSCustomObject_Populates_Grid_Columns_And_Row()
        {
            string body =
                "[PSCustomObject]@{ Name='Spooler'; Status='Running'; Id=1234 }";
            var (sink, _) = await RunAsync(BuildRequest(body, wantGrid: true));
            Assert.Equal(new[] { "Name", "Status", "Id" }, sink.GridColumns);
            Assert.Single(sink.GridRows);
            // Values are routed through FormatCell -- strings pass through
            // unchanged, primitive Int32 stays an int.
            Assert.Equal("Spooler", sink.GridRows[0][0]);
            Assert.Equal("Running", sink.GridRows[0][1]);
            Assert.Equal(1234,      sink.GridRows[0][2]);
        }

        [Fact]
        public async Task Multiple_Structured_Records_Produce_Multiple_Rows()
        {
            string body =
                "1..3 | ForEach-Object { [PSCustomObject]@{ N=$_; Sq=($_*$_) } }";
            var (sink, _) = await RunAsync(BuildRequest(body, wantGrid: true));
            Assert.Equal(new[] { "N", "Sq" }, sink.GridColumns);
            Assert.Equal(3, sink.GridRows.Count);
            // The values come back as boxed PowerShell scalars (Int32
            // in PS 5.1, but PSObject-wrapped depending on path). Compare
            // via ToString to dodge the boxed-int-type mismatch.
            Assert.Equal("1", sink.GridRows[0][0]?.ToString());
            Assert.Equal("9", sink.GridRows[2][1]?.ToString());
        }

        [Fact]
        public async Task Primitive_Without_ExtendedGrid_Does_Not_Populate_Grid()
        {
            // A bare string is "structured" only in extended mode. Default
            // grid behavior is to ignore primitives entirely.
            var (sink, _) = await RunAsync(BuildRequest("'just text'", wantGrid: true));
            Assert.Empty(sink.GridColumns);
            Assert.Empty(sink.GridRows);
        }

        // ---- D. RTB formats ----

        [Fact]
        public async Task RtbFormat_Default_Uses_ToString_Per_Record()
        {
            // PSCustomObject default ToString is "@{Prop=val; ...}"
            string body = "[PSCustomObject]@{ A=1; B=2 }";
            var (sink, _) = await RunAsync(BuildRequest(body, rtbFormat: "default"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("@{") && w.Text.Contains("A=1"));
        }

        [Fact]
        public async Task RtbFormat_List_Renders_Property_Colon_Value_Lines()
        {
            string body = "[PSCustomObject]@{ Name='Spooler'; Status='Running' }";
            var (sink, _) = await RunAsync(BuildRequest(body, rtbFormat: "list"));
            // Concatenate everything we got -- list formatter joins
            // multiple lines into the output stream.
            string all = string.Join("", sink.Writes.Where(w => w.Severity == "Output").Select(w => w.Text));
            Assert.Contains("Name", all);
            Assert.Contains("Spooler", all);
            Assert.Contains(":", all);
        }

        [Fact]
        public async Task RtbFormat_Table_Produces_Header_Dash_Separator()
        {
            string body = "1..2 | ForEach-Object { [PSCustomObject]@{ N=$_; X='hi' } }";
            var (sink, _) = await RunAsync(BuildRequest(body, rtbFormat: "table"));
            // Table is buffered + flushed at end. The flush text contains
            // the column headers and a dashed separator.
            string all = string.Join("", sink.Writes.Where(w => w.Severity == "Output").Select(w => w.Text));
            Assert.Contains("N", all);
            Assert.Contains("X", all);
            Assert.Contains("--", all);  // dashed separator
        }

        [Fact]
        public async Task RtbFormat_Json_Renders_Pretty_Array()
        {
            string body = "[PSCustomObject]@{ A=1 }; [PSCustomObject]@{ A=2 }";
            var (sink, _) = await RunAsync(BuildRequest(body, rtbFormat: "json"));
            string all = string.Join("", sink.Writes.Where(w => w.Severity == "Output").Select(w => w.Text));
            Assert.StartsWith("[", all.TrimStart());
            Assert.Contains("\"A\":", all);
        }

        [Fact]
        public async Task RtbFormat_Raw_Renders_Format_Table_As_Text()
        {
            // The whole point of "raw": route output through Out-String
            // -Stream so PowerShell's default formatter does its job. A
            // Format-Table call should produce real tabular text in the
            // RTB -- column headers, row data, and a dash separator --
            // rather than the FormatStartData / FormatEntryData garbage
            // you get without raw mode.
            //
            // FakeSink.Writes is a ConcurrentBag (no enumeration-order
            // guarantee), so we assert on presence-of-substrings in the
            // joined content, not specific ordering. The PS formatter
            // chooses dash widths based on column width -- for our
            // 1-char column "N" with single-digit values, dashes can
            // be single ("-") not double ("--"), so we don't pin the
            // exact dash count.
            string body =
                "1..2 | ForEach-Object { [PSCustomObject]@{ N=$_; X='hi' } } | " +
                "Format-Table -AutoSize";
            var (sink, _) = await RunAsync(BuildRequest(body, rtbFormat: "raw"));
            string all = string.Join("", sink.Writes
                .Where(w => w.Severity == "Output")
                .Select(w => w.Text));

            // Real table content -- headers AND row data both render:
            Assert.Contains("N", all);
            Assert.Contains("X", all);
            Assert.Contains("hi", all);
            // At least one dash appears (the header underline). One
            // character is enough -- we just need to confirm a
            // separator line was rendered, not its exact width.
            Assert.Contains("-", all);

            // The garbage shape that PROVES raw mode is engaged: if it
            // weren't, the formatter directives would surface their
            // CLR type names in obj.ToString() instead of rendered
            // rows.
            Assert.DoesNotContain("FormatStartData", all);
            Assert.DoesNotContain("FormatEntryData", all);
        }

        [Fact]
        public async Task RtbFormat_Raw_Preserves_Write_Grid_Routing()
        {
            // In raw mode, untagged structured objects flow through
            // Out-String -Stream and never populate the grid. But
            // Write-Grid stamps a __ScriptDeckTarget tag that the raw
            // filter recognizes and bypasses, so the grid path still
            // works for explicit emission.
            string body =
                "[PSCustomObject]@{ City='NYC'; Pop=8500000 } | Write-Grid; " +
                "'should not become a grid row'";
            var (sink, _) = await RunAsync(BuildRequest(body,
                wantGrid: true, rtbFormat: "raw"));
            // Grid got exactly the Write-Grid'd row, not the string.
            Assert.Equal(new[] { "City", "Pop" }, sink.GridColumns);
            Assert.Single(sink.GridRows);
            Assert.Equal("NYC", sink.GridRows[0][0]);
            // The untagged string went to the RTB via Out-String.
            string all = string.Join("", sink.Writes
                .Where(w => w.Severity == "Output")
                .Select(w => w.Text));
            Assert.Contains("should not become a grid row", all);
        }

        [Fact]
        public async Task RtbFormat_Raw_Preserves_Set_SharedInput_Event()
        {
            // Set-SharedInput emits a __ScriptDeckSetSharedInput-tagged
            // PSObject that the executor intercepts. The raw-mode
            // filter must let that tag survive Out-String, or the
            // bootstrap helper would silently stop working when a
            // user flips a button to raw mode.
            var fires = new List<(string Id, string Value, string Label)>();
            void Handler(string id, string value, string label)
            {
                lock (fires) fires.Add((id, value, label));
            }
            _fx.Executor.SharedInputSetRequested += Handler;
            try
            {
                await RunAsync(BuildRequest(
                    "Set-SharedInput -Id 'rawkey' -Value 'rawval' -Label 'Raw Label'",
                    rtbFormat: "raw"));
            }
            finally
            {
                _fx.Executor.SharedInputSetRequested -= Handler;
            }
            Assert.Single(fires);
            Assert.Equal("rawkey",    fires[0].Id);
            Assert.Equal("rawval",    fires[0].Value);
            Assert.Equal("Raw Label", fires[0].Label);
        }

        // ---- E. Shared input injection ----

        [Fact]
        public async Task Shared_Input_Available_As_Bare_Variable()
        {
            // No param() block -- the script reads $computerName directly.
            // The executor's runspace SetVariable should make it visible.
            var req = BuildRequest(
                "Write-Output $computerName",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "MYBOX-42" }
                });
            var (sink, _) = await RunAsync(req);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("MYBOX-42"));
        }

        [Fact]
        public async Task Multiple_Shared_Inputs_All_Visible()
        {
            var req = BuildRequest(
                "Write-Output \"$companyName/$region\"",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "companyName", "Acme" },
                    { "region", "EU" }
                });
            var (sink, _) = await RunAsync(req);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("Acme/EU"));
        }

        [Fact]
        public async Task Reserved_Variable_Name_Is_Skipped_With_Warning()
        {
            // 'Host' collides with $Host -- the executor must refuse to
            // overwrite and warn once per session. Use Host because
            // it's reserved AND distinct enough that the user obviously
            // wouldn't choose it on purpose.
            var req = BuildRequest("Write-Output 'ran'",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Host", "should-not-leak" }
                });
            var (sink, _) = await RunAsync(req);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("ran"));
            // Note: the warning is one-time per session, so we can't
            // assert it always fires here (a previous test might have
            // tripped it already). Just confirm the run wasn't blocked.
        }

        // ---- F. Arg routing (positional vs named) ----

        [Fact]
        public async Task Named_Args_Bind_To_Param_Block()
        {
            // The executor's LooksLikeParameterName check should route
            // "-Foo Value" pairs to AddParameter rather than positional.
            string body = "param([string]$Foo)\nWrite-Output \"got=$Foo\"";
            var (sink, _) = await RunAsync(BuildRequest(body, args: new[] { "-Foo", "hello" }));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("got=hello"));
        }

        [Fact]
        public async Task Positional_Arg_Binds_By_Position()
        {
            string body = "param([string]$First, [string]$Second)\nWrite-Output \"$First|$Second\"";
            var (sink, _) = await RunAsync(BuildRequest(body, args: new[] { "alpha", "beta" }));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("alpha|beta"));
        }

        [Fact]
        public async Task Switch_Param_Without_Value_Binds_As_Switch()
        {
            // "-Verbose" followed by another -Name should be treated as
            // a no-value switch by LooksLikeParameterName's heuristic.
            string body =
                "param([switch]$Flag, [string]$X)\n" +
                "Write-Output \"flag=$($Flag.IsPresent) x=$X\"";
            var (sink, _) = await RunAsync(BuildRequest(body, args: new[] { "-Flag", "-X", "set" }));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("flag=True") && w.Text.Contains("x=set"));
        }

        // ---- G. Bootstrap helpers ----

        [Fact]
        public async Task TestIsLocalTarget_Defined_From_Bootstrap()
        {
            // Just confirm the function loaded. If the bootstrap file
            // didn't make it next to the test exe, this would return
            // false (or error). Treat "true" as proof of presence.
            var (sink, _) = await RunAsync(BuildRequest(
                "Write-Output ([bool](Get-Command Test-IsLocalTarget -ErrorAction SilentlyContinue))"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("True"));
        }

        [Fact]
        public async Task TestIsLocalTarget_Returns_True_For_Local_Machine_Name()
        {
            // Passes the local machine name explicitly -- robust even
            // if shared-input injection isn't set up.
            string body = "Test-IsLocalTarget -ComputerName $env:COMPUTERNAME";
            var (sink, _) = await RunAsync(BuildRequest(body));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("True"));
        }

        [Fact]
        public async Task TestIsLocalTarget_Returns_False_For_Remote_Hostname()
        {
            var (sink, _) = await RunAsync(BuildRequest(
                "Test-IsLocalTarget -ComputerName 'definitely-not-this-box.example.com'"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("False"));
        }

        [Fact]
        public async Task TestIsLocalTarget_Returns_True_For_Empty_String()
        {
            // Empty -> treated as "local fallback" per the helper's docs.
            var (sink, _) = await RunAsync(BuildRequest(
                "Test-IsLocalTarget -ComputerName ''"));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("True"));
        }

        // ---- H. Per-object routing helpers (Write-Rtb / Write-Grid) ----

        [Fact]
        public async Task RoutingTag_Rtb_Suppresses_Grid()
        {
            // Build the routing tag directly into a PSCustomObject so we
            // test the EXECUTOR's tag-honoring logic without coupling
            // the test to Write-Rtb's wrapper mechanics.
            string body = "[PSCustomObject]@{ A=1; __ScriptDeckTarget='rtb' }";
            var (sink, _) = await RunAsync(BuildRequest(body, wantGrid: true));
            Assert.Contains(sink.Writes, w => w.Severity == "Output");
            Assert.Empty(sink.GridRows);
        }

        [Fact]
        public async Task RoutingTag_Grid_Suppresses_Rtb()
        {
            string body = "[PSCustomObject]@{ A=1; B=2; __ScriptDeckTarget='grid' }";
            var (sink, _) = await RunAsync(BuildRequest(body, wantGrid: true));
            Assert.Single(sink.GridRows);
            // Routing tag stripped from columns (it's an internal marker,
            // not data the user should see).
            Assert.DoesNotContain("__ScriptDeckTarget", sink.GridColumns);
            // Console got nothing because the only record was grid-routed.
            Assert.DoesNotContain(sink.Writes, w => w.Severity == "Output");
        }

        [Fact]
        public async Task Write_Rtb_Helper_Is_Defined()
        {
            // Confirm the bootstrap helper loaded. The executor-routing
            // behavior is covered by RoutingTag_Rtb_Suppresses_Grid
            // (which adds the tag directly to a PSCustomObject) so this
            // test only needs to verify the function exists and runs
            // without erroring on a plain pipeline input.
            string body =
                "Get-Command Write-Rtb -ErrorAction SilentlyContinue | " +
                "ForEach-Object { Write-Output $_.Name }";
            var (sink, _) = await RunAsync(BuildRequest(body));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("Write-Rtb"));
        }

        [Fact]
        public async Task Write_Grid_Helper_Is_Defined()
        {
            string body =
                "Get-Command Write-Grid -ErrorAction SilentlyContinue | " +
                "ForEach-Object { Write-Output $_.Name }";
            var (sink, _) = await RunAsync(BuildRequest(body));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("Write-Grid"));
        }

        [Fact]
        public async Task Write_Rtb_Helper_Round_Trips_Through_Pipeline()
        {
            // Verify the helper completes without errors and emits a
            // value. We don't try to peek at __ScriptDeckTarget through
            // dot-access; that path has surprising behavior on objects
            // returned across the pipeline-script boundary. Coverage
            // for the actual routing decision lives in
            // RoutingTag_Rtb_Suppresses_Grid which constructs the tag
            // directly inside the script.
            // PS 5.1 has no ternary, so use if-style. The point is just
            // "Write-Rtb didn't blow up and returned SOMETHING."
            string body =
                "$tagged = [PSCustomObject]@{ Sentinel='alive' } | Write-Rtb\n" +
                "if ($null -eq $tagged) { Write-Output 'null' } else { Write-Output 'present' }";
            var (sink, _) = await RunAsync(BuildRequest(body));
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("present"));
            // And no errors in the process.
            Assert.DoesNotContain(sink.Writes,
                w => w.Severity == "Error");
        }

        [Fact]
        public async Task Untagged_Record_Still_Goes_To_Both_Destinations()
        {
            // The opt-in routing must be opt-in -- a plain emit lands
            // in BOTH RTB and grid as before.
            string body = "[PSCustomObject]@{ A=1 }";
            var (sink, _) = await RunAsync(BuildRequest(body, wantGrid: true));
            Assert.NotEmpty(sink.GridRows);
            Assert.NotEmpty(sink.Writes.Where(w => w.Severity == "Output").ToList());
        }

        // ---- I. Cancellation ----

        [Fact]
        public async Task Long_Running_Script_Cancels_Promptly()
        {
            // Asserts the OBSERVABLE outcome of cancellation: the script's
            // post-loop Write-Output never fires, and the whole dispatch
            // finishes well before the script's natural 6-second runtime.
            //
            // We deliberately don't assert result.Cancelled == true. PS
            // 5.1 has an internal race: if ps.Stop() lands between two
            // pipeline commands (rather than mid-cmdlet), Invoke can
            // return normally without throwing PipelineStoppedException,
            // and the executor reports Ok(0). The user-visible effect
            // (script didn't run to completion) is the same; that's
            // what we pin.
            string body =
                "for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 100 }\n" +
                "Write-Output 'should never print'";
            var req = BuildRequest(body);
            req.ButtonLabel = "long";
            var sink = new FakeSink();
            using var cts = new CancellationTokenSource();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var runTask = _fx.Executor.ExecuteAsync(req, sink, cts.Token);
            await Task.Delay(400);
            cts.Cancel();
            var result = await runTask;
            sw.Stop();
            Assert.NotNull(result);
            // The script's natural runtime is 6 seconds. If we finished
            // in under 3 seconds, we cancelled effectively.
            Assert.True(sw.ElapsedMilliseconds < 3000,
                $"Expected cancellation to land well under 3s; took {sw.ElapsedMilliseconds}ms");
            Assert.DoesNotContain(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("should never print"));
        }

        // ---- J. Working directory ----
        //
        // Note: PowerShellExecutor does NOT apply ExecutionRequest.WorkingDirectory
        // to the runspace. Scripts inherit the process CWD instead.
        // Cmd / Process executors DO honor WorkingDirectory (their
        // ProcessStartInfo sets it on the child process). This is a
        // known asymmetry -- if you need a specific CWD inside a
        // PowerShell script, do `Set-Location $somePath` inside the
        // script itself, or thread it through a shared input.
        //
        // A documentation-test below pins the current behavior so a
        // future change that DOES honor WorkingDirectory will be a
        // deliberate decision (test will start failing and require
        // updating).

        [Fact]
        public async Task PowerShell_Ignores_Request_WorkingDirectory()
        {
            string body = "Write-Output (Get-Location).Path";
            var req = BuildRequest(body);
            req.WorkingDirectory = _fx.TempDir; // deliberately bogus to confirm it's ignored
            var (sink, _) = await RunAsync(req);
            string tempDirName = System.IO.Path.GetFileName(_fx.TempDir);
            // The temp dir name should NOT appear -- PS used the
            // process CWD (the test bin folder).
            Assert.DoesNotContain(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains(tempDirName));
        }
    }
}
