using System;
using System.IO;
using ScriptDeck.Hosting;

namespace ScriptDeck.Tests.Fakes
{
    /// <summary>
    /// xUnit class fixture that owns a single <see cref="PowerShellExecutor"/>
    /// shared across all integration tests in the collection. Opening a
    /// real PowerShell runspace costs ~1-2 seconds; if every test paid
    /// that, the suite would take minutes. Sharing the executor matches
    /// what production does anyway (one runspace per Shell instance).
    ///
    /// Tests in <see cref="ScriptDeck.Tests.RealPowerShellTests"/> must
    /// NOT mutate global runspace state (no <c>$global:foo</c> writes,
    /// no <c>Import-Module</c> calls, etc.) -- if they do, they'll
    /// pollute every subsequent test. Each test should rely only on
    /// per-invocation state (shared inputs, args, script body).
    /// </summary>
    public sealed class PowerShellFixture : IDisposable
    {
        public PowerShellExecutor Executor { get; }
        public string TempDir { get; }

        public PowerShellFixture()
        {
            Executor = new PowerShellExecutor();
            TempDir = Path.Combine(Path.GetTempPath(), "ScriptDeckPSTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
        }

        // Write a script to the fixture's temp dir under a unique name
        // and return the absolute path. Caller can pass it as
        // ExecutionRequest.ScriptPath.
        public string WriteScript(string body)
        {
            string path = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(path, body);
            return path;
        }

        public void Dispose()
        {
            try { Executor?.Dispose(); } catch { }
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
            catch { }
        }
    }
}
