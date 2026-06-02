# ScriptDeck User Manual

ScriptDeck is a Windows Forms launcher for personal and team automation. You
load a *workspace* — a JSON file that defines tabs, buttons, top-level menus,
and shared input fields — and ScriptDeck renders them. Clicking a button runs
the script or executable behind it; output streams into a console pane, and
structured PowerShell objects fill a results grid.

The app is built on .NET Framework 4.8 (x64) and ships as a single executable
plus a few satellite DLLs. State (run history, recent workspaces) lives under
`%LocalAppData%\ScriptDeck\`.

---

## 1. Layout

![Main window with a workspace loaded — menu strip at top, shared-input band in green-on-black, the active tab showing labeled groups of buttons, the search/view toolbar, console RTB on the left + results grid on the right, logs RTB below, status bar at the bottom](docs/images/01-main-window.png)

```
+------------------------------------------------------------+
|  File   Edit   <workspace menus>   Tools                   |  <-- MenuStrip
+------------------------------------------------------------+
|  [ Computer Name: MYBOX ]  [ <other shared inputs> ]       |  <-- shared inputs (black band)
+------------------------------------------------------------+
|                                                            |
|   Diagnostics                Tools                         |  <-- TabControl with workspace tabs
|   +----------------+    +----------------+                 |
|   | [Ping]  [...]  |    | [Notepad]      |                 |
|   +----------------+    +----------------+                 |
|                                                            |
+------------------------------------------------------------+
|  Find: [_______]  [Find]  [Clear]    [x] Console [x] Grid  |  <-- toolbar (search + view toggles)
+------------------------------------------------------------+
|  [ Console (RTB) ]                |  [ Results grid ]      |  <-- output split
+------------------------------------------------------------+
|  [ Logs RTB ]                                              |  <-- below output
+------------------------------------------------------------+
|  workspace name -- C:\path\workspace.json   |  Run mode    |  <-- StatusStrip
+------------------------------------------------------------+
```

- **Shared inputs** (top black band): one text field per workspace input
  (e.g. `computerName`). Values are auto-injected as variables in scripts
  (`$companyName` in PowerShell, `%companyName%` in cmd) and also
  substituted into button arguments via `{{token}}` placeholders.
- **Tabs**: each shows buttons (and optionally labeled "label boxes" that
  visually group related buttons). Tabs are configured per workspace and
  can be re-ordered by dragging their headers in edit mode.
- **Toolbar**: a thin band between the tabs and the output area with a
  Find box (highlights matches in the console + grid) and two view
  toggles that collapse the console or the grid so the other expands
  to fill. At least one panel must stay visible.
- **Console RTB**: free-text output and informational messages.
- **Results grid**: shows structured PowerShell objects as columns/rows when
  the running button declares `"grid"` in its `outputs`.
- **Logs RTB**: lower pane that records every dispatch (start/end time,
  button label, exit code) for the current session.
- **Status bar**: shows the loaded workspace path, plus the current mode
  ("Run", "EDIT", or `Running: <label>` while a script is mid-flight).
  The window title shows an asterisk (`*`) when the in-memory workspace
  has unsaved changes.

---

## 2. Opening a workspace

- **File -> Open Workspace** (`Ctrl+O`) — pick a `.json` file.
- **File -> New Workspace** (`Ctrl+N`) — create an empty one with a single
  tab. Saves immediately so subsequent edits flow into a real file on disk.
- **File -> Recent Workspaces** — last 8 paths you've loaded successfully.
  `&1`..`&9` keyboard accelerators jump straight to the top entries; full
  paths show as tooltips. *Clear Recent* wipes the list.
- **File -> Save Workspace** (`Ctrl+S`) — persist the in-memory model
  (including any edit-mode changes) back to disk.

### The trust prompt

Opening a workspace pops a confirmation dialog:

> This workspace's buttons run scripts and executables on your machine.

Workspaces are JSON, but the JSON references executable scripts — opening
one is morally equivalent to running them. Only open workspaces from sources
you trust. The prompt is skipped on freshly-created workspaces (you just
made it).

The MRU list is updated only after a workspace renders successfully, so a
file that errored out won't be promoted to "recent".

---

## 3. Running buttons

Click a button to dispatch it. While a script is running:

- the status bar reads `Running: <button label>  (Esc to cancel)`,
- the console shows live output,
- other buttons still render but a second click is rejected ("Already
  running '<name>'. Cancel first or wait."),
- **Edit -> Cancel Running** or pressing `Esc` cancels the active run.

If a button has `"confirm": true` in the workspace JSON, a Yes/No dialog
appears first; useful for destructive actions (Stop service, Restart,
Disable RDP, Bulk Uninstall, etc.).

![Toolbar close-up: Find textbox with the term highlighted in the console below, both the console RTB and the data grid showing yellow-highlighted matches, the Show Console / Show Grid checkboxes both checked on the right](docs/images/03-toolbar-search.png)

### Toolbar (between the tabs and output)

A thin band sits between the tab strip and the console/grid pair:

- **Find** -- type a term + click **Find** (or press Enter) to highlight
  every match in the console RTB and the results grid in yellow. Press
  **Esc** in the Find box (or click **Clear**) to remove highlights.
  Simple substring match, case-insensitive.
- **Show Console** / **Show Grid** -- two checkboxes. Uncheck either to
  collapse that panel; the other expands to fill the whole output area.
  At least one must stay checked (un-checking the only-checked one
  silently re-checks it).

### Output streams

Each button declares an `outputs` list:

| Value  | Meaning                                                      |
|--------|--------------------------------------------------------------|
| `rtb`  | Plain-text output flows into the console RTB.                |
| `grid` | PowerShell objects (anything that isn't a primitive string/number) populate the results grid. Property names become columns. |

Specifying both is common — text + structured side-by-side. Omitting the
list defaults to `rtb`. Information-stream messages (`Write-Information ...
-InformationAction Continue`) render in cyan to distinguish them from
regular output.

### RTB format (PowerShell only)

![Four side-by-side captures of the same Get-Service output rendered with rtbFormat=default, list, table, json — same script, same data, four very different reading experiences](docs/images/03-rtb-formats.png)


PowerShell scripts emit structured records (PSObjects) into the pipeline.
By default ScriptDeck renders each record into the console with
`obj.ToString()`, which for `[PSCustomObject]` produces the terse
`@{Name=Spooler; Status=Running}` shape. The button's optional
**RTB format** field changes that:

| Value         | What the console shows                                                    |
|---------------|---------------------------------------------------------------------------|
| `default`     | `obj.ToString()` per record. Streams as records arrive.                   |
| `list`        | `Property : Value` per line, blank line between records (PowerShell `Format-List` style). Streams. |
| `table`       | Aligned text columns with a header underline. Buffered until the script finishes (column widths need the full result). |
| `json`        | Pretty-printed JSON array. Buffered.                                      |

Set it from **Edit Button -> RTB format** in edit mode, or hand-edit the
button's `rtbFormat` field in the workspace JSON. Buffered formats
(`table` / `json`) cap at 2,000 records to keep the console usable —
above that you'll see a `(rtb output truncated to first 2000 record(s);
use the grid for the full set)` warning.

The **grid** output is unaffected — it always receives the structured
form regardless of `rtbFormat`. Primitives (strings, numbers from
`Write-Output`) stream inline to the RTB even in buffered formats, so
the script's textual progress messages appear in order around the
buffered table/json block.

### Per-object output routing (`Write-Rtb` / `Write-Grid`)

By default, every object a script emits goes to BOTH the console RTB
(formatted by `rtbFormat`) and the grid (when `outputs` includes
`grid`). That's the right default — you pick the view per-button, not
per-object.

When you want different *shapes* in the two views — e.g. a compact
summary in the RTB, the full property surface in the grid — pipe each
shape through the matching helper. The helpers come from the bootstrap
module that's auto-loaded into every PowerShell runspace, so you can
call them anywhere with no `Import-Module`.

```powershell
$processes = Get-Process

# Compact in RTB (Process objects ToString to "System.Diagnostics.Process (notepad)"
# under default rtbFormat, or render with just Name/Id/etc. under list/table/json)
$processes | Write-Rtb

# Full property surface in the grid -- every column Process exposes
$processes | Select-Object -Property * | Write-Grid
```

What the helpers do, mechanically:

- Wrap each input object in a PSObject and stamp an internal
  `__ScriptDeckTarget` NoteProperty (`rtb` or `grid`).
- Emit the wrapped object back into the pipeline.

The executor reads the tag in its output handler and skips the
unwanted destination. Untagged emissions (bare statements,
`Write-Output`) keep the both-destinations default. The internal tag
property is stripped before any grid column or RTB rendering — it
never appears in your output.

Use cases:

- **Different shape per panel** — the canonical case. Compact RTB +
  detailed grid (or the inverse).
- **Suppress the grid for a specific record** — pipe text-only progress
  notes through `Write-Rtb` so they don't end up as grid rows when
  `outputs` includes `"grid"`.
- **Suppress the RTB for a specific record** — pipe a heavy structured
  object through `Write-Grid` so the RTB stays focused on summary
  lines.

Variable names are irrelevant — the helpers route by what's in the
pipeline at the moment they're called, not by name. Pipe whatever you
have, under whatever name you like:

```powershell
$myData      | Write-Rtb     # variable name irrelevant
Get-Service  | Write-Grid    # cmdlet output, no variable at all
$obj.Field   | Write-Rtb     # any expression
```

### Background jobs (long-running scripts)

![Jobs tab with three rows: one Running, one Queued, one Done. The Done row is selected; below the grid the buffered output for that job is shown in a black RTB with colored info/warning lines](docs/images/03-jobs-tab.png)

Setting `runInBackground: true` on a button (or checking **Run in
background** in the Edit Button dialog) sends that script through a
separate execution path:

- **Foreground gate** is bypassed. You can keep clicking other buttons
  while the background job runs. The foreground console + grid keep
  responding to foreground clicks normally.
- **At most two scripts run simultaneously** — one foreground, one
  background. Further background submissions queue (FIFO) and run as
  soon as the previous one finishes.
- **Output flows to the Jobs tab**, not the main console / grid.
  Each job has its own buffered output; the Jobs tab's RTB shows the
  output of whichever job you've selected, live-updating while it runs.

The Jobs tab (in the output area, alongside the Output tab) has:

- **Job list** (top) — Status, Button, Started, Elapsed, Exit, Error.
  Rows update live; the tab header shows counts:
  `Jobs (1 running, 2 queued, 4 total)`.
- **Output pane** (bottom) — buffered output for the selected job,
  colored the same way the main console is (cyan info, gold warnings,
  salmon errors, etc.).
- **Cancel Job** — cancels the selected job. If it's queued, it stays
  in the list as `Cancelled` (so you have an audit trail). If it's
  running, the underlying executor is signalled (PowerShell pipeline
  Stop, process Kill).
- **Send to Console** — replays the selected job's full buffered
  output onto the main console + grid. Useful when a background job
  produces results you want to keep around alongside the foreground
  panel.
- **Dismiss** — removes a finished job from the list. Refuses to
  dismiss queued / running jobs (cancel first).

### How foreground and background interact

| Scenario | What happens |
|---|---|
| Click foreground button A. Then click foreground button B. | B is rejected (`Already running 'A'`). Same as before. |
| Click foreground button A. Then click background button B. | B enqueues to the Jobs tab and starts immediately (foreground gate doesn't apply to background). Both run in parallel. |
| Click background button A. Then click background button B. | A starts immediately. B queues. B starts when A finishes. |
| Click background button A. Then click foreground button B. | A keeps running in background. B runs in foreground. Both progress in parallel. |

PowerShell-specific note: background jobs use a **second runspace**
dedicated to the background path, isolated from the foreground
runspace. Variables and modules don't leak between them; each runspace
dot-sources `ScriptDeck.Bootstrap.ps1` independently at startup.

If you later need true parallel background jobs (more than one at a
time), the queue's worker count is a single SemaphoreSlim swap from
`1` to `N` — no other surgery. We've left it at 1 as the explicit
"two scripts max simultaneous" the design called for.

### Shared inputs as variables

Every shared input becomes a *variable* inside the script that runs.
Define a shared input with `"id": "computerName"` and a PowerShell
script can read `$computerName` directly:

```powershell
# No param block needed -- the value is just there.
Get-Service -ComputerName $ComputerName | Where Status -eq 'Running'
```

Cmd / batch scripts read the same value as an environment variable:

```cmd
@echo Querying %computerName%...
sc.exe \\%computerName% query state= all
```

The Process executor doesn't get env-var injection (Windows `ShellExecute`
doesn't allow it for child processes); for those buttons, fall back to
`{{token}}` substitution in `args`.

ScriptDeck takes a snapshot of the current textbox values at the moment
you click — change the value, click again, and the second invocation
sees the new value.

### The `computerName` convention

A shared input flagged with `"normalize": "computerName"` gets special
handling so scripts can be location-agnostic:

- The textbox is **pre-filled with the local machine name** on workspace
  load (so it visibly reads `MYBOX` instead of `.`).
- Empty / `.` / `localhost` get **resolved to the local machine name**
  at click time before the variable is published to the runspace, so
  scripts always see a real hostname (never `.` or an empty string).
- That means **any value that doesn't equal the local machine name is
  treated as remote** — by your scripts, not by ScriptDeck. The app
  doesn't decide local vs. remote; it just ensures the value is sane.

The bootstrap script (auto-loaded into the runspace at startup) ships a
helper that gives you the standard local-or-remote branch in one line:

```powershell
if (Test-IsLocalTarget) {
    # Local code path -- direct calls, no -ComputerName.
    Get-Service | Where Status -eq 'Running'
} else {
    # Remote code path -- Invoke-Command, or whatever fits.
    Invoke-Command -ComputerName $ComputerName -ScriptBlock {
        Get-Service | Where Status -eq 'Running'
    }
}
```

`Test-IsLocalTarget` defaults its parameter to the runspace-injected
`$ComputerName`, so you can call it bare. Pass any string explicitly to
test a different value.

Note: many cmdlets handle local-as-loopback transparently when given
`-ComputerName` (Get-CimInstance, Get-WmiObject, Test-Connection in 5.1
and later). For those you don't need the if/else — just write
`-ComputerName $ComputerName` once and PowerShell routes the call
correctly whether the target is local or remote. The if/else pattern
above is for cmdlets that *don't* take `-ComputerName` (Get-NetIPAddress,
Get-LocalUser, anything domain-specific).

### Token substitution (legacy / supplemental)

The old `{{tokenName}}` syntax still works in `args`, `workingDirectory`,
and button labels. It's most useful when you want a value embedded
*inside* a string literal:

```json
{ "label": "Ping ({{computerName}})", "args": ["-Path", "C:\\{{companyName}}\\logs"] }
```

For the common case of "just pass the computer name to the script,"
prefer the variable injection (no `args` plumbing needed). For embedding
a value into a label or a partial path, prefer tokens. Both flow from
the same shared-input snapshot, so they always agree.

If a token can't be resolved (no matching shared input id), the dispatch
is aborted with a red error in the console — the script is *not* invoked
with a literal `{{computerName}}`.

### Reserved variable names

PowerShell reserves a number of variable names for engine state and
preferences (`$Host`, `$Error`, `$args`, `$_`, `$PSItem`,
`$ErrorActionPreference`, etc.). Naming a shared input the same as one
of those would clobber the engine state, so ScriptDeck refuses to inject
under those names. You'll see a one-time warning in the console:

> Shared input 'Host' collides with a reserved PowerShell name; skipping injection.

Pick a different `id` — the textbox still works, `{{token}}` still
works, only the runspace variable is suppressed.

Shared input ids must also be valid PowerShell identifiers
(`[A-Za-z_][A-Za-z0-9_]*`). Hyphens, dots, and spaces are rejected with
a similar warning.

### Variable persistence between button clicks

ScriptDeck holds a long-lived PowerShell runspace open from app start
to shutdown (actually two -- one for foreground, one for background;
they're isolated from each other). What persists across clicks within
a single runspace is governed by PowerShell's scoping rules.

**Persists across clicks:**

| Item                              | Why                                                              |
|-----------------------------------|------------------------------------------------------------------|
| Shared input variables            | ScriptDeck re-injects them at runspace global scope on every click. Whatever value the textbox holds at click time wins; a script that does `$computerName = "override"` shadows the global at script scope only -- next click starts fresh. |
| Bootstrap-defined functions       | `ScriptDeck.Bootstrap.ps1` is dot-sourced into the runspace once at open. Functions live at global scope until shutdown. |
| Imported modules                  | `Import-Module Foo` modifies the runspace's session state. Worth keeping -- some modules cost seconds to load. |
| Engine preferences (`$ErrorActionPreference`, etc.) | These are runspace-global. A script that overwrites one affects subsequent runs unless reset. Generally a bug if it happens accidentally. |

**Does NOT persist (PowerShell-default):**

| Item                              | Why                                                              |
|-----------------------------------|------------------------------------------------------------------|
| `$x = ...` inside a script        | Script-scope local. Disappears when the script exits.            |
| `function Foo { ... }` inside a script | Script-scope. Disappears with the script.                   |

**Persists only because the script asked it to:**

```powershell
$global:foo = "bar"               # explicit global -- WILL leak across clicks
Set-Variable -Scope Global -Name foo -Value "bar"
function global:Get-MyHelper { }  # explicit global function
```

Treat `$global:` (and `$script:` outside a `.ps1`, which targets the
runspace's session state) as the explicit opt-in for cross-click
persistence. Used deliberately, it's useful for caches, login tokens,
expensive-to-recompute lookup tables. Used accidentally, it's the
classic "Button B works only after I click Button A first" footgun.

For helpers you want every script to see, prefer adding them to
`ScriptDeck.Bootstrap.ps1` rather than relying on a `$global:` set
elsewhere. The bootstrap is the documented home for shared functions
and survives independently of any button click order.

**Foreground vs background runspaces are isolated.** A `$global:` set
in a foreground click is NOT visible to background jobs, and vice
versa. Each path runs in its own runspace, dot-sources the bootstrap
independently, and sees its own injected shared inputs. If you need
a value visible to both paths, the right move is a shared input (which
both runspaces see) or a small file you read/write.

### Extending the bootstrap (`ScriptDeck.Bootstrap.ps1`)

The bootstrap is the supported way to ship custom helpers, constants,
or module imports that should be available to every script in
ScriptDeck. It's a regular `.ps1` file -- nothing magic about its
internals -- but the loading semantics are specific to ScriptDeck and
worth understanding.

**File location**

```
<install dir>\ScriptDeck.Bootstrap.ps1
```

(Same folder as `ScriptDeck.exe`. The repo's source copy is
`src/ScriptDeck/ScriptDeck.Bootstrap.ps1`; msbuild copies it to the
build output as `Content` with `CopyToOutputDirectory=PreserveNewest`,
so a `dotnet build` will overwrite a deployed file. Edit the source
copy when working in a checked-out tree; edit the deployed file
otherwise.)

**Loading semantics**

- ScriptDeck dot-sources the bootstrap **once per runspace** when the
  runspace opens (which is at app startup).
- There are **two runspaces** -- one for foreground clicks, one for
  background jobs -- and the bootstrap is loaded into BOTH,
  independently. Helpers defined here are visible to both paths.
- **Edits don't take effect until ScriptDeck restarts.** There's no
  hot-reload because re-dot-sourcing mid-session would surprise any
  script currently mid-flight that expected the previous state.
- If `ScriptDeck.Bootstrap.ps1` is missing, ScriptDeck silently
  proceeds with no helpers loaded. Scripts that reference
  `Test-IsLocalTarget` etc. will then fail with "command not
  recognized" -- the safety net is "ScriptDeck doesn't crash on a
  missing bootstrap," not "every helper is guaranteed available."

**Adding a function**

1. Open `ScriptDeck.Bootstrap.ps1` in any editor.
2. Define your helper at top level (not inside another function):
   ```powershell
   function Test-PortOpen {
       [CmdletBinding()]
       param(
           [Parameter(Mandatory=$true)][string]$ComputerName,
           [Parameter(Mandatory=$true)][int]$Port,
           [int]$TimeoutMs = 1000
       )
       $client = [System.Net.Sockets.TcpClient]::new()
       try {
           $task = $client.ConnectAsync($ComputerName, $Port)
           if (-not $task.Wait($TimeoutMs)) { return $false }
           return $client.Connected
       } catch { return $false }
       finally { $client.Dispose() }
   }
   ```
3. Save.
4. Restart ScriptDeck.
5. Any button script can now call the helper as if it were built in.

**Conventions**

- **Verb-Noun naming.** Helpers read naturally alongside built-in
  cmdlets in scripts. `Test-PortOpen`, `Get-FreeDisk`, `New-AuditFile`.
- **`[CmdletBinding()]`** on every helper. Gives you `-Verbose`,
  `-Debug`, `-ErrorAction`, `-WarningAction` for free, plus the
  ability to use `$PSCmdlet.WriteVerbose(...)` etc.
- **`param()` block with named parameters.** Avoid positional-only
  helpers; named args are self-documenting at the call site.
- **Pure functions.** A bootstrap helper that only reads inputs and
  emits outputs is far easier to reason about than one that mutates
  global state. Reserve `$global:` for genuine cross-call state
  (caches, login tokens) and prefer module-scope `$script:` over
  global when you can keep state encapsulated.

**Pitfalls**

| Don't | Why |
|---|---|
| Run heavy work at top level (`Import-Module Az`, `Get-ADUser ...`) | Blocks runspace open; happens on every app launch. Defer into the helpers themselves. |
| Mutate engine preferences (`$ErrorActionPreference = 'Stop'`, `Set-StrictMode -Version Latest`) at top level | Leaks to every script the user runs -- you've redefined the engine for the whole app. Set those inside individual functions where they apply. |
| Emit output at top level (bare statements, `Write-Output "loaded"`) | The output ends up in the FIRST script's pipeline, looking like that script produced it. The bootstrap should silently load. |
| Override built-in cmdlets (`function Get-Service { ... }`) | Shadows the built-in for every script in the app. Almost never what you intend. |
| Dot-source other files at top level | Each `. .\helpers.ps1` runs every time the runspace opens. If you must split helpers across files, accept the per-startup cost or fold them back into one file. |
| Store secrets (passwords / API keys / tokens) | Plaintext on disk. Use `Microsoft.PowerShell.SecretManagement` or Windows Credential Manager. |

**Distributing across machines**

ScriptDeck has no separate "import" or "load" step beyond looking for
the file in the install directory. To share a custom bootstrap across
machines, just copy `ScriptDeck.Bootstrap.ps1` alongside
`ScriptDeck.exe` on each target.

If you maintain helpers as a team, treat the bootstrap like any other
source file: keep it in version control, code-review changes, and
deploy by copying to the install directory. There's no
ScriptDeck-specific tooling required.

**Auditing what's loaded**

To see what's defined after startup, type into a button script:

```powershell
Get-Command -CommandType Function | Where-Object {
    $_.ScriptBlock.File -like '*ScriptDeck.Bootstrap.ps1*'
} | Select-Object Name, Module
```

That lists every function the bootstrap added to the runspace.

---

## 4. Edit mode

![Edit mode active: a tab with several buttons each carrying a red outline and an orange-red triangle in the bottom-right corner; one button is mid-drag and a thin alignment is visible against a sibling; status bar reads "EDIT mode" in orange; title bar shows the workspace name with a trailing asterisk indicating unsaved changes](docs/images/04-edit-mode.png)

Toggle with **Edit -> Toggle Edit Mode** (`Ctrl+E`). Status bar turns
orange and reads `EDIT mode`. Buttons grow a red outline so you can tell
them apart from runnable buttons at a glance.

In edit mode:

- **Left-click a button** opens the edit dialog (instead of running it).
- **Click-drag a button or label box** moves it. A 3-pixel jitter
  threshold prevents tiny accidental drags.
- **Click-drag the bottom-right corner** resizes it. The grip is a
  20x20 hit zone; an orange-red triangle is painted there so you can
  see exactly where to grab.
- **Drop a button inside a label box** re-parents it into that group, so
  moving the group later carries the button along.
- **Drop a button outside any group** parents it back to the tab canvas.
- **Right-click** for context menus:
  - On a button: Edit / Delete / Bring Forward / Send Backward /
    Match Width of / Match Height of / Match Size of.
  - On a label box: Add Button / Rename / Delete (members are kept
    on the canvas).
  - On empty tab space: Add Button / Add Label Box / Rename Tab /
    Delete Tab / Add New Tab.
- **Drag a tab header** to reorder tabs in the strip.

The "Add Button" command auto-positions new buttons in a 4-column grid so
they don't pile up on top of each other.

`Bring Forward` / `Send Backward` adjust z-order — useful when buttons
overlap. List order in the JSON is render order is z-order.

### Snap-to-align (live, while dragging)

Drag-move and drag-resize both snap to nearby siblings within a 4-pixel
tolerance, so it's easy to keep buttons tidy without precise mouse work:

- **Move snap** — your dragged button's left/right edges snap to other
  buttons' left/right edges; top/bottom edges snap similarly. Each axis
  is independent, so a button can snap its X to one neighbor and its Y
  to another simultaneously.
- **Resize snap** — your dragged dimension snaps to any sibling's
  matching dimension (so resizing toward a 170px-wide neighbor will
  catch at exactly 170px). Right and bottom edges also snap to other
  buttons' right/bottom edges, useful for "make these end at the same
  column."
- **Sibling = same parent**. Buttons inside a label box only snap to
  other members of that box and to the box's interior, not to canvas
  buttons outside it. Keeps grouped layouts stable.
- **Hold Shift while dragging to bypass snap** for pixel-precise
  placement — same convention as VS, Sketch, and Figma.

![Right-click context menu open on a button in edit mode showing Edit / Delete / Bring Forward / Send Backward / Match Width of (submenu open with three sibling buttons listed as "Label  (170 x 32)") / Match Height of / Match Size of](docs/images/04-match-size-menu.png)

### Match Size of... (right-click command)

The button right-click menu has three "Match X of" submenus that
populate from every other button in the workspace (across all tabs):

- **Match Width of** -- lists each candidate as `Label  (170 x 32)` so
  you can pick the canonical size at a glance.
- **Match Height of** -- same, height-only.
- **Match Size of** -- copies both width and height.

Useful for retroactive tidying when you have several buttons that grew
slightly different sizes during free-drag and you want them all to
match the one you've decided is canonical.

### Saving edit-mode changes

When the in-memory model differs from the file on disk, the title bar
shows an asterisk: `ScriptDeck — My Workspace *`. Hit `Ctrl+S` to save.
If you try to close the window with unsaved changes, ScriptDeck prompts
**Save / Don't Save / Cancel**.

![Unsaved-changes dialog with the message "Save changes to 'My Workspace' before closing?" and three buttons: Yes / No / Cancel](docs/images/04-save-prompt.png)

Switching back to Run mode (`Ctrl+E`) does NOT save — the dirty marker
follows the model, not the mode.

### Edit -> Edit Shared Inputs

Pop-up dialog to add, rename, or remove the shared-input fields in the
black top band. The `id` is what scripts will see as a variable name
(`$companyName`, `%companyName%`) so pick one that reads well in script
code; renaming an `id` later means updating any scripts that referenced
the old name and any `{{...}}` tokens.

### Edit -> Edit Workspace Menus

Pop-up dialog to manage the top-level menus the workspace contributes
(between Edit and Tools). Tree view: menus are root nodes, items are
children. Menu items reuse the same execution path as tab buttons —
confirm dialogs, cancel via Esc, history recording, etc. all work
identically.

---

## 5. Workspace JSON format

Workspaces are case-insensitive on read, written PascalCase on save. Path
fields can be absolute or relative; relative paths resolve against
`scriptsRoot` (defaults to the workspace JSON's own directory).

```json
{
  "version": 1,
  "name": "Sample Workspace",
  "scriptsRoot": "scripts",            // optional; default = JSON dir
  "sharedInputs": [
    {
      "id": "computerName",
      "label": "Computer Name",
      "type": "text",
      "default": ".",
      "normalize": "computerName"
    }
  ],
  "menus": [
    {
      "title": "&Quick",
      "items": [
        { "id": "menu-ping", "label": "Ping",
          "executor": "powershell",
          "scriptPath": "scripts\\Test-Ping.ps1",
          "args": [ "-ComputerName", "{{computerName}}" ],
          "outputs": [ "rtb" ], "log": true },
        { "label": "-", "executor": "powershell" }   // separator
      ]
    }
  ],
  "tabs": [
    {
      "id": "diagnostics",
      "title": "Diagnostics",
      "groups": [
        { "id": "grp-net", "title": "Network",
          "x": 16, "y": 16, "width": 380, "height": 200 }
      ],
      "buttons": [
        {
          "id": "btn-ping",
          "label": "Ping",
          "executor": "powershell",
          "scriptPath": "scripts\\Test-Ping.ps1",
          "args": [ "-ComputerName", "{{computerName}}" ],
          "outputs": [ "rtb" ],
          "log": true,
          "confirm": false,
          "groupId": "grp-net",
          "x": 14, "y": 28, "width": 170, "height": 32,
          "workingDirectory": null
        }
      ]
    }
  ]
}
```

### Field reference

**SharedInput**

| Field   | Notes |
|---------|-------|
| `id`    | unique within the workspace. Becomes the variable name scripts see (`$id` in PowerShell, `%id%` in cmd). Must be a valid PowerShell identifier (`[A-Za-z_][A-Za-z0-9_]*`) and not collide with reserved names like `Host`, `Error`, `args`. Also referenced by `{{id}}` tokens. |
| `label` | shown next to the textbox |
| `type`  | currently `text` only (more types planned) |
| `default` | initial value. With `normalize: "computerName"`, an empty / `.` / `localhost` default is auto-resolved to the local machine name on workspace load — keeps the JSON portable across machines. |
| `normalize` | optional rule that adjusts the value before injection. `"computerName"` is the only value defined: empty / `.` / `localhost` become `$env:COMPUTERNAME`, both at load time (textbox pre-fill) and at click time (variable injection). Omit for inputs that should pass through verbatim. |

**Button**

| Field           | Notes |
|-----------------|-------|
| `id`            | optional but recommended; used in run history |
| `label`         | text on the button (also used for the run-history row) |
| `executor`      | `powershell`, `cmd`, or `process` |
| `scriptPath`    | absolute, or relative to `scriptsRoot` |
| `args`          | array of strings; `{{tokens}}` substituted at click time |
| `outputs`       | array of `"rtb"` and/or `"grid"`; default `["rtb"]` |
| `log`           | append a one-line entry to the Logs RTB on completion |
| `confirm`       | show a Yes/No dialog before dispatch |
| `groupId`       | optional; reference a `groups[].id` to live inside that label box |
| `x`, `y`        | position (canvas-relative if no `groupId`, group-relative otherwise) |
| `width`, `height` | 0 or omitted = defaults (150x36) |
| `workingDirectory` | optional; relative paths resolve against `scriptsRoot` |
| `rtbFormat`        | PowerShell-only. How structured records render in the console RTB. One of `default`, `list`, `table`, `json`, or `raw` (script controls formatting; auto-grid disabled but `Write-Grid` still works -- see §12.6). Omit / `null` = `default`. See `Running buttons -> RTB format`. |
| `runInBackground`  | bool. When true, clicking the button enqueues onto the background job queue (one worker, FIFO) instead of running on the foreground single-flight gate. Output appears in the Jobs tab. See §6 below. |

**ButtonGroup** (a "label box")

| Field   | Notes |
|---------|-------|
| `id`    | referenced by `button.groupId` |
| `title` | rendered as the GroupBox header |
| `x`, `y`, `width`, `height` | bounds on the tab canvas |

**Tab**

| Field     | Notes |
|-----------|-------|
| `id`      | optional |
| `title`   | rendered as the tab header |
| `buttons` | the button list (order = z-order) |
| `groups`  | the label-box list |

**MenuDefinition**

| Field   | Notes |
|---------|-------|
| `title` | top-level menu text. `&` marks the keyboard accelerator. |
| `items` | array of *Button*-shaped entries (separators use `"label": "-"`) |

### Executors

| Executor   | What it does |
|------------|--------------|
| `powershell` | runs the script in a long-lived PowerShell runspace owned by the app. Streams Information/Verbose/Warning/Error to the console; any object output flows to the grid (when `"grid"` is in `outputs`). |
| `cmd`        | runs the script via `cmd.exe /c`. Stdout / stderr stream to the console. |
| `process`    | launches the executable directly (no shell). Use for `notepad.exe`, `taskmgr.exe`, etc. — the user takes over from there. |

The PowerShell runspace stays open between runs, so module imports and
variable state persist across button clicks within a session. Closing
ScriptDeck disposes it.

---

## 6. Run history

Every successful dispatch is recorded to `%LocalAppData%\ScriptDeck\history.db`
(SQLite). The store captures: timestamp, workspace name and path, button id
and label, executor, script path, resolved args, exit code, duration, and a
truncated stdout/stderr preview.

**Tools -> Recent Runs** (`Ctrl+H`) opens a grid you can sort and filter.
Selecting a row populates a details panel with the full args list and any
captured output preview.

![Recent Runs dialog: top half is a sortable grid with columns for Time, Workspace, Button, Executor, Exit code, Duration, Status; bottom half is a details panel showing the selected row's full script path, args, and captured output preview](docs/images/06-history-dialog.png)

If history initialization fails (locked file, bad permissions), the store
silently no-ops — you'll see a one-line warning at startup but nothing
blocks the rest of the app.

---

## 7. Script Editor

![Script Editor dialog: path bar at top with a script path filled in; ScintillaNET editor in the middle with line numbers and PowerShell syntax coloring; "Test inputs" grid below showing computerName=MYBOX with one editable row; output pane at the bottom showing structured Get-Service results from a recent test run; action buttons across the bottom (Run Test, Cancel, Insert Template, Format dropdown, Save, Save As, Close); status strip showing "Syntax: OK" on the left and "Idle" on the right](docs/images/07-script-editor.png)

**Tools -> Script Editor** (`F7`) opens a modal editor for `.ps1` files.
Two entry points use the same dialog:

- standalone, from the Tools menu — opens an empty editor; **Save** /
  **Save As** writes the file wherever you point it,
- from the Edit Button dialog's **Edit Script...** button — opens on the
  current Script Path (or empty for new). On Save, the path flows back
  into the button dialog.

### Layout

```
+----------------------------------------------------+
|  Path:  [ scripts\foo.ps1                  ] [...] |
+----------------------------------------------------+
|  1 |  if (Test-IsLocalTarget) {                    |
|  2 |      Get-CimInstance Win32_BIOS               |   <-- ScintillaNET
|  3 |  } else {                                     |       editor with
|  4 |      Invoke-Command ...                       |       line numbers,
|  5 |  }                                            |       brace match,
|    |                                               |       syntax color
+----------------------------------------------------+
|  Test inputs (these populate $variables for Run    |
|  Test):                                            |
|  +-- id -----------+-- value --------------+       |
|  | computerName    | MYBOX                 |       |   <-- Test Inputs grid
|  +-----------------+-----------------------+       |
+----------------------------------------------------+
|  Output                                            |   <-- in-dialog RTB
|  ...                                               |       (test runs land
|                                                    |       here, not the
+----------------------------------------------------+       main console)
| [Run Test] [Cancel] [Insert Template]  Format: [.] |
| ...                  [Save] [Save As] [Close]      |
+----------------------------------------------------+
| Syntax: OK                              Idle       |   <-- status strip
+----------------------------------------------------+
```

### Test Inputs grid

Snapshot of the workspace's shared inputs taken when the dialog opened.
The `id` column is read-only; the `value` column is editable. **Run Test**
collects the grid's current values, applies the same normalization rules
a real button click would (e.g. `computerName` empty/`.`/`localhost` ->
local machine name), and injects them as `$variable` values into the
runspace before invoking your script. Edits in this grid don't affect
the main shared-input textboxes — the snapshot is local to the editor.

If no workspace is loaded, the grid is disabled with a "(no shared
inputs in this workspace)" hint.

### Format dropdown

Editor-local override for the Run Test output format (same options as
the per-button `rtbFormat`: `default` / `list` / `table` / `json` /
`raw`). Doesn't touch any saved button — purely steers what the
in-dialog output pane shows. Useful for previewing how a script will
render under different formats before committing one to the button.

### Run / Cancel

- **Run Test** (`F5`) writes the current editor text to a temp `.ps1`
  in `%TEMP%\ScriptDeck\`, dispatches it through the same long-lived
  runspace real button clicks use, and routes output to the in-dialog
  RTB instead of the main console. Test runs do **not** record into the
  history database.
- **Cancel Test** (or `Esc`) cancels the running test the same way Esc
  cancels a normal button-dispatch.
- One-at-a-time: while a test is running, button clicks in the main
  window are rejected (and vice versa). The dispatcher's busy gate is
  shared across both surfaces.

### Syntax validation

A debounced parse via `[System.Management.Automation.Language.Parser]::ParseInput`
runs as you type. The status strip shows `Syntax: OK` or
`Syntax: N error(s) -- line L: ...`. Run Test refuses to fire while
there are syntax errors (the script wouldn't make it past the parser
anyway).

### Insert Template

Pastes a starter snippet into an empty editor — the canonical
`Test-IsLocalTarget` if/else skeleton plus a comment about which shared
inputs are available as `$variables`.

### Save semantics

- **Save** writes to the path in the Path field. If empty (new script),
  prompts via Save File dialog defaulted to the workspace's `scripts\`
  folder.
- **Save As** always prompts.
- A `*` appears in the dialog title when the editor text differs from
  the file on disk; closing with unsaved changes prompts Save / Discard
  / Cancel.

---

## 8. Files and locations

| Path                                          | What |
|-----------------------------------------------|------|
| `bin\x64\Debug\net48\ScriptDeck.exe`          | the app (relative to repo) |
| `bin\x64\Debug\net48\ScriptDeck.Bootstrap.ps1` | dot-sourced into the runspace at startup; provides `Test-IsLocalTarget`, `Write-Rtb`, `Write-Grid`. Edit (or replace) to add helpers visible to every script. |
| `Workspaces\sample.json`                      | example workspace (open via File -> Open Workspace) |
| `Workspaces\scripts\`                         | sample scripts referenced by `sample.json` |
| `%LocalAppData%\ScriptDeck\history.db`        | run history (SQLite) |
| `%LocalAppData%\ScriptDeck\recent.json`       | recent-workspaces MRU list |

The `Workspaces\` directory is portable as a unit — JSON references its
scripts via relative paths, so copying the folder elsewhere keeps everything
working.

---

## 9. Keyboard shortcuts

### Main window

| Shortcut         | Action |
|------------------|--------|
| `Ctrl+N`         | New Workspace |
| `Ctrl+O`         | Open Workspace |
| `Ctrl+S`         | Save Workspace |
| `Ctrl+E`         | Toggle Edit Mode |
| `Ctrl+H`         | Recent Runs (history dialog) |
| `F7`             | Tools -> Script Editor |
| `Esc`            | Cancel the currently running script |
| `&1` .. `&9`     | Quick-jump to the first 9 Recent Workspaces (when submenu open) |
| `Enter` (in Find) | Highlight matches in console + grid |
| `Esc`   (in Find) | Clear highlights |

### Edit mode

| Shortcut         | Action |
|------------------|--------|
| Drag             | Move a button or label box; snaps to neighbors within 4 px |
| Drag corner grip | Resize; snaps width/height to siblings within 4 px |
| **Hold Shift while dragging** | Bypass snap for pixel-precise placement |
| Right-click button | Edit / Delete / Bring Forward / Send Backward / Match Width-Height-Size |
| Right-click label box | Add Button / Rename / Delete |
| Right-click empty canvas | Add Button / Add Label Box / Rename Tab / Delete Tab / Add New Tab |
| Drag tab header  | Reorder tabs |

### Script Editor (`F7`)

| Shortcut | Action |
|----------|--------|
| `F5`     | Run Test (uses Test Inputs grid + selected Format) |
| `Esc`    | Cancel running test |
| `Ctrl+S` | Save script to its current path |

---

## 10. Building your own workspace

The fastest path:

1. **File -> New Workspace**, pick a folder and a name. ScriptDeck saves a
   skeleton with one empty tab.
2. `Ctrl+E` to enter edit mode.
3. Right-click on the empty tab canvas, **Add Button**. Fill in label,
   executor, and script path. For PowerShell or cmd buttons targeting
   the standard `computerName` input, you can usually leave **args**
   empty — the script will see `$computerName` (or `%computerName%`)
   automatically.
4. Add a label box (right-click -> **Add Label Box**) when buttons get
   crowded; drag buttons into it to group visually.
5. **Edit -> Edit Shared Inputs** to add new top-band fields. Pick `id`
   values that read well as variable names (`companyName`, `region`,
   `targetUser`); they become `$companyName` etc. inside scripts.
6. **Edit -> Edit Workspace Menus** to add a `&Quick` style top menu.
7. `Ctrl+E` to leave edit mode, `Ctrl+S` to save.

For larger sets of commands, hand-editing the JSON in your editor of choice
is often faster than the in-app editor — both flows produce the same file.

---

## 11. Troubleshooting

**Button click does nothing visible.**
Check the Logs RTB at the bottom — every dispatch records start/end. If
`Output Targets` is empty for that button, the script's stdout has nowhere
to go. Add `"outputs": ["rtb"]` to the button.

**"Refusing to run with unresolved tokens" in red.**
A button's `args` or `workingDirectory` references a `{{token}}` that
doesn't match any shared input id. Either edit the button to remove the
token, or **Edit -> Edit Shared Inputs** and add a matching field.

**Script runs but the grid stays empty.**
The script needs to *emit* objects (not just `Write-Output` strings).
`Get-Service`, `Get-Process`, `Select-Object`, etc. produce objects.
`Write-Host` and `Format-Table | Out-String` produce text and won't fill
the grid. Add `"grid"` to `outputs`.

**Run history is empty even after several runs.**
Check the startup log for a `Run history disabled: ...` warning. The most
common cause is the SQLite file being locked or unwriteable; deleting
`%LocalAppData%\ScriptDeck\history.db` and restarting usually fixes it.

**My script can't see `$companyName` (or whatever shared input).**
First check the console for a one-time warning — invalid identifier
characters (hyphens, dots) and reserved names (`Host`, `Error`, `args`)
are skipped at injection time. Rename the input id to a plain identifier
and reload the workspace. Second, confirm the script is being run by the
**powershell** executor; the **process** executor doesn't get env-var or
runspace-variable injection (use `{{token}}` in `args` for those).

**`Test-IsLocalTarget` is undefined.**
The bootstrap script `ScriptDeck.Bootstrap.ps1` must live next to the
EXE. If you've moved or renamed the executable, copy the bootstrap
alongside it. The runspace dot-sources it at startup; missing file means
no helpers, but scripts that don't reference `Test-IsLocalTarget` still
run normally.

**App fails to launch with `SplitterDistance must be between Panel1MinSize
and Width - Panel2MinSize`.**
A SplitContainer is being initialized with constraints that don't fit its
default size. Open the relevant `*.Designer.cs` and add an explicit
`Size = new Size(W, H)` line *before* `SplitterDistance` and
`Panel2MinSize` so the validator passes during `EndInit`.

**Edit mode left a button I can't grab.**
Edit-mode buttons require ~14px in the bottom-right corner for the resize
grip. If a button is smaller than 40x22 you may have shrunk it too far —
hand-edit the JSON to bump `width`/`height` back up, or right-click and
delete it.

**The Recent Workspaces submenu is empty after I closed and reopened.**
Each open is "best effort" — if `%LocalAppData%\ScriptDeck\recent.json`
can't be written (write-protected profile, etc.), the MRU silently
no-ops. Check the LocalAppData path is reachable.

---

## 12. Recent additions (post-v0.1)

Everything in this section was added after the initial v0.1 release. The
earlier sections describe behavior that was stable at v0.1; this section
describes what's new and how it interacts with that baseline.

### 12.1 Volatile (session) shared inputs

Workspaces have always had **shared inputs** -- the JSON-defined values
that surface as the band of text boxes across the top of the window. These
are now classified as **Static** inputs: they persist in the workspace
file, render at the top, and are the same every time the workspace opens.

Alongside them, ScriptDeck now tracks a parallel set of **Volatile**
shared inputs that live only for the current session:

- Created either by the user (Inputs grid → right-click → Add Volatile)
  or by a script (`Set-SharedInput -Id ... -Value ...`)
- Visible in the **Inputs grid** in the bottom-right corner of the main
  window, alongside the workspace's Static inputs
- Cleared automatically when the workspace is closed, switched, or the
  app exits -- volatile state never survives a workspace boundary
- Available to every script run via the same `{{name}}` token substitution
  as Static inputs (and the script-side `$name` variable injection)

**No-duplicates rule across scopes.** A given id can be Static *or*
Volatile, but never both. The Add dialog refuses collisions with either
side, and `Set-SharedInput` refuses to overwrite a Static id from inside
a script (it throws an error visible in the console).

**The Inputs grid** has three columns -- Name / Value / Scope -- and
right-click context-menu entries:

| Entry                | Effect                                                |
|----------------------|-------------------------------------------------------|
| Add Static…          | Opens a dialog to add a workspace-persisted input.    |
| Add Volatile…        | Opens a dialog to add a session-only input.           |
| Remove               | Drops the selected row (Static = workspace gets dirty).|
| Clear All Volatile   | Wipes every Volatile row in one click.                |

Volatile values are editable in place by clicking the Value cell. Static
rows are read-only in the grid -- edit them via the top band as before.

### 12.2 Bootstrap helpers: Set/Get/Remove-SharedInput

Three new cmdlets are pre-imported into every script run:

```powershell
Set-SharedInput    -Id 'authToken' -Value 'abc123' -Label 'OAuth bearer'
Get-SharedInput    -Id 'authToken'    # returns the value (sugar for $authToken)
Get-SharedInput                       # bare: emits PSObjects with Id/Value/Scope
Remove-SharedInput -Id 'authToken'
```

`Set-SharedInput` creates or updates a **Volatile** input -- you cannot
overwrite a Static one. The new input is immediately visible in the
Inputs grid and available to subsequent script runs.

`Get-SharedInput` with `-Id` returns the value of that variable (which is
already injected as `$<id>` by the executor, so `$authToken` and
`Get-SharedInput -Id 'authToken'` are interchangeable). Without `-Id` it
emits one PSObject per known input with `Id`, `Value`, and `Scope` (the
string "Static" or "Volatile") properties -- useful for diagnostics.

`Remove-SharedInput` deletes a Volatile id. Like Set, it refuses to touch
Static ids (those belong to the workspace file and must be removed
through the editor).

These three helpers are the foundation for a planned **task-chains**
feature where one button's output feeds the next button's input via
session state.

### 12.3 Output toolbar action buttons

Four small glyph buttons live on the right edge of the output toolbar
(between the visibility checkboxes and the right margin). All four are
single-glyph Segoe MDL2 Assets icons with hover tooltips:

| Glyph | Tooltip                                  | Action                                                                                                          |
|-------|------------------------------------------|-----------------------------------------------------------------------------------------------------------------|
| 🗑    | Clear console text                       | Empties the console RTB and any active Find highlights.                                                          |
| 💾    | Export console text…                     | `SaveFileDialog` with `.txt` (plain) or `.rtf` (preserves green-on-black coloring) filters.                      |
| 💾↗   | Export grid to CSV…                      | RFC-4180-quoted CSV, UTF-8 with BOM so Excel auto-detects encoding. Skips hidden columns; honors display order. |
| 🪟    | Open grid in a separate window           | Snapshots the current grid contents into a new modeless **Grid-Out** window (see §12.4).                         |

Each action has a matching entry in a right-click context menu on the
relevant control:

- **Console RTB** → right-click → *Clear console* / *Export console text…*
- **Results grid** → right-click → *Export to CSV…* / *Open in new window*

The menu items disable themselves on hover when the source is empty, so
a click never silently no-ops.

### 12.4 Grid-Out popout window

Clicking the popout glyph (or right-click → Open in new window) snapshots
the current grid into an independent, modeless window:

- The snapshot is **frozen at open time** -- clearing or refilling the
  main grid doesn't affect the popout
- Multiple popouts allowed; the title bar shows the snapshot's row count
  and capture timestamp so two windows are distinguishable in Alt+Tab
- A bottom status bar shows `N rows, N columns -- captured YYYY-MM-DD HH:MM:SS`
- An **Export CSV…** button on the popout's bottom panel lets a snapshot
  be saved out even after the main grid has moved on
- Esc closes the window

Useful for keeping one result visible while running a different script,
or comparing two query outputs side-by-side.

### 12.5 RTB output spacing

Two small UX polishes to the console RTB:

1. **End-of-run separator** now writes two blank lines, the
   `********…` divider, then two MORE blank lines below. Back-to-back
   runs no longer crowd their first output up against the asterisks.

2. **Workspace-loaded intro** (`Workspace 'X' loaded. N tab(s), …`) now
   ends with two trailing blank lines so the first script-output line
   doesn't visually slam into the intro text.

Both changes affect the **console only**. The bottom Logs RTB stays
dense by design -- the timestamped audit lines like `Loaded workspace:
…` and `Added button: …` are unchanged.

### 12.6 Raw RtbFormat mode

The button-level `rtbFormat` field has a new value: **`raw`**. Selecting
it hands console rendering entirely to PowerShell's default formatter.
Use this when you want your script to fully control what appears in the
RTB -- `Format-Table`, `Format-List`, custom whitespace, ASCII art, etc.

Mechanically, raw mode appends a `ForEach-Object` filter to the executor's
pipeline. The filter:

- Lets **tagged** objects (`Write-Rtb` / `Write-Grid` /
  `Set-SharedInput` / `Remove-SharedInput`) pass through verbatim, so
  their tag-routing keeps working
- Buffers all **untagged** objects and at script-end flushes them through
  `Out-String -Stream`, which invokes the default formatter as a unit
  (so `Format-Table` etc. render correctly -- per-object would break
  the formatter's `StartData`/`EntryData`/`EndData` sequence)

**What raw mode does NOT do:**

- **Doesn't disable the grid.** `Write-Grid` still works -- explicit
  grid output is supported. What *does* go away is **auto-population**
  (untagged structured objects no longer become grid rows automatically).
  If you want full-column grid output in raw mode, emit explicitly via
  `Write-Grid`.
- **Doesn't strip stream colors.** Errors are still red, Warnings yellow,
  Info cyan. Color is a *classification*, not a *format*.
- **Doesn't remove the end-of-run separator.** That's run-boundary
  navigation, not user output.

**Trade-off:** untagged output appears at script-completion rather than
streaming live, because the buffer has to be flushed all at once for the
formatter to render multi-record output coherently. Raw mode is opt-in --
the user is asking for "PowerShell's default rendering," which inherently
wants the full record set. For long-running scripts where you want to
see output as it happens, leave `rtbFormat` at one of the streaming
values (`default` / `list`).

### 12.7 Removal of "Extended Grid Data" toggle

The old per-button `extendedGridData` checkbox is gone. It was meant to
include every property (including `PS*` engine metadata) in the grid and
turn primitive output into single-column `Value` rows -- but it couldn't
do what most users hoped (re-expand properties past a script-side
`Select-Object`), because once `Select-Object` collapses an object the
original is already gone before the executor sees it.

In its place:

- Grid output uses a curated user-property view (PS-engine metadata and
  internal `__*` routing tags are filtered out automatically).
- Primitives go to the RTB only, never the grid.
- Scripts that want full-property grid output should emit unprojected
  objects via `Write-Grid` and route the curated view to the RTB via
  `Write-Rtb` (or use raw mode for full PS-default rendering -- §12.6).

`extendedGridData` entries in existing workspace JSON files are silently
ignored -- the field is dropped on next save.

### 12.8 Group-button placement fix (under the hood)

Cosmetic but visible: adding a button to a group (right-click a label
box → *Add Button…*) used to drop every new button at the same
hardcoded `(14, 28)` position. WinForms z-orders newer siblings to the
back, so the new button was rendered *behind* the existing ones and
appeared to silently not work. The placement now uses a free-spot search
(matching the canvas-direct behavior), so successive Add-Button-to-group
clicks step across the row.

### 12.9 Edit the bootstrap helper from the app

`ScriptDeck.Bootstrap.ps1` -- the file that ships next to
`ScriptDeck.exe` and gets dot-sourced into every fresh PowerShell
runspace -- can now be edited live from inside the app.

**Tools → Edit Bootstrap Helper…** opens the file in the same Script
Editor used for workspace scripts. When you close the dialog, if the
file's modification time changed, ScriptDeck prompts:

> *ScriptDeck.Bootstrap.ps1 was modified. Reload the PowerShell session
> now so the updated helpers take effect?*

Choosing **Yes** calls the same reset path that fires when you switch
workspaces -- both PS runspaces (foreground and background) tear down,
fresh ones open, and the bootstrap is re-dot-sourced. The next button
click sees your updated helpers. **Any script currently running gets
cancelled** -- that's the trade-off for not waiting on potentially
long-running jobs.

**Two caveats the dialog will warn you about up-front:**

1. **Build output gets overwritten.** If you're running the EXE out of
   `bin\Debug\net48\` or `bin\Release\net48\` (typical for in-development
   testing), the next rebuild copies the source-tree `src\ScriptDeck\
   ScriptDeck.Bootstrap.ps1` over your in-app edits. For permanent
   changes, edit the source file directly. ScriptDeck detects this case
   and shows an info dialog before opening the editor.

2. **Write-protected installs.** If ScriptDeck is installed under
   `Program Files`, the Script Editor's save will fail with an OS
   "access denied" error. There's no UAC elevation -- copy the file
   to a writable location, edit, and copy back, or run the EXE
   elevated for that session.

The Save-and-reload cycle is non-destructive in the sense that the file
is the only state changed: if you break the bootstrap (syntax error,
deleted helper), the next script run surfaces a clear warning
("ScriptDeck.Bootstrap.ps1 reported errors loading: …") via the
deferred-warning latch in `PowerShellExecutor.TryRunBootstrap`. Restore
the file (or rebuild from source) to recover.

### 12.10 Managed computer list (Tools → Manage Computers)

ScriptDeck now maintains a ScriptDeck-wide list of computer names that
backs the dropdown rendered for any shared input whose `normalize` rule
is `computerName`. The list is per-user (`%LocalAppData%\ScriptDeck\
computers.json`), shared across every workspace you open.

**Behavior in the shared-inputs band:**

For a workspace input like

```jsonc
{
  "id": "computerName",
  "label": "Computer Name",
  "type": "text",
  "default": ".",
  "normalize": "computerName"
}
```

the field renders as an editable ComboBox (not a plain TextBox):

- The dropdown lists every entry from the managed list, alphabetically
- Three "magic" entries are pinned at the top regardless: `.` /
  `localhost` / `%COMPUTERNAME%` (each of which resolves to the local
  box via the existing `computerName` normalization)
- **Manual entry still works** — type any hostname, FQDN, or IP and it
  goes through unchanged
- Auto-complete suggests matching entries as you type (`SuggestAppend`)

The dropdown is **select-or-type only** — you can't add/edit/remove
entries from the field itself. Editing is exclusively via the Tools
menu, by design (the dropdown stays "clean" and the list is the one
source of truth).

**Tools → Manage Computers** opens a dialog where you can:

| Action | Behavior |
|---|---|
| Add… | Single-field prompt; case-insensitive duplicate check refuses redundancies |
| Edit | Renames the selected entry; duplicate check applies to the new name |
| Remove | Drops one or more selected entries |
| Import from text file… | Reads a `.txt` (or `.lst` / `.csv`) — one name per line; lines starting with `#` are comments, blank lines and whitespace are ignored, duplicates dedupe silently. Status text shows "Imported N new; M already existed". |
| Save | Persists to `computers.json` and refreshes every visible computer-name combo in place (preserves any value you've typed but not committed) |
| Cancel | Discards changes; disk file and live combos untouched |

**Import file format:**

```
# Servers
web01.corp.local
db01.corp.local

# Workstations
laptop-gtw

# IPs are fine too
192.168.1.50
```

This is import-only — Save writes a clean alphabetized JSON envelope,
not the comments-and-blanks format. The on-disk shape is:

```json
{
  "version": 1,
  "computers": [
    "192.168.1.50",
    "db01.corp.local",
    "laptop-gtw",
    "web01.corp.local"
  ]
}
```

The `version` field is a forward-compatibility hook; future schema
evolution (per-entry tags, descriptions, etc.) will bump it and the
loader will branch accordingly.

**Failure modes:**

- Missing file → empty list (Tools → Manage Computers populates it on
  first Save)
- Malformed JSON → empty list silently; first Save overwrites with a
  clean envelope
- Save IO failure (write-protected `%LocalAppData%`, full disk) →
  warning dialog with the OS error message; in-memory list keeps the
  edits for the session but they won't survive an app restart

### 12.11 Python support

`python` is now a first-class button executor alongside `powershell`,
`cmd`, and `process`. A Python button runs the script as a one-shot
subprocess (each click spawns a fresh `python.exe`); the model is the
same as `cmd` -- session state doesn't persist between runs.

**Interpreter selection.** Three layers, precedence highest-to-lowest:

1. **Per-button override** -- `Button.PythonInterpreter` (set via the
   Edit Button dialog's *Python:* field when executor is `python`).
   Typical use: pin a button to a specific venv:
   `C:\projects\foo\.venv\Scripts\python.exe`.
2. **Workspace default** -- `Workspace.PythonInterpreter` in the JSON
   root. Shared by every Python button in the workspace.
3. **PATH fallback** -- bare `python` if both above are unset.

Each layer can be null/empty to defer to the next.

**Bootstrap module** -- `scriptdeck_bootstrap.py` ships next to
`ScriptDeck.exe` and is added to `PYTHONPATH` automatically, so any
button script can:

```python
from scriptdeck_bootstrap import (
    write_rtb, write_grid,
    set_shared_input, get_shared_input, remove_shared_input,
    is_local_target, inputs,
)
```

The helpers mirror the PowerShell bootstrap one-to-one:

| Python helper                          | PowerShell equivalent      | What it does |
|----------------------------------------|----------------------------|---|
| `write_rtb(value)`                     | `Write-Rtb`                | Route a string to the console RTB only. |
| `write_grid(rows)`                     | `Write-Grid`               | Append one dict (or list of dicts) as grid rows. First record pins the column set. |
| `set_shared_input(id, value, label?)`  | `Set-SharedInput`          | Create / update a Volatile shared input. Refuses Static ids (raises `ValueError`). |
| `get_shared_input(id?)`                | `Get-SharedInput`          | Return one value, or the full inputs dict if no id. |
| `remove_shared_input(id)`              | `Remove-SharedInput`       | Drop a Volatile shared input. Refuses Static ids. |
| `is_local_target(name)`                | `Test-IsLocalTarget`       | True when the name is `.` / `localhost` / `%COMPUTERNAME%` / empty. |
| `inputs` (module attribute)            | `$ScriptDeckInputs` (PS)   | Read-only snapshot of every shared input dict. |

**Output protocol** -- the executor watches stdout for lines starting
with the sentinel `__SCRIPTDECK_JSON__`. Each such line is one JSON
event (RTB-route, grid-append, set/remove-shared-input). The bootstrap
helpers above emit these for you; ordinary `print()` output bypasses
the tag mechanism and goes to the console RTB as plain text. Stderr
always routes to the sink's Error stream (red in the console).

**Shared inputs** -- published as individual environment variables
(`os.environ['computerName']`) AND as a typed dict via the bootstrap
module's `inputs` attribute. Same `{{token}}` substitution as the
other executors works in the button's `args`.

**Encoding** -- the executor sets `PYTHONIOENCODING=utf-8` and forces
UTF-8 on the redirected stdout/stderr streams, so `print('café')`
works without `chcp` hacks.

**Script Editor** -- opens `.py` files with the Python lexer
(syntax highlighting for keywords, strings, decorators, class/def
headers, etc.) and a syntax checker that shells out to
`python -c "import ast; ast.parse(sys.stdin.read())"`. The Run Test
button writes to a `scratch.py` and dispatches through the same
PythonExecutor used at run time -- you preview exactly what a button
click will produce. If `python` isn't on PATH the syntax-check
silently falls back to "not checked" rather than failing the editor.

**Sample** -- the bundled sample workspace's Tools tab now has a
**Python Info** button running `scripts/Get-PythonInfo.py`. It
demonstrates: `print()` to the console, `write_grid()` populating the
results grid with interpreter metadata, and `set_shared_input()`
creating a `lastPythonRun` Volatile input you can see appear in the
Inputs grid.

**What's NOT supported in v1:**

- No long-lived runspace -- each click is a fresh subprocess. State
  set by one click does not persist to the next. (Use Volatile shared
  inputs via `set_shared_input` for cross-click state.)
- No tab-completion / linting (mypy, pylint) in the editor.
- `requirements.txt` auto-install is not part of ScriptDeck; manage
  your venvs externally and point the interpreter override at them.
- Interactive `input()` / stdin prompts will hang -- there's no host
  UI to satisfy them.

---

## 13. Credits

ScriptDeck stands on these third-party libraries:

| Library                       | Version | What it does                                                       |
|-------------------------------|---------|--------------------------------------------------------------------|
| Newtonsoft.Json               | 13.0.3  | Workspace JSON serialization (read / write).                       |
| Microsoft.Data.Sqlite         | 6.0.36  | SQLite-backed run history at `%LocalAppData%\ScriptDeck\history.db`. |
| jacobslusser.ScintillaNET     | 3.6.3   | Embedded code editor in the Script Editor dialog.                  |
| System.Management.Automation  | (system) | PowerShell hosting -- runspace lifecycle, pipeline streams.        |

PowerShell 5.1 is bundled with Windows 10 1809+ and every Windows 11
build, so no separate runtime install is required on a typical
end-user machine.
