using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.Hosting;
using ScriptDeck.Workspace;

// Workspace.Button collides with WinForms.Button. Alias once at the top of
// every renderer/form file that touches both, rather than fully qualifying
// at every call site (loud and error-prone).
using WsButton = ScriptDeck.Workspace.Button;
using WinButton = System.Windows.Forms.Button;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Owns the dynamic part of the Shell UI: shared-input fields in the
    /// black top band, and tabs/buttons in the TabControl. The Shell hands
    /// it the relevant controls plus a click callback; the renderer
    /// builds and tears down dynamic content as workspaces load and close.
    ///
    /// Layout model: every tab is a free-positioned canvas. Buttons have
    /// absolute X/Y/Width/Height; optional <see cref="ButtonGroup"/>s are
    /// labeled frames (rendered as WinForms <c>GroupBox</c>) that own
    /// member buttons as real child controls — so moving/resizing a
    /// group naturally carries its buttons along, no synchronization
    /// code that could drift.
    ///
    /// In edit mode (<see cref="IsEditMode"/>):
    ///   - left-click on a button opens its editor instead of running it,
    ///   - click-drag the body of a button or group moves it,
    ///   - click-drag the bottom-right corner (14×14 grip) resizes it,
    ///   - dropping a button inside a group's bounds re-parents it into
    ///     that group; dropping outside re-parents to the canvas,
    ///   - right-click surfaces context menus for edit/delete/move/etc.
    /// The renderer never mutates the workspace model except for these
    /// positional updates, which are atomic and committed on mouse-up
    /// so a crash mid-drag can't leave dangling state.
    ///
    /// Every dynamic control is disposed when replaced — WinForms doesn't
    /// dispose on Remove, and over many reloads that leaks GDI handles.
    /// </summary>
    internal sealed class WorkspaceRenderer
    {
        // Default size when a button's JSON omits dimensions or a freshly-
        // added button is dropped on the canvas with no size yet.
        private const int DefaultButtonWidth  = 150;
        private const int DefaultButtonHeight = 36;

        // Minimums protect against accidentally resizing something to 0×0
        // and "losing" it (no hit target left to grab).
        private const int MinButtonWidth  = 40;
        private const int MinButtonHeight = 22;
        private const int MinGroupWidth   = 80;
        private const int MinGroupHeight  = 60;

        // Bottom-right resize handle hit zone (px square).
        // 20 px gives the user an unambiguous target on a 36-tall button.
        // The grip is also painted (a small ◢ in the corner) so users
        // see where to grab without trial-and-error hovering.
        private const int ResizeGripSize = 20;

        private readonly FlowLayoutPanel _sharedInputsPanel;
        private readonly TabControl _tabControl;
        private readonly TabPage _welcomeTab;
        private readonly Action<WsButton> _onButtonClick;

        // Map shared-input id -> control (TextBox by default, ComboBox
        // for normalize=="computerName" inputs that source from the
        // ComputerListStore). Control is the common base; .Text works
        // on both, which is all the dispatcher's value-snapshot code
        // needs. Picking Control over a Func<string> wrapper keeps the
        // refactor footprint tiny.
        private readonly Dictionary<string, Control> _sharedInputs =
            new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);

        // Track the currently rendered workspace model so context menu
        // handlers can find the parent Tab of a button by reference, and
        // so the tab-strip context menu knows which tabs exist.
        private Workspace.Workspace _workspace;

        public bool IsEditMode { get; private set; }

        // ---- Edit-mode events ----
        // The Shell subscribes; the renderer fires from context-menu
        // clicks. Each event passes enough context (the model object,
        // optionally a direction) for the Shell to mutate the workspace
        // and call Render again.

        /// <summary>User wants to edit this button's properties.</summary>
        public event Action<WsButton> EditButtonRequested;
        /// <summary>User wants to delete this button.</summary>
        public event Action<WsButton> DeleteButtonRequested;
        /// <summary>
        /// Move this button by +1 / -1 within its tab's button list. The
        /// list order is z-order (later = drawn on top), so this becomes
        /// "Bring Forward" / "Send Backward" when buttons overlap.
        /// </summary>
        public event Action<WsButton, int> MoveButtonRequested;

        /// <summary>
        /// Match-size requests from the button context menu. The first
        /// arg is the button being adjusted; the second is the source
        /// button it should copy size from; the third specifies which
        /// dimensions to copy ("width", "height", or "both").
        /// </summary>
        public event Action<WsButton, WsButton, string> MatchSizeRequested;

        /// <summary>User wants to edit a tab's title/id.</summary>
        public event Action<Tab> EditTabRequested;
        /// <summary>User wants to delete this tab (and its buttons).</summary>
        public event Action<Tab> DeleteTabRequested;
        /// <summary>User wants to add a new button to this tab.</summary>
        public event Action<Tab> AddButtonRequested;
        /// <summary>User wants to add a new tab to the workspace.</summary>
        public event Action AddTabRequested;

        /// <summary>Add a new labeled group frame to this tab.</summary>
        public event Action<Tab> AddGroupRequested;
        /// <summary>Rename this group's title.</summary>
        public event Action<ButtonGroup> EditGroupRequested;
        /// <summary>Delete this group (member buttons re-parent to the canvas).</summary>
        public event Action<ButtonGroup> DeleteGroupRequested;
        /// <summary>
        /// Add a new button parented into this group's frame. Distinct
        /// from <see cref="AddButtonRequested"/> (which lands the new
        /// button on the tab canvas) so the Shell can preset GroupId.
        /// </summary>
        public event Action<ButtonGroup> AddButtonToGroupRequested;

        /// <summary>
        /// Fired AFTER the renderer mutates the workspace model on the
        /// user's behalf — currently only at drag/resize commit. Shell
        /// uses this to flip its dirty flag without us having to plumb
        /// state back through the request/response events. (The other
        /// renderer events are *requests* the Shell fulfils — they
        /// don't mutate the model on their own.)
        /// </summary>
        public event Action LayoutCommitted;

        // Optional ScriptDeck-wide computer list. Drives the dropdown
        // shown for any shared input with normalize == "computerName".
        // When null, the renderer falls back to a plain TextBox so the
        // renderer remains usable without the store (tests, headless
        // callers, etc.).
        private readonly ComputerListStore _computerList;

        // Currently-live computerName ComboBoxes, keyed by SharedInput.Id.
        // Tracked so the ComputerListStore.Changed handler can re-point
        // each combo's items list in place when the user adds / removes
        // computers via Tools -> Manage Computers, without forcing a
        // full re-render of the workspace (which would lose any value
        // the user has manually typed).
        private readonly System.Collections.Generic.Dictionary<string, ComboBox> _computerCombos =
            new System.Collections.Generic.Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase);

        public WorkspaceRenderer(
            FlowLayoutPanel sharedInputsPanel,
            TabControl tabControl,
            TabPage welcomeTab,
            Action<WsButton> onButtonClick)
            : this(sharedInputsPanel, tabControl, welcomeTab, onButtonClick, computerList: null) { }

        public WorkspaceRenderer(
            FlowLayoutPanel sharedInputsPanel,
            TabControl tabControl,
            TabPage welcomeTab,
            Action<WsButton> onButtonClick,
            ComputerListStore computerList)
        {
            _sharedInputsPanel = sharedInputsPanel ?? throw new ArgumentNullException(nameof(sharedInputsPanel));
            _tabControl        = tabControl        ?? throw new ArgumentNullException(nameof(tabControl));
            _welcomeTab        = welcomeTab        ?? throw new ArgumentNullException(nameof(welcomeTab));
            _onButtonClick     = onButtonClick     ?? throw new ArgumentNullException(nameof(onButtonClick));
            _computerList      = computerList; // optional

            // Tab drag-reorder. Subscribed once at construction (the
            // TabControl outlives every render cycle) so we don't have to
            // unsubscribe on Render and avoid the leak that comes with
            // re-attaching every time. Handlers no-op outside edit mode
            // and skip the synthetic "+" / Welcome tabs by checking Tag.
            _tabControl.MouseDown += OnTabStripMouseDown;
            _tabControl.MouseMove += OnTabStripMouseMove;
            _tabControl.MouseUp   += OnTabStripMouseUp;

            // Re-populate live combo items when the user edits the
            // computer list via Tools -> Manage Computers. Subscribed
            // once at construction; ComputerListStore outlives this
            // renderer for the lifetime of the app.
            if (_computerList != null)
                _computerList.Changed += OnComputerListChanged;
        }

        private void OnComputerListChanged()
        {
            // Marshal to the UI thread because the store can technically
            // fire Changed off any thread (Save in the dialog is on UI
            // today, but future callers might not be).
            if (_sharedInputsPanel.InvokeRequired)
            {
                _sharedInputsPanel.BeginInvoke(new Action(OnComputerListChanged));
                return;
            }
            foreach (var combo in _computerCombos.Values)
                RepopulateComputerCombo(combo);
        }

        // Build [magic entries] + [user list] and assign to a combo's
        // Items collection in place. Preserves the current Text so the
        // user's typed-in value survives the refresh.
        private void RepopulateComputerCombo(ComboBox combo)
        {
            if (combo == null || combo.IsDisposed) return;
            string preserved = combo.Text;
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                foreach (var magic in MagicComputerEntries())
                    combo.Items.Add(magic);
                if (_computerList != null)
                {
                    foreach (var c in _computerList.GetAll())
                        combo.Items.Add(c);
                }
            }
            finally
            {
                combo.EndUpdate();
            }
            combo.Text = preserved;
        }

        // Universally-useful "pseudo-machine" tokens pinned at the top
        // of every computerName dropdown. Each resolves to the local box
        // at runtime via the existing normalize=="computerName" pipeline,
        // so the user never has to add these manually.
        private static System.Collections.Generic.IEnumerable<string> MagicComputerEntries()
        {
            yield return ".";
            yield return "localhost";
            yield return "%COMPUTERNAME%";
        }

        /// <summary>
        /// Snapshot of every shared-input id -> current text value. Built
        /// fresh on each call so updates after-load are reflected.
        /// </summary>
        public IDictionary<string, string> GetSharedInputValues()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _sharedInputs)
                result[kv.Key] = kv.Value?.Text ?? string.Empty;
            return result;
        }

        /// <summary>
        /// True when <paramref name="value"/> is one of the well-known
        /// "I mean the local box" placeholders. Used to decide whether to
        /// auto-fill a computerName-style textbox with the actual machine
        /// name on workspace load.
        /// </summary>
        private static bool IsLocalSentinel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return string.Equals(value, ".",         StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        public void Render(Workspace.Workspace workspace)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            _workspace = workspace;
            ClearAll();
            RenderSharedInputs(workspace.SharedInputs);
            RenderTabs(workspace.Tabs);
        }

        /// <summary>
        /// Reset to the no-workspace-loaded state: empty shared-inputs band,
        /// just the Welcome tab. Called when the user closes a workspace.
        /// </summary>
        public void ShowEmpty()
        {
            _workspace = null;
            ClearAll();
            if (!_tabControl.TabPages.Contains(_welcomeTab))
                _tabControl.TabPages.Add(_welcomeTab);
        }

        /// <summary>
        /// Toggle edit-mode visuals on/off. Buttons stay clickable in
        /// edit mode (left-click opens the editor instead of running)
        /// so users can immediately fix a misconfigured button without
        /// hunting through menus.
        /// </summary>
        public void SetEditMode(bool enabled)
        {
            IsEditMode = enabled;
            if (_workspace != null) Render(_workspace);
        }

        // ---- Internals ----

        private void ClearAll()
        {
            foreach (Control c in _sharedInputsPanel.Controls.Cast<Control>().ToArray())
            {
                _sharedInputsPanel.Controls.Remove(c);
                c.Dispose();
            }
            _sharedInputs.Clear();
            // The dispose loop above kills the combo controls; clearing
            // the tracking dict drops dead references so the next
            // ComputerListStore.Changed event doesn't try to repopulate
            // a disposed combo.
            _computerCombos.Clear();

            foreach (TabPage tp in _tabControl.TabPages.Cast<TabPage>().ToArray())
            {
                _tabControl.TabPages.Remove(tp);
                if (!ReferenceEquals(tp, _welcomeTab))
                    tp.Dispose();
            }
        }

        private void RenderSharedInputs(IList<SharedInput> inputs)
        {
            if (inputs == null || inputs.Count == 0) return;

            foreach (var input in inputs)
            {
                if (string.IsNullOrEmpty(input?.Id)) continue;

                var pair = new Panel
                {
                    BackColor = Color.Black,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 16, 4),
                    Padding = new Padding(0),
                };

                var label = new Label
                {
                    Text = input.Label ?? input.Id,
                    AutoSize = true,
                    ForeColor = Color.LightGreen,
                    BackColor = Color.Black,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                    Location = new Point(0, 6),
                };

                // For inputs flagged as "computerName", pre-resolve a
                // sentinel default ("" / "." / "localhost") to the actual
                // local machine name so the textbox visibly reads "MYBOX"
                // from the moment the workspace loads. Workspace JSON
                // stays portable across machines — each box fills in its
                // own name. Any other Default value passes through verbatim.
                //
                // Detection: explicit normalize=="computerName" is the
                // canonical opt-in, AND we treat id=="computerName" as
                // an implicit fallback so workspaces that lost or never
                // set the normalize hint (e.g. a JSON round-trip that
                // dropped null fields, a user copying a sample without
                // realizing) still get the dropdown + machine-name
                // resolution. The id name is a strong convention and
                // matches the only token most users care about.
                string initialText = input.Default ?? string.Empty;
                bool isComputerNameField =
                    string.Equals(input.Normalize, "computerName", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(input.Id,    "computerName", StringComparison.OrdinalIgnoreCase);
                if (isComputerNameField && IsLocalSentinel(initialText))
                {
                    initialText = Environment.MachineName;
                }

                // computerName fields render as an editable ComboBox
                // sourced from the ComputerListStore (+ pinned magic
                // entries like "." and "localhost"). All other inputs
                // stay as plain TextBoxes. The ComboBox is editable
                // (DropDownStyle=DropDown) so manual entry of any
                // hostname / IP still works -- the dropdown is just a
                // pick-list shortcut.
                Control box;
                if (isComputerNameField)
                {
                    var combo = new ComboBox
                    {
                        Name = "shared_" + input.Id,
                        Text = initialText,
                        Width = 180,
                        Font = new Font("Consolas", 9.5F),
                        Location = new Point(label.PreferredWidth + 6, 4),
                        DropDownStyle      = ComboBoxStyle.DropDown,
                        AutoCompleteMode   = AutoCompleteMode.SuggestAppend,
                        AutoCompleteSource = AutoCompleteSource.ListItems,
                        // Cap visible rows at 5 (default is 8). Past that
                        // the dropdown scrolls -- keeps a long managed
                        // list from eating most of the screen, and makes
                        // the dropdown's resting size predictable.
                        MaxDropDownItems   = 5,
                    };
                    RepopulateComputerCombo(combo);
                    // Restore the initial text AFTER populating (the
                    // populate path preserves Text, but assigning Items
                    // can sometimes nudge it).
                    combo.Text = initialText;
                    _computerCombos[input.Id] = combo;
                    box = combo;
                }
                else
                {
                    box = new TextBox
                    {
                        Name = "shared_" + input.Id,
                        Text = initialText,
                        Width = 180,
                        Font = new Font("Consolas", 9.5F),
                        Location = new Point(label.PreferredWidth + 6, 4),
                    };
                }

                pair.Controls.Add(label);
                pair.Controls.Add(box);
                _sharedInputsPanel.Controls.Add(pair);

                _sharedInputs[input.Id] = box;
            }
        }

        private void RenderTabs(IList<Tab> tabs)
        {
            if (tabs == null) return;

            foreach (var t in tabs)
            {
                if (t == null) continue;
                _tabControl.TabPages.Add(BuildTabPage(t));
            }

            if (_tabControl.TabPages.Count == 0)
            {
                if (IsEditMode) _tabControl.TabPages.Add(BuildAddTabPage());
                else _tabControl.TabPages.Add(_welcomeTab);
                return;
            }

            if (IsEditMode)
                _tabControl.TabPages.Add(BuildAddTabPage());
        }

        // ----------------------------------------------------------------
        // Tab page = canvas Panel hosting groups (GroupBox) and free
        // buttons. Groups are added before buttons so any canvas-direct
        // buttons render above them in z-order.
        // ----------------------------------------------------------------

        private TabPage BuildTabPage(Tab tab)
        {
            var page = new TabPage
            {
                Text = string.IsNullOrEmpty(tab.Title) ? (tab.Id ?? "Tab") : tab.Title,
                UseVisualStyleBackColor = true,
                Padding = new Padding(8),
                Tag = tab,
            };

            // Canvas scrolls if children exceed its viewport.
            // AutoScrollMinSize is recomputed after each child move so
            // dragging past the right edge grows the scroll region
            // rather than clipping.
            var canvas = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 248, 248),
                Tag = tab,
            };
            page.Controls.Add(canvas);

            if (IsEditMode)
                canvas.ContextMenuStrip = BuildTabContextMenu(tab);

            // Groups first (z-order: first added = bottom). Buttons that
            // live INSIDE a group are children of that group's GroupBox
            // and aren't affected by canvas z-order.
            var groupCtls = new Dictionary<string, GroupBox>(StringComparer.OrdinalIgnoreCase);
            if (tab.Groups != null)
            {
                foreach (var g in tab.Groups)
                {
                    if (g == null || string.IsNullOrEmpty(g.Id)) continue;
                    var gb = BuildGroupBox(g);
                    canvas.Controls.Add(gb);
                    groupCtls[g.Id] = gb;
                }
            }

            // Buttons: parent into their group's GroupBox if GroupId
            // resolves; otherwise onto the canvas directly. Unknown
            // GroupIds tolerate a hand-edited JSON with stale references
            // (the button just renders on the canvas).
            if (tab.Buttons != null)
            {
                foreach (var b in tab.Buttons)
                {
                    if (b == null) continue;
                    var ui = BuildButton(b);
                    if (!string.IsNullOrEmpty(b.GroupId)
                        && groupCtls.TryGetValue(b.GroupId, out var gb))
                    {
                        gb.Controls.Add(ui);
                    }
                    else
                    {
                        canvas.Controls.Add(ui);
                    }
                }
            }

            UpdateCanvasAutoScrollMinSize(canvas);
            return page;
        }

        private void UpdateCanvasAutoScrollMinSize(Panel canvas)
        {
            // Smallest box that contains every visible top-level child
            // plus a small margin so the user can drag past the current
            // edge to grow the workspace.
            int maxR = 0, maxB = 0;
            foreach (Control c in canvas.Controls)
            {
                if (c.Right > maxR) maxR = c.Right;
                if (c.Bottom > maxB) maxB = c.Bottom;
            }
            canvas.AutoScrollMinSize = new Size(maxR + 32, maxB + 32);
        }

        // ----------------------------------------------------------------
        // Button + GroupBox construction
        // ----------------------------------------------------------------

        private WinButton BuildButton(WsButton btn)
        {
            int w = btn.Width  > 0 ? btn.Width  : DefaultButtonWidth;
            int h = btn.Height > 0 ? btn.Height : DefaultButtonHeight;

            var ui = new WinButton
            {
                Text = string.IsNullOrEmpty(btn.Label) ? (btn.Id ?? "(unnamed)") : btn.Label,
                Location = new Point(Math.Max(0, btn.X), Math.Max(0, btn.Y)),
                Size = new Size(w, h),
                UseCompatibleTextRendering = true,
                UseVisualStyleBackColor = true,
                Tag = btn,
            };
            ui.Click += UiButton_Click;

            if (IsEditMode)
            {
                // Visual hint that buttons are now editable rather than
                // runnable. Subtle border keeps the chrome from screaming.
                ui.FlatStyle = FlatStyle.Flat;
                ui.FlatAppearance.BorderColor = Color.OrangeRed;
                ui.FlatAppearance.BorderSize = 2;
                ui.ContextMenuStrip = BuildButtonContextMenu(btn);
                AttachDragResize(ui, isGroup: false);
                AttachResizeGripPaint(ui);
            }
            return ui;
        }

        private GroupBox BuildGroupBox(ButtonGroup g)
        {
            var gb = new GroupBox
            {
                Text = g.Title ?? string.Empty,
                Location = new Point(Math.Max(0, g.X), Math.Max(0, g.Y)),
                Size = new Size(Math.Max(MinGroupWidth, g.Width), Math.Max(MinGroupHeight, g.Height)),
                Tag = g,
                // Slight tint so groups read as "containers" against the
                // off-white canvas without screaming for attention.
                BackColor = Color.FromArgb(240, 244, 250),
            };
            if (IsEditMode)
            {
                gb.ContextMenuStrip = BuildGroupContextMenu(g);
                AttachDragResize(gb, isGroup: true);
                AttachResizeGripPaint(gb);
            }
            return gb;
        }

        /// <summary>
        /// Paint a small orange-red triangle in the bottom-right corner
        /// of an edit-mode control so the user can SEE the resize grip.
        /// Without this hint, the 20-px hit zone is invisible and most
        /// users would never discover that buttons can be resized.
        ///
        /// Drawn after the default control painting via the Paint event,
        /// so it sits on top of the button face / group frame.
        /// </summary>
        private static void AttachResizeGripPaint(Control ctl)
        {
            ctl.Paint += (s, e) =>
            {
                var c = (Control)s;
                int side = ResizeGripSize - 6; // visual triangle is a touch smaller than the hit zone
                if (c.Width < side || c.Height < side) return;
                var pts = new[]
                {
                    new Point(c.Width - side, c.Height - 2),
                    new Point(c.Width - 2,    c.Height - 2),
                    new Point(c.Width - 2,    c.Height - side),
                };
                using (var brush = new SolidBrush(Color.OrangeRed))
                    e.Graphics.FillPolygon(brush, pts);
            };
        }

        private TabPage BuildAddTabPage()
        {
            var page = new TabPage
            {
                Text = "  +  ",
                UseVisualStyleBackColor = true,
                Padding = new Padding(12),
            };
            var lbl = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 30,
                Text = "Click below to add a new tab to this workspace.",
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var addBtn = new WinButton
            {
                Text = "+ New Tab",
                Width = 160,
                Height = 36,
                Top = 40,
                Left = 12,
                UseVisualStyleBackColor = true,
            };
            addBtn.Click += (_, __) => AddTabRequested?.Invoke();
            page.Controls.Add(addBtn);
            page.Controls.Add(lbl);
            return page;
        }

        // ---- Context menus ----

        private ContextMenuStrip BuildButtonContextMenu(WsButton btn)
        {
            var menu = new ContextMenuStrip();
            var edit = new ToolStripMenuItem("Edit...");
            var del  = new ToolStripMenuItem("Delete");
            var sep1 = new ToolStripSeparator();
            // Move Up / Move Down adjust the button's position in the
            // tab's button list, which is render order, which is z-order.
            // Useful when buttons overlap and you need one in front of
            // another. Renamed in the UI to make that clearer.
            var fwd  = new ToolStripMenuItem("Bring Forward");
            var back = new ToolStripMenuItem("Send Backward");
            var sep2 = new ToolStripSeparator();
            // Tier 3: explicit "make this button the same size as another"
            // commands. Submenus are populated on DropDownOpening so the
            // sibling list is always current — buttons added or resized
            // since the menu was built show up correctly.
            var matchW = new ToolStripMenuItem("Match Width of");
            var matchH = new ToolStripMenuItem("Match Height of");
            var matchS = new ToolStripMenuItem("Match Size of");
            matchW.DropDownOpening += (_, __) => PopulateMatchSubmenu(matchW, btn, "width");
            matchH.DropDownOpening += (_, __) => PopulateMatchSubmenu(matchH, btn, "height");
            matchS.DropDownOpening += (_, __) => PopulateMatchSubmenu(matchS, btn, "both");

            edit.Click += (_, __) => EditButtonRequested?.Invoke(btn);
            del.Click  += (_, __) => DeleteButtonRequested?.Invoke(btn);
            fwd.Click  += (_, __) => MoveButtonRequested?.Invoke(btn, +1);
            back.Click += (_, __) => MoveButtonRequested?.Invoke(btn, -1);
            menu.Items.AddRange(new ToolStripItem[] {
                edit, del, sep1, fwd, back, sep2, matchW, matchH, matchS
            });
            return menu;
        }

        // Find every other button in the active workspace and offer it
        // as a "match size of X (W x H)" target. Searches the entire
        // workspace (all tabs) so the user can match a button on Tab A
        // to a canonical-size button on Tab B without first navigating
        // there. Skips the button itself.
        private void PopulateMatchSubmenu(ToolStripMenuItem parent, WsButton self, string dimension)
        {
            parent.DropDownItems.Clear();
            if (_workspace?.Tabs == null)
            {
                var none = new ToolStripMenuItem("(no workspace loaded)") { Enabled = false };
                parent.DropDownItems.Add(none);
                return;
            }

            int count = 0;
            foreach (var t in _workspace.Tabs)
            {
                if (t?.Buttons == null) continue;
                foreach (var b in t.Buttons)
                {
                    if (b == null || ReferenceEquals(b, self)) continue;
                    int w = b.Width  > 0 ? b.Width  : DefaultButtonWidth;
                    int h = b.Height > 0 ? b.Height : DefaultButtonHeight;
                    string label = string.IsNullOrEmpty(b.Label) ? (b.Id ?? "(unnamed)") : b.Label;
                    // Show the dimensions inline so the user can pick
                    // the canonical size at a glance without hover.
                    string text = $"{label}  ({w} x {h})";
                    var mi = new ToolStripMenuItem(text);
                    var captured = b;
                    mi.Click += (_, __) => MatchSizeRequested?.Invoke(self, captured, dimension);
                    parent.DropDownItems.Add(mi);
                    count++;
                }
            }
            if (count == 0)
            {
                var none = new ToolStripMenuItem("(no other buttons)") { Enabled = false };
                parent.DropDownItems.Add(none);
            }
        }

        private ContextMenuStrip BuildTabContextMenu(Tab tab)
        {
            var menu = new ContextMenuStrip();
            var addBtn    = new ToolStripMenuItem("Add Button...");
            var addGroup  = new ToolStripMenuItem("Add Label Box...");
            var sep1      = new ToolStripSeparator();
            var renameTab = new ToolStripMenuItem("Rename Tab...");
            var delTab    = new ToolStripMenuItem("Delete Tab");
            var sep2      = new ToolStripSeparator();
            var addTab    = new ToolStripMenuItem("Add New Tab...");

            addBtn.Click    += (_, __) => AddButtonRequested?.Invoke(tab);
            addGroup.Click  += (_, __) => AddGroupRequested?.Invoke(tab);
            renameTab.Click += (_, __) => EditTabRequested?.Invoke(tab);
            delTab.Click    += (_, __) => DeleteTabRequested?.Invoke(tab);
            addTab.Click    += (_, __) => AddTabRequested?.Invoke();

            menu.Items.AddRange(new ToolStripItem[] { addBtn, addGroup, sep1, renameTab, delTab, sep2, addTab });
            return menu;
        }

        private ContextMenuStrip BuildGroupContextMenu(ButtonGroup g)
        {
            var menu = new ContextMenuStrip();
            var addBtn = new ToolStripMenuItem("Add Button...");
            var sep    = new ToolStripSeparator();
            var rename = new ToolStripMenuItem("Rename...");
            var del    = new ToolStripMenuItem("Delete (keeps buttons)");
            // AddButtonToGroupRequested differs from AddButtonRequested
            // in two important ways: the new button gets GroupId pre-set
            // to this frame's id, and its X/Y are interpreted relative
            // to the group's interior. Routing through a separate event
            // keeps Shell's two add-paths simple and explicit.
            addBtn.Click += (_, __) => AddButtonToGroupRequested?.Invoke(g);
            rename.Click += (_, __) => EditGroupRequested?.Invoke(g);
            del.Click    += (_, __) => DeleteGroupRequested?.Invoke(g);
            menu.Items.AddRange(new ToolStripItem[] { addBtn, sep, rename, del });
            return menu;
        }

        private void UiButton_Click(object sender, EventArgs e)
        {
            if (!(sender is WinButton ui)) return;
            if (!(ui.Tag is WsButton model)) return;

            // A 1-pixel drag shouldn't open the editor. AttachDragResize
            // sets _suppressNextClick when an actual move/resize happened
            // so we eat the synthesized Click that follows mouse-up.
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return;
            }

            if (IsEditMode)
                EditButtonRequested?.Invoke(model);
            else
                _onButtonClick(model);
        }

        // ----------------------------------------------------------------
        // Drag / resize behavior — used by both buttons and group boxes
        // in edit mode. State lives at the renderer level (not per-control)
        // because mouse capture means at most one drag is active at a
        // time. WinForms raises events on the captured control even when
        // the cursor leaves it, so a single state machine here is simpler
        // than tracking per-control state.
        // ----------------------------------------------------------------

        private enum DragKind { None, Move, Resize }

        private DragKind _dragKind;
        private Control _dragCtl;       // the control being dragged
        private Point _dragStartScreen; // mouse position when drag started (screen coords)
        private Point _dragStartLoc;    // ctl.Location at drag start
        private Size _dragStartSize;    // ctl.Size at drag start
        private bool _dragMoved;        // any movement happened? (suppress click)
        private bool _suppressNextClick; // set by drag-end so the trailing Click is ignored

        private void AttachDragResize(Control ctl, bool isGroup)
        {
            ctl.MouseDown  += (s, e) => OnDragMouseDown((Control)s, e, isGroup);
            ctl.MouseMove  += (s, e) => OnDragMouseMove((Control)s, e);
            ctl.MouseUp    += (s, e) => OnDragMouseUp((Control)s, e, isGroup);
            ctl.MouseEnter += (s, e) => UpdateCursor((Control)s);
            ctl.MouseLeave += (s, e) => { if (_dragKind == DragKind.None) ((Control)s).Cursor = Cursors.Default; };
        }

        private bool InResizeGrip(Control ctl, Point clientPt)
        {
            return clientPt.X >= ctl.Width  - ResizeGripSize
                && clientPt.Y >= ctl.Height - ResizeGripSize;
        }

        private void UpdateCursor(Control ctl)
        {
            if (_dragKind != DragKind.None) return;
            var clientPt = ctl.PointToClient(Control.MousePosition);
            ctl.Cursor = InResizeGrip(ctl, clientPt) ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        private void OnDragMouseDown(Control ctl, MouseEventArgs e, bool isGroup)
        {
            // Left-button only. Right-button is for the context menu;
            // middle-button has no defined behavior.
            if (e.Button != MouseButtons.Left) return;

            _dragCtl = ctl;
            _dragStartScreen = Control.MousePosition;
            _dragStartLoc = ctl.Location;
            _dragStartSize = ctl.Size;
            _dragMoved = false;

            _dragKind = InResizeGrip(ctl, e.Location) ? DragKind.Resize : DragKind.Move;
            ctl.Cursor = (_dragKind == DragKind.Resize) ? Cursors.SizeNWSE : Cursors.SizeAll;
            ctl.Capture = true;
        }

        private void OnDragMouseMove(Control ctl, MouseEventArgs e)
        {
            if (_dragKind == DragKind.None || !ReferenceEquals(_dragCtl, ctl))
            {
                UpdateCursor(ctl);
                return;
            }

            var screen = Control.MousePosition;
            int dx = screen.X - _dragStartScreen.X;
            int dy = screen.Y - _dragStartScreen.Y;

            // Tiny jitters happen between Down and Up even on a clean
            // click — a 3px threshold prevents an editor dialog from
            // opening unexpectedly while still feeling responsive.
            if (!_dragMoved && (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3))
                _dragMoved = true;
            if (!_dragMoved) return;

            // Hold Shift while dragging to bypass snap entirely. Matches
            // the convention from VS / Sketch / Figma and gives the user
            // an escape hatch when they want pixel-precise placement
            // that happens to fall close to a snap target.
            bool snapEnabled = (Control.ModifierKeys & Keys.Shift) == 0;

            if (_dragKind == DragKind.Move)
            {
                int newX = Math.Max(0, _dragStartLoc.X + dx);
                int newY = Math.Max(0, _dragStartLoc.Y + dy);

                if (snapEnabled)
                {
                    // Snap our left/right edges against any sibling's
                    // left/right edges. Same for top/bottom on the Y axis.
                    // Each axis is independent so a button can snap its
                    // X to one neighbor and its Y to another simultaneously.
                    int w = ctl.Width;
                    int h = ctl.Height;
                    var (xs, ys) = CollectEdgeTargets(ctl);
                    int snappedLeft  = SnapEdge(newX,         xs);
                    int snappedRight = SnapEdge(newX + w,     xs);
                    int snappedTop   = SnapEdge(newY,         ys);
                    int snappedBot   = SnapEdge(newY + h,     ys);
                    // Prefer the left/top snap when both edges hit; otherwise
                    // shift the position so the hitting edge aligns.
                    if (snappedLeft  != newX)         newX = snappedLeft;
                    else if (snappedRight != newX+w)  newX = snappedRight - w;
                    if (snappedTop   != newY)         newY = snappedTop;
                    else if (snappedBot   != newY+h)  newY = snappedBot - h;
                }

                ctl.Location = new Point(newX, newY);
            }
            else // Resize
            {
                int minW = ctl is GroupBox ? MinGroupWidth  : MinButtonWidth;
                int minH = ctl is GroupBox ? MinGroupHeight : MinButtonHeight;
                int newW = Math.Max(minW, _dragStartSize.Width  + dx);
                int newH = Math.Max(minH, _dragStartSize.Height + dy);

                if (snapEnabled)
                {
                    // Match-size snap: prefer aligning width to a sibling's
                    // width, height to a sibling's height. Minor edge case:
                    // if the drag would put our right edge near a sibling's
                    // right edge, also snap to that — useful for "make
                    // these two end at the same column."
                    var (widths, heights) = CollectSizeTargets(ctl);
                    newW = SnapEdge(newW, widths);
                    newH = SnapEdge(newH, heights);

                    // Right-edge alignment snap during resize: if our left
                    // is at X and width W, our right is X+W. Snap to a
                    // sibling's right edge (in the X-edge target list).
                    var (xs, ys) = CollectEdgeTargets(ctl);
                    int leftEdge = ctl.Left;
                    int topEdge  = ctl.Top;
                    int snappedRight = SnapEdge(leftEdge + newW, xs);
                    int snappedBot   = SnapEdge(topEdge  + newH, ys);
                    if (snappedRight != leftEdge + newW) newW = Math.Max(minW, snappedRight - leftEdge);
                    if (snappedBot   != topEdge  + newH) newH = Math.Max(minH, snappedBot   - topEdge);
                }

                ctl.Size = new Size(newW, newH);
            }
        }

        // ---- snap helpers (Tier 1: live size + edge alignment) ----

        // Pixel tolerance for snap. 4 px is the sweet spot in informal
        // testing -- small enough to feel intentional (the user must
        // deliberately drag near the target), large enough to be hit
        // without surgical mouse work on a typical 100% DPI display.
        private const int SnapToleranceP = 4;

        // Snap a candidate value to the nearest target, if any target is
        // within tolerance. Returns the candidate unchanged otherwise.
        private static int SnapEdge(int candidate, List<int> targets)
        {
            int best = candidate;
            int bestDist = SnapToleranceP + 1;
            foreach (var t in targets)
            {
                int d = Math.Abs(t - candidate);
                if (d <= SnapToleranceP && d < bestDist)
                {
                    best = t;
                    bestDist = d;
                }
            }
            return best;
        }

        // Collect every sibling control's left/right edges (xs) and
        // top/bottom edges (ys) in canvas / group-local coordinates.
        // Sibling = same Parent — buttons inside a group only snap to
        // each other (and the group's own interior), not to canvas
        // controls outside the frame.
        private static (List<int>, List<int>) CollectEdgeTargets(Control ctl)
        {
            var xs = new List<int>();
            var ys = new List<int>();
            var parent = ctl.Parent;
            if (parent == null) return (xs, ys);
            foreach (Control sib in parent.Controls)
            {
                if (ReferenceEquals(sib, ctl)) continue;
                xs.Add(sib.Left);
                xs.Add(sib.Right);
                ys.Add(sib.Top);
                ys.Add(sib.Bottom);
            }
            return (xs, ys);
        }

        // Collect every sibling's width and height. Used by resize-snap
        // to make "matches another button's width / height" easy.
        private static (List<int>, List<int>) CollectSizeTargets(Control ctl)
        {
            var widths  = new List<int>();
            var heights = new List<int>();
            var parent = ctl.Parent;
            if (parent == null) return (widths, heights);
            foreach (Control sib in parent.Controls)
            {
                if (ReferenceEquals(sib, ctl)) continue;
                widths.Add(sib.Width);
                heights.Add(sib.Height);
            }
            return (widths, heights);
        }

        private void OnDragMouseUp(Control ctl, MouseEventArgs e, bool isGroup)
        {
            if (_dragKind == DragKind.None) return;
            if (e.Button != MouseButtons.Left)
            {
                _dragKind = DragKind.None;
                _dragCtl = null;
                ctl.Capture = false;
                return;
            }

            ctl.Capture = false;
            ctl.Cursor = Cursors.Default;

            if (_dragMoved)
            {
                CommitDragToModel(ctl, isGroup);
                _suppressNextClick = true; // eat the synthesized Click
                // Notify Shell that the model now differs from disk.
                // Wrapped in try/catch so a misbehaving subscriber can't
                // strand the renderer mid-cleanup.
                try { LayoutCommitted?.Invoke(); } catch { /* swallow */ }
            }

            _dragKind = DragKind.None;
            _dragCtl = null;
            _dragMoved = false;
        }

        // Persist post-drag Location/Size back to the model. For
        // buttons, also handles drop-into-group / drop-out-of-group
        // based on where the button ended up in screen coordinates.
        private void CommitDragToModel(Control ctl, bool isGroup)
        {
            if (isGroup)
            {
                if (!(ctl.Tag is ButtonGroup g)) return;
                g.X = ctl.Location.X;
                g.Y = ctl.Location.Y;
                g.Width  = ctl.Width;
                g.Height = ctl.Height;
                if (ctl.Parent is Panel p) UpdateCanvasAutoScrollMinSize(p);
                return;
            }

            if (!(ctl.Tag is WsButton b)) return;

            b.Width  = ctl.Width;
            b.Height = ctl.Height;

            // Drop-into-group only on Move (resize doesn't change parent).
            if (TryReparentByDrop(ctl, out var newParent, out var newGroupId, out var newLocation))
            {
                ctl.Parent = newParent;
                ctl.Location = newLocation;
                b.GroupId = newGroupId;
            }

            b.X = ctl.Location.X;
            b.Y = ctl.Location.Y;

            // Refresh scroll bounds on the canvas. ctl.Parent might be a
            // GroupBox after reparent — walk up to the canvas Panel.
            // Canvas panels are tagged with their owning Tab in
            // BuildTabPage, so we identify by Tag (a Tab) rather than
            // Dock=Fill -- the latter would match the wrong panel if
            // anything else in the parent chain ever uses Dock=Fill.
            for (Control p = ctl.Parent; p != null; p = p.Parent)
            {
                if (p is Panel pnl && pnl.Tag is Tab)
                {
                    UpdateCanvasAutoScrollMinSize(pnl);
                    break;
                }
            }
        }

        // Decides where a just-dragged button should live (which parent
        // control) and what its GroupId should become.
        //  - Center inside a GroupBox on the canvas → parent into that group.
        //  - Otherwise                              → parent onto the canvas.
        // Returns false if no change is needed (button stayed in the
        // same parent), so the caller can skip the reparent dance.
        private bool TryReparentByDrop(Control ctl, out Control newParent, out string newGroupId, out Point newLocation)
        {
            newParent = null;
            newGroupId = null;
            newLocation = ctl.Location;

            // Find the canvas Panel that ultimately hosts everything for
            // this tab. The button's grandparent in the GroupBox case;
            // its parent in the canvas-direct case.
            Panel canvas = ctl.Parent as Panel;
            if (canvas == null && ctl.Parent is GroupBox gbParent)
                canvas = gbParent.Parent as Panel;
            if (canvas == null) return false;

            var screenCenter = ctl.PointToScreen(new Point(ctl.Width / 2, ctl.Height / 2));
            var canvasCenter = canvas.PointToClient(screenCenter);

            // Look for a GroupBox sibling (child of canvas) whose bounds
            // contain that center. Direct children only — no nested groups.
            GroupBox hitGroup = null;
            foreach (Control c in canvas.Controls)
            {
                if (c is GroupBox gb && gb.Bounds.Contains(canvasCenter))
                {
                    hitGroup = gb;
                    break;
                }
            }

            if (hitGroup != null)
            {
                if (ReferenceEquals(ctl.Parent, hitGroup)) return false; // no-op
                newParent = hitGroup;
                newGroupId = (hitGroup.Tag as ButtonGroup)?.Id;
                var topLeftScreen = ctl.PointToScreen(Point.Empty);
                newLocation = hitGroup.PointToClient(topLeftScreen);
                return true;
            }
            else
            {
                if (ReferenceEquals(ctl.Parent, canvas)) return false; // no-op
                newParent = canvas;
                newGroupId = null;
                var topLeftScreen = ctl.PointToScreen(Point.Empty);
                newLocation = canvas.PointToClient(topLeftScreen);
                return true;
            }
        }

        // ----------------------------------------------------------------
        // Tab strip drag-to-reorder (edit mode only)
        //
        // The TabControl doesn't surface a "begin drag header" event, so
        // we hit-test mouse coordinates against GetTabRect for every tab
        // header. On MouseMove with the left button held we live-shuffle
        // both the workspace model and the TabPages collection — moving
        // pages around without re-rendering preserves child controls,
        // scroll positions, and any unsaved focus state. Save still
        // requires Ctrl+S; we just persist the reorder into the in-memory
        // model so it'll be there when the user does save.
        //
        // The synthetic "+ Add Tab" page and the Welcome page have a null
        // (or non-Tab) Tag and are skipped both as drag source and drop
        // target so users can't accidentally bury "+" between real tabs.
        // ----------------------------------------------------------------

        // -1 means "no tab drag in progress". Stored as the index of the
        // tab page being dragged; rewritten on every successful shuffle
        // so it always tracks the current visual position.
        private int _tabDragSource = -1;

        private void OnTabStripMouseDown(object sender, MouseEventArgs e)
        {
            if (!IsEditMode || e.Button != MouseButtons.Left) return;
            int idx = HitTestTabHeader(e.Location);
            if (idx < 0) return;
            // Only real workspace tabs (Tag is Tab) are draggable. Welcome
            // and the "+" page have non-Tab Tags and stay put.
            if (!(_tabControl.TabPages[idx].Tag is Tab)) return;
            _tabDragSource = idx;
        }

        private void OnTabStripMouseMove(object sender, MouseEventArgs e)
        {
            if (_tabDragSource < 0 || e.Button != MouseButtons.Left) return;

            int target = HitTestTabHeader(e.Location);
            if (target < 0 || target == _tabDragSource) return;

            // Refuse to move a real tab past the "+" / Welcome page —
            // those carry non-Tab Tags. Otherwise we'd silently corrupt
            // the model (the synthetic page has no entry in workspace.Tabs).
            if (!(_tabControl.TabPages[target].Tag is Tab)) return;

            // Update the workspace model. Only proceed if both indices
            // map cleanly — defensive against unexpected list/UI drift.
            if (_workspace?.Tabs != null
                && _tabDragSource < _workspace.Tabs.Count
                && target < _workspace.Tabs.Count)
            {
                var moved = _workspace.Tabs[_tabDragSource];
                _workspace.Tabs.RemoveAt(_tabDragSource);
                _workspace.Tabs.Insert(target, moved);
            }

            // Mirror the move in the visible TabPages collection.
            // Remove/Insert preserves the TabPage instance, so child
            // controls and scroll state survive the reorder — much
            // better UX than calling Render() and rebuilding everything.
            var page = _tabControl.TabPages[_tabDragSource];
            _tabControl.TabPages.Remove(page);
            _tabControl.TabPages.Insert(target, page);
            _tabControl.SelectedTab = page;
            _tabDragSource = target;
        }

        private void OnTabStripMouseUp(object sender, MouseEventArgs e)
        {
            _tabDragSource = -1;
        }

        // Walk the tab strip rects looking for one that contains the
        // given client-space point. Returns -1 when the point is outside
        // every header (e.g. the user clicked the page body or the
        // empty area to the right of the last tab).
        private int HitTestTabHeader(Point clientPoint)
        {
            for (int i = 0; i < _tabControl.TabCount; i++)
            {
                if (_tabControl.GetTabRect(i).Contains(clientPoint)) return i;
            }
            return -1;
        }
    }
}
