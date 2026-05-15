using System;
using System.Threading;
using System.Threading.Tasks;
using ScriptDeck.Hosting;

namespace ScriptDeck.Tests.Fakes
{
    /// <summary>
    /// Configurable IExecutor for dispatcher / queue tests. The Behavior
    /// callback is invoked inside ExecuteAsync; tests use it to simulate
    /// a slow run, an exception, a non-zero exit, cancellation, etc.
    /// </summary>
    public sealed class FakeExecutor : IExecutor
    {
        public string Kind { get; }
        public Func<ExecutionRequest, IOutputSink, CancellationToken, Task<ExecutionResult>> Behavior { get; set; }

        public int InvocationCount;
        public ExecutionRequest LastRequest;
        public IOutputSink LastSink;

        public FakeExecutor(string kind = "powershell")
        {
            Kind = kind;
        }

        public Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request, IOutputSink sink, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InvocationCount);
            LastRequest = request;
            LastSink = sink;
            if (Behavior != null) return Behavior(request, sink, cancellationToken);
            return Task.FromResult(ExecutionResult.Ok(0, TimeSpan.Zero));
        }
    }
}
