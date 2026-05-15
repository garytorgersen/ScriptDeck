using System;
using ScriptDeck.Hosting;
using Xunit;

namespace ScriptDeck.Tests
{
    public class JobTests
    {
        [Fact]
        public void New_Job_Defaults_To_Queued_Status()
        {
            var j = new Job { ButtonLabel = "x" };
            Assert.Equal(Job.JobStatus.Queued, j.Status);
            Assert.Null(j.StartedAtUtc);
            Assert.Null(j.CompletedAtUtc);
            Assert.Null(j.ExitCode);
            Assert.Null(j.ErrorMessage);
            Assert.Null(j.CancelReason);
        }

        [Fact]
        public void New_Job_Has_Unique_Id_And_Cts()
        {
            var a = new Job();
            var b = new Job();
            Assert.NotEqual(Guid.Empty, a.Id);
            Assert.NotEqual(a.Id, b.Id);
            Assert.NotNull(a.Cts);
            Assert.False(a.Cts.IsCancellationRequested);
        }

        [Fact]
        public void Elapsed_Is_Zero_While_Queued()
        {
            var j = new Job();
            Assert.Equal(TimeSpan.Zero, j.Elapsed);
        }

        [Fact]
        public void Elapsed_Computes_Once_Running()
        {
            var j = new Job { Status = Job.JobStatus.Running };
            j.StartedAtUtc = DateTime.UtcNow.AddSeconds(-2);
            // Elapsed uses CompletedAtUtc when set, else DateTime.UtcNow.
            // A 2s gap should be >= 1.5s and < 60s (rough bounds for a
            // wall-clock-dependent test).
            var elapsed = j.Elapsed;
            Assert.True(elapsed.TotalSeconds >= 1.5);
            Assert.True(elapsed.TotalSeconds < 60);
        }

        [Fact]
        public void Elapsed_Uses_CompletedAt_When_Set()
        {
            var j = new Job { Status = Job.JobStatus.Completed };
            j.StartedAtUtc   = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);
            j.CompletedAtUtc = new DateTime(2026, 5, 5, 10, 0, 5, DateTimeKind.Utc);
            Assert.Equal(TimeSpan.FromSeconds(5), j.Elapsed);
        }

        [Fact]
        public void Cancel_Reason_Roundtrips()
        {
            var j = new Job { CancelReason = "User" };
            Assert.Equal("User", j.CancelReason);
        }
    }
}
