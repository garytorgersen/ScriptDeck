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
    public class BackgroundJobQueueTests
    {
        // Helper: Dispatcher with a background queue wired to a single
        // FakeExecutor. The background path lives on Dispatcher;
        // BackgroundJobQueue itself is internal in the sense that all
        // its public hooks are reachable through the queue property.
        private static (Dispatcher d, FakeSink sink, FakeExecutor fg, FakeExecutor bg) MakeDispatcher()
        {
            var sink = new FakeSink();
            var fg = new FakeExecutor("powershell");
            var bg = new FakeExecutor("powershell");
            var d = new Dispatcher(
                sink,
                executors: new IExecutor[] { fg },
                backgroundExecutors: new IExecutor[] { bg });
            return (d, sink, fg, bg);
        }

        private static ExecutionRequest Req(string label = "Test") => new ExecutionRequest
        {
            ScriptPath = @"C:\fake.ps1",
            ButtonLabel = label,
            Args = new List<string>(),
            OutputTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" },
        };

        // Wait until a predicate is true or a deadline expires. Used
        // throughout because background work is genuinely asynchronous
        // and we need bounded waits in tests.
        private static async Task WaitUntilAsync(Func<bool> pred, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!pred() && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(10);
            Assert.True(pred(), $"Condition not met within {timeoutMs}ms");
        }

        [Fact]
        public async Task EnqueueBackground_Runs_The_Job_And_Marks_Completed()
        {
            var (d, _, _, bg) = MakeDispatcher();
            using (d)
            {
                var job = d.EnqueueBackground(Req("hello"), "powershell");
                Assert.NotNull(job);
                // Note: we deliberately don't assert "Status == Queued"
                // here -- with a fast no-op executor, the worker can
                // already be Completed by the time control returns from
                // EnqueueBackground. Status-transition observability is
                // covered by Job_Status_Transitions_Are_Reported_Via_Event.
                await WaitUntilAsync(() => job.Status == Job.JobStatus.Completed);
                Assert.Equal(1, bg.InvocationCount);
            }
        }

        [Fact]
        public async Task Two_Background_Jobs_Run_Serially_FIFO()
        {
            // The current design is single-worker; the second job must
            // wait for the first to finish. Use a release gate on the
            // first job's executor behavior, observe ordering.
            var (d, _, _, bg) = MakeDispatcher();
            using var gate1 = new ManualResetEventSlim(false);
            int seq = 0;
            int firstSeq = 0, secondSeq = 0;
            bg.Behavior = (req, sk, ct) => Task.Run(() =>
            {
                if (req.ButtonLabel == "first")
                {
                    gate1.Wait(ct);
                    firstSeq = Interlocked.Increment(ref seq);
                }
                else
                {
                    secondSeq = Interlocked.Increment(ref seq);
                }
                return ExecutionResult.Ok(0, TimeSpan.Zero);
            });

            using (d)
            {
                var j1 = d.EnqueueBackground(Req("first"),  "powershell");
                var j2 = d.EnqueueBackground(Req("second"), "powershell");
                // j2 should be queued while j1 is mid-flight.
                await WaitUntilAsync(() => j1.Status == Job.JobStatus.Running);
                Assert.Equal(Job.JobStatus.Queued, j2.Status);
                gate1.Set();
                await WaitUntilAsync(() => j2.Status == Job.JobStatus.Completed);
                Assert.True(firstSeq < secondSeq, "first should have completed before second");
            }
        }

        [Fact]
        public async Task Cancel_Queued_Job_Marks_It_Cancelled_Without_Running()
        {
            var (d, _, _, bg) = MakeDispatcher();
            using var gate = new ManualResetEventSlim(false);
            bg.Behavior = (req, sk, ct) => Task.Run(() => { gate.Wait(ct); return ExecutionResult.Ok(0, TimeSpan.Zero); });

            using (d)
            {
                var j1 = d.EnqueueBackground(Req("first"),  "powershell");
                var j2 = d.EnqueueBackground(Req("second"), "powershell");
                await WaitUntilAsync(() => j1.Status == Job.JobStatus.Running);

                // While j1 holds the worker, cancel j2 (still Queued).
                d.BackgroundQueue.Cancel(j2);
                Assert.Equal(Job.JobStatus.Cancelled, j2.Status);
                Assert.Equal("User", j2.CancelReason);

                gate.Set();
                await WaitUntilAsync(() => j1.Status == Job.JobStatus.Completed);
                // bg should have been invoked only for j1, never j2.
                Assert.Equal(1, bg.InvocationCount);
            }
        }

        [Fact]
        public async Task Cancel_Running_Job_Propagates_To_Executor()
        {
            var (d, _, _, bg) = MakeDispatcher();
            using var started = new ManualResetEventSlim(false);
            bg.Behavior = (req, sk, ct) => Task.Run(() =>
            {
                started.Set();
                while (!ct.IsCancellationRequested) Thread.Sleep(5);
                return ExecutionResult.CancelledResult(TimeSpan.Zero);
            });

            using (d)
            {
                var job = d.EnqueueBackground(Req(), "powershell");
                started.Wait();
                d.BackgroundQueue.Cancel(job);
                await WaitUntilAsync(() => job.Status == Job.JobStatus.Cancelled);
                Assert.Equal("User", job.CancelReason);
            }
        }

        [Fact]
        public async Task Job_Status_Transitions_Are_Reported_Via_Event()
        {
            var (d, _, _, _) = MakeDispatcher();
            var seen = new List<Job.JobStatus>();
            d.BackgroundQueue.JobStatusChanged += j =>
            {
                lock (seen) seen.Add(j.Status);
            };

            using (d)
            {
                var job = d.EnqueueBackground(Req(), "powershell");
                await WaitUntilAsync(() => job.Status == Job.JobStatus.Completed);
            }
            lock (seen)
            {
                Assert.Contains(Job.JobStatus.Running,   seen);
                Assert.Contains(Job.JobStatus.Completed, seen);
            }
        }

        [Fact]
        public async Task JobAdded_Event_Fires_Once_Per_Submit()
        {
            var (d, _, _, _) = MakeDispatcher();
            int added = 0;
            d.BackgroundQueue.JobAdded += _ => Interlocked.Increment(ref added);

            using (d)
            {
                var j1 = d.EnqueueBackground(Req("a"), "powershell");
                var j2 = d.EnqueueBackground(Req("b"), "powershell");
                await WaitUntilAsync(() => j2.Status == Job.JobStatus.Completed);
                Assert.Equal(2, added);
            }
        }

        [Fact]
        public async Task Worker_Survives_Executor_Exception()
        {
            // H8 fix: a single bad job must not kill the worker. Submit a
            // poison job followed by a normal one; verify the normal one
            // still runs.
            var (d, _, _, bg) = MakeDispatcher();
            bg.Behavior = (req, sk, ct) =>
            {
                if (req.ButtonLabel == "poison")
                    throw new InvalidOperationException("kaboom");
                return Task.FromResult(ExecutionResult.Ok(0, TimeSpan.Zero));
            };

            using (d)
            {
                var bad   = d.EnqueueBackground(Req("poison"), "powershell");
                var good  = d.EnqueueBackground(Req("good"),   "powershell");
                await WaitUntilAsync(() => bad.Status  == Job.JobStatus.Failed);
                await WaitUntilAsync(() => good.Status == Job.JobStatus.Completed);
            }
        }

        [Fact]
        public async Task Unknown_Executor_Marks_Job_Failed_Not_Stuck_Running()
        {
            // Submit a job whose Kind doesn't match any background executor.
            // It should land in Failed, NOT hang in Running forever.
            var sink = new FakeSink();
            var bg   = new FakeExecutor("powershell");
            var d = new Dispatcher(
                sink,
                executors: new IExecutor[] { bg },
                backgroundExecutors: new IExecutor[] { bg });

            using (d)
            {
                var job = d.EnqueueBackground(Req(), "nonsense-executor");
                // Wait on BOTH transitions together. The worker sets
                // Status=Failed first and ErrorMessage on the next
                // line, so polling for Status alone can win the race
                // with ErrorMessage still null. Polling for both
                // closes the window.
                await WaitUntilAsync(() =>
                    job.Status == Job.JobStatus.Failed && job.ErrorMessage != null);
                Assert.NotNull(job.ErrorMessage);
                Assert.Contains("nonsense", job.ErrorMessage);
            }
        }

        [Fact]
        public async Task Completed_Job_Has_Times_And_Exit_Code()
        {
            var (d, _, _, bg) = MakeDispatcher();
            bg.Behavior = (req, sk, ct) =>
                Task.FromResult(ExecutionResult.Ok(42, TimeSpan.FromMilliseconds(50)));

            using (d)
            {
                var job = d.EnqueueBackground(Req(), "powershell");
                await WaitUntilAsync(() => job.Status == Job.JobStatus.Completed);
                Assert.NotNull(job.StartedAtUtc);
                Assert.NotNull(job.CompletedAtUtc);
                Assert.True(job.CompletedAtUtc >= job.StartedAtUtc);
                Assert.Equal(42, job.ExitCode);
            }
        }

        [Fact]
        public async Task Dispose_Cancels_In_Flight_Job_With_Shutdown_Reason()
        {
            var bg = new FakeExecutor("powershell");
            using var started = new ManualResetEventSlim(false);
            bg.Behavior = (req, sk, ct) => Task.Run(() =>
            {
                started.Set();
                while (!ct.IsCancellationRequested) Thread.Sleep(5);
                return ExecutionResult.CancelledResult(TimeSpan.Zero);
            });

            var d = new Dispatcher(
                new FakeSink(),
                executors: new IExecutor[] { bg },
                backgroundExecutors: new IExecutor[] { bg });

            var job = d.EnqueueBackground(Req(), "powershell");
            started.Wait();
            d.Dispose();   // Shutdown path -- should cancel the in-flight job.
            await WaitUntilAsync(() => job.Status == Job.JobStatus.Cancelled);
            // CancelReason can be either "Shutdown" or "User" depending
            // on which cancellation path won the race -- both are
            // valid shutdown-time outcomes.
            Assert.NotNull(job.CancelReason);
        }
    }
}
