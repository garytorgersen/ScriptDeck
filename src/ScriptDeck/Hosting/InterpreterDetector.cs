using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Probes the local machine for installed script interpreters and
    /// surfaces a list of <see cref="DetectedInterpreter"/>s that the
    /// Shell displays in the welcome banner. Lets the user see at a
    /// glance which PowerShell / Python / Bash variants are reachable
    /// and which one a given workspace will actually use.
    ///
    /// Detection covers:
    ///   - PowerShell 5.1 (always present on supported Windows)
    ///   - System Python on PATH
    ///   - Workspace-configured Python interpreter (if different)
    ///   - Git Bash at canonical install paths
    ///   - WSL distros (via wsl --list)
    ///   - MSYS2 / Cygwin bash at canonical paths
    ///
    /// Performance: each probe spawns the candidate exe with `--version`
    /// (50-200ms each). Results are cached in memory for the session
    /// AND on disk at %LocalAppData%\ScriptDeck\interpreters.json keyed
    /// by (path, mtime), so unchanged interpreters skip the spawn on
    /// subsequent launches. Worst-case cold start is ~1 second.
    /// </summary>
    public sealed class InterpreterDetector
    {
        private readonly string _cachePath;
        private readonly object _gate = new object();
        // Loaded once on first DetectAsync(); reused across detect calls.
        private Dictionary<string, CacheEntry> _cache;

        public InterpreterDetector() : this(DefaultCachePath()) { }

        public InterpreterDetector(string cachePath)
        {
            _cachePath = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
        }

        public static string DefaultCachePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptDeck");
            return Path.Combine(dir, "interpreters.json");
        }

        /// <summary>
        /// Run the full detection scan. Returns a flat list ordered by
        /// kind (PowerShell first, then Python, then Bash) and by
        /// probe order within each kind. Subsequent calls reuse the
        /// in-memory cache from the first call's probes when paths +
        /// mtimes haven't changed.
        ///
        /// <paramref name="workspacePythonInterpreter"/> /
        /// <paramref name="workspaceBashInterpreter"/> let the detector
        /// flag a particular result with IsWorkspaceDefault=true so the
        /// welcome banner can highlight "the one this workspace uses."
        /// Null/empty means no workspace default for that kind.
        /// </summary>
        public async Task<IList<DetectedInterpreter>> DetectAsync(
            string workspacePythonInterpreter = null,
            string workspaceBashInterpreter = null)
        {
            lock (_gate)
            {
                if (_cache == null) _cache = LoadCache();
            }

            var results = new List<DetectedInterpreter>();

            // ---- PowerShell ----
            // 5.1 is part of Windows -- we always include it. The
            // version comes from in-process PowerShell hosting, so no
            // subprocess cost.
            results.Add(DetectPowerShell());

            // ---- Python ----
            // System Python (whatever 'python' resolves to on PATH).
            var sysPy = await ProbeAsync("python", InterpreterKind.Python,
                "python (PATH)", workspacePythonInterpreter).ConfigureAwait(false);
            if (sysPy != null) results.Add(sysPy);

            // Workspace-configured Python, if different from system.
            if (!string.IsNullOrWhiteSpace(workspacePythonInterpreter)
                && !PathsMatch(sysPy?.Path, workspacePythonInterpreter))
            {
                var wsPy = await ProbeAsync(workspacePythonInterpreter,
                    InterpreterKind.Python, "workspace default",
                    workspacePythonInterpreter).ConfigureAwait(false);
                if (wsPy != null) results.Add(wsPy);
            }

            // ---- Bash variants ----
            // Bare 'bash' on PATH (often Git Bash if Git for Windows is
            // installed and added itself to PATH).
            var pathBash = await ProbeAsync("bash", InterpreterKind.Bash,
                "bash (PATH)", workspaceBashInterpreter).ConfigureAwait(false);
            if (pathBash != null) results.Add(pathBash);

            // Canonical Git Bash install paths. Probed separately even
            // if PATH already resolves to one of them -- having the
            // explicit path in the banner is useful diagnostic info.
            string[] gitBashPaths = {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
            };
            foreach (var p in gitBashPaths)
            {
                if (!File.Exists(p)) continue;
                if (PathsMatch(p, pathBash?.Path)) continue; // already counted
                var hit = await ProbeAsync(p, InterpreterKind.Bash,
                    "Git Bash", workspaceBashInterpreter).ConfigureAwait(false);
                if (hit != null) results.Add(hit);
            }

            // MSYS2 + Cygwin -- less common, but worth detecting since
            // path semantics differ from Git Bash.
            string[] otherBashPaths = {
                @"C:\msys64\usr\bin\bash.exe",   // MSYS2
                @"C:\cygwin64\bin\bash.exe",     // Cygwin x64
                @"C:\cygwin\bin\bash.exe",       // Cygwin x86 legacy
            };
            foreach (var p in otherBashPaths)
            {
                if (!File.Exists(p)) continue;
                if (PathsMatch(p, pathBash?.Path)) continue;
                string label = p.IndexOf("msys", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "MSYS2" : "Cygwin";
                var hit = await ProbeAsync(p, InterpreterKind.Bash, label,
                    workspaceBashInterpreter).ConfigureAwait(false);
                if (hit != null) results.Add(hit);
            }

            // WSL distros (each is its own bash). wsl.exe is part of
            // Windows on supported builds; if it's absent the call
            // just returns no entries.
            results.AddRange(await ProbeWslAsync().ConfigureAwait(false));

            SaveCache();
            return results;
        }

        // ---- Specific probes -------------------------------------------------

        private static DetectedInterpreter DetectPowerShell()
        {
            // We host PowerShell in-process for the executor, so the
            // version is one reflection call away -- no subprocess.
            string version = "(unknown)";
            try
            {
                using (var ps = System.Management.Automation.PowerShell.Create())
                {
                    ps.AddScript("$PSVersionTable.PSVersion.ToString()");
                    var results = ps.Invoke();
                    if (results != null && results.Count > 0 && results[0] != null)
                        version = results[0].ToString();
                }
            }
            catch { /* keep "unknown" */ }
            return new DetectedInterpreter(
                kind:       InterpreterKind.PowerShell,
                version:    version,
                path:       "(built-in)",
                label:      "Windows PowerShell",
                isWorkspaceDefault: false);
        }

        // Generic --version probe. Returns null when the path / command
        // can't be invoked (interpreter missing). Uses the on-disk
        // cache keyed by (resolvedPath, mtime) so unchanged binaries
        // skip the spawn cost on subsequent app launches.
        private async Task<DetectedInterpreter> ProbeAsync(
            string fileName, InterpreterKind kind, string label,
            string workspaceDefaultPath)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            // Resolve to a real path so the cache key is stable. If the
            // caller passed bare 'python', try to resolve via PATH. If
            // resolution fails (interpreter missing), bail.
            string resolved = ResolveExe(fileName);
            if (string.IsNullOrEmpty(resolved)) return null;

            // Cache hit check.
            string cacheKey = resolved.ToLowerInvariant();
            DateTime mtimeUtc;
            try { mtimeUtc = File.GetLastWriteTimeUtc(resolved); }
            catch { mtimeUtc = DateTime.MinValue; }

            CacheEntry hit;
            lock (_gate)
            {
                if (_cache.TryGetValue(cacheKey, out hit)
                    && hit.MtimeUtc == mtimeUtc
                    && !string.IsNullOrEmpty(hit.Version))
                {
                    return new DetectedInterpreter(
                        kind, hit.Version, resolved, label,
                        IsDefaultForWorkspace(resolved, workspaceDefaultPath));
                }
            }

            // Cache miss -- spawn for --version.
            string version = await SpawnVersionAsync(resolved).ConfigureAwait(false);
            if (string.IsNullOrEmpty(version)) return null;

            lock (_gate)
            {
                _cache[cacheKey] = new CacheEntry
                {
                    MtimeUtc = mtimeUtc,
                    Version  = version,
                };
            }

            return new DetectedInterpreter(
                kind, version, resolved, label,
                IsDefaultForWorkspace(resolved, workspaceDefaultPath));
        }

        // wsl.exe --list --quiet enumerates installed distros; for each
        // we probe `wsl -d <distro> bash --version` to get the bash
        // version inside. Cached per-distro since wsl --list is fast
        // but the bash version probe isn't.
        private async Task<IList<DetectedInterpreter>> ProbeWslAsync()
        {
            var list = new List<DetectedInterpreter>();
            string wslExe = ResolveExe("wsl.exe") ?? ResolveExe("wsl");
            if (string.IsNullOrEmpty(wslExe)) return list;

            string distroList = await RunCaptureAsync(wslExe,
                "--list --quiet").ConfigureAwait(false);
            if (string.IsNullOrEmpty(distroList)) return list;

            // --list --quiet output on modern WSL is UTF-16 LE; we
            // detect a BOM-like pattern and re-decode. RunCaptureAsync
            // already handled the common case via the encoding override
            // in its psi setup.
            var distros = distroList
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("Windows", StringComparison.Ordinal))
                .ToList();

            foreach (var distro in distros)
            {
                // Cache key for WSL bash is "wsl:<distro>"; there's no
                // file mtime to invalidate against, so we use a stable
                // sentinel and accept that distro updates aren't
                // automatically picked up (the user can re-run Detect
                // Interpreters from the Tools menu to refresh).
                string cacheKey = "wsl:" + distro.ToLowerInvariant();
                lock (_gate)
                {
                    if (_cache.TryGetValue(cacheKey, out var hit)
                        && !string.IsNullOrEmpty(hit.Version))
                    {
                        list.Add(new DetectedInterpreter(
                            InterpreterKind.Bash, hit.Version,
                            "wsl.exe", "WSL: " + distro, false));
                        continue;
                    }
                }

                string ver = await RunCaptureAsync(wslExe,
                    "-d \"" + distro + "\" bash --version").ConfigureAwait(false);
                ver = FirstVersionLine(ver);
                if (string.IsNullOrEmpty(ver)) continue;

                lock (_gate)
                {
                    _cache[cacheKey] = new CacheEntry
                    {
                        MtimeUtc = DateTime.MinValue, // n/a for WSL
                        Version  = ver,
                    };
                }
                list.Add(new DetectedInterpreter(
                    InterpreterKind.Bash, ver, "wsl.exe",
                    "WSL: " + distro, false));
            }
            return list;
        }

        // ---- Subprocess helpers ---------------------------------------------

        private static async Task<string> SpawnVersionAsync(string filePath)
        {
            string raw = await RunCaptureAsync(filePath, "--version").ConfigureAwait(false);
            return FirstVersionLine(raw);
        }

        private static async Task<string> RunCaptureAsync(string fileName, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = fileName,
                    Arguments       = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    // Many --version implementations print to stderr
                    // (Python 2 historically, some bash builds), so we
                    // capture both and concat.
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    string stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                }
            }
            catch (Exception)
            {
                // Interpreter not found / launch failed. Caller treats
                // null as "not detected" and moves on.
                return null;
            }
        }

        // Extract a short, displayable version line from a --version
        // dump. Bash prints multi-line GNU banner; Python prints one
        // line; PowerShell would too. We take the FIRST non-empty
        // line and trim a common prefix ("Python ", "GNU bash, version ").
        private static string FirstVersionLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string first = raw
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim();
            if (string.IsNullOrEmpty(first)) return null;

            // Strip common boilerplate so the banner stays compact.
            string[] prefixes = {
                "Python ", "GNU bash, version ", "bash, version ",
            };
            foreach (var pfx in prefixes)
            {
                if (first.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                {
                    first = first.Substring(pfx.Length).Trim();
                    break;
                }
            }
            // Truncate trailing space-separated noise (e.g. bash version
            // strings end with build target tuples like
            // "(x86_64-pc-linux-gnu)" that we don't need).
            int paren = first.IndexOf(' ');
            if (paren > 0) first = first.Substring(0, paren);
            return first;
        }

        // ---- Path resolution -------------------------------------------------

        // Resolve a bare command to a full path via PATH lookup. A full
        // path is returned as-is when it exists. Null when the command
        // can't be found (so the caller can skip the probe).
        private static string ResolveExe(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
            if (Path.IsPathRooted(nameOrPath))
                return File.Exists(nameOrPath) ? nameOrPath : null;

            // Append .exe if missing and the name doesn't look like
            // it's already extension-suffixed.
            string withExt = nameOrPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? nameOrPath
                : nameOrPath + ".exe";
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), withExt);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* invalid PATH entry -- skip */ }
            }
            return null;
        }

        private static bool PathsMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a), Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool IsDefaultForWorkspace(string resolvedPath, string workspaceDefault)
        {
            return !string.IsNullOrWhiteSpace(workspaceDefault)
                && PathsMatch(resolvedPath, workspaceDefault);
        }

        // ---- Cache file ------------------------------------------------------

        private Dictionary<string, CacheEntry> LoadCache()
        {
            try
            {
                if (!File.Exists(_cachePath))
                    return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(_cachePath);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, CacheEntry>>(json);
                return loaded ?? new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveCache()
        {
            try
            {
                Dictionary<string, CacheEntry> snapshot;
                lock (_gate) { snapshot = new Dictionary<string, CacheEntry>(_cache); }
                var dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_cachePath,
                    JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            }
            catch
            {
                // Cache write failed (write-protected LocalAppData,
                // disk full). Detection still works; just no cache for
                // next run. Same best-effort posture as RecentWorkspaces.
            }
        }

        /// <summary>Force a fresh probe by dropping the in-memory cache.</summary>
        public void ClearCache()
        {
            lock (_gate)
            {
                _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
            try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { /* swallow */ }
        }

        // Cached per-path version info. Mtime invalidation means an
        // interpreter update is automatically re-probed on next launch.
        private sealed class CacheEntry
        {
            [JsonProperty("mtime")] public DateTime MtimeUtc { get; set; }
            [JsonProperty("version")] public string Version { get; set; }
        }
    }

    public enum InterpreterKind
    {
        PowerShell,
        Python,
        Bash,
    }

    /// <summary>
    /// One row in the welcome banner's "Detected interpreters" list.
    /// Immutable record-style POCO.
    /// </summary>
    public sealed class DetectedInterpreter
    {
        public DetectedInterpreter(
            InterpreterKind kind, string version, string path,
            string label, bool isWorkspaceDefault)
        {
            Kind    = kind;
            Version = version ?? string.Empty;
            Path    = path    ?? string.Empty;
            Label   = label   ?? string.Empty;
            IsWorkspaceDefault = isWorkspaceDefault;
        }

        public InterpreterKind Kind  { get; }
        public string Version        { get; }
        public string Path           { get; }
        public string Label          { get; }
        public bool   IsWorkspaceDefault { get; }
    }
}
