using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScriptDeck.Hosting;
using ScriptDeck.History;
using ScriptDeck.Workspace;

// See WorkspaceRenderer.cs for the rationale on these aliases — Workspace.Button
// vs WinForms.Button collide on bare 'Button' references.
using WsButton = ScriptDeck.Workspace.Button;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// The main ScriptDeck window. Layout uses a familiar
    /// menus + shared-inputs band + tabs + output split + logs + status
    /// arrangement common to launcher tools:
    ///
    ///     [MenuStrip]
    ///     [SharedInputsPanel]   ← black band, populated from workspace
    ///     [TabControl]          ← tabs from workspace
    ///     [SplitContainer]      ← console RTB | results grid
    ///     [RichTextBox: logs]
    ///     [StatusStrip]
    ///
    /// Phase 2 wires up workspace JSON load/save and renders shared
    /// inputs + tabs + buttons. Button clicks log a stub line — Phase 3
    /// adds the executors that actually run scripts.
    /// </summary>
    public partial class Shell : Form
    {
        // The single sink every executor (and the stub click handler)
        // writes to. Constructed once after the Designer has laid out
        // controls; see <see cref="OnShellLoad"/>.
        internal IOutputSink Sink { get; private set; }

        // Renderer owns the dynamic UI: shared-input fields and dynamic
        // tabs/buttons. Created in OnShellLoad; cleared/rebuilt every
        // time a workspace loads or closes.
        private WorkspaceRenderer _renderer;

        // Routes button clicks to the right executor (powershell/cmd/process)
        // and enforces one-at-a-time. Owns the executor instances (and
        // therefore the long-lived PowerShell runspace).
        private Dispatcher _dispatcher;

        // Phase 6: SQLite-backed run history. Lives on the Shell so the
        // Tools \u2192 Recent Runs dialog can read it directly. Passed into
        // the dispatcher so finished runs auto-record. Best-effort: if
        // init fails (locked file, bad permissions) the store no-ops and
        // we surface a one-line warning rather than blocking startup.
        private RunHistory _runHistory;

        // Phase 8: most-recently-used workspace paths, persisted to
        // %LocalAppData%\ScriptDeck\recent.json. Populated on every
        // successful workspace load; renders into the File menu's
        // Recent Workspaces submenu on demand.
        private RecentWorkspaces _recentWorkspaces;

        // User-managed list of computer names backing the dropdown
        // shown for shared inputs whose normalize == "computerName".
        // Persists to %LocalAppData%\ScriptDeck\computers.json. The
        // WorkspaceRenderer holds a reference and re-points its combo
        // boxes when this store fires Changed (after Tools -> Manage
        // Computers saves).
        private ComputerListStore _computerList;

        // Phase 7: top-level menus rendered from MenuDefinition entries.
        // We track the inserted ToolStripMenuItems separately so reload /
        // close / edit cycles can remove them cleanly without touching
        // the built-in File / Edit / Tools menus. Anchor index is recomputed
        // on every render — inserting always between Edit and Tools so the
        // workspace menus group visually with Edit (their context) but
        // sit before Tools (the "global" cross-cutting actions).
        private readonly List<ToolStripMenuItem> _workspaceMenuItems = new List<ToolStripMenuItem>();

        // Currently loaded workspace + its source path. Both null when no
        // workspace is open. Kept on the Shell (not in the renderer)
        // because Save needs the path and the renderer is presentation-only.
        private Workspace.Workspace _activeWorkspace;
        private string _activeWorkspacePath;

        // Session-scoped shared inputs (Volatile). Created at runtime by
        // either scripts (via Set-SharedInput) or the user (Inputs grid
        // -> Add Volatile Input). Never persisted; cleared on workspace
        // open / close / switch. Merged on top of the workspace's
        // Static inputs at dispatch time; the duplicate-prevention rules
        // mean a given id is only ever in ONE of the two sources.
        private readonly Dictionary<string, SessionInput> _sessionInputs =
            new Dictionary<string, SessionInput>(StringComparer.OrdinalIgnoreCase);
        // Fires after _sessionInputs changes (Add / Update / Remove /
        // Clear). The Inputs grid UI subscribes to refresh. Marshalled
        // to the UI thread by the raiser since some triggers (script
        // emissions) come in on background threads.
        private event Action SessionInputsChanged;

        public Shell()
        {
            InitializeComponent();
            this.Load += OnShellLoad;
            this.FormClosed += OnShellClosed;
            this.FormClosing += OnShellFormClosing;
        }

        // True after the first workspace has been loaded. Used to gate
        // runspace resets on workspace-switch -- we don't want to
        // re-create the runspace on the very first load (it's already
        // pristine) but every subsequent load should wipe stale state.
        private bool _previousWorkspaceLoaded;

        // True when the in-memory workspace model differs from disk.
        // Cleared on Load / Save / New (each leaves the model and the
        // file in sync). Set by MarkDirty(), called from every handler
        // that mutates the workspace, plus from the renderer's
        // LayoutCommitted event for drag/resize edits the Shell never
        // sees as discrete events.
        private bool _isDirty;

        private void OnShellLoad(object sender, EventArgs e)
        {
            Sink = new OutputSink(richTextBox_Console, richTextBox_Logs, dataGridView_Results);

            // Open the history store before the dispatcher so we can pass
            // it in. Failures are non-fatal — RunHistory.Disabled just
            // means recording silently no-ops.
            _runHistory = new RunHistory();
            if (_runHistory.Disabled)
            {
                Sink.WriteWarning(
                    $"Run history disabled: {_runHistory.DisabledReason}." + Environment.NewLine);
            }

            // MRU store. Best-effort: any failure inside Add/GetLive is
            // swallowed by the store itself, so a write-protected
            // LocalAppData won't crash the Shell.
            _recentWorkspaces = new RecentWorkspaces();

            // Computer list. Loaded from %LocalAppData%\ScriptDeck\
            // computers.json; if the file is missing or malformed the
            // store presents an empty list. Edited via
            // Tools -> Manage Computers; consumed by the renderer when
            // it sees a shared input with normalize == "computerName".
            _computerList = new ComputerListStore();

            // Dispatcher owns the executors. The PowerShell runspace
            // inside PowerShellExecutor opens here and stays open until
            // Dispose. Long-lived host pattern: open the runspace once
            // at startup, reuse for every click, close on shutdown.
            // Keeps PowerShell's per-pipeline cold-start cost out of
            // every button click.
            //
            // Two PowerShellExecutor instances: one for the foreground
            // single-flight gate, one for the background queue. Each
            // owns its own runspace so a long-running background job
            // doesn't serialize behind a foreground click. Cmd /
            // Process are stateless and shared across both paths --
            // every invocation forks a fresh OS process so there's no
            // shared state to protect.
            var psFg  = new PowerShellExecutor();
            var psBg  = new PowerShellExecutor();
            var cmd   = new CmdExecutor();
            var proc  = new ProcessExecutor();
            var py    = new PythonExecutor();
            // Both PS executors push session-input mutations through the
            // Shell's session dict. A script run in foreground OR in a
            // background job can equally call Set-SharedInput; either
            // path lands in the same Shell-side state. Subscribers
            // marshal to the UI thread internally.
            psFg.SharedInputSetRequested    += OnSessionInputSetRequested;
            psFg.SharedInputRemoveRequested += OnSessionInputRemoveRequested;
            psBg.SharedInputSetRequested    += OnSessionInputSetRequested;
            psBg.SharedInputRemoveRequested += OnSessionInputRemoveRequested;
            // PythonExecutor's tag events are static (single subscription
            // covers both fg + bg) -- python is one-shot so a single
            // instance handles both paths; the events are on the type
            // itself, not the instance.
            PythonExecutor.SharedInputSetRequested    += OnSessionInputSetRequested;
            PythonExecutor.SharedInputRemoveRequested += OnSessionInputRemoveRequested;
            _dispatcher = new Dispatcher(
                Sink,
                executors:           new IExecutor[] { psFg, cmd, proc, py },
                backgroundExecutors: new IExecutor[] { psBg, cmd, proc, py },
                history:             _runHistory);
            _dispatcher.BusyChanged += OnDispatcherBusyChanged;
            if (_dispatcher.BackgroundQueue != null)
            {
                _dispatcher.BackgroundQueue.JobAdded         += OnJobAdded;
                _dispatcher.BackgroundQueue.JobStatusChanged += OnJobStatusChanged;
            }

            _renderer = new WorkspaceRenderer(
                panel_SharedInputs,
                tabControl_Workspace,
                tabPage_Welcome,
                OnWorkspaceButtonClicked,
                _computerList);

            // Edit-mode wiring. The renderer fires events when the user
            // picks a context-menu item; Shell mutates the workspace
            // model and asks for a re-render. Keeping the model owned by
            // Shell (not the renderer) means Save just serializes
            // _activeWorkspace as-is.
            _renderer.EditButtonRequested   += OnEditButton;
            _renderer.DeleteButtonRequested += OnDeleteButton;
            _renderer.MoveButtonRequested   += OnMoveButton;
            _renderer.AddButtonRequested    += OnAddButton;
            _renderer.EditTabRequested      += OnEditTab;
            _renderer.DeleteTabRequested    += OnDeleteTab;
            _renderer.AddTabRequested       += OnAddTab;
            _renderer.AddGroupRequested     += OnAddGroup;
            _renderer.EditGroupRequested    += OnEditGroup;
            _renderer.DeleteGroupRequested  += OnDeleteGroup;
            _renderer.AddButtonToGroupRequested += OnAddButtonToGroup;
            _renderer.MatchSizeRequested        += OnMatchSize;
            // Drag/resize commits land directly on the model; the renderer
            // is the only mutator the Shell doesn't see, so it tells us.
            _renderer.LayoutCommitted += MarkDirty;

            // Inputs grid (bottom-right). Owns no state; the Shell pushes
            // a full snapshot whenever something changes (workspace load,
            // session-input mutation, etc.). All user gestures surface
            // as events the Shell turns into model mutations.
            inputsGridPanel.AddStaticRequested    += OnInputsGrid_AddStatic;
            inputsGridPanel.AddVolatileRequested  += OnInputsGrid_AddVolatile;
            inputsGridPanel.RemoveRequested       += OnInputsGrid_Remove;
            inputsGridPanel.ClearVolatileRequested += OnInputsGrid_ClearVolatile;
            inputsGridPanel.VolatileValueEdited   += OnInputsGrid_VolatileValueEdited;
            // Re-render the grid whenever the session-input dict changes
            // -- Set / Remove / Clear all flow through this event.
            SessionInputsChanged += RefreshInputsGrid;
            // Initial render: empty workspace, no volatiles. Just paints
            // the grid header so the UI doesn't look broken at launch.
            RefreshInputsGrid();

            // Right-click context menus on the console RTB and the
            // results grid. Each item routes through the same handler
            // as its toolbar twin so the two surfaces stay in sync --
            // change the behavior in one place and both menus + buttons
            // pick it up. Built lazily here rather than in the Designer
            // file so the menu/handler coupling stays visible in one
            // file.
            BuildConsoleContextMenu();
            BuildGridContextMenu();

            Sink.Log("ScriptDeck started.");
            Sink.WriteInfo("Welcome to ScriptDeck." + Environment.NewLine);
            Sink.WriteInfo("Open a workspace (File \u2192 Open Workspace) to load tabs, buttons, and shared inputs." + Environment.NewLine);
            Sink.WriteInfo("Toggle Edit mode (Ctrl+E) to add, rename, or remove tabs and buttons. Save with Ctrl+S." + Environment.NewLine);

            UpdateStatusBar();
            UpdateBusyUi(false);
        }

        // ----- Right-click menus -------------------------------------------
        //
        // Both menus delegate to the same handlers the toolbar buttons
        // use, so context-menu Clear == toolbar Clear, no behavior drift.

        private void BuildConsoleContextMenu()
        {
            if (richTextBox_Console == null) return;
            var menu = new ContextMenuStrip();
            var miClear  = new ToolStripMenuItem("Clear console");
            var miExport = new ToolStripMenuItem("Export console text...");
            miClear.Click  += button_ClearConsole_Click;
            miExport.Click += button_ExportConsole_Click;
            menu.Items.AddRange(new ToolStripItem[] { miClear, miExport });

            // Disable inapplicable items on open so the user gets a
            // "nothing to do" signal instead of a no-op click + popup.
            menu.Opening += (_, __) =>
            {
                bool hasText = richTextBox_Console.TextLength > 0;
                miClear.Enabled  = hasText;
                miExport.Enabled = hasText;
            };

            richTextBox_Console.ContextMenuStrip = menu;
        }

        private void BuildGridContextMenu()
        {
            if (dataGridView_Results == null) return;
            var menu = new ContextMenuStrip();
            var miExport  = new ToolStripMenuItem("Export to CSV...");
            var miPopout  = new ToolStripMenuItem("Open in new window");
            miExport.Click += button_ExportGridCsv_Click;
            miPopout.Click += button_GridPopout_Click;
            menu.Items.AddRange(new ToolStripItem[] { miExport, miPopout });

            menu.Opening += (_, __) =>
            {
                bool hasData = dataGridView_Results.Columns.Count > 0
                            && dataGridView_Results.Rows.Count    > 0;
                miExport.Enabled = hasData;
                miPopout.Enabled = hasData;
            };

            dataGridView_Results.ContextMenuStrip = menu;
        }

        private void OnShellClosed(object sender, FormClosedEventArgs e)
        {
            // Dispose the dispatcher (and through it, the PowerShell
            // runspace) so the process actually exits cleanly. Without
            // this the runspace's STA worker thread can outlive the form
            // and pin the process.
            try { _dispatcher?.Dispose(); } catch { /* swallow on shutdown */ }
            // Dispose history *after* the dispatcher — the dispatcher is
            // what calls Record, so it must be quiesced first to avoid a
            // last-millisecond write into a closed connection pool.
            try { _runHistory?.Dispose(); } catch { /* swallow on shutdown */ }
            // OutputSink owns a Timer that drives the coalesced UI
            // writes. Stop the timer + final-drain pending output here
            // so any last-millisecond writes still surface.
            try { (Sink as IDisposable)?.Dispose(); } catch { /* swallow on shutdown */ }
        }

        // ===================================================================
        // File menu
        // ===================================================================

        private void menu_File_New_Click(object sender, EventArgs e)
        {
            // SaveFileDialog rather than InputBox: workspaces are files,
            // and this nudges the user into picking a sensible location
            // up front so relative scriptPath references resolve cleanly.
            using (var dlg = new SaveFileDialog
            {
                Title = "New Workspace",
                Filter = "ScriptDeck workspace (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "json",
                FileName = "workspace.json",
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var ws = new Workspace.Workspace
                {
                    Name = Path.GetFileNameWithoutExtension(dlg.FileName),
                    Tabs = new List<Tab>
                    {
                        // A single empty tab so the new workspace renders
                        // SOMETHING — staring at blank chrome after "New"
                        // would be confusing.
                        new Tab { Id = "tab1", Title = "Tab 1", Buttons = new List<WsButton>() }
                    },
                };

                try
                {
                    WorkspaceLoader.Save(ws, dlg.FileName);
                    LoadWorkspace(dlg.FileName, skipTrustPrompt: true); // user just created it
                }
                catch (Exception ex)
                {
                    Sink.WriteError($"Failed to create workspace: {ex.Message}{Environment.NewLine}");
                }
            }
        }

        private void menu_File_Open_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Open Workspace",
                Filter = "ScriptDeck workspace (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                LoadWorkspace(dlg.FileName, skipTrustPrompt: false);
            }
        }

        private void menu_File_Save_Click(object sender, EventArgs e)
        {
            if (_activeWorkspace == null || string.IsNullOrEmpty(_activeWorkspacePath))
            {
                Sink.WriteWarning("No workspace is open." + Environment.NewLine);
                return;
            }
            try
            {
                WorkspaceLoader.Save(_activeWorkspace, _activeWorkspacePath);
                ClearDirty();
                Sink.Log($"Saved workspace: {Path.GetFileName(_activeWorkspacePath)}");
            }
            catch (Exception ex)
            {
                Sink.WriteError($"Save failed: {ex.Message}{Environment.NewLine}");
            }
        }

        private void menu_File_Exit_Click(object sender, EventArgs e) => Close();

        private void menu_Edit_ToggleMode_Click(object sender, EventArgs e)
        {
            // Refuse to flip into edit mode if no workspace is loaded —
            // there's nothing to edit, and the renderer would render the
            // empty/Welcome state with no tabs to add buttons to.
            if (_activeWorkspace == null && !_renderer.IsEditMode)
            {
                Sink.WriteWarning("Open or create a workspace first." + Environment.NewLine);
                return;
            }
            bool nowEditing = !_renderer.IsEditMode;
            _renderer.SetEditMode(nowEditing);
            menu_Edit_ToggleMode.Checked = nowEditing;
            UpdateStatusBar();
            Sink.Log(nowEditing ? "Entered Edit mode." : "Returned to Run mode.");
            if (nowEditing)
                Sink.WriteInfo("Edit mode: left-click a button to edit it; right-click for delete/move. Save with Ctrl+S." + Environment.NewLine);
        }

        private void menu_Edit_SharedInputs_Click(object sender, EventArgs e)
        {
            if (_activeWorkspace == null)
            {
                Sink.WriteWarning("Open or create a workspace first." + Environment.NewLine);
                return;
            }
            using (var dlg = new EditSharedInputsDialog(_activeWorkspace.SharedInputs))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _activeWorkspace.SharedInputs = dlg.GetEditedList();
                _renderer.Render(_activeWorkspace);
                MarkDirty();
                // Mirror static changes into the inputs grid -- otherwise
                // the grid still shows the pre-edit Static row set until
                // the next session-input mutation.
                RefreshInputsGrid();
                Sink.Log($"Updated shared inputs ({_activeWorkspace.SharedInputs.Count}).");
            }
        }

        private void menu_Edit_CancelRunning_Click(object sender, EventArgs e)
        {
            if (_dispatcher == null || !_dispatcher.IsBusy) return;
            Sink.Log($"Cancel requested for: {_dispatcher.ActiveLabel}");
            _dispatcher.CancelActive();
        }

        // ===================================================================
        // Session inputs (Volatile)
        // ===================================================================

        // Called by PowerShellExecutor when a script invokes
        // Set-SharedInput. Runs on the executor's background thread --
        // marshal to the UI thread before mutating the dict so observers
        // (e.g. the Inputs grid) can refresh without InvokeRequired
        // checks of their own.
        private void OnSessionInputSetRequested(string id, string value, string label)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action<string, string, string>)OnSessionInputSetRequested, id, value, label);
                return;
            }
            if (string.IsNullOrEmpty(id)) return;

            // No-duplicate rule: refuse a session input that shadows a
            // static workspace input. The bootstrap helper itself
            // performs a client-side check using $ScriptDeckInputs, so
            // a well-behaved script never hits this path. Belt-and-
            // suspenders for ad-hoc / pre-existing scripts that don't
            // know about the helper's check.
            if (_activeWorkspace?.SharedInputs != null
                && _activeWorkspace.SharedInputs.Any(s =>
                    string.Equals(s?.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                Sink?.WriteWarning(
                    $"Refused to set session input '{id}': a Static workspace input with that id exists.{Environment.NewLine}");
                return;
            }

            _sessionInputs[id] = new SessionInput
            {
                Id = id,
                Value = value ?? string.Empty,
                Label = label,
            };
            SessionInputsChanged?.Invoke();
        }

        private void OnSessionInputRemoveRequested(string id)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)OnSessionInputRemoveRequested, id); return; }
            if (string.IsNullOrEmpty(id)) return;
            if (_sessionInputs.Remove(id))
            {
                SessionInputsChanged?.Invoke();
            }
            // No error if id wasn't in the volatile set -- bootstrap
            // helper's contract is "silent no-op for non-existent".
            // Static ids never get here because the bootstrap helper
            // refuses to emit a remove tag for them.
        }

        // Clear all volatile inputs. Called by lifecycle hooks (workspace
        // open / close / switch) and the user's grid action "Clear All
        // Volatile". Idempotent.
        private void ClearSessionInputs()
        {
            if (_sessionInputs.Count == 0) return;
            _sessionInputs.Clear();
            SessionInputsChanged?.Invoke();
        }

        // ---- Inputs grid (bottom-right) ----

        // Rebuild the grid from current workspace + session state. Cheap
        // (a few dozen rows worst case), so we re-issue a full LoadData
        // rather than diffing -- avoids drift between the visible rows
        // and the underlying model.
        private void RefreshInputsGrid()
        {
            if (inputsGridPanel == null) return;
            var rows = new List<InputsGridPanel.InputRow>();

            // Static rows: the workspace-defined inputs. We surface the
            // CURRENT runtime textbox value (not the Default) so the grid
            // doesn't lie when the user has typed something into the top
            // bar. The renderer is the source of truth for runtime values.
            if (_activeWorkspace?.SharedInputs != null)
            {
                IDictionary<string, string> liveValues = null;
                try { liveValues = _renderer?.GetSharedInputValues(); }
                catch { liveValues = null; }  // renderer may not be initialized yet on first paint

                foreach (var si in _activeWorkspace.SharedInputs)
                {
                    if (si == null || string.IsNullOrEmpty(si.Id)) continue;
                    string value = null;
                    if (liveValues != null && liveValues.TryGetValue(si.Id, out var v)) value = v;
                    if (value == null) value = si.Default ?? string.Empty;
                    rows.Add(new InputsGridPanel.InputRow
                    {
                        Id = si.Id,
                        Value = value,
                        Scope = InputsGridPanel.ScopeStatic,
                    });
                }
            }

            // Volatile rows: session-only. The no-duplicate rule means
            // these ids are guaranteed not to clash with the Static set
            // above, but a belt-and-suspenders skip is still cheap.
            var staticIds = new HashSet<string>(
                rows.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var v in _sessionInputs.Values)
            {
                if (v == null || string.IsNullOrEmpty(v.Id)) continue;
                if (staticIds.Contains(v.Id)) continue;
                rows.Add(new InputsGridPanel.InputRow
                {
                    Id = v.Id,
                    Value = v.Value ?? string.Empty,
                    Scope = InputsGridPanel.ScopeVolatile,
                });
            }

            inputsGridPanel.LoadData(rows);
        }

        // Build the existing-id set passed to AddInputDialog so it can
        // refuse a duplicate name. Union of Static + Volatile -- the
        // no-duplicate rule cuts across both scopes.
        private ISet<string> CollectAllInputIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_activeWorkspace?.SharedInputs != null)
            {
                foreach (var si in _activeWorkspace.SharedInputs)
                    if (si != null && !string.IsNullOrEmpty(si.Id)) ids.Add(si.Id);
            }
            foreach (var v in _sessionInputs.Values)
                if (v != null && !string.IsNullOrEmpty(v.Id)) ids.Add(v.Id);
            return ids;
        }

        private void OnInputsGrid_AddStatic(object sender, EventArgs e)
        {
            // Static inputs live in the workspace JSON, so we need a
            // loaded workspace to attach to. Without one, refuse with a
            // visible hint rather than silently doing nothing.
            if (_activeWorkspace == null)
            {
                MessageBox.Show(this,
                    "Open a workspace first. Static inputs are stored in the workspace file.",
                    "No workspace loaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new AddInputDialog(InputsGridPanel.ScopeStatic, CollectAllInputIds()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                _activeWorkspace.SharedInputs.Add(new SharedInput
                {
                    Id = dlg.EnteredName,
                    Label = dlg.EnteredLabel ?? dlg.EnteredName,
                    Type = "text",
                    Default = dlg.EnteredValue ?? string.Empty,
                });
                MarkDirty();
                // Re-render the top input band so the new textbox appears
                // immediately; then refresh the grid to mirror.
                try { _renderer?.Render(_activeWorkspace); }
                catch (Exception ex)
                {
                    Sink.WriteError($"Render failed after adding input: {ex.Message}{Environment.NewLine}");
                }
                RefreshInputsGrid();
            }
        }

        private void OnInputsGrid_AddVolatile(object sender, EventArgs e)
        {
            // Volatile inputs are session-only -- no workspace gating.
            using (var dlg = new AddInputDialog(InputsGridPanel.ScopeVolatile, CollectAllInputIds()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _sessionInputs[dlg.EnteredName] = new SessionInput
                {
                    Id = dlg.EnteredName,
                    Value = dlg.EnteredValue ?? string.Empty,
                    Label = dlg.EnteredLabel,
                };
                SessionInputsChanged?.Invoke();
            }
        }

        private void OnInputsGrid_Remove(object sender, string id)
        {
            // The grid's context menu disables Remove on Static rows, so
            // this only fires for Volatile. Defense in depth: confirm
            // before mutating the dict.
            if (string.IsNullOrEmpty(id)) return;
            if (_sessionInputs.Remove(id))
            {
                SessionInputsChanged?.Invoke();
            }
        }

        private void OnInputsGrid_ClearVolatile(object sender, EventArgs e)
        {
            if (_sessionInputs.Count == 0) return;
            var answer = MessageBox.Show(this,
                $"Remove all {_sessionInputs.Count} session (Volatile) input(s)? Static workspace inputs are unaffected.",
                "Clear Volatile Inputs", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            ClearSessionInputs();
        }

        private void OnInputsGrid_VolatileValueEdited(object sender,
            InputsGridPanel.VolatileValueEditedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.Id)) return;
            if (!_sessionInputs.TryGetValue(e.Id, out var existing))
            {
                // Edit hit a row we no longer have (shouldn't happen
                // unless the grid is stale). Promote to an Add rather
                // than silently dropping the user's change.
                _sessionInputs[e.Id] = new SessionInput
                {
                    Id = e.Id,
                    Value = e.Value ?? string.Empty,
                    Label = null,
                };
            }
            else
            {
                existing.Value = e.Value ?? string.Empty;
            }
            // Do NOT fire SessionInputsChanged here -- the edit came
            // FROM the grid, and re-pushing LoadData mid-commit risks
            // disposing the active editor. Other observers (none today)
            // would need their own hook if this list grows.
        }

        // ===================================================================
        // Background jobs
        // ===================================================================

        // All jobs we've seen this session, oldest first. The Jobs grid
        // mirrors this list directly. Disposal of a job (cancel /
        // dismiss) removes from here AND from the grid.
        private readonly List<Job> _jobs = new List<Job>();

        private void OnJobAdded(Job job)
        {
            // Marshal to UI thread -- the queue raises events from
            // its own worker / submitter threads.
            if (InvokeRequired) { BeginInvoke((Action<Job>)OnJobAdded, job); return; }

            // Wire each job's per-entry event so we can live-tail the
            // job's RTB when it's the currently-selected one.
            if (job.Sink != null)
                job.Sink.EntryAppended += entry => OnJobEntryAppended(job, entry);

            _jobs.Add(job);
            AppendJobRow(job);
            UpdateJobsTabHeader();
        }

        private void OnJobStatusChanged(Job job)
        {
            if (InvokeRequired) { BeginInvoke((Action<Job>)OnJobStatusChanged, job); return; }
            UpdateJobRow(job);
            UpdateJobsTabHeader();
            // If the user has THIS job selected, refresh the output
            // pane (status banner, exit code, etc.). Output already
            // streams via OnJobEntryAppended; this just updates the
            // metadata footer line we may add later. For now no-op.
        }

        private void OnJobEntryAppended(Job job, BufferedSink.Entry entry)
        {
            if (InvokeRequired) { BeginInvoke((Action<Job, BufferedSink.Entry>)OnJobEntryAppended, job, entry); return; }
            // Live-tail only when the user is looking at this job's row.
            var selected = GetSelectedJob();
            if (!ReferenceEquals(selected, job)) return;
            AppendEntryToJobOutput(entry);
        }

        // The Jobs grid is constructed lazily on first use so we don't
        // pay the column-setup cost when no background buttons exist.
        private bool _jobsGridReady;
        private void EnsureJobsGrid()
        {
            if (_jobsGridReady) return;
            _jobsGridReady = true;

            var g = dataGridView_Jobs;
            g.Columns.Clear();
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status",  HeaderText = "Status",  Width = 90  });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Button",  HeaderText = "Button",  Width = 220 });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Started", HeaderText = "Started", Width = 80  });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Elapsed", HeaderText = "Elapsed", Width = 80  });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Exit",    HeaderText = "Exit",    Width = 60  });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Error",   HeaderText = "Error",   Width = 240 });
        }

        private void AppendJobRow(Job job)
        {
            EnsureJobsGrid();
            int idx = dataGridView_Jobs.Rows.Add(
                FormatJobStatus(job),
                job.ButtonLabel ?? "(unnamed)",
                job.StartedAtUtc?.ToLocalTime().ToString("HH:mm:ss") ?? string.Empty,
                FormatElapsed(job.Elapsed),
                job.ExitCode?.ToString() ?? string.Empty,
                job.ErrorMessage ?? string.Empty);
            dataGridView_Jobs.Rows[idx].Tag = job; // for selection lookup + dismiss
        }

        private void UpdateJobRow(Job job)
        {
            for (int i = 0; i < dataGridView_Jobs.Rows.Count; i++)
            {
                if (ReferenceEquals(dataGridView_Jobs.Rows[i].Tag, job))
                {
                    var cells = dataGridView_Jobs.Rows[i].Cells;
                    cells["Status"].Value  = FormatJobStatus(job);
                    cells["Started"].Value = job.StartedAtUtc?.ToLocalTime().ToString("HH:mm:ss") ?? string.Empty;
                    cells["Elapsed"].Value = FormatElapsed(job.Elapsed);
                    cells["Exit"].Value    = job.ExitCode?.ToString() ?? string.Empty;
                    cells["Error"].Value   = job.ErrorMessage ?? string.Empty;
                    return;
                }
            }
        }

        private void UpdateJobsTabHeader()
        {
            int running = 0, queued = 0;
            foreach (var j in _jobs)
            {
                if (j.Status == Job.JobStatus.Running) running++;
                else if (j.Status == Job.JobStatus.Queued) queued++;
            }
            tabPage_Jobs.Text = (running + queued) > 0
                ? $"Jobs ({running} running, {queued} queued, {_jobs.Count} total)"
                : (_jobs.Count > 0 ? $"Jobs ({_jobs.Count})" : "Jobs");
        }

        private static string FormatJobStatus(Job j)
        {
            switch (j.Status)
            {
                case Job.JobStatus.Queued:    return "Queued";
                case Job.JobStatus.Running:   return "Running...";
                case Job.JobStatus.Completed: return "Done";
                case Job.JobStatus.Failed:    return "Failed";
                case Job.JobStatus.Cancelled: return "Cancelled";
                default: return j.Status.ToString();
            }
        }

        private static string FormatElapsed(TimeSpan ts)
        {
            if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0} ms";
            if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:F1} s";
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        }

        private Job GetSelectedJob()
        {
            var rows = dataGridView_Jobs.SelectedRows;
            if (rows.Count == 0) return null;
            return rows[0].Tag as Job;
        }

        private void dataGridView_Jobs_SelectionChanged(object sender, EventArgs e)
        {
            // Replace the contents of the job-output RTB with the
            // currently-selected job's full buffered output. Subsequent
            // entries appended to that job will live-tail via
            // OnJobEntryAppended above.
            richTextBox_JobOutput.Clear();
            var job = GetSelectedJob();
            if (job?.Sink == null) return;
            foreach (var entry in job.Sink.SnapshotEntries())
            {
                AppendEntryToJobOutput(entry);
            }
        }

        // Map BufferedSink severity to colors that mirror OutputSink's
        // foreground console palette. Keeps the visual contract
        // consistent regardless of which RTB the output lands in.
        private void AppendEntryToJobOutput(BufferedSink.Entry entry)
        {
            var rtb = richTextBox_JobOutput;
            System.Drawing.Color fg;
            switch (entry.Severity)
            {
                case BufferedSink.Severity.Error:   fg = System.Drawing.Color.Salmon;     break;
                case BufferedSink.Severity.Warning: fg = System.Drawing.Color.Gold;       break;
                case BufferedSink.Severity.Info:    fg = System.Drawing.Color.DeepSkyBlue;break;
                case BufferedSink.Severity.Verbose: fg = System.Drawing.Color.MediumPurple; break;
                case BufferedSink.Severity.Debug:   fg = System.Drawing.Color.DarkGray;   break;
                case BufferedSink.Severity.Log:     fg = System.Drawing.Color.LightGreen; break;
                default:                            fg = rtb.ForeColor;                   break;
            }
            int start = rtb.TextLength;
            rtb.AppendText(entry.Text);
            rtb.Select(start, entry.Text.Length);
            rtb.SelectionColor = fg;
            rtb.Select(rtb.TextLength, 0);
            rtb.ScrollToCaret();
        }

        private void button_JobCancel_Click(object sender, EventArgs e)
        {
            var job = GetSelectedJob();
            if (job == null) return;
            if (job.Status == Job.JobStatus.Running || job.Status == Job.JobStatus.Queued)
            {
                _dispatcher.BackgroundQueue?.Cancel(job);
            }
        }

        private void button_JobSendToConsole_Click(object sender, EventArgs e)
        {
            // Replay a finished (or in-progress) job's buffered output
            // onto the main console + grid sink. Useful when you want
            // to keep a record of a background result alongside the
            // foreground panel.
            var job = GetSelectedJob();
            if (job?.Sink == null) return;
            Sink.Log("---- Replaying job output: " + (job.ButtonLabel ?? "(unnamed)") + " ----");
            foreach (var entry in job.Sink.SnapshotEntries())
            {
                switch (entry.Severity)
                {
                    case BufferedSink.Severity.Error:   Sink.WriteError(entry.Text);   break;
                    case BufferedSink.Severity.Warning: Sink.WriteWarning(entry.Text); break;
                    case BufferedSink.Severity.Info:    Sink.WriteInfo(entry.Text);    break;
                    case BufferedSink.Severity.Verbose: Sink.WriteVerbose(entry.Text); break;
                    case BufferedSink.Severity.Debug:   Sink.WriteDebug(entry.Text);   break;
                    case BufferedSink.Severity.Log:     Sink.WriteOutput(entry.Text);  break; // raw, already stamped
                    default:                            Sink.WriteOutput(entry.Text);  break;
                }
            }
            // Also forward grid columns/rows if the job emitted any.
            var cols = job.Sink.SnapshotGridColumns();
            if (cols != null && cols.Count > 0)
            {
                Sink.SetColumns(cols);
                foreach (var row in job.Sink.SnapshotGridRows()) Sink.AppendRow(row);
            }
        }

        private void button_JobDismiss_Click(object sender, EventArgs e)
        {
            var job = GetSelectedJob();
            if (job == null) return;
            // Refuse to dismiss a job that's still active -- forces a
            // deliberate Cancel first. Prevents accidental "lost
            // forever" footguns when the user is impatient.
            if (job.Status == Job.JobStatus.Queued || job.Status == Job.JobStatus.Running)
            {
                MessageBox.Show(this,
                    "Job is still active. Cancel it first before dismissing.",
                    "Job in progress",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            for (int i = 0; i < dataGridView_Jobs.Rows.Count; i++)
            {
                if (ReferenceEquals(dataGridView_Jobs.Rows[i].Tag, job))
                {
                    dataGridView_Jobs.Rows.RemoveAt(i);
                    break;
                }
            }
            _jobs.Remove(job);
            richTextBox_JobOutput.Clear();
            UpdateJobsTabHeader();
        }

        // ===================================================================
        // Toolbar (search + view toggles)
        // ===================================================================

        // Suppresses the recursive CheckedChanged that fires when the
        // handler programmatically re-checks a box to enforce the
        // "at least one panel visible" invariant.
        private bool _suppressViewToggle;

        private void textBox_Search_KeyDown(object sender, KeyEventArgs e)
        {
            // Enter triggers Find; Esc clears. Keeps the workflow
            // keyboard-first for users running scripts back to back.
            if (e.KeyCode == Keys.Enter)
            {
                button_FindNext_Click(sender, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                button_ClearFind_Click(sender, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void button_FindNext_Click(object sender, EventArgs e)
        {
            string term = textBox_Search.Text;
            if (string.IsNullOrEmpty(term)) return;

            // Always start from a clean slate: previous highlights from a
            // different term would otherwise pile up. Cheaper than a
            // diffing algorithm and more predictable for the user.
            ClearFindHighlights();
            HighlightInConsole(term);
            HighlightInGrid(term);
        }

        private void button_ClearFind_Click(object sender, EventArgs e)
        {
            ClearFindHighlights();
        }

        // ----- Console + grid quick actions ---------------------------------
        //
        // The four toolbar glyph buttons and the matching context-menu
        // entries on the RTB / grid all route here so a single handler
        // owns the operation. Each is intentionally permissive about
        // empty state: clicking Clear/Export on an empty console or grid
        // is a no-op (with a brief log line) rather than an error -- the
        // user shouldn't have to think about whether there's content
        // before clicking.

        private void button_ClearConsole_Click(object sender, EventArgs e)
        {
            if (richTextBox_Console == null || richTextBox_Console.TextLength == 0)
                return;
            // Wipe text AND any find-highlight state -- otherwise the
            // next typed find would search a zero-length doc and the
            // user might see stale "no matches" feedback.
            ClearFindHighlights();
            richTextBox_Console.Clear();
        }

        private void button_ExportConsole_Click(object sender, EventArgs e)
        {
            if (richTextBox_Console == null || richTextBox_Console.TextLength == 0)
            {
                MessageBox.Show(this,
                    "There is no console content to export.",
                    "Nothing to export",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Title    = "Export console text",
                // .txt = SaveFile with PlainText (strips RTF/colors).
                // .rtf = SaveFile with RichText (preserves green-on-black).
                Filter   = "Plain text (*.txt)|*.txt|Rich text with formatting (*.rtf)|*.rtf",
                FileName = "ScriptDeck-console-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt",
                AddExtension     = true,
                OverwritePrompt  = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                // FilterIndex is 1-based: 1=txt, 2=rtf. We honor the
                // user's choice rather than guessing from the typed
                // extension -- they may have edited the FileName.
                var fileType = dlg.FilterIndex == 2
                    ? RichTextBoxStreamType.RichText
                    : RichTextBoxStreamType.PlainText;
                try
                {
                    richTextBox_Console.SaveFile(dlg.FileName, fileType);
                    Sink.Log($"Console exported: {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Failed to write the console export:\n\n" + ex.Message,
                        "Export failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void button_ExportGridCsv_Click(object sender, EventArgs e)
        {
            var grid = dataGridView_Results;
            if (grid == null || grid.Columns.Count == 0 || grid.Rows.Count == 0)
            {
                MessageBox.Show(this,
                    "There are no grid results to export.",
                    "Nothing to export",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Title    = "Export grid to CSV",
                Filter   = "CSV (Comma-delimited) (*.csv)|*.csv",
                FileName = "ScriptDeck-grid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv",
                AddExtension    = true,
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    WriteGridToCsv(grid, dlg.FileName);
                    Sink.Log($"Grid exported: {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Failed to write the CSV:\n\n" + ex.Message,
                        "Export failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void button_GridPopout_Click(object sender, EventArgs e)
        {
            var grid = dataGridView_Results;
            if (grid == null || grid.Columns.Count == 0 || grid.Rows.Count == 0)
            {
                MessageBox.Show(this,
                    "The grid is empty -- run a script that emits structured output first.",
                    "Nothing to show",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Snapshot the current shape + data and hand it to a new
            // modeless window. The popout is independent: clearing or
            // re-filling the main grid doesn't change the snapshot.
            // Modeless so the user can compare side-by-side or run
            // another script while keeping the previous result visible.
            var popout = new GridOutForm(grid);
            popout.Show(this);
        }

        // RFC 4180-ish CSV writer. UTF-8 with BOM so Excel auto-detects
        // the encoding instead of mojibake-ing high-unicode cells. Each
        // cell quoted only if it contains a comma, quote, or newline --
        // that keeps the output diffable for trivial grids while staying
        // safe for the messy ones.
        private static void WriteGridToCsv(DataGridView grid, string path)
        {
            var utf8WithBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            using (var sw = new StreamWriter(path, append: false, encoding: utf8WithBom))
            {
                // Header row: visible column order, skipping hidden columns
                // so the CSV matches what the user sees on screen.
                var cols = grid.Columns.Cast<DataGridViewColumn>()
                    .Where(c => c.Visible)
                    .OrderBy(c => c.DisplayIndex)
                    .ToList();

                sw.WriteLine(string.Join(",", cols.Select(c => CsvEscape(c.HeaderText ?? c.Name ?? string.Empty))));

                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (row.IsNewRow) continue; // the "*" placeholder row at the bottom
                    var values = cols.Select(c =>
                    {
                        var cell = row.Cells[c.Index];
                        var v = cell?.Value;
                        if (v == null || v == DBNull.Value) return string.Empty;
                        return v.ToString();
                    });
                    sw.WriteLine(string.Join(",", values.Select(CsvEscape)));
                }
            }
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            bool needsQuotes = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!needsQuotes) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private void HighlightInConsole(string term)
        {
            // RichTextBox.Find returns the start index of a match or -1.
            // We loop until exhausted, painting each hit with a yellow
            // background. First hit also drives ScrollToCaret so the
            // user sees something happen even on a scrolled-off match.
            var rtb = richTextBox_Console;
            if (rtb.TextLength == 0) return;

            int start = 0;
            int firstMatch = -1;
            while (start < rtb.TextLength)
            {
                int found = rtb.Find(term, start, RichTextBoxFinds.None);
                if (found < 0) break;
                if (firstMatch < 0) firstMatch = found;
                rtb.Select(found, term.Length);
                rtb.SelectionBackColor = System.Drawing.Color.Yellow;
                rtb.SelectionColor = System.Drawing.Color.Black;
                start = found + term.Length;
            }
            if (firstMatch >= 0)
            {
                rtb.Select(firstMatch, term.Length);
                rtb.ScrollToCaret();
            }
        }

        private void HighlightInGrid(string term)
        {
            var grid = dataGridView_Results;
            if (grid.Rows.Count == 0) return;

            int firstRow = -1;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                foreach (DataGridViewCell cell in row.Cells)
                {
                    string s = cell.Value?.ToString();
                    if (!string.IsNullOrEmpty(s)
                        && s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        cell.Style.BackColor = System.Drawing.Color.Yellow;
                        cell.Style.ForeColor = System.Drawing.Color.Black;
                        if (firstRow < 0) firstRow = row.Index;
                    }
                }
            }
            if (firstRow >= 0)
            {
                grid.FirstDisplayedScrollingRowIndex = firstRow;
                grid.ClearSelection();
                grid.Rows[firstRow].Selected = true;
            }
        }

        private void ClearFindHighlights()
        {
            // Console: SelectAll + reset BackColor. Restore selection
            // afterwards so the user's caret position survives.
            var rtb = richTextBox_Console;
            int caret = rtb.SelectionStart;
            int len = rtb.SelectionLength;
            rtb.SelectAll();
            rtb.SelectionBackColor = rtb.BackColor;
            rtb.SelectionColor = rtb.ForeColor;
            rtb.Select(caret, len);

            // Grid: reset every cell's BackColor / ForeColor. Setting
            // Color.Empty makes the cell inherit the column / grid
            // defaults, which is the right "no highlight" state.
            var grid = dataGridView_Results;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                foreach (DataGridViewCell cell in row.Cells)
                {
                    cell.Style.BackColor = System.Drawing.Color.Empty;
                    cell.Style.ForeColor = System.Drawing.Color.Empty;
                }
            }
        }

        private void checkBox_ShowConsole_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressViewToggle) return;

            // At least one panel must stay visible.
            if (!checkBox_ShowConsole.Checked && !checkBox_ShowGrid.Checked)
            {
                _suppressViewToggle = true;
                try { checkBox_ShowConsole.Checked = true; }
                finally { _suppressViewToggle = false; }
                return;
            }
            // SplitContainer.Panel1Collapsed makes the other panel claim
            // the entire splitter area — exactly the "expand to fill"
            // behavior we want, with no manual size juggling.
            splitContainer_Output.Panel1Collapsed = !checkBox_ShowConsole.Checked;
        }

        private void checkBox_ShowGrid_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressViewToggle) return;
            if (!checkBox_ShowConsole.Checked && !checkBox_ShowGrid.Checked)
            {
                _suppressViewToggle = true;
                try { checkBox_ShowGrid.Checked = true; }
                finally { _suppressViewToggle = false; }
                return;
            }
            splitContainer_Output.Panel2Collapsed = !checkBox_ShowGrid.Checked;
        }

        // ===================================================================
        // Tools menu
        // ===================================================================

        private void menu_Tools_ScriptEditor_Click(object sender, EventArgs e)
        {
            // Modeless-feeling but technically modal: ShowDialog parents
            // it to Shell so it floats over the main window. The editor
            // shares Shell's dispatcher so test-runs go through the same
            // long-lived runspace, and it observes BusyChanged the same
            // way the main UI does.
            if (_dispatcher == null) return;
            string scriptsRoot = _activeWorkspace?.ScriptsRoot
                ?? (string.IsNullOrEmpty(_activeWorkspacePath) ? null : Path.GetDirectoryName(_activeWorkspacePath));
            using (var dlg = new ScriptEditorDialog(_dispatcher, scriptsRoot, initialPath: null,
                                                    sharedInputs: BuildSharedInputSnapshot()))
            {
                dlg.ShowDialog(this);
            }
        }

        private void menu_Tools_ManageComputers_Click(object sender, EventArgs e)
        {
            // Modal dialog over the Shell. ComputerListStore.Save fires
            // its Changed event from within the dialog's Save handler,
            // which the WorkspaceRenderer subscribes to -- so any
            // computerName combo boxes refresh in place by the time the
            // dialog returns. No explicit re-render needed here.
            if (_computerList == null) return;
            using (var dlg = new ManageComputersDialog(_computerList))
            {
                dlg.ShowDialog(this);
            }
        }

        private void menu_Tools_EditBootstrap_Click(object sender, EventArgs e)
        {
            // The bootstrap is the .ps1 file dot-sourced into every fresh
            // runspace; its path is the same one PowerShellExecutor uses
            // (AppContext.BaseDirectory) so editing here is editing the
            // exact file the executors load.
            //
            // Two caveats to surface up-front:
            //
            //   1. The file is overwritten by every build (Copy to output)
            //      -- edits made here disappear on the next rebuild from
            //      source. For permanent changes, edit the file under
            //      src/ScriptDeck/ in the source tree.
            //
            //   2. If the install is in a write-protected location
            //      (Program Files), the editor's save will fail. The user
            //      sees the OS error directly; we don't try to elevate.
            if (_dispatcher == null) return;
            string bootstrapPath = Path.Combine(AppContext.BaseDirectory, "ScriptDeck.Bootstrap.ps1");
            if (!File.Exists(bootstrapPath))
            {
                MessageBox.Show(this,
                    "ScriptDeck.Bootstrap.ps1 was not found at:\n\n" + bootstrapPath +
                    "\n\nThe bootstrap file ships next to ScriptDeck.exe. " +
                    "If it's missing, helpers like Write-Rtb, Write-Grid, and " +
                    "Set-SharedInput won't be available.",
                    "Bootstrap helper not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // mtime captured BEFORE opening so we can detect any save
            // performed inside the editor (including hand-edits made
            // outside the app, if the user has the file open elsewhere).
            // FileInfo.LastWriteTimeUtc avoids DST/local-time foot-guns.
            var fi = new FileInfo(bootstrapPath);
            DateTime mtimeBefore = fi.LastWriteTimeUtc;

            // Heads-up about the edits-lost-on-rebuild caveat. Shown
            // once per click; users who don't like the modal can ignore
            // it -- it's purely informational. (We don't suppress it
            // with "don't show again" because the warning is short and
            // the consequence -- silently losing your edits -- is real
            // enough to warrant the reminder.)
            //
            // Skipped only when the running EXE is clearly the source
            // build (Debug or Release output folder under the repo),
            // since that's the case where the build will indeed clobber.
            // For installed builds the file is the install copy, no
            // build will overwrite it, so the warning is irrelevant.
            string baseDirLower = AppContext.BaseDirectory.ToLowerInvariant().Replace('\\', '/');
            bool looksLikeBuildOutput =
                baseDirLower.Contains("/bin/debug/") || baseDirLower.Contains("/bin/release/");
            if (looksLikeBuildOutput)
            {
                MessageBox.Show(this,
                    "Heads up: you're running the Debug/Release build output. The next " +
                    "build will overwrite ScriptDeck.Bootstrap.ps1 with the source-tree " +
                    "copy under src/ScriptDeck/. For permanent edits, modify the source " +
                    "file instead.",
                    "Edits may be overwritten on next build",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            string scriptsRoot = _activeWorkspace?.ScriptsRoot
                ?? (string.IsNullOrEmpty(_activeWorkspacePath) ? null : Path.GetDirectoryName(_activeWorkspacePath));
            using (var dlg = new ScriptEditorDialog(_dispatcher, scriptsRoot, initialPath: bootstrapPath,
                                                    sharedInputs: BuildSharedInputSnapshot()))
            {
                dlg.Text = "Edit Bootstrap Helper -- " + Path.GetFileName(bootstrapPath);
                dlg.ShowDialog(this);
            }

            // Detect whether the bootstrap was actually saved during the
            // session. mtime delta is the simplest signal -- works whether
            // the user clicked Save inside the editor, or edited the file
            // externally in another tool while the dialog was open.
            DateTime mtimeAfter;
            try { mtimeAfter = File.GetLastWriteTimeUtc(bootstrapPath); }
            catch { mtimeAfter = mtimeBefore; }

            if (mtimeAfter <= mtimeBefore) return;

            // File changed. Offer to reload the PS runspaces so the
            // edits take effect immediately. Default to Yes -- the
            // user just edited the file, they almost certainly want it
            // applied. No is the safety hatch for "I want to compare
            // before reloading" or "I have a foreground job mid-run
            // that I don't want killed."
            var dr = MessageBox.Show(this,
                "ScriptDeck.Bootstrap.ps1 was modified.\n\n" +
                "Reload the PowerShell session now so the updated helpers " +
                "take effect?\n\n" +
                "(This cancels any script that's currently running.)",
                "Reload PowerShell session?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (dr != DialogResult.Yes) return;

            try
            {
                _dispatcher.ResetExecutors();
                Sink.Log("Reloaded PowerShell session after bootstrap edit.");
                Sink.WriteInfo("Bootstrap helpers reloaded." + Environment.NewLine);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Failed to reset the PowerShell session:\n\n" + ex.Message +
                    "\n\nThe edits are saved to disk; restart ScriptDeck to apply them.",
                    "Reset failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Build a snapshot of every shared input's id, label, current
        /// textbox value, and normalize rule. The Script Editor renders
        /// these in its Test Inputs grid so the user can override values
        /// for a test run without leaving the dialog.
        ///
        /// Returns null when no workspace is loaded — the editor handles
        /// null by showing a "no shared inputs in this workspace" hint.
        /// </summary>
        internal IList<SharedInputSnapshot> BuildSharedInputSnapshot()
        {
            if (_activeWorkspace?.SharedInputs == null || _renderer == null) return null;

            var values = _renderer.GetSharedInputValues();
            var result = new List<SharedInputSnapshot>(_activeWorkspace.SharedInputs.Count);
            foreach (var s in _activeWorkspace.SharedInputs)
            {
                if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                values.TryGetValue(s.Id, out var current);
                result.Add(new SharedInputSnapshot
                {
                    Id = s.Id,
                    Label = s.Label,
                    Value = current ?? s.Default ?? string.Empty,
                    Normalize = s.Normalize,
                });
            }
            return result;
        }

        private void menu_Tools_RecentRuns_Click(object sender, EventArgs e)
        {
            // Modal so the user can't kick off a run while staring at
            // history and wonder why the row count didn't change. The
            // dialog reads from disk on Open and on Refresh; live-update
            // would be neat but isn't worth the plumbing.
            if (_runHistory == null)
            {
                Sink.WriteWarning("Run history is not available." + Environment.NewLine);
                return;
            }
            using (var dlg = new HistoryDialog(_runHistory))
            {
                dlg.ShowDialog(this);
            }
        }

        // Escape → cancel the active run. We can't put Escape in a menu
        // shortcut (WinForms rejects bare Esc), so KeyPreview routes it
        // to us before any focused child eats it. We deliberately do NOT
        // mark the event handled when idle — Esc still closes child
        // dialogs / pickers normally in that case.
        private void Shell_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _dispatcher != null && _dispatcher.IsBusy)
            {
                Sink.Log($"Cancel requested for: {_dispatcher.ActiveLabel}");
                _dispatcher.CancelActive();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ===================================================================
        // Workspace lifecycle
        // ===================================================================

        private void LoadWorkspace(string path, bool skipTrustPrompt)
        {
            // Trust prompt: workspaces are JSON, but the JSON references
            // executable scripts, so opening one is morally equivalent to
            // running them. Belt-and-suspenders: warn before any UI binds
            // a button to a path the user hasn't reviewed. Skipped on
            // freshly-created workspaces (the user just made it; they
            // already trust it).
            if (!skipTrustPrompt && !PromptToTrustWorkspace(path))
            {
                Sink.Log("Workspace open cancelled by user.");
                return;
            }

            Workspace.Workspace ws;
            try
            {
                ws = WorkspaceLoader.Load(path);
            }
            catch (Exception ex)
            {
                Sink.WriteError($"Could not load workspace: {ex.Message}{Environment.NewLine}");
                return;
            }

            // Workspace switch = clean slate for PowerShell state. Wipes
            // any $global: variables, Import-Module imports, or other
            // session-scope detritus from the previous workspace's
            // scripts; reloads the bootstrap so any edits take effect.
            // Skip on first load (no previous workspace) so we don't
            // pay the runspace-recreate cost when there's nothing to
            // reset.
            if (_previousWorkspaceLoaded && _dispatcher != null)
            {
                try { _dispatcher.ResetExecutors(); }
                catch (Exception ex)
                {
                    Sink.WriteWarning($"Runspace reset reported: {ex.Message}{Environment.NewLine}");
                }
            }
            _previousWorkspaceLoaded = true;

            // Volatile session inputs are scoped to a single workspace.
            // Loading a new workspace must NOT carry $authToken etc.
            // from the previous one -- those would silently shadow new
            // workspace inputs with stale values.
            ClearSessionInputs();

            _activeWorkspace = ws;
            _activeWorkspacePath = path;
            try
            {
                _renderer.Render(ws);
            }
            catch (Exception ex)
            {
                Sink.WriteError($"Render failed: {ex.Message}{Environment.NewLine}");
                _renderer.ShowEmpty();
                _activeWorkspace = null;
                _activeWorkspacePath = null;
                ClearWorkspaceMenus();
                // Static rows referenced a workspace that no longer exists --
                // drop them now or the grid will keep showing stale rows.
                RefreshInputsGrid();
                UpdateStatusBar();
                return;
            }

            // Phase 7: render the workspace's top-level menus into the
            // MenuStrip. Failures here are non-fatal — a malformed menu
            // shouldn't block the user from running tab buttons.
            try { RenderWorkspaceMenus(ws); }
            catch (Exception ex)
            {
                Sink.WriteWarning($"Menu render failed: {ex.Message}{Environment.NewLine}");
            }

            // Mark this path as recently-used. Done after a successful
            // render so we never promote a workspace that errored partway
            // through — the MRU should reflect "things I actually opened",
            // not "things I tried to open". Add() is best-effort; it
            // swallows IO errors internally.
            _recentWorkspaces?.Add(path);

            // Fresh-from-disk model is by definition clean. Clears any
            // stale dirty state from a previously-loaded workspace too.
            ClearDirty();

            UpdateStatusBar();
            // ClearSessionInputs already fired SessionInputsChanged which
            // refreshed the grid -- but at that point _activeWorkspace was
            // still the OLD workspace (the swap happens after Clear). Do
            // an explicit refresh here so the grid now shows the NEW
            // workspace's Static rows.
            RefreshInputsGrid();
            Sink.Log($"Loaded workspace: {ws.Name} ({path})");
            // Trailing blank lines give the console a quiet zone before
            // the user's first script-run output lands, so the intro
            // doesn't visually crowd up against the first record.
            // Also fires for menu_File_New (which calls LoadWorkspace
            // after creating + saving the file).
            Sink.WriteInfo(
                $"Workspace '{ws.Name}' loaded. " +
                $"{ws.Tabs.Count} tab(s), " +
                $"{ws.Tabs.Sum(t => t.Buttons?.Count ?? 0)} button(s), " +
                $"{ws.SharedInputs.Count} shared input(s)." +
                Environment.NewLine + Environment.NewLine + Environment.NewLine);
        }

        // ===================================================================
        // Recent Workspaces submenu (Phase 8)
        // ===================================================================

        /// <summary>
        /// Rebuilds the Recent Workspaces submenu every time it opens.
        /// Doing it on DropDownOpening (rather than once at startup) means
        /// the list is always live: paths added by other instances of the
        /// app, or paths that just got deleted on disk, show up correctly
        /// without us having to subscribe to anything.
        ///
        /// Each item shows the file name as the visible label and the full
        /// path as a tooltip — file names alone are ambiguous when users
        /// have several "workspace.json" files in different folders, but
        /// full paths make the menu unreadably wide.
        /// </summary>
        private void menu_File_Recent_DropDownOpening(object sender, EventArgs e)
        {
            menu_File_Recent.DropDownItems.Clear();
            var live = _recentWorkspaces?.GetLive() ?? new List<string>();

            if (live.Count == 0)
            {
                // Disabled placeholder rather than hiding the submenu —
                // an empty submenu signals "feature exists, nothing to
                // show" more clearly than the menu silently vanishing.
                var empty = new ToolStripMenuItem("(no recent workspaces)") { Enabled = false };
                menu_File_Recent.DropDownItems.Add(empty);
                return;
            }

            // Number the first 9 with &1..&9 accelerators so users with
            // a few regulars can pop them open from the keyboard.
            for (int i = 0; i < live.Count; i++)
            {
                string path = live[i];
                string name = Path.GetFileName(path);
                string label = (i < 9)
                    ? $"&{i + 1}  {name}"
                    : $"     {name}";
                var mi = new ToolStripMenuItem(label) { ToolTipText = path };
                // Capture path locally — closing over the loop var would
                // hand every item the last iteration's path.
                string captured = path;
                mi.Click += (s, ev) => LoadWorkspace(captured, skipTrustPrompt: false);
                menu_File_Recent.DropDownItems.Add(mi);
            }

            menu_File_Recent.DropDownItems.Add(new ToolStripSeparator());
            var clear = new ToolStripMenuItem("&Clear Recent");
            clear.Click += (s, ev) =>
            {
                _recentWorkspaces?.Clear();
                Sink.Log("Cleared recent workspaces.");
            };
            menu_File_Recent.DropDownItems.Add(clear);
        }

        private bool PromptToTrustWorkspace(string path)
        {
            var msg =
                "This workspace's buttons run scripts and executables on your machine.\r\n\r\n" +
                $"File: {path}\r\n\r\n" +
                "ScriptDeck only runs them when you click their buttons, but you should " +
                "open workspaces only from sources you trust.\r\n\r\n" +
                "Continue opening this workspace?";
            var dr = MessageBox.Show(this, msg, "Confirm workspace open",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return dr == DialogResult.Yes;
        }

        private void UpdateStatusBar()
        {
            statusLabel_Workspace.Text = _activeWorkspace == null
                ? "No workspace"
                : $"{_activeWorkspace.Name}  \u2014  {_activeWorkspacePath}";
            // Mode reflects edit/run state. Busy state overrides this in
            // UpdateBusyUi. Edit-mode is shown in orange to mirror the
            // button border highlight the renderer applies.
            bool edit = _renderer != null && _renderer.IsEditMode;
            statusLabel_Mode.Text = edit ? "EDIT mode" : "Run mode";
            statusLabel_Mode.ForeColor = edit
                ? System.Drawing.Color.OrangeRed
                : System.Drawing.SystemColors.ControlText;
            // Title format:
            //   "ScriptDeck"                          (no workspace)
            //   "ScriptDeck \u2014 Foo"                    (clean)
            //   "ScriptDeck \u2014 Foo *"                  (dirty)
            //   "ScriptDeck \u2014 Foo * [EDIT]"           (dirty + edit mode)
            // The * is the conventional "unsaved changes" marker. Goes
            // before [EDIT] so it stays visible if the title gets truncated.
            string dirtyMark = _isDirty ? " *" : string.Empty;
            this.Text = _activeWorkspace == null
                ? "ScriptDeck"
                : $"ScriptDeck \u2014 {_activeWorkspace.Name}{dirtyMark}{(edit ? " [EDIT]" : string.Empty)}";
        }

        // ===================================================================
        // Dirty-state tracking + save-on-close
        // ===================================================================

        /// <summary>
        /// Flip the dirty flag on and refresh the title bar. Idempotent
        /// \u2014 calling repeatedly on an already-dirty model is cheap, so
        /// every mutating handler can call it without bookkeeping.
        /// </summary>
        private void MarkDirty()
        {
            if (_isDirty) return;
            _isDirty = true;
            UpdateStatusBar();
        }

        /// <summary>
        /// Reset the dirty flag \u2014 workspace model and file are now in
        /// sync. Called after Save and after Load (a fresh model from
        /// disk is by definition clean).
        /// </summary>
        private void ClearDirty()
        {
            if (!_isDirty) return;
            _isDirty = false;
            UpdateStatusBar();
        }

        private void OnShellFormClosing(object sender, FormClosingEventArgs e)
        {
            // Already-cancelled close (e.g. another handler set Cancel)
            // shouldn't get a second prompt.
            if (e.Cancel) return;
            if (!_isDirty || _activeWorkspace == null) return;

            string name = _activeWorkspace.Name ?? "this workspace";
            var dr = MessageBox.Show(this,
                $"Save changes to '{name}' before closing?",
                "Unsaved changes",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (dr == DialogResult.Cancel)
            {
                // Stay open. User can Save manually or keep editing.
                e.Cancel = true;
                return;
            }
            if (dr == DialogResult.Yes)
            {
                // Save synchronously. If the save fails (locked file,
                // permissions), give the user a chance to back out
                // rather than silently losing their work.
                try
                {
                    if (!string.IsNullOrEmpty(_activeWorkspacePath))
                    {
                        WorkspaceLoader.Save(_activeWorkspace, _activeWorkspacePath);
                        ClearDirty();
                    }
                }
                catch (Exception ex)
                {
                    var dr2 = MessageBox.Show(this,
                        $"Save failed: {ex.Message}\r\n\r\nClose anyway and lose changes?",
                        "Save failed",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button2);
                    if (dr2 != DialogResult.Yes) e.Cancel = true;
                }
            }
            // dr == No: fall through. User chose to discard; close proceeds.
        }

        // ===================================================================
        // Edit mode handlers (renderer events)
        // ===================================================================

        private void OnEditButton(WsButton btn)
        {
            if (btn == null || _activeWorkspace == null) return;
            using (var dlg = new EditButtonDialog(btn, _activeWorkspace.ScriptsRoot, _dispatcher, BuildSharedInputSnapshot()))
            {
                dlg.Text = "Edit Button";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(btn);
                _renderer.Render(_activeWorkspace);
                MarkDirty();
                Sink.Log($"Edited button: {btn.Label}");
            }
        }

        private void OnDeleteButton(WsButton btn)
        {
            if (btn == null || _activeWorkspace == null) return;
            var owner = _activeWorkspace.Tabs.FirstOrDefault(t => t.Buttons != null && t.Buttons.Contains(btn));
            if (owner == null) return;
            var dr = MessageBox.Show(this,
                $"Delete button '{btn.Label}'?",
                "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
            owner.Buttons.Remove(btn);
            _renderer.Render(_activeWorkspace);
            MarkDirty();
            Sink.Log($"Deleted button: {btn.Label}");
        }

        private void OnMoveButton(WsButton btn, int delta)
        {
            if (btn == null || _activeWorkspace == null) return;
            var owner = _activeWorkspace.Tabs.FirstOrDefault(t => t.Buttons != null && t.Buttons.Contains(btn));
            if (owner == null) return;
            int i = owner.Buttons.IndexOf(btn);
            int j = i + delta;
            // Clamp silently — common case is the user mashes Move Up at
            // the top of the list and we shouldn't pop a dialog for it.
            if (j < 0 || j >= owner.Buttons.Count) return;
            owner.Buttons.RemoveAt(i);
            owner.Buttons.Insert(j, btn);
            MarkDirty();
            _renderer.Render(_activeWorkspace);
        }

        private void OnAddButton(Tab tab)
        {
            if (tab == null || _activeWorkspace == null) return;
            // Seed the new button with sensible defaults so the editor's
            // required-field validation isn't blocked the first time the
            // user clicks OK on an empty form.
            var fresh = new WsButton
            {
                Id = string.Empty,
                Label = "New Button",
                Executor = "powershell",
                ScriptPath = string.Empty,
                Args = new List<string>(),
                Outputs = new List<string> { "rtb" },
                Log = true,
                Width = 150,
                Height = 36,
            };
            // Pick a non-overlapping spot on the canvas so successive
            // "Add Button" clicks don't pile every new button at (0,0).
            // The user can drag-reposition immediately after adding;
            // this just ensures each one is reachable.
            var spot = FindFreeSpot(tab, 150, 36);
            fresh.X = spot.X;
            fresh.Y = spot.Y;

            using (var dlg = new EditButtonDialog(fresh, _activeWorkspace.ScriptsRoot, _dispatcher, BuildSharedInputSnapshot()))
            {
                dlg.Text = "New Button";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(fresh);
                if (tab.Buttons == null) tab.Buttons = new List<WsButton>();
                tab.Buttons.Add(fresh);
                _renderer.Render(_activeWorkspace);
                // Re-select the tab the user was editing — Render above
                // rebuilds tabs in order, so the index is stable as long
                // as we look it up by Tag identity rather than the title.
                SelectTabByModel(tab);
                MarkDirty();
                Sink.Log($"Added button: {fresh.Label}");
            }
        }

        // Walk a 4-column grid of slot positions on the tab canvas and
        // return the first one that doesn't overlap any existing
        // canvas-direct button. Buttons that live inside a group don't
        // count — they occupy group-relative coordinates that don't
        // collide with the canvas grid. If every grid slot is taken,
        // fall back to the next row off the bottom.
        private static System.Drawing.Point FindFreeSpot(Tab tab, int w, int h)
        {
            const int padX = 16, padY = 16, cellW = 162, cellH = 44, cols = 4;
            var occupied = new List<System.Drawing.Rectangle>();
            if (tab?.Buttons != null)
            {
                foreach (var b in tab.Buttons)
                {
                    if (b == null) continue;
                    if (!string.IsNullOrEmpty(b.GroupId)) continue; // group-local, ignored
                    int bw = b.Width  > 0 ? b.Width  : 150;
                    int bh = b.Height > 0 ? b.Height : 36;
                    occupied.Add(new System.Drawing.Rectangle(b.X, b.Y, bw, bh));
                }
            }
            for (int row = 0; row < 64; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int x = padX + col * cellW;
                    int y = padY + row * cellH;
                    var candidate = new System.Drawing.Rectangle(x, y, w, h);
                    bool clash = false;
                    foreach (var occ in occupied)
                    {
                        if (occ.IntersectsWith(candidate)) { clash = true; break; }
                    }
                    if (!clash) return new System.Drawing.Point(x, y);
                }
            }
            // 64 rows full — extreme edge case. Drop at bottom-right.
            return new System.Drawing.Point(padX, padY + 64 * cellH);
        }

        // Group-interior version of FindFreeSpot. Coordinates are
        // GroupBox-interior-relative: (0, 0) is the top-left INSIDE
        // the frame, just under the title. (14, 28) is the historical
        // "first button" slot; subsequent columns step right by cellW,
        // then we wrap to the next row.
        //
        // Considers only buttons whose GroupId matches this group --
        // canvas-direct siblings live in a different coordinate space
        // and can't collide here.
        //
        // Falls back to (14, 28) if the search is exhausted. WinForms
        // adds new children to the BACK of z-order (highest index =
        // bottom of the visual stack), so a duplicate-position button
        // would be invisible behind its older siblings. Returning a
        // distinct slot is the actual fix; the fallback only triggers
        // if 64 rows are full, at which point an overlap is acceptable.
        private static System.Drawing.Point FindFreeSpotInGroup(
            Tab tab, ButtonGroup group, int w, int h)
        {
            const int padX = 14, padY = 28, cellW = 162, cellH = 44, cols = 4;
            var occupied = new List<System.Drawing.Rectangle>();
            if (tab?.Buttons != null && group != null)
            {
                foreach (var b in tab.Buttons)
                {
                    if (b == null) continue;
                    if (!string.Equals(b.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int bw = b.Width  > 0 ? b.Width  : 150;
                    int bh = b.Height > 0 ? b.Height : 36;
                    occupied.Add(new System.Drawing.Rectangle(b.X, b.Y, bw, bh));
                }
            }
            for (int row = 0; row < 64; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int x = padX + col * cellW;
                    int y = padY + row * cellH;
                    var candidate = new System.Drawing.Rectangle(x, y, w, h);
                    bool clash = false;
                    foreach (var occ in occupied)
                    {
                        if (occ.IntersectsWith(candidate)) { clash = true; break; }
                    }
                    if (!clash) return new System.Drawing.Point(x, y);
                }
            }
            return new System.Drawing.Point(padX, padY);
        }

        private void OnEditTab(Tab tab)
        {
            if (tab == null || _activeWorkspace == null) return;
            using (var dlg = new EditTabDialog(tab))
            {
                dlg.Text = "Edit Tab";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(tab);
                _renderer.Render(_activeWorkspace);
                SelectTabByModel(tab);
                MarkDirty();
                Sink.Log($"Renamed tab: {tab.Title}");
            }
        }

        private void OnDeleteTab(Tab tab)
        {
            if (tab == null || _activeWorkspace == null) return;
            var dr = MessageBox.Show(this,
                $"Delete tab '{tab.Title}' and its {tab.Buttons?.Count ?? 0} button(s)?",
                "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
            _activeWorkspace.Tabs.Remove(tab);
            _renderer.Render(_activeWorkspace);
            MarkDirty();
            Sink.Log($"Deleted tab: {tab.Title}");
        }

        private void OnAddTab()
        {
            if (_activeWorkspace == null) return;
            var fresh = new Tab { Id = string.Empty, Title = "New Tab", Buttons = new List<WsButton>() };
            using (var dlg = new EditTabDialog(fresh))
            {
                dlg.Text = "New Tab";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(fresh);
                _activeWorkspace.Tabs.Add(fresh);
                _renderer.Render(_activeWorkspace);
                SelectTabByModel(fresh);
                MarkDirty();
                Sink.Log($"Added tab: {fresh.Title}");
            }
        }

        // ---- Group handlers ----

        private void OnAddGroup(Tab tab)
        {
            if (tab == null || _activeWorkspace == null) return;
            string title = PromptForString("Label box title:", "Add Label Box", "Group");
            if (title == null) return;
            if (tab.Groups == null) tab.Groups = new List<ButtonGroup>();
            var g = new ButtonGroup
            {
                Id = NewGroupId(tab),
                Title = title,
                // Drop new groups in the upper-left of the canvas; user
                // drags to wherever it should live. Picking (24, 24)
                // (instead of 0,0) so the group doesn't hide under any
                // existing buttons that auto-positioned to (16,16).
                X = 24,
                Y = 24,
                Width = 240,
                Height = 140,
            };
            tab.Groups.Add(g);
            _renderer.Render(_activeWorkspace);
            SelectTabByModel(tab);
            MarkDirty();
            Sink.Log($"Added label box '{g.Title}' to '{tab.Title}'.");
        }

        private void OnAddButtonToGroup(ButtonGroup group)
        {
            if (group == null || _activeWorkspace == null) return;
            var tab = FindTabContainingGroup(group);
            if (tab == null) return;

            // Same defaults as OnAddButton, plus GroupId so the new
            // button parents into the frame on render. X/Y are
            // group-relative — FindFreeSpotInGroup picks a slot that
            // doesn't collide with buttons already in this frame.
            // (Hardcoding (14, 28) used to bury every new button
            // behind the existing stack: WinForms z-orders later-added
            // siblings to the BACK, so a fresh button at an occupied
            // position was rendered but invisible.)
            var spotG = FindFreeSpotInGroup(tab, group, 150, 36);
            var fresh = new WsButton
            {
                Id = string.Empty,
                Label = "New Button",
                Executor = "powershell",
                ScriptPath = string.Empty,
                Args = new List<string>(),
                Outputs = new List<string> { "rtb" },
                Log = true,
                Width = 150,
                Height = 36,
                GroupId = group.Id,
                X = spotG.X,
                Y = spotG.Y,
            };

            using (var dlg = new EditButtonDialog(fresh, _activeWorkspace.ScriptsRoot, _dispatcher, BuildSharedInputSnapshot()))
            {
                dlg.Text = "New Button (in '" + (group.Title ?? group.Id) + "')";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(fresh);
                // ApplyTo doesn't touch GroupId, but re-affirm
                // defensively so a future change to the dialog can't
                // silently strip the parent group from the new button.
                fresh.GroupId = group.Id;
                if (tab.Buttons == null) tab.Buttons = new List<WsButton>();
                tab.Buttons.Add(fresh);
                _renderer.Render(_activeWorkspace);
                SelectTabByModel(tab);
                MarkDirty();
                Sink.Log($"Added button to '{group.Title ?? group.Id}': {fresh.Label}");
            }
        }

        /// <summary>
        /// Tier 3 of the snap/align toolbox: copy size from one button
        /// to another. The renderer shows a "Match Width / Height / Size
        /// of [other button]" submenu in the right-click menu; this
        /// handler applies the actual change to the model and re-renders.
        /// </summary>
        private void OnMatchSize(WsButton target, WsButton source, string dimension)
        {
            if (target == null || source == null || _activeWorkspace == null) return;

            int srcW = source.Width  > 0 ? source.Width  : 150;
            int srcH = source.Height > 0 ? source.Height : 36;

            bool changed = false;
            if (string.Equals(dimension, "width", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dimension, "both", StringComparison.OrdinalIgnoreCase))
            {
                if (target.Width != srcW) { target.Width = srcW; changed = true; }
            }
            if (string.Equals(dimension, "height", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dimension, "both", StringComparison.OrdinalIgnoreCase))
            {
                if (target.Height != srcH) { target.Height = srcH; changed = true; }
            }
            if (!changed) return;

            _renderer.Render(_activeWorkspace);
            // Keep the user on the same tab they were editing — Render
            // rebuilds tabs in order, so tab Tag identity drives reselection.
            var owner = _activeWorkspace.Tabs.FirstOrDefault(t => t.Buttons != null && t.Buttons.Contains(target));
            if (owner != null) SelectTabByModel(owner);

            MarkDirty();
            string srcLabel = string.IsNullOrEmpty(source.Label) ? (source.Id ?? "(unnamed)") : source.Label;
            string tgtLabel = string.IsNullOrEmpty(target.Label) ? (target.Id ?? "(unnamed)") : target.Label;
            Sink.Log($"Matched {dimension} of '{tgtLabel}' to '{srcLabel}' ({srcW}x{srcH}).");
        }

        // Stable id generator. We don't need crypto-strength uniqueness;
        // monotonic + a short random suffix is enough to avoid collisions
        // across the small number of groups a tab will ever have.
        private static string NewGroupId(Tab tab)
        {
            int n = (tab?.Groups?.Count ?? 0) + 1;
            string baseId;
            do
            {
                baseId = $"grp-{n}";
                n++;
            } while (tab?.Groups != null && tab.Groups.Any(x => string.Equals(x?.Id, baseId, StringComparison.OrdinalIgnoreCase)));
            return baseId;
        }

        private void OnEditGroup(ButtonGroup g)
        {
            if (g == null || _activeWorkspace == null) return;
            string title = PromptForString("Label box title:", "Rename Label Box", g.Title ?? string.Empty);
            if (title == null) return;
            g.Title = title;
            _renderer.Render(_activeWorkspace);
            // Keep the user on the same tab they were editing.
            var tab = FindTabContainingGroup(g);
            if (tab != null) SelectTabByModel(tab);
            MarkDirty();
            Sink.Log($"Renamed label box to '{g.Title}'.");
        }

        private void OnDeleteGroup(ButtonGroup g)
        {
            if (g == null || _activeWorkspace == null) return;
            var tab = FindTabContainingGroup(g);
            if (tab == null) return;

            // Count members for the confirm prompt — users who set up a
            // populated group benefit from knowing what gets unparented.
            int memberCount = tab.Buttons?.Count(b => string.Equals(b?.GroupId, g.Id, StringComparison.OrdinalIgnoreCase)) ?? 0;
            string suffix = memberCount > 0
                ? $"\r\n\r\n{memberCount} member button(s) will be moved to the tab canvas (not deleted)."
                : string.Empty;
            if (MessageBox.Show(this,
                    $"Delete label box '{g.Title}'?{suffix}",
                    "Delete Label Box",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            // Reset GroupId on every button that pointed here. Their
            // X/Y coordinates were group-relative; translate them to
            // canvas-absolute by adding the group's origin so visual
            // position is preserved on the next render.
            if (tab.Buttons != null)
            {
                foreach (var b in tab.Buttons)
                {
                    if (b == null) continue;
                    if (string.Equals(b.GroupId, g.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        // Group's interior origin is offset from the GroupBox
                        // top-left by ~10px on the left and ~18px on top
                        // (the title bar). Approximate that here so the
                        // button doesn't visibly jump.
                        b.X = g.X + b.X + 10;
                        b.Y = g.Y + b.Y + 18;
                        b.GroupId = null;
                    }
                }
            }
            tab.Groups.Remove(g);
            _renderer.Render(_activeWorkspace);
            SelectTabByModel(tab);
            MarkDirty();
            Sink.Log($"Deleted label box '{g.Title}'.");
        }

        private Tab FindTabContainingGroup(ButtonGroup g)
        {
            if (_activeWorkspace?.Tabs == null) return null;
            foreach (var t in _activeWorkspace.Tabs)
            {
                if (t?.Groups == null) continue;
                if (t.Groups.Any(x => ReferenceEquals(x, g))) return t;
            }
            return null;
        }

        // Tiny single-field prompt. Same shape as the helper in
        // EditMenusDialog — keeping a private copy here rather than
        // pulling in Microsoft.VisualBasic.Interaction.InputBox just
        // for two callers. Returns null on Cancel; whitespace-only
        // input is rejected so we don't create blank-titled groups.
        private string PromptForString(string prompt, string title, string initial)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new System.Drawing.Size(360, 120);

                var lbl = new Label { Text = prompt, Left = 12, Top = 12, AutoSize = true };
                var tb  = new TextBox { Left = 12, Top = 36, Width = 336, Text = initial ?? string.Empty };
                var ok  = new System.Windows.Forms.Button { Text = "OK", DialogResult = DialogResult.OK,
                              Left = 192, Top = 76, Width = 75 };
                var cn  = new System.Windows.Forms.Button { Text = "Cancel", DialogResult = DialogResult.Cancel,
                              Left = 273, Top = 76, Width = 75 };

                f.Controls.AddRange(new Control[] { lbl, tb, ok, cn });
                f.AcceptButton = ok;
                f.CancelButton = cn;

                tb.SelectAll();
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                var s = tb.Text?.Trim();
                if (string.IsNullOrEmpty(s)) return null;
                return s;
            }
        }

        /// <summary>
        /// Find the rendered TabPage whose Tag is the given model and
        /// activate it. Render rebuilds pages, so a stale TabPage
        /// reference would point at a disposed control — match by Tag
        /// identity instead.
        /// </summary>
        private void SelectTabByModel(Tab tab)
        {
            if (tab == null) return;
            foreach (TabPage tp in tabControl_Workspace.TabPages)
            {
                if (ReferenceEquals(tp.Tag, tab))
                {
                    tabControl_Workspace.SelectedTab = tp;
                    return;
                }
            }
        }

        // ===================================================================
        // Button click → dispatcher → executor
        // ===================================================================

        private void OnWorkspaceButtonClicked(WsButton btn)
        {
            if (btn == null) return;
            if (_dispatcher == null) return;

            // Background buttons skip the foreground busy gate -- they
            // have their own queue. Reentrancy check only applies to
            // foreground clicks.
            if (!btn.RunInBackground && _dispatcher.IsBusy)
            {
                Sink.WriteWarning(
                    $"Already running '{_dispatcher.ActiveLabel}'. Cancel first or wait." + Environment.NewLine);
                return;
            }

            if (btn.Confirm)
            {
                var dr = MessageBox.Show(this,
                    $"Run '{btn.Label}' ({btn.Executor})?",
                    "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes)
                {
                    Sink.Log($"Cancelled at confirm: {btn.Label}");
                    return;
                }
            }

            var request = BuildRequest(btn);
            if (request == null) return;

            if (btn.RunInBackground)
            {
                // Background path: enqueue and return immediately. The
                // job's BufferedSink captures all output; the Jobs tab
                // surfaces status + replays output on demand.
                request.RunInBackground = true;
                var job = _dispatcher.EnqueueBackground(request, btn.Executor);
                if (job == null)
                {
                    Sink.WriteError("Background queue is not available." + Environment.NewLine);
                }
                return;
            }

            // Foreground: fire-and-forget. The dispatcher writes
            // everything to the sink and updates busy state via
            // BusyChanged — Shell never needs to await the task.
            // Swallow exceptions defensively; the dispatcher is
            // already supposed to catch all of them, but unobserved
            // task faults would otherwise terminate the app on GC
            // under .NET Framework.
            _ = RunAndIgnoreFaults(_dispatcher.ExecuteAsync(request, btn.Executor));
        }

        private static async Task RunAndIgnoreFaults(Task t)
        {
            try { await t.ConfigureAwait(false); } catch { /* logged by dispatcher */ }
        }

        private ExecutionRequest BuildRequest(WsButton btn)
        {
            var resolvedPath = ResolveScriptPath(btn.ScriptPath);
            if (string.IsNullOrEmpty(resolvedPath))
            {
                Sink.WriteError($"'{btn.Label}' has no scriptPath configured." + Environment.NewLine);
                return null;
            }

            // Phase 5: resolve {{tokens}} centrally. Errors abort the
            // dispatch so we never invoke a script with a literal
            // "{{computerName}}" in argv. Warnings are surfaced but
            // don't block — empty optional values are legitimate.
            //
            // Apply per-input normalization rules BEFORE token substitution
            // so "{{computerName}}" and the injected $computerName variable
            // see the SAME resolved value (e.g. "MYBOX" instead of "" or
            // ".") — anything else would be confusing.
            var staticValues = NormalizeSharedInputs(
                _renderer.GetSharedInputValues(),
                _activeWorkspace?.SharedInputs);
            // Merge volatile session inputs ON TOP of the static set.
            // The no-duplicate rule means they shouldn't actually clash
            // (Set-SharedInput refuses to shadow a static id), but if a
            // collision somehow happens, volatile wins -- per the
            // documented semantics. staticIds tracks the pre-merge keys
            // so the executor can publish $ScriptDeckInputs.Static for
            // bootstrap helpers.
            var staticIds = new HashSet<string>(staticValues.Keys, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(staticValues, StringComparer.OrdinalIgnoreCase);
            foreach (var v in _sessionInputs.Values)
            {
                if (v == null || string.IsNullOrEmpty(v.Id)) continue;
                values[v.Id] = v.Value ?? string.Empty;
            }
            var resolved = TokenResolver.Resolve(btn.Args, btn.WorkingDirectory, values);

            foreach (var w in resolved.Warnings)
                Sink.WriteWarning($"[{btn.Label}] {w}{Environment.NewLine}");

            if (resolved.HasErrors)
            {
                foreach (var err in resolved.Errors)
                    Sink.WriteError($"[{btn.Label}] {err}{Environment.NewLine}");
                Sink.WriteError(
                    $"[{btn.Label}] Refusing to run with unresolved tokens. " +
                    $"Edit the button or add the missing shared input." + Environment.NewLine);
                return null;
            }

            // Resolve the working directory against the workspace too,
            // so users can write `"workingDirectory": "subdir"` and have
            // it land relative to the scripts root.
            string workingDir = resolved.ResolvedWorkingDirectory;
            if (!string.IsNullOrEmpty(workingDir)
                && !Path.IsPathRooted(workingDir)
                && !string.IsNullOrEmpty(_activeWorkspace?.ScriptsRoot))
            {
                workingDir = Path.GetFullPath(Path.Combine(_activeWorkspace.ScriptsRoot, workingDir));
            }

            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (btn.Outputs != null)
                foreach (var o in btn.Outputs) if (!string.IsNullOrEmpty(o)) outputs.Add(o);
            // Default to "rtb" if the workspace omitted outputs entirely —
            // matches the JSON schema default and keeps console-app
            // launches from being silent.
            if (outputs.Count == 0) outputs.Add("rtb");

            return new ExecutionRequest
            {
                ScriptPath = resolvedPath,
                Args = resolved.ResolvedArgs.ToList(),
                WorkingDirectory = workingDir,
                ButtonLabel = btn.Label,
                OutputTargets = outputs,
                // Phase 6 history metadata. None of these affect execution —
                // they ride along so the dispatcher can record an audit row
                // without us plumbing a parallel "context" object through
                // every layer.
                ButtonId = btn.Id,
                WorkspaceName = _activeWorkspace?.Name,
                WorkspacePath = _activeWorkspacePath,
                // Same normalized values that fed token substitution above.
                // PowerShellExecutor publishes them as runspace variables
                // (so a script can use $computerName directly) and Cmd /
                // Process executors publish them as env vars.
                SharedInputs = values,
                // The pre-merge static ids tell PowerShellExecutor which
                // entries are Static vs Volatile so it can publish a
                // $ScriptDeckInputs metadata hashtable for the bootstrap
                // helpers' duplicate detection.
                StaticInputIds = staticIds,
                RtbFormat    = btn.RtbFormat,
                // Python interpreter precedence: per-button override
                // wins; otherwise fall through to the workspace default;
                // otherwise null (executor uses bare "python" on PATH).
                // Non-Python executors ignore this field entirely.
                PythonInterpreter = !string.IsNullOrWhiteSpace(btn.PythonInterpreter)
                    ? btn.PythonInterpreter
                    : _activeWorkspace?.PythonInterpreter,
            };
        }

        // Apply per-input "normalize" rules to a snapshot of the live
        // textbox values. Defined rules:
        //
        //   "computerName"  empty / whitespace / "." / "localhost" ->
        //                   Environment.MachineName.
        //
        // Only inputs whose schema declared a Normalize value are touched;
        // everything else passes through verbatim. The rule is applied
        // even though the textbox is pre-filled at load time — users can
        // clear the box, and we want a sensible value at click time
        // regardless.
        private static IDictionary<string, string> NormalizeSharedInputs(
            IDictionary<string, string> raw,
            IList<SharedInput> schema)
        {
            var result = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
            if (schema == null) return result;

            foreach (var s in schema)
            {
                if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                if (!result.TryGetValue(s.Id, out var current)) continue;

                // computerName fields: explicit Normalize is the canonical
                // opt-in; id=="computerName" is an implicit fallback so a
                // workspace that lost the normalize hint (JSON round-trip
                // dropping null fields, hand-edits, etc.) still gets the
                // same runtime resolution as the renderer's display path.
                bool isComputerNameField =
                    string.Equals(s.Normalize, "computerName", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Id,    "computerName", StringComparison.OrdinalIgnoreCase);
                if (isComputerNameField)
                {
                    if (string.IsNullOrWhiteSpace(current)
                        || string.Equals(current, ".",         StringComparison.OrdinalIgnoreCase)
                        || string.Equals(current, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        result[s.Id] = Environment.MachineName;
                    }
                }
            }
            return result;
        }

        private string ResolveScriptPath(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath)) return string.Empty;
            if (Path.IsPathRooted(scriptPath)) return scriptPath;
            var root = _activeWorkspace?.ScriptsRoot;
            if (string.IsNullOrEmpty(root)) return scriptPath;
            return Path.GetFullPath(Path.Combine(root, scriptPath));
        }

        // ===================================================================
        // Busy-state UI
        // ===================================================================

        private void OnDispatcherBusyChanged(object sender, EventArgs e)
        {
            // Marshal to UI thread — BusyChanged fires from the dispatcher's
            // continuation thread, which is whatever pool thread the
            // executor task happened to finish on.
            bool busy = _dispatcher?.IsBusy ?? false;
            if (InvokeRequired) BeginInvoke((Action<bool>)UpdateBusyUi, busy);
            else UpdateBusyUi(busy);
        }

        private void UpdateBusyUi(bool busy)
        {
            menu_Edit_CancelRunning.Enabled = busy;
            statusLabel_Mode.Text = busy
                ? $"Running: {_dispatcher.ActiveLabel}  (Esc to cancel)"
                : "Idle";
            // Subtle visual hint on the status bar so users notice without
            // having to read the label. Cyan-ish when busy, default otherwise.
            statusLabel_Mode.ForeColor = busy
                ? System.Drawing.Color.DeepSkyBlue
                : System.Drawing.SystemColors.ControlText;
        }

        // ===================================================================
        // Phase 7 — workspace-defined top menus
        // ===================================================================

        /// <summary>
        /// Tear down any previously-rendered workspace menus and rebuild from
        /// the supplied workspace's <see cref="MenuDefinition"/> list. Inserts
        /// the resulting items between the built-in Edit and Tools menus so
        /// they sit in a consistent place regardless of menu count.
        ///
        /// Click handlers reuse <see cref="OnWorkspaceButtonClicked"/>, which
        /// means workspace-menu items go through the same token-resolve +
        /// dispatcher + history-record path tab buttons do. There is no
        /// second code path for "menu execution" — keeping it that way makes
        /// every feature we add to button dispatch (cancel, confirm, log,
        /// extended grid, etc.) work for menu items for free.
        /// </summary>
        private void RenderWorkspaceMenus(Workspace.Workspace ws)
        {
            ClearWorkspaceMenus();
            if (ws?.Menus == null || ws.Menus.Count == 0) return;

            // Anchor index = position of menu_Tools. Workspace menus go
            // immediately before Tools so Tools (the cross-cutting global)
            // stays as the rightmost built-in. Recomputed each render in
            // case future code reorders the static menus.
            int anchor = menuStrip.Items.IndexOf(menu_Tools);
            if (anchor < 0) anchor = menuStrip.Items.Count;

            foreach (var def in ws.Menus)
            {
                if (def == null) continue;
                var top = BuildMenu(def);
                if (top == null) continue;
                menuStrip.Items.Insert(anchor, top);
                _workspaceMenuItems.Add(top);
                anchor++; // keep order: each subsequent menu lands after the previous
            }
        }

        private void ClearWorkspaceMenus()
        {
            foreach (var item in _workspaceMenuItems)
            {
                try { menuStrip.Items.Remove(item); item.Dispose(); }
                catch { /* swallow — menu strip teardown shouldn't crash on edge cases */ }
            }
            _workspaceMenuItems.Clear();
        }

        /// <summary>
        /// Build a top-level <see cref="ToolStripMenuItem"/> from a
        /// <see cref="MenuDefinition"/>. Each child item is either a
        /// separator (button with Label "-") or a clickable item that
        /// dispatches via the standard click path. We deliberately do NOT
        /// support nested submenus yet — flat menus cover the typical
        /// "always-visible quick action" use case and keep the editor
        /// UI single-level.
        /// </summary>
        private ToolStripMenuItem BuildMenu(MenuDefinition def)
        {
            var top = new ToolStripMenuItem(string.IsNullOrEmpty(def.Title) ? "(menu)" : def.Title);
            if (def.Items != null)
            {
                foreach (var btn in def.Items)
                {
                    if (btn == null) continue;

                    // Separator convention: a button with Label "-" renders
                    // as a horizontal rule. Cheap and JSON-friendly — no
                    // separate "type" field required.
                    if (string.Equals(btn.Label?.Trim(), "-", StringComparison.Ordinal))
                    {
                        top.DropDownItems.Add(new ToolStripSeparator());
                        continue;
                    }

                    var item = new ToolStripMenuItem(
                        string.IsNullOrEmpty(btn.Label) ? "(unnamed)" : btn.Label);
                    // Capture by closure rather than Tag — Tag would force a
                    // cast in the handler and offers no benefit here.
                    var captured = btn;
                    item.Click += (s, e) => OnWorkspaceButtonClicked(captured);
                    top.DropDownItems.Add(item);
                }
            }
            return top;
        }

        private void menu_Edit_WorkspaceMenus_Click(object sender, EventArgs e)
        {
            if (_activeWorkspace == null)
            {
                Sink.WriteWarning("Open or create a workspace first." + Environment.NewLine);
                return;
            }
            using (var dlg = new EditMenusDialog(_activeWorkspace.Menus, _activeWorkspace.ScriptsRoot))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _activeWorkspace.Menus = dlg.GetEditedList();
                // Re-render the MenuStrip so changes are visible without
                // requiring a workspace reload. The user still has to Save
                // to persist, matching the rest of the edit-mode UX.
                RenderWorkspaceMenus(_activeWorkspace);
                MarkDirty();
                Sink.Log($"Updated workspace menus ({_activeWorkspace.Menus.Count}).");
            }
        }
    }
}
