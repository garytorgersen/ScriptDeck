using System.Threading;
using System.Threading.Tasks;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// One executor per supported runtime: PowerShell, cmd.exe, raw process.
    /// The dispatcher picks an implementation based on the button's
    /// configured <c>executor</c> kind, then calls <see cref="ExecuteAsync"/>.
    ///
    /// Implementations MUST:
    ///  - run on a background thread (Task.Run inside) so the UI stays alive,
    ///  - route every byte of stdout/stderr through the sink,
    ///  - honor cancellation by killing the underlying process or pipeline,
    ///  - NEVER touch UI controls directly.
    /// </summary>
    public interface IExecutor
    {
        /// <summary>
        /// Identifier matching the workspace JSON's <c>executor</c> field
        /// (e.g. "powershell", "cmd", "process"). Case-insensitive matching
        /// is applied at dispatch time.
        /// </summary>
        string Kind { get; }

        Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request,
            IOutputSink sink,
            CancellationToken cancellationToken);
    }
}
