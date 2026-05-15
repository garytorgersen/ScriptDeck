using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.Hosting;
using ScriptDeck.Tests.Fakes;
using Xunit;

namespace ScriptDeck.Tests
{
    public class DispatcherTests
    {
        // Helper: build a dispatcher wired to a single FakeExecutor for
        // a known executor kind. Background path off by default.
        private static (Dispatcher d, FakeSink sink, FakeExecutor exec) MakeDispatcher(string kind = "powershell")
        {
            var sink = new FakeSink();
            var exec = new FakeExecutor(kind);
            var d = new Dispatcher(sink, new IExecutor[] { exec });
            return (d, sink, exec);
        }

        private static ExecutionRequest MakeRequest(string label = "Test") => new ExecutionRequest
        {
            ScriptPath = @"C:\fake.ps1",
            ButtonLabel = label,
            Args = new System.Collections.Generic.List<string>(),
            OutputTargets = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" },
        };

        [Fact]
        public async Task ExecuteAsync_Routes_To_Matching_Executor()
        {
            var (d, _, exec) = MakeDispatcher();
            using (d)
            {
                var result = await d.ExecuteAsync(MakeRequest(), "powershell");
                Assert.NotNull(result);
                Assert.Equal(1, exec.InvocationCount);
            }
        }

        [Fact]
        public async Task Unknown_Executor_Returns_Null_And_Writes_Error()
        {
            var (d, sink, _) = MakeDispatcher();
            using (d)
            {
                var result = await d.ExecuteAsync(MakeRequest(), "nonsense");
                Assert.Null(result);
                Assert.Contains(sink.Writes, w =>
                    w.Severity == "Error" && w.Text.Contains("Unknown executor"));
            }
        }

        [Fact]
        public async Task SingleFlight_Gate_Rejects_Second_Click_While_Busy()
        {
            // The fake's behavior holds the executor "running" until we
            // release the gate. Second ExecuteAsync should bail
            // immediately with a warning.
            var (d, sink, exec) = MakeDispatcher();
            using var gate = new ManualResetEventSlim(false);
            exec.Behavior = (req, sk, ct) => Task.Run(() =>
            {
                gate.Wait(ct);
                return ExecutionResult.Ok(0, TimeSpan.Zero);
            });

            using (d)
            {
                var t1 = d.ExecuteAsync(MakeRequest("first"), "powershell");

                // Spin briefly until the executor is actually mid-flight,
                // so the second click hits the busy gate. The Behavior
                // increments InvocationCount synchronously at entry.
                while (exec.InvocationCount == 0) await Task.Delay(5);

                var second = await d.ExecuteAsync(MakeRequest("second"), "powershell");
                Assert.Null(second);
                Assert.Contains(sink.Writes, w =>
                    w.Severity == "Warning" && w.Text.Contains("first"));

                gate.Set();
                await t1;
            }
        }

        [Fact]
        public async Task ActiveLabel_Is_Set_Before_Busy_Visible_To_Rejector()
        {
            // H2 fix: ActiveLabel must be the FIRST thing under the lock
            // so a rejection-message reader never sees null.
            var (d, sink, exec) = MakeDispatcher();
            using var gate = new ManualResetEventSlim(false);
            exec.Behavior = (req, sk, ct) => Task.Run(() => { gate.Wait(ct); return ExecutionResult.Ok(0, TimeSpan.Zero); });

            using (d)
            {
                var t1 = d.ExecuteAsync(MakeRequest("FIRST_LABEL"), "powershell");
                while (exec.InvocationCount == 0) await Task.Delay(5);
                var second = await d.ExecuteAsync(MakeRequest("ignored"), "powershell");
                Assert.Null(second);
                var warn = sink.Writes.First(w => w.Severity == "Warning");
                Assert.Contains("FIRST_LABEL", warn.Text);
                gate.Set();
                await t1;
            }
        }

        [Fact]
        public async Task BusyChanged_Fires_On_Both_Transitions()
        {
            var (d, _, exec) = MakeDispatcher();
            int events = 0;
            d.BusyChanged += (s, e) => Interlocked.Increment(ref events);

            using (d)
            {
                await d.ExecuteAsync(MakeRequest(), "powershell");
            }
            // Idle -> Busy -> Idle = 2 events.
            Assert.Equal(2, events);
        }

        [Fact]
        public async Task Override_Sink_Receives_Output_Not_Main_Sink()
        {
            var (d, mainSink, exec) = MakeDispatcher();
            var altSink = new FakeSink();
            exec.Behavior = (req, sk, ct) =>
            {
                // The executor writes to whichever sink it got -- here it
                // should be the override.
                sk.WriteOutput("hello from script");
                return Task.FromResult(ExecutionResult.Ok(0, TimeSpan.Zero));
            };
            using (d)
            {
                await d.ExecuteAsync(MakeRequest(), "powershell", overrideSink: altSink);
            }
            Assert.Contains(altSink.Writes,  w => w.Text == "hello from script");
            Assert.DoesNotContain(mainSink.Writes, w => w.Text == "hello from script");
        }

        [Fact]
        public async Task Override_Sink_Receives_Unknown_Executor_Error()
        {
            // H1 fix: pre-execute warnings/errors route through the
            // override sink too, not the main sink.
            var (d, mainSink, _) = MakeDispatcher();
            var altSink = new FakeSink();
            using (d)
            {
                var result = await d.ExecuteAsync(MakeRequest(), "nonsense", overrideSink: altSink);
                Assert.Null(result);
            }
            Assert.Contains(altSink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("Unknown executor"));
            Assert.DoesNotContain(mainSink.Writes,
                w => w.Severity == "Error" && w.Text.Contains("Unknown executor"));
        }

        [Fact]
        public async Task CancelActive_Propagates_Cancellation_To_Executor()
        {
            var (d, _, exec) = MakeDispatcher();
            CancellationToken seenToken = default;
            using var started = new ManualResetEventSlim(false);
            exec.Behavior = (req, sk, ct) => Task.Run(() =>
            {
                seenToken = ct;
                started.Set();
                // Wait for cancellation.
                while (!ct.IsCancellationRequested) Thread.Sleep(5);
                return ExecutionResult.CancelledResult(TimeSpan.Zero);
            });

            using (d)
            {
                var t = d.ExecuteAsync(MakeRequest(), "powershell");
                started.Wait();
                d.CancelActive();
                var result = await t;
                Assert.NotNull(result);
                Assert.True(result.Cancelled);
                Assert.True(seenToken.IsCancellationRequested);
            }
        }

        [Fact]
        public async Task IsBusy_True_While_Running_False_After()
        {
            var (d, _, exec) = MakeDispatcher();
            using var gate = new ManualResetEventSlim(false);
            exec.Behavior = (req, sk, ct) => Task.Run(() => { gate.Wait(ct); return ExecutionResult.Ok(0, TimeSpan.Zero); });

            using (d)
            {
                Assert.False(d.IsBusy);
                var t = d.ExecuteAsync(MakeRequest(), "powershell");
                while (exec.InvocationCount == 0) await Task.Delay(5);
                Assert.True(d.IsBusy);
                gate.Set();
                await t;
                Assert.False(d.IsBusy);
            }
        }

        [Fact]
        public async Task Null_Request_Throws_ArgumentNullException()
        {
            var (d, _, _) = MakeDispatcher();
            using (d)
            {
                await Assert.ThrowsAsync<ArgumentNullException>(
                    async () => await d.ExecuteAsync(null, "powershell"));
            }
        }

        [Fact]
        public async Task Executor_Exception_Surfaces_As_Error_Result()
        {
            var (d, sink, exec) = MakeDispatcher();
            exec.Behavior = (req, sk, ct) => throw new InvalidOperationException("boom");
            using (d)
            {
                var result = await d.ExecuteAsync(MakeRequest(), "powershell");
                Assert.NotNull(result);
                Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
                Assert.Contains("boom", result.ErrorMessage);
            }
        }

        [Fact]
        public async Task BackgroundQueue_Null_When_No_BackgroundExecutors()
        {
            var (d, _, _) = MakeDispatcher();
            using (d) { Assert.Null(d.BackgroundQueue); }
            // EnqueueBackground returns null when queue is disabled.
            var (d2, _, _) = MakeDispatcher();
            using (d2)
            {
                Assert.Null(d2.EnqueueBackground(MakeRequest(), "powershell"));
            }
        }
    }
}
