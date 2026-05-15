using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ScriptDeck.Forms;

namespace ScriptDeck
{
    internal static class Program
    {
        // Crash + startup log lives next to per-user app data so even
        // non-admin runs can write it. We treat any failure to log as
        // non-fatal — never let logging itself prevent the app from
        // starting (or from reporting another error).
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScriptDeck");

        // STA is required for WinForms + clipboard + the in-process PowerShell
        // host (PS 5.1 wants STA when invoked from the calling thread).
        [STAThread]
        private static void Main()
        {
            // Hook EVERY error pathway before we do anything that might
            // throw. Without this, a WinExe target swallows startup
            // exceptions and the user sees "nothing happens" — which is
            // exactly the symptom we're chasing.
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
            Application.ThreadException += OnThreadException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Trace("Main entered.");

            // Single-instance lock. A user-scoped named Mutex prevents
            // two ScriptDeck instances from racing on shared per-user
            // state (recent.json, history.db). The "Local\\" prefix
            // limits the mutex to the current Windows session, so two
            // different users on the same machine (or two RDP sessions)
            // each get their own ScriptDeck. Using a Guid keeps us from
            // colliding with an unrelated app that picked the same name.
            const string mutexName = "Local\\ScriptDeck-{2661DD04-E033-4D7A-A564-2A20657B2025}";
            bool createdNew;
            using (var instanceLock = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out createdNew))
            {
                if (!createdNew)
                {
                    Trace("Another ScriptDeck instance is already running. Exiting.");
                    try
                    {
                        MessageBox.Show(
                            "ScriptDeck is already running. Switch to that window instead of starting a new one.",
                            "ScriptDeck",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch { /* even the messagebox failed; just exit */ }
                    return;
                }

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    Trace("About to construct Shell.");
                    using (var shell = new Shell())
                    {
                        Trace("Shell constructed; calling Application.Run.");
                        Application.Run(shell);
                        Trace("Application.Run returned normally.");
                    }
                }
                catch (Exception ex)
                {
                    ReportFatal(ex, "Main");
                }
                finally
                {
                    // Release the mutex explicitly so a fast restart
                    // (rare but possible) finds the slot free.
                    try { instanceLock.ReleaseMutex(); } catch { /* not owned */ }
                }
            }
        }

        private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown non-Exception throwable");
            ReportFatal(ex, "AppDomain.UnhandledException");
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ReportFatal(e.Exception, "Application.ThreadException");
        }

        private static void Trace(string message)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(
                    Path.Combine(LogDir, "startup.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }
        }

        private static void ReportFatal(Exception ex, string source)
        {
            string logPath = "(unwritten)";
            try
            {
                Directory.CreateDirectory(LogDir);
                logPath = Path.Combine(LogDir,
                    $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(logPath,
                    $"Source : {source}{Environment.NewLine}" +
                    $"Time   : {DateTime.Now:O}{Environment.NewLine}" +
                    $"Type   : {ex.GetType().FullName}{Environment.NewLine}" +
                    $"Message: {ex.Message}{Environment.NewLine}" +
                    $"---- StackTrace ----{Environment.NewLine}" +
                    ex.ToString() + Environment.NewLine);
            }
            catch { /* swallow; we'll still try the message box */ }

            try
            {
                // Best-effort modal so the user knows something went
                // wrong even if they never see the log file. Use a
                // null owner — our form may not exist yet.
                MessageBox.Show(
                    $"ScriptDeck failed to start.\r\n\r\n" +
                    $"{ex.GetType().Name}: {ex.Message}\r\n\r\n" +
                    $"Log: {logPath}",
                    "ScriptDeck \u2014 Fatal error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* even the messagebox failed; nothing else to do */ }
        }
    }
}
