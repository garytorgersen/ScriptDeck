# ScriptDeck Quick Guide

A friendly walkthrough for using ScriptDeck day to day. Click buttons, see results, run things in the background, edit your scripts. No technical detours -- if you'd like the under-the-hood reference, see `USER_MANUAL.md`.

---

## What is ScriptDeck?

ScriptDeck is a personal automation launcher. It shows a grid of buttons; each button runs a script when you click it. The output streams into panels you can read, search, and copy from. You decide what each button does -- run a PowerShell command, launch a tool, run a batch file, kick off a long-running collection job.

![Main window with a workspace loaded](docs/images/01-main-window.png)

---

## Getting started

### Open a workspace

A *workspace* is just a file (`.json`) that lists tabs, buttons, and shared inputs. ScriptDeck ships with `Workspaces\sample.json` -- a small example you can open to try things out.

To open one: **File → Open Workspace** (`Ctrl+O`), pick a `.json` file. ScriptDeck will pop a "this workspace runs scripts on your machine" prompt -- click **Yes** to load it. From then on, the file shows up under **File → Recent Workspaces** (`&1` through `&9` jump to the top of the list).

### Run your first button

Click any button on a tab. While it's running:

- The status bar at the bottom shows `Running: <name>` in cyan.
- Output streams into the **Console** panel.
- If the script returns structured data (a list of services, a list of files), it also fills the **Results grid** alongside the console.
- The **Logs** panel at the bottom records start/done lines so you can scroll back through the session.

Press **Esc** to cancel a running script.

If you click another button while one is running, ScriptDeck warns you (`Already running 'X'. Cancel first or wait.`). For scripts you expect to take a long time, mark the button **Run in background** and ScriptDeck will stop blocking other clicks -- see [Long-running jobs](#long-running-jobs) below.

---

## The Computer Name field (and other shared inputs)

The green-on-black bar at the top is **shared inputs**. The most common one is `Computer Name` -- type a hostname there once, and every button you click sees that value. No need to retype it for each script.

- **Empty / `.` / `localhost`** are auto-replaced with your local machine name on load. So an empty box reads `MYBOX` from the moment the workspace opens.
- Any other value is treated as a remote machine.
- Scripts that target the local box and scripts that target a remote box are usually the *same* script -- ScriptDeck makes the value available, the script does the right thing.

If the workspace defines other shared inputs (CompanyName, TargetUser, whatever), you'll see additional fields in the same green-on-black bar. Same idea: type once, used everywhere.

---

## Reading the output

The output area has three views you can switch between:

### The Console (the dark "RTB" panel)

Free-form text output. Each kind of message is colored:

- **Light gray**: regular output (what the script printed).
- **Cyan**: information messages (the script announcing what it's doing).
- **Gold / yellow**: warnings.
- **Salmon / red**: errors.
- **Light green**: log lines (timestamps + status: `Running: X`, `Done: X (exit 0, 1.2s)`).

### The Results grid

When a script returns a list of things (services, processes, files), each thing becomes a row, each property a column. You can sort the columns, click cells, and copy text out.

### The Jobs tab

For background jobs (see below).

### Show / hide a panel

The toolbar above the output has two checkboxes -- **Show Console** and **Show Grid**. Uncheck one and the other expands to fill the area. At least one must stay checked.

![Toolbar with Find box and Show Console / Show Grid checkboxes](docs/images/03-toolbar-search.png)

### Find a word in the output

Type a term in the **Find** box and press Enter (or click **Find**). Matches are highlighted in yellow in both the console and the grid; the first match scrolls into view. Press **Esc** in the Find box (or click **Clear**) to clear.

Simple substring search, case-insensitive.

---

## RTB format -- making structured output readable

For PowerShell buttons that return objects (services, processes, etc.), the **RTB format** setting on the button decides how the console renders them:

| Format    | Looks like                                     |
|-----------|------------------------------------------------|
| `default` | One line per object: `@{Name=Spooler; Status=Running}` |
| `list`    | One *property* per line, blank line between objects (best for "see everything about each one") |
| `table`   | Aligned text columns with a header (best for scanning many) |
| `json`    | Pretty-printed JSON array                      |

The grid is unaffected -- it always shows the structured rows.

To change a button's format: enter Edit mode (`Ctrl+E`), click the button (or right-click → Edit), and pick from the **RTB format** dropdown.

![Same Get-Service output rendered four ways](docs/images/03-rtb-formats.png)

---

## Long-running jobs

Some scripts take a while -- a baseline scan, a domain query, a remote inventory. You don't want them blocking the rest of the app.

Mark a button as **Run in background** (Edit mode → click the button → check the box) and ScriptDeck will:

1. Send the script to a separate execution slot when you click it.
2. Let you keep clicking other buttons while it runs.
3. Capture all the output into a *job* you can monitor in the **Jobs** tab.

![Jobs tab with a running job, a queued job, and a completed job](docs/images/03-jobs-tab.png)

### The Jobs tab

Found alongside the Output tab in the output area. Each row is one background job:

| Column   | What it shows                                       |
|----------|-----------------------------------------------------|
| Status   | Queued, Running, Done, Failed, Cancelled            |
| Button   | The button you clicked                              |
| Started  | When it began                                       |
| Elapsed  | Time so far (live updates while running)            |
| Exit     | Process exit code (0 = success, usually)            |
| Error    | Failure message if applicable                       |

Click any row to see that job's output below. Output streams live for the running job; for finished jobs you see the full buffered output.

The tab header keeps a running count: `Jobs (1 running, 2 queued, 4 total)`.

### Buttons in the Jobs toolbar

- **Cancel Job** -- stops the selected job. Cancelling a queued job marks it Cancelled (still appears in the list as a record). Cancelling a running job stops it mid-flight.
- **Send to Console** -- replays the selected job's full output into the main console + grid, so you can search / copy from it the same way you would for foreground output.
- **Dismiss** -- removes a finished job from the list. Won't dismiss queued or running jobs (cancel first).

### How parallel is it?

At most **two scripts run at the same time** -- one foreground, one background. Submit more background jobs and they queue up FIFO; the next one starts as soon as the current one finishes.

---

## Recent Runs (the history list)

Every script you run gets logged. Open **Tools → Recent Runs** (`Ctrl+H`) for a sortable list of every dispatch this session has produced.

Click a row to see the full args, working directory, exit code, and a snippet of the captured output. Useful for "what did I run last Tuesday at 3pm?" or "did that script actually succeed?"

History persists across restarts.

---

## Editing your workspace

Press `Ctrl+E` to enter **Edit mode**. The status bar turns orange and reads `EDIT mode`. Buttons grow a red outline; their bottom-right corners show a small orange-red triangle.

In Edit mode you can:

- **Drag a button** to move it. As you drag, it snaps to nearby siblings within a few pixels for tidy alignment. Hold **Shift** to bypass snap.
- **Drag the corner triangle** to resize. Same snap behavior to match nearby sizes.
- **Right-click a button** for: Edit / Delete / Bring Forward / Send Backward / Match Width of / Match Height of / Match Size of.
- **Right-click empty space** to: Add Button / Add Label Box / Rename Tab / Delete Tab / Add New Tab.
- **Right-click a label box** to: Add Button (parents the new button into that group) / Rename / Delete.
- **Drag a tab header** to reorder tabs.

When you're done, press `Ctrl+E` again to leave edit mode.

### Saving

The title bar shows an asterisk `*` when there are unsaved changes:

```
ScriptDeck — My Workspace *
```

Press `Ctrl+S` to save. If you try to close ScriptDeck with unsaved changes, you'll get a **Save / Don't Save / Cancel** prompt.

### "Match Size of..." -- making buttons consistent

Right-click any button in edit mode and look for the three submenus:

- **Match Width of** -- list of every other button in the workspace, with its current size. Pick one and your button takes its width.
- **Match Height of** -- same, height only.
- **Match Size of** -- both dimensions.

Useful for tidying up after free-drag has left you with mostly-but-not-quite-the-same-size buttons.

![Right-click context menu with Match Size submenu open](docs/images/04-match-size-menu.png)

---

## Writing your own scripts

ScriptDeck includes a built-in editor for PowerShell scripts. Open it via **Tools → Script Editor** (`F7`), or from inside the Edit Button dialog with the **Edit Script...** button.

![Script Editor with code, test inputs, and output](docs/images/07-script-editor.png)

### What you get

- A code editor with syntax highlighting, line numbers, and brace matching.
- A **Test inputs** grid populated from the workspace's shared inputs (you can override values for a single test).
- An output pane that shows the result of pressing **Run Test** (`F5`).
- A **Format** dropdown that lets you preview different RTB formats without touching the saved button.
- Live syntax checking -- the status strip says `Syntax: OK` or `Syntax: 1 error -- line 4: ...`. If there's an error, Run Test refuses to run.
- An **Insert Template** button that drops a starter snippet showing the standard local-vs-remote pattern.

### Save / Save As

`Ctrl+S` saves to the current path. **Save As** lets you choose a new file. When you opened the editor from a button's Edit dialog, saving sends the path back into that dialog automatically.

The dialog title shows a `*` if you have unsaved edits.

---

## What carries between button clicks

ScriptDeck keeps a PowerShell session open from the moment the app
starts until you close it. A few things deliberately stick around
across button clicks — usually exactly the things you'd want:

- **Shared input values** (the green-on-black textboxes). ScriptDeck
  re-reads them on every click, so each script always sees the current
  value of `$computerName`, `$companyName`, and any others you've
  defined. Even if one script does `$companyName = "override"` for its
  own use, the next click starts again with whatever's in the textbox.
- **Helper functions** from `ScriptDeck.Bootstrap.ps1` (the file that
  ships next to the EXE). `Test-IsLocalTarget`, `Write-Rtb`,
  `Write-Grid` -- loaded once at startup, available to every script
  forever.
- **Imported modules.** If a script does `Import-Module ActiveDirectory`,
  the module stays loaded so the next click that needs it doesn't pay
  the (often multi-second) load cost again.

Most things, however, do **not** stick around. Each script runs in its
own scope; when it finishes, its variables and locally-defined
functions disappear. So if Button A does:

```powershell
$result = Get-Service
```

…then you click Button B, Button B's script does NOT see `$result`
from Button A. PowerShell handles that for you -- no cleanup code
required.

### Making a value persist on purpose

If you actually want a variable to outlive the script, declare it
explicitly at global scope:

```powershell
$global:lastBackupTime = Get-Date
# or
Set-Variable -Scope Global -Name lastBackupTime -Value (Get-Date)
```

A later button can then read `$lastBackupTime`. Same idea for a helper
function you want available everywhere:

```powershell
function global:Get-MyHelper { ... }
```

Use this sparingly. "Spooky action at a distance" -- where Button B
behaves differently because Button A ran first -- is hard to debug.
For helpers you'd reach for in lots of scripts, the better home is
`ScriptDeck.Bootstrap.ps1` (described in the next section).

### Adding your own shared helpers

`ScriptDeck.Bootstrap.ps1` lives next to `ScriptDeck.exe`. ScriptDeck
loads it once at startup and makes everything in it available to every
script you run -- as if those functions were built in.

To add a helper of your own:

1. Open `ScriptDeck.Bootstrap.ps1` in any text editor.
2. Add a function at the bottom:
   ```powershell
   function Test-PortOpen {
       [CmdletBinding()]
       param(
           [Parameter(Mandatory)][string]$ComputerName,
           [Parameter(Mandatory)][int]$Port,
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
3. Save the file.
4. **Close and reopen ScriptDeck** -- the bootstrap is loaded when
   ScriptDeck starts up; changes don't take effect until you restart.
5. Now any button script can call `Test-PortOpen -ComputerName
   $ComputerName -Port 3389`.

A few rules of thumb:

- Use Verb-Noun names (PowerShell convention) so helpers read
  naturally alongside built-in cmdlets.
- Keep the file fast to load. A function definition is free; a
  `Import-Module SomeBigModule` at the top of the bootstrap costs
  seconds on every app launch.
- Don't print or emit output from the top of the file -- it would
  appear in the first script's output and look like that script
  produced it. Define functions; let scripts produce output.
- Don't store passwords or API keys in the bootstrap; it's plaintext
  on disk. Use Windows Credential Manager or the `SecretManagement`
  module.

The technical manual (`USER_MANUAL.md`, §3) has more on this --
overriding built-ins, sharing across machines, etc.

### Foreground vs background runspaces

Foreground clicks and background jobs use **separate** PowerShell
sessions internally. A `$global:` value set by a foreground click is
NOT visible to a background job, and vice versa. Each path keeps its
own state. If you need a value visible to both, prefer a shared input
or save / load via a small file.

---

## Tips & common gotchas

### `$ComputerName` is just there

In a PowerShell button's script, you don't need a `param(...)` block to receive the shared input. Type `$ComputerName` and it works:

```powershell
Get-Service -ComputerName $ComputerName | Where-Object Status -eq 'Running'
```

If your workspace defines `companyName` as a shared input, scripts get `$companyName` for free too. Same in cmd batch files via `%companyName%`.

### Local vs remote -- one helper, one if/else

The bootstrap module provides `Test-IsLocalTarget` so you can write scripts that work for either:

```powershell
if (Test-IsLocalTarget) {
    # Local code path
    Get-NetIPAddress | Where AddressFamily -eq 'IPv4'
} else {
    # Remote code path
    Invoke-Command -ComputerName $ComputerName -ScriptBlock {
        Get-NetIPAddress | Where AddressFamily -eq 'IPv4'
    }
}
```

Many cmdlets (Get-CimInstance, Get-WmiObject, Test-Connection) accept `-ComputerName` and handle the local case transparently -- for those you don't need the if/else, just pass `-ComputerName $ComputerName`.

### When you want different output in the console vs the grid

By default, every object a script emits goes to both the console and the grid. If you want a compact view in the console and the full property list in the grid, pipe each shape through `Write-Rtb` or `Write-Grid`:

```powershell
$processes = Get-Process

$processes                                   | Write-Rtb    # console only
$processes | Select-Object -Property *       | Write-Grid   # grid only
```

Untagged emissions (the default) still flow to both panels. The helpers are an opt-in escape hatch.

### Output looks wrong / weird

If a button's console output is duplicated or garbled, check whether the script is emitting the same data multiple times under different names. The grid will pin to the first shape it sees and ignore the rest, so the grid often looks fine while the console is messy. Either consolidate the emissions or use `Write-Rtb` / `Write-Grid` to route shapes to specific panels.

### Confirm before destructive actions

If a button restarts a server, deletes files, etc. -- check **Prompt before running** in the Edit Button dialog. ScriptDeck will pop a Yes/No before each click.

### A script needs admin

ScriptDeck runs scripts in the same security context it itself runs in. Right-click ScriptDeck.exe → **Run as administrator** if you need elevated rights.

### Cancel something

- `Esc` cancels the currently-running foreground script.
- In the Jobs tab, select a job and click **Cancel Job**.

---

## Keyboard shortcuts at a glance

| Shortcut       | Action                                        |
|----------------|-----------------------------------------------|
| `Ctrl+N`       | New Workspace                                 |
| `Ctrl+O`       | Open Workspace                                |
| `Ctrl+S`       | Save Workspace                                |
| `Ctrl+E`       | Toggle Edit mode                              |
| `Ctrl+H`       | Recent Runs (history)                         |
| `F7`           | Tools → Script Editor                         |
| `F5`           | Run Test (in the Script Editor)               |
| `Esc`          | Cancel the current foreground script          |
| `&1`-`&9`      | Quick-jump to a Recent Workspace (when submenu open) |

In Edit mode:

| Shortcut          | Action                                  |
|-------------------|-----------------------------------------|
| Drag              | Move a button (snaps to neighbors)      |
| Drag corner       | Resize (snaps to siblings' sizes)       |
| **Hold Shift**    | Bypass snap                             |
| Right-click       | Context menu (Edit / Delete / Match…)   |
| Drag tab header   | Reorder tabs                            |

---

## "Where do I find...?"

| Question                                   | Where                                          |
|--------------------------------------------|------------------------------------------------|
| The list of recent workspaces I've opened  | File → Recent Workspaces                       |
| The history of every run                   | Tools → Recent Runs (`Ctrl+H`)                 |
| The Script Editor                          | Tools → Script Editor (`F7`)                   |
| Background jobs                            | "Jobs" tab in the output area                  |
| The button rearrangement controls          | Edit mode (`Ctrl+E`) → right-click             |
| What buttons sit inside which label box    | Edit mode -- the box visibly contains them     |
| The settings for one button                | Edit mode → click (or right-click → Edit)      |

---

If you hit something the guide doesn't cover, the technical manual (`USER_MANUAL.md`) goes deeper on every feature.
