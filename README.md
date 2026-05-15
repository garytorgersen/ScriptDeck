# ScriptDeck

A WinForms button-driven script launcher for Windows. Define a JSON
*workspace* of tabs and buttons; ScriptDeck renders them and routes
each click to PowerShell, cmd, or any process executor. Output streams
to a console pane in real time; structured PowerShell objects fill a
results grid alongside.

Built on .NET Framework 4.8 (x64). No web server, no installer
service, no admin rights — it installs entirely into your user
profile.

---

## Download

Grab the latest release from the GitHub releases page:

**https://github.com/garytorgersen/ScriptDeck/releases/latest**

You'll get a single ZIP — `ScriptDeck-vN.N.N.zip` — containing the
app, an installer script, the documentation, and a sample workspace
to try.

## Install

1. **Unzip** the archive anywhere convenient (Desktop, Downloads,
   wherever).
2. **Double-click `Install.cmd`** inside the unzipped folder.
3. The installer first checks for .NET Framework 4.8. If your
   Windows install doesn't have it (rare on Windows 10 May 2019+
   or Windows 11), you'll be prompted to open Microsoft's download
   page. Install .NET 4.8 (~70 MB, one-time, ~2 minutes), then
   re-run `Install.cmd`.
4. ScriptDeck installs to `%LocalAppData%\Programs\ScriptDeck`
   (no admin / UAC required) and creates Desktop + Start Menu
   shortcuts.

To uninstall, run `Uninstall.cmd` from the same folder. User data
(run history, recent workspaces under `%LocalAppData%\ScriptDeck\`)
is preserved by default.

## Quick start

1. Launch **ScriptDeck** from the desktop shortcut.
2. **File → Open Workspace** and pick
   `%LocalAppData%\Programs\ScriptDeck\Workspaces\sample.json`.
3. The sample workspace shows a few buttons in a tab. Click one
   (try **IP Configuration**) — its PowerShell script runs and
   results appear in the console + grid below.

## Features

| | |
|---|---|
| **Executors** | PowerShell (long-lived runspace), cmd / batch, any launchable process |
| **Output routing** | Console RTB with severity-colored streams; structured PS objects auto-populate a side grid |
| **Format options** | Per-button RTB rendering: `default`, `list`, `table`, `json` |
| **Shared inputs** | Top-bar textboxes auto-injected as `$variables` into every PowerShell script |
| **Background jobs** | Long-running scripts run on a separate runspace; queued, monitored from a Jobs tab |
| **Search** | Live find-and-highlight across the console and the grid |
| **Run history** | Every dispatch recorded to SQLite; sortable history dialog |
| **Edit mode** | Drag, resize, snap-to-align buttons; right-click to add, group, match-size |
| **In-app script editor** | ScintillaNET with PowerShell syntax coloring, live parse validation, test runs |
| **Workspace JSON** | Plain hand-editable JSON; full schema reference in the technical manual |

## Documentation

- **[USER\_GUIDE.md](USER_GUIDE.md)** — friendly walkthrough for end users (also bundled as `.docx`).
- **[USER\_MANUAL.md](USER_MANUAL.md)** — technical reference: workspace JSON schema, executor contracts, variable injection rules, extending the bootstrap, troubleshooting (also bundled as `.docx`).

Both docs ship inside the install ZIP under `Documentation/` so you
have them offline.

## Requirements

- Windows 10 (May 2019 update / 1903) or later, or Windows 11
- .NET Framework 4.8 (built into supported Windows versions; installer
  checks and prompts if missing)
- PowerShell 5.1 (built in)
- 64-bit (x64) only

## Building from source

```powershell
# Clone
git clone https://github.com/garytorgersen/ScriptDeck.git
cd ScriptDeck

# Build the app
dotnet build src/ScriptDeck/ScriptDeck.csproj -c Release -p:Platform=x64

# Run the test suite (156 tests, ~3 seconds)
dotnet test src/ScriptDeck.Tests/ScriptDeck.Tests.csproj -p:Platform=x64

# Produce a redistributable installer ZIP under dist/
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/Build-Installer.ps1
```

The installer script reads the version from `ScriptDeck.csproj`,
builds Release/x64, stages everything (binaries + documentation +
`Install.cmd`), and emits
`dist/ScriptDeck-v<version>.zip` with a SHA-256 summary.

## Project layout

```
ScriptDeck/
├── src/
│   ├── ScriptDeck/              Production code (.NET 4.8 / x64)
│   │   ├── Forms/                  WinForms dialogs + Shell
│   │   ├── Hosting/                Executors, sinks, dispatcher, job queue
│   │   ├── History/                SQLite-backed run history
│   │   ├── Workspace/              JSON schema + loader
│   │   ├── Workspaces/             Sample workspace + scripts
│   │   └── ScriptDeck.Bootstrap.ps1  Auto-loaded helpers (Test-IsLocalTarget, Write-Rtb, Write-Grid)
│   └── ScriptDeck.Tests/        xUnit test suite (156 tests)
├── tools/
│   ├── Build-Installer.ps1      Cuts a redistributable zip
│   └── installer-templates/     Install.cmd / Uninstall.cmd / README.txt
├── docs/images/                  Screenshot placeholders (see docs/images/README.md)
├── USER_GUIDE.md  +  .docx
├── USER_MANUAL.md +  .docx
├── README.md     (this file)
└── LICENSE
```

## License

MIT — see [LICENSE](LICENSE). Use it however you like; please don't
strip the copyright notice from redistributed copies.

## Acknowledgements

ScriptDeck was inspired by an in-house PowerShell-form workflow tool
the author maintained for years. The visual layout (top-bar shared
inputs, tabbed buttons, console + grid output) borrows directly from
that origin. The implementation is a clean-room rewrite on top of
WinForms + the modern PowerShell hosting API.

Third-party dependencies:

- [Newtonsoft.Json](https://www.newtonsoft.com/json) — workspace JSON serialization
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) — run history store
- [ScintillaNET](https://github.com/jacobslusser/ScintillaNET) — in-app script editor
- [xUnit.net](https://xunit.net/) — test framework
