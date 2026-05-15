using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Launches an arbitrary executable via the OS shell — fire-and-forget,
    /// no output capture, no wait-for-exit. Designed for buttons that open
    /// a GUI app (notepad, taskmgr, mstsc) and let the user interact in
    /// that app's own window.
    ///
    /// If you need stdout/stderr captured into the console, route through
    /// <see cref="CmdExecutor"/> or <see cref="PowerShellExecutor"/> instead.
    /// Mixing capture into this executor would force CreateNoWindow which
    /// breaks GUI apps, and skipping the wait would race UI threads
    /// reading streams that already closed — we keep the contract simple
    /// by saying "this one doesn't capture."
    /// </summary>
    public sealed class ProcessExecutor : IExecutor
    {
        public string Kind => "process";

        public Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request, IOutputSink sink, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            var sw = Stopwatch.StartNew();

            if (string.IsNullOrEmpty(request.ScriptPath))
            {
                sink.WriteError("Process: scriptPath is required." + Environment.NewLine);
                return Task.FromResult(ExecutionResult.Failed("scriptPath required", sw.Elapsed));
            }

            // Pre-check: distinguish "valid launch target" from "shell will
            // silently reject" before calling Start. Process.Start with
            // UseShellExecute=true returns null in two unrelated cases:
            //   1. URL handlers / async shell ops succeeded (our happy path)
            //   2. The shell rejected the launch silently
            // We can't tell those apart from the return value, so we
            // pre-validate. If the FileName looks like a URL (scheme://)
            // we accept it; otherwise it must exist on disk OR resolve
            // via PATH lookup. Without this, a typo in a button's
            // scriptPath is silently logged as "Launched (detached)".
            bool isUrl = LooksLikeUrl(request.ScriptPath);
            if (!isUrl && !FileResolves(request.ScriptPath))
            {
                sink.WriteError(
                    $"Process: launch target not found: {request.ScriptPath}" + Environment.NewLine);
                return Task.FromResult(
                    ExecutionResult.Failed("Launch target not found", sw.Elapsed));
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = request.ScriptPath,
                    Arguments = BuildArguments(request),
                    // ShellExecute=true so users can target .lnk shortcuts,
                    // URLs, document files associated with default apps,
                    // and so the OS picks an appropriate window mode.
                    UseShellExecute = true,
                    WorkingDirectory = ResolveWorkingDirectory(request),
                };

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    // After the pre-check above, a null return here is
                    // the legitimate async-handler case (URL opened a
                    // browser, document opened in its associated app).
                    sink.Log($"Launched (detached): {Path.GetFileName(request.ScriptPath)}");
                    return Task.FromResult(ExecutionResult.Ok(0, sw.Elapsed));
                }

                sink.Log($"Launched: {Path.GetFileName(request.ScriptPath)} (PID {proc.Id})");
                // Detach immediately — we don't want to keep a handle open
                // and we don't want cancellation to kill a window the
                // user is actively typing in.
                proc.Dispose();

                return Task.FromResult(ExecutionResult.Ok(0, sw.Elapsed));
            }
            catch (Exception ex)
            {
                sink.WriteError($"[process] Could not launch '{request.ScriptPath}': {ex.Message}{Environment.NewLine}");
                return Task.FromResult(ExecutionResult.Failed(ex.Message, sw.Elapsed));
            }
        }

        private static string BuildArguments(ExecutionRequest req)
        {
            if (req.Args == null || req.Args.Count == 0) return string.Empty;
            // CRT-style argument quoting (the convention Windows EXEs see
            // through CommandLineToArgvW). Different from cmd.exe quoting
            // — we backslash-escape internal quotes and double trailing
            // backslashes that precede a closing quote.
            return string.Join(" ", req.Args.Select(QuoteArgCrt));
        }

        private static string QuoteArgCrt(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (!arg.Any(c => c == ' ' || c == '\t' || c == '"')) return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    // Escape this quote AND every preceding backslash.
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                    backslashes = 0;
                }
            }
            // Trailing backslashes need doubling so the closing quote
            // doesn't get escaped away.
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static string ResolveWorkingDirectory(ExecutionRequest req)
        {
            if (!string.IsNullOrEmpty(req.WorkingDirectory)) return req.WorkingDirectory;
            // For a detached GUI launch, default to the EXE's directory
            // so relative-path startup behavior matches a Start-menu launch.
            try { return Path.GetDirectoryName(Path.GetFullPath(req.ScriptPath)) ?? string.Empty; }
            catch { return string.Empty; }
        }

        // True if the target looks like a URL (anything ShellExecute would
        // hand to a registered protocol handler). We don't validate the
        // scheme is registered -- if the user typed git://foo we trust
        // them. Worst case, ShellExecute throws Win32Exception and we
        // fall into the catch in the main path.
        private static bool LooksLikeUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int colon = path.IndexOf(':');
            if (colon <= 1) return false; // "C:\..." -> not a URL
            // Schemes are letters / digits / + / - / . per RFC 3986.
            for (int i = 0; i < colon; i++)
            {
                char c = path[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.';
                if (!ok) return false;
            }
            return true;
        }

        // Resolve a launch target that should exist on disk -- either an
        // absolute path that File/Directory.Exists, OR a bare command
        // name that Windows can find on PATH (notepad.exe, mstsc, etc.).
        private static bool FileResolves(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (File.Exists(path)) return true;
                if (Directory.Exists(path)) return true; // ShellExecute opens folders
                // PATH lookup: if no directory component, search PATHEXT
                // through the environment PATH.
                if (!path.Contains(Path.DirectorySeparatorChar)
                    && !path.Contains(Path.AltDirectorySeparatorChar))
                {
                    string envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
                    var exts = new[] { string.Empty }
                        .Concat(pathExt.Split(';'))
                        .Where(e => !string.IsNullOrWhiteSpace(e) || e == string.Empty)
                        .ToArray();
                    foreach (var dir in envPath.Split(';'))
                    {
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        foreach (var ext in exts)
                        {
                            var candidate = Path.Combine(dir, path + ext);
                            if (File.Exists(candidate)) return true;
                        }
                    }
                }
            }
            catch
            {
                // Malformed path / permissions issue / etc. Treat as
                // "not resolvable" -- the caller surfaces a clean error.
            }
            return false;
        }
    }
}
