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
    /// Integration tests for the bootstrap-helper trio:
    ///   Set-SharedInput / Get-SharedInput / Remove-SharedInput
    ///
    /// Each helper emits a tagged PSObject that PowerShellExecutor's
    /// DataAdded handler intercepts. We assert on the events the
    /// executor fires (SharedInputSetRequested / SharedInputRemoveRequested)
    /// since that's the contract with Shell.
    ///
    /// Tests share the PowerShellFixture for fast startup -- they DO
    /// briefly attach/detach event handlers but never leave global
    /// runspace state behind.
    /// </summary>
    public class SharedInputHelpersTests : IClassFixture<PowerShellFixture>
    {
        private readonly PowerShellFixture _fx;

        public SharedInputHelpersTests(PowerShellFixture fx) { _fx = fx; }

        // Runs the script and returns:
        //   sink          -- captured output / errors
        //   setEvents     -- (id, value, label) tuples fired during the run
        //   removeEvents  -- ids fired during the run
        private async Task<(FakeSink Sink,
                            List<(string Id, string Value, string Label)> SetEvents,
                            List<string> RemoveEvents)>
            RunAndCapture(string body,
                          IDictionary<string, string> sharedInputs = null,
                          ISet<string> staticIds = null)
        {
            string path = _fx.WriteScript(body);
            var req = new ExecutionRequest
            {
                ScriptPath    = path,
                Args          = new List<string>(),
                ButtonLabel   = "test",
                OutputTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" },
                SharedInputs  = sharedInputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                StaticInputIds = staticIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };
            var sink = new FakeSink();

            var setEvents    = new List<(string, string, string)>();
            var removeEvents = new List<string>();
            Action<string, string, string> onSet = (id, value, label) =>
            {
                lock (setEvents) setEvents.Add((id, value, label));
            };
            Action<string> onRemove = id =>
            {
                lock (removeEvents) removeEvents.Add(id);
            };

            _fx.Executor.SharedInputSetRequested    += onSet;
            _fx.Executor.SharedInputRemoveRequested += onRemove;
            try
            {
                await _fx.Executor.ExecuteAsync(req, sink, CancellationToken.None);
            }
            finally
            {
                _fx.Executor.SharedInputSetRequested    -= onSet;
                _fx.Executor.SharedInputRemoveRequested -= onRemove;
            }
            return (sink, setEvents, removeEvents);
        }

        [Fact]
        public async Task Set_SharedInput_Fires_Set_Event_With_Id_Value_Label()
        {
            var (_, sets, removes) = await RunAndCapture(
                "Set-SharedInput -Id 'authToken' -Value 'abc123' -Label 'OAuth bearer'");
            Assert.Single(sets);
            Assert.Equal("authToken",     sets[0].Id);
            Assert.Equal("abc123",        sets[0].Value);
            Assert.Equal("OAuth bearer",  sets[0].Label);
            Assert.Empty(removes);
        }

        [Fact]
        public async Task Set_SharedInput_With_No_Label_Fires_Empty_Label()
        {
            // -Label is optional. PowerShell's [string]$Label parameter
            // defaults to "" (empty string), not $null, when omitted.
            // The Shell-side handler treats "" and null equivalently
            // (both mean "no label, fall back to Id in the grid").
            var (_, sets, _) = await RunAndCapture(
                "Set-SharedInput -Id 'k' -Value 'v'");
            Assert.Single(sets);
            Assert.True(string.IsNullOrEmpty(sets[0].Label),
                $"Expected empty/null Label, got '{sets[0].Label}'");
        }

        [Fact]
        public async Task Set_SharedInput_Empty_Value_Is_Allowed()
        {
            // Empty string is a meaningful "clear" / sentinel value; the
            // helper must NOT reject it.
            var (_, sets, _) = await RunAndCapture(
                "Set-SharedInput -Id 'k' -Value ''");
            Assert.Single(sets);
            Assert.Equal(string.Empty, sets[0].Value);
        }

        [Fact]
        public async Task Set_SharedInput_Tag_Object_Never_Lands_In_Console()
        {
            // The sentinel-tagged PSObject must be intercepted by the
            // executor. The script does NOTHING else, so the console
            // should be completely silent.
            var (sink, sets, _) = await RunAndCapture(
                "Set-SharedInput -Id 'silent' -Value 'x'");
            Assert.Single(sets);
            Assert.Empty(sink.Writes);  // no output, no errors, no warnings
        }

        [Fact]
        public async Task Set_SharedInput_Refused_On_Static_Id_Throws()
        {
            // The bootstrap helper consults the injected $ScriptDeckInputs
            // hashtable -- if the id is already Static, it throws BEFORE
            // emitting any tag. So we should see an error in the stream
            // and zero Set events.
            var (sink, sets, _) = await RunAndCapture(
                "try { Set-SharedInput -Id 'computerName' -Value 'X' } catch { Write-Error $_.Exception.Message }",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "MYBOX" }
                },
                staticIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "computerName" });
            Assert.Empty(sets);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("Static"));
        }

        [Fact]
        public async Task Set_SharedInput_Allowed_When_Id_Is_Volatile_Or_New()
        {
            // No collision with a Static id -- the script should succeed
            // and fire a Set event. companyName is in SharedInputs but
            // NOT in StaticInputIds, so it counts as Volatile.
            var (_, sets, _) = await RunAndCapture(
                "Set-SharedInput -Id 'companyName' -Value 'Acme'",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "companyName", "(previous)" }
                },
                staticIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Assert.Single(sets);
            Assert.Equal("companyName", sets[0].Id);
            Assert.Equal("Acme",        sets[0].Value);
        }

        [Fact]
        public async Task Remove_SharedInput_Fires_Remove_Event()
        {
            var (_, _, removes) = await RunAndCapture(
                "Remove-SharedInput -Id 'authToken'");
            Assert.Single(removes);
            Assert.Equal("authToken", removes[0]);
        }

        [Fact]
        public async Task Remove_SharedInput_On_Static_Id_Throws()
        {
            // The bootstrap helper refuses to even emit a remove tag for
            // a Static id -- it throws client-side. So the test should
            // see an error in the stream and zero Remove events.
            var (sink, _, removes) = await RunAndCapture(
                "try { Remove-SharedInput -Id 'computerName' } catch { Write-Error $_.Exception.Message }",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "MYBOX" }
                },
                staticIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "computerName" });
            Assert.Empty(removes);
            Assert.Contains(sink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("Static"));
        }

        [Fact]
        public async Task Get_SharedInput_With_Id_Returns_Variable_Value()
        {
            // Get-SharedInput -Id X is sugar for $X. Confirm round-trip.
            var (sink, _, _) = await RunAndCapture(
                "Write-Output (Get-SharedInput -Id 'computerName')",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "MYBOX-99" }
                },
                staticIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "computerName" });
            Assert.Contains(sink.Writes,
                w => w.Severity == "Output" && w.Text.Contains("MYBOX-99"));
        }

        [Fact]
        public async Task Get_SharedInput_Bare_Returns_All_With_Scope()
        {
            // Get-SharedInput with no -Id emits one PSObject per known
            // input, each tagged Static or Volatile. Write the Scope
            // values out so we can assert without coupling to the
            // object's ToString format.
            var (sink, _, _) = await RunAndCapture(
                "Get-SharedInput | ForEach-Object { Write-Output ($_.Id + ':' + $_.Scope) }",
                sharedInputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "computerName", "MYBOX"  },  // Static
                    { "tempToken",    "abc"    },  // Volatile (in dict but not in staticIds)
                },
                staticIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "computerName" });
            var outputs = sink.Writes
                .Where(w => w.Severity == "Output")
                .Select(w => w.Text.Trim())
                .ToList();
            Assert.Contains(outputs, t => t.Contains("computerName:Static"));
            Assert.Contains(outputs, t => t.Contains("tempToken:Volatile"));
        }

        [Fact]
        public async Task Multiple_Sets_In_One_Script_Fire_Multiple_Events()
        {
            // Confirms the executor's DataAdded handler doesn't squash
            // duplicates and that the order matches script execution.
            string body =
                "Set-SharedInput -Id 'a' -Value '1'\n" +
                "Set-SharedInput -Id 'b' -Value '2'\n" +
                "Set-SharedInput -Id 'c' -Value '3'\n";
            var (_, sets, _) = await RunAndCapture(body);
            Assert.Equal(3, sets.Count);
            Assert.Equal(new[] { "a", "b", "c" }, sets.Select(s => s.Id).ToArray());
            Assert.Equal(new[] { "1", "2", "3" }, sets.Select(s => s.Value).ToArray());
        }

        [Fact]
        public async Task Bootstrap_Helpers_Are_Defined()
        {
            // Pin existence of all three helpers via Get-Command so a
            // missing-from-bootstrap regression surfaces immediately.
            var (sink, _, _) = await RunAndCapture(
                "@('Set-SharedInput','Get-SharedInput','Remove-SharedInput') | ForEach-Object { " +
                "if (Get-Command $_ -ErrorAction SilentlyContinue) { Write-Output $_ } }");
            var outputs = string.Join(",", sink.Writes
                .Where(w => w.Severity == "Output")
                .Select(w => w.Text.Trim()));
            Assert.Contains("Set-SharedInput",    outputs);
            Assert.Contains("Get-SharedInput",    outputs);
            Assert.Contains("Remove-SharedInput", outputs);
        }
    }
}
