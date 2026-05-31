using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    partial class Shell
    {
        private System.ComponentModel.IContainer components = null;

        // ---- Top chrome ----
        private MenuStrip menuStrip;
        private ToolStripMenuItem menu_File;
        private ToolStripMenuItem menu_File_New;
        private ToolStripMenuItem menu_File_Open;
        // Recent Workspaces submenu — populated dynamically on
        // DropDownOpening from the RecentWorkspaces store. The Designer
        // creates the parent item; child items are added/removed at
        // runtime as the list changes.
        private ToolStripMenuItem menu_File_Recent;
        private ToolStripSeparator menu_File_Sep0;
        private ToolStripMenuItem menu_File_Save;
        private ToolStripSeparator menu_File_Sep1;
        private ToolStripMenuItem menu_File_Exit;
        private ToolStripMenuItem menu_Edit;
        private ToolStripMenuItem menu_Edit_ToggleMode;
        private ToolStripMenuItem menu_Edit_SharedInputs;
        // Phase 7 — opens the dialog that lets the user add/rename/delete
        // top-level "workspace menus" (the Customer-defined menus that
        // appear between the built-in Edit and Tools menus at runtime).
        private ToolStripMenuItem menu_Edit_WorkspaceMenus;
        private ToolStripMenuItem menu_Edit_CancelRunning;
        private ToolStripSeparator menu_Edit_Sep1;
        private ToolStripSeparator menu_Edit_Sep2;
        private ToolStripSeparator menu_Edit_Sep3;
        // Phase 6 — Tools menu hosts cross-cutting features that don't
        // belong under File or Edit. Recent Runs is the inaugural entry;
        // future utilities (export workspace, validate scripts, etc.)
        // would slot in here too.
        private ToolStripMenuItem menu_Tools;
        private ToolStripMenuItem menu_Tools_RecentRuns;
        private ToolStripMenuItem menu_Tools_ScriptEditor;
        private ToolStripMenuItem menu_Tools_EditBootstrap;
        private ToolStripMenuItem menu_Tools_ManageComputers;
        private ToolStripSeparator menu_Tools_Sep0;

        // ---- Main content ----
        // Three nested SplitContainers stack the four major regions
        // (shared inputs / tabs / output / logs) with draggable splitters
        // between each. Replaces an earlier TableLayoutPanel that pinned
        // each band to a fixed pixel height. Each SplitContainer's
        // Orientation = Horizontal means the SPLITTER bar is horizontal —
        // so the two panels are stacked top/bottom.
        private SplitContainer splitContainer_Outer;   // sharedInputs | (everything below)
        private SplitContainer splitContainer_Mid;     // tabs         | (output + logs)
        private SplitContainer splitContainer_Lower;   // output       | logs
        private FlowLayoutPanel panel_SharedInputs;
        private TabControl tabControl_Workspace;
        // Placeholder tab shown until a workspace loads. Stored as a field
        // so Phase 2's renderer can call tabControl_Workspace.TabPages.Remove
        // (this.tabPage_Welcome) once real tabs replace it.
        private TabPage tabPage_Welcome;
        private Label label_WelcomeText;
        private SplitContainer splitContainer_Output;
        private RichTextBox richTextBox_Console;
        private DataGridView dataGridView_Results;
        // Splits the lower bottom band horizontally: logs on the left,
        // shared/session inputs grid on the right. Sits inside
        // splitContainer_Lower.Panel2.
        private SplitContainer splitContainer_LogsAndInputs;
        private RichTextBox richTextBox_Logs;
        private InputsGridPanel inputsGridPanel;

        // Toolbar between the tab strip and the output area.
        // Houses a simple find-and-highlight box, four small action
        // buttons (clear/export console, export/popout grid), and two
        // view toggles that collapse / expand the console and grid
        // panels. Action buttons are 28x24 Segoe-MDL2-glyph buttons --
        // tooltipped on hover to keep the strip compact.
        private Panel    panel_Toolbar;
        private Label    label_Search;
        private TextBox  textBox_Search;
        private System.Windows.Forms.Button   button_FindNext;
        private System.Windows.Forms.Button   button_ClearFind;
        private System.Windows.Forms.Button   button_ClearConsole;
        private System.Windows.Forms.Button   button_ExportConsole;
        private System.Windows.Forms.Button   button_ExportGridCsv;
        private System.Windows.Forms.Button   button_GridPopout;
        private ToolTip  toolTip_Toolbar;
        private CheckBox checkBox_ShowConsole;
        private CheckBox checkBox_ShowGrid;

        // Jobs tab: tab control wraps the existing console+grid split
        // (Output page) plus a new Jobs page. The Jobs page hosts a
        // grid of submitted background jobs (top) and an RTB showing
        // the output of whichever job is selected (bottom).
        private TabControl    tabControl_Output;
        private TabPage       tabPage_Output;
        private TabPage       tabPage_Jobs;
        private Panel         panel_JobsToolbar;
        private System.Windows.Forms.Button button_JobCancel;
        private System.Windows.Forms.Button button_JobSendToConsole;
        private System.Windows.Forms.Button button_JobDismiss;
        private SplitContainer splitContainer_Jobs;
        private DataGridView  dataGridView_Jobs;
        private RichTextBox   richTextBox_JobOutput;

        // ---- Bottom chrome ----
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel_Workspace;
        private ToolStripStatusLabel statusLabel_Spring;
        private ToolStripStatusLabel statusLabel_Mode;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            // Instantiate all controls up front so we can wire parents and
            // event handlers in any order below without reading-before-write.
            this.menuStrip = new MenuStrip();
            this.menu_File = new ToolStripMenuItem();
            this.menu_File_New = new ToolStripMenuItem();
            this.menu_File_Open = new ToolStripMenuItem();
            this.menu_File_Recent = new ToolStripMenuItem();
            this.menu_File_Sep0 = new ToolStripSeparator();
            this.menu_File_Save = new ToolStripMenuItem();
            this.menu_File_Sep1 = new ToolStripSeparator();
            this.menu_File_Exit = new ToolStripMenuItem();
            this.menu_Edit = new ToolStripMenuItem();
            this.menu_Edit_ToggleMode = new ToolStripMenuItem();
            this.menu_Edit_SharedInputs = new ToolStripMenuItem();
            this.menu_Edit_CancelRunning = new ToolStripMenuItem();
            this.menu_Edit_Sep1 = new ToolStripSeparator();
            this.menu_Edit_Sep2 = new ToolStripSeparator();
            this.menu_Edit_Sep3 = new ToolStripSeparator();
            this.menu_Edit_WorkspaceMenus = new ToolStripMenuItem();
            this.menu_Tools = new ToolStripMenuItem();
            this.menu_Tools_RecentRuns = new ToolStripMenuItem();
            this.menu_Tools_ScriptEditor = new ToolStripMenuItem();
            this.menu_Tools_EditBootstrap = new ToolStripMenuItem();
            this.menu_Tools_ManageComputers = new ToolStripMenuItem();
            this.menu_Tools_Sep0 = new ToolStripSeparator();

            this.splitContainer_Outer = new SplitContainer();
            this.splitContainer_Mid = new SplitContainer();
            this.splitContainer_Lower = new SplitContainer();
            this.panel_SharedInputs = new FlowLayoutPanel();
            this.tabControl_Workspace = new TabControl();
            this.tabPage_Welcome = new TabPage();
            this.label_WelcomeText = new Label();
            this.splitContainer_Output = new SplitContainer();
            this.richTextBox_Console = new RichTextBox();
            this.dataGridView_Results = new DataGridView();
            this.splitContainer_LogsAndInputs = new SplitContainer();
            this.richTextBox_Logs = new RichTextBox();
            this.inputsGridPanel = new InputsGridPanel();

            this.panel_Toolbar       = new Panel();
            this.label_Search        = new Label();
            this.textBox_Search      = new TextBox();
            this.button_FindNext     = new System.Windows.Forms.Button();
            this.button_ClearFind    = new System.Windows.Forms.Button();
            this.button_ClearConsole = new System.Windows.Forms.Button();
            this.button_ExportConsole = new System.Windows.Forms.Button();
            this.button_ExportGridCsv = new System.Windows.Forms.Button();
            this.button_GridPopout   = new System.Windows.Forms.Button();
            this.toolTip_Toolbar     = new ToolTip();
            this.checkBox_ShowConsole = new CheckBox();
            this.checkBox_ShowGrid    = new CheckBox();

            this.tabControl_Output       = new TabControl();
            this.tabPage_Output          = new TabPage();
            this.tabPage_Jobs            = new TabPage();
            this.panel_JobsToolbar       = new Panel();
            this.button_JobCancel        = new System.Windows.Forms.Button();
            this.button_JobSendToConsole = new System.Windows.Forms.Button();
            this.button_JobDismiss       = new System.Windows.Forms.Button();
            this.splitContainer_Jobs     = new SplitContainer();
            this.dataGridView_Jobs       = new DataGridView();
            this.richTextBox_JobOutput   = new RichTextBox();

            this.statusStrip = new StatusStrip();
            this.statusLabel_Workspace = new ToolStripStatusLabel();
            this.statusLabel_Spring = new ToolStripStatusLabel();
            this.statusLabel_Mode = new ToolStripStatusLabel();

            this.menuStrip.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Outer)).BeginInit();
            this.splitContainer_Outer.Panel1.SuspendLayout();
            this.splitContainer_Outer.Panel2.SuspendLayout();
            this.splitContainer_Outer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Mid)).BeginInit();
            this.splitContainer_Mid.Panel1.SuspendLayout();
            this.splitContainer_Mid.Panel2.SuspendLayout();
            this.splitContainer_Mid.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Lower)).BeginInit();
            this.splitContainer_Lower.Panel1.SuspendLayout();
            this.splitContainer_Lower.Panel2.SuspendLayout();
            this.splitContainer_Lower.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_LogsAndInputs)).BeginInit();
            this.splitContainer_LogsAndInputs.Panel1.SuspendLayout();
            this.splitContainer_LogsAndInputs.Panel2.SuspendLayout();
            this.splitContainer_LogsAndInputs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Output)).BeginInit();
            this.splitContainer_Output.Panel1.SuspendLayout();
            this.splitContainer_Output.Panel2.SuspendLayout();
            this.splitContainer_Output.SuspendLayout();
            this.tabControl_Output.SuspendLayout();
            this.tabPage_Output.SuspendLayout();
            this.tabPage_Jobs.SuspendLayout();
            this.panel_JobsToolbar.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Jobs)).BeginInit();
            this.splitContainer_Jobs.Panel1.SuspendLayout();
            this.splitContainer_Jobs.Panel2.SuspendLayout();
            this.splitContainer_Jobs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Results)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Jobs)).BeginInit();
            this.statusStrip.SuspendLayout();
            this.SuspendLayout();

            //
            // menuStrip
            //
            this.menuStrip.Items.AddRange(new ToolStripItem[] {
                this.menu_File,
                this.menu_Edit,
                this.menu_Tools
            });
            this.menuStrip.Location = new System.Drawing.Point(0, 0);
            this.menuStrip.Name = "menuStrip";
            this.menuStrip.Size = new System.Drawing.Size(1100, 24);
            this.menuStrip.TabIndex = 0;
            //
            // menu_File
            //
            this.menu_File.DropDownItems.AddRange(new ToolStripItem[] {
                this.menu_File_New,
                this.menu_File_Open,
                this.menu_File_Recent,
                this.menu_File_Sep0,
                this.menu_File_Save,
                this.menu_File_Sep1,
                this.menu_File_Exit
            });
            this.menu_File.Name = "menu_File";
            this.menu_File.Text = "&File";
            //
            // menu_File_New
            //
            this.menu_File_New.Name = "menu_File_New";
            this.menu_File_New.Text = "&New Workspace";
            this.menu_File_New.ShortcutKeys = Keys.Control | Keys.N;
            this.menu_File_New.Click += new System.EventHandler(this.menu_File_New_Click);
            //
            // menu_File_Open
            //
            this.menu_File_Open.Name = "menu_File_Open";
            this.menu_File_Open.Text = "&Open Workspace...";
            this.menu_File_Open.ShortcutKeys = Keys.Control | Keys.O;
            this.menu_File_Open.Click += new System.EventHandler(this.menu_File_Open_Click);
            //
            // menu_File_Recent
            //
            // The submenu's items are populated at runtime from the
            // RecentWorkspaces store on DropDownOpening (see Shell.cs).
            // We only set the parent's Text + name here.
            this.menu_File_Recent.Name = "menu_File_Recent";
            this.menu_File_Recent.Text = "Recent &Workspaces";
            this.menu_File_Recent.DropDownOpening += new System.EventHandler(this.menu_File_Recent_DropDownOpening);
            //
            // menu_File_Sep0
            //
            this.menu_File_Sep0.Name = "menu_File_Sep0";
            //
            // menu_File_Save
            //
            this.menu_File_Save.Name = "menu_File_Save";
            this.menu_File_Save.Text = "&Save Workspace";
            this.menu_File_Save.ShortcutKeys = Keys.Control | Keys.S;
            this.menu_File_Save.Click += new System.EventHandler(this.menu_File_Save_Click);
            //
            // menu_File_Sep1
            //
            this.menu_File_Sep1.Name = "menu_File_Sep1";
            //
            // menu_File_Exit
            //
            this.menu_File_Exit.Name = "menu_File_Exit";
            this.menu_File_Exit.Text = "E&xit";
            this.menu_File_Exit.Click += new System.EventHandler(this.menu_File_Exit_Click);
            //
            // menu_Edit
            //
            this.menu_Edit.DropDownItems.AddRange(new ToolStripItem[] {
                this.menu_Edit_ToggleMode,
                this.menu_Edit_Sep1,
                this.menu_Edit_SharedInputs,
                this.menu_Edit_WorkspaceMenus,
                this.menu_Edit_Sep2,
                this.menu_Edit_Sep3,
                this.menu_Edit_CancelRunning
            });
            this.menu_Edit.Name = "menu_Edit";
            this.menu_Edit.Text = "&Edit";
            //
            // menu_Edit_ToggleMode
            //
            this.menu_Edit_ToggleMode.Name = "menu_Edit_ToggleMode";
            this.menu_Edit_ToggleMode.Text = "Toggle &Edit Mode";
            this.menu_Edit_ToggleMode.ShortcutKeys = Keys.Control | Keys.E;
            this.menu_Edit_ToggleMode.Click += new System.EventHandler(this.menu_Edit_ToggleMode_Click);
            //
            // menu_Edit_Sep1
            //
            this.menu_Edit_Sep1.Name = "menu_Edit_Sep1";
            //
            // menu_Edit_Sep2
            //
            this.menu_Edit_Sep2.Name = "menu_Edit_Sep2";
            //
            // menu_Edit_SharedInputs
            //
            this.menu_Edit_SharedInputs.Name = "menu_Edit_SharedInputs";
            this.menu_Edit_SharedInputs.Text = "Edit &Shared Inputs...";
            this.menu_Edit_SharedInputs.Click += new System.EventHandler(this.menu_Edit_SharedInputs_Click);
            //
            // menu_Edit_WorkspaceMenus
            //
            // Phase 7. Opens a TreeView-based editor that manages the
            // workspace's top-level menus + their items. Disabled until
            // a workspace is loaded — same gating as Edit Shared Inputs.
            this.menu_Edit_WorkspaceMenus.Name = "menu_Edit_WorkspaceMenus";
            this.menu_Edit_WorkspaceMenus.Text = "Edit Workspace &Menus...";
            this.menu_Edit_WorkspaceMenus.Click += new System.EventHandler(this.menu_Edit_WorkspaceMenus_Click);
            //
            // menu_Edit_Sep3
            //
            this.menu_Edit_Sep3.Name = "menu_Edit_Sep3";
            //
            // menu_Edit_CancelRunning
            //
            this.menu_Edit_CancelRunning.Name = "menu_Edit_CancelRunning";
            this.menu_Edit_CancelRunning.Text = "&Cancel Running";
            // WinForms rejects bare Escape as a ToolStripMenuItem.ShortcutKeys
            // — modifier or function key required. We instead handle Esc via
            // the form's KeyPreview path in Shell.cs (Shell_KeyDown), and show
            // the hint in ShortcutKeyDisplayString so the menu still reads
            // "Cancel Running    Esc".
            this.menu_Edit_CancelRunning.ShortcutKeyDisplayString = "Esc";
            this.menu_Edit_CancelRunning.Enabled = false;
            this.menu_Edit_CancelRunning.Click += new System.EventHandler(this.menu_Edit_CancelRunning_Click);
            //
            // menu_Tools
            //
            this.menu_Tools.DropDownItems.AddRange(new ToolStripItem[] {
                this.menu_Tools_RecentRuns,
                this.menu_Tools_Sep0,
                this.menu_Tools_ScriptEditor,
                this.menu_Tools_EditBootstrap,
                this.menu_Tools_ManageComputers
            });
            this.menu_Tools.Name = "menu_Tools";
            this.menu_Tools.Text = "&Tools";
            //
            // menu_Tools_RecentRuns
            //
            // Ctrl+H = History — same convention browsers use, so it's
            // the muscle-memory shortcut. The dialog reads from the
            // SQLite store on Open and never blocks the UI thread.
            this.menu_Tools_RecentRuns.Name = "menu_Tools_RecentRuns";
            this.menu_Tools_RecentRuns.Text = "&Recent Runs...";
            this.menu_Tools_RecentRuns.ShortcutKeys = Keys.Control | Keys.H;
            this.menu_Tools_RecentRuns.Click += new System.EventHandler(this.menu_Tools_RecentRuns_Click);
            //
            // menu_Tools_Sep0 -- divider between read-only tools and authoring tools.
            //
            this.menu_Tools_Sep0.Name = "menu_Tools_Sep0";
            //
            // menu_Tools_ScriptEditor
            //
            // Standalone Script Editor (also reachable from EditButtonDialog's
            // "Edit Script..." button when binding a script to a button).
            // F7 follows the editor convention used by other authoring tools
            // (PowerShell ISE, VS), staying out of the Ctrl+letter range.
            this.menu_Tools_ScriptEditor.Name = "menu_Tools_ScriptEditor";
            this.menu_Tools_ScriptEditor.Text = "&Script Editor...";
            this.menu_Tools_ScriptEditor.ShortcutKeys = Keys.F7;
            this.menu_Tools_ScriptEditor.Click += new System.EventHandler(this.menu_Tools_ScriptEditor_Click);
            //
            // menu_Tools_EditBootstrap
            //
            // Opens ScriptDeck.Bootstrap.ps1 (the file dot-sourced into
            // every fresh runspace) in the same Script Editor used for
            // workspace scripts. On close, if the file's mtime changed,
            // the handler offers to reset the live PS runspaces so the
            // edited helpers take effect without an app restart. Saving
            // writes in-place; if the install lives in a write-protected
            // location (e.g. Program Files), the editor's save will
            // surface the OS error and nothing is reset.
            this.menu_Tools_EditBootstrap.Name = "menu_Tools_EditBootstrap";
            this.menu_Tools_EditBootstrap.Text = "Edit &Bootstrap Helper...";
            this.menu_Tools_EditBootstrap.Click += new System.EventHandler(this.menu_Tools_EditBootstrap_Click);
            //
            // menu_Tools_ManageComputers
            //
            // ScriptDeck-wide list of computer names backing the
            // dropdown rendered for any shared input with
            // normalize=="computerName". Stored as JSON at
            // %LocalAppData%\ScriptDeck\computers.json. Manage is the
            // only edit surface -- the dropdown itself is select-or-
            // type, never edit-the-list.
            this.menu_Tools_ManageComputers.Name = "menu_Tools_ManageComputers";
            this.menu_Tools_ManageComputers.Text = "&Manage Computers...";
            this.menu_Tools_ManageComputers.Click += new System.EventHandler(this.menu_Tools_ManageComputers_Click);

            //
            // splitContainer_Outer — top: shared inputs band, bottom: everything else
            //
            // Replaces the old TableLayoutPanel root. Orientation = Horizontal
            // means the splitter bar runs left-to-right (panels stack vertically).
            // Panel1MinSize keeps the shared-inputs band from being collapsed
            // out of view; Panel2MinSize keeps the rest of the UI usable.
            this.splitContainer_Outer.Dock = DockStyle.Fill;
            this.splitContainer_Outer.Orientation = Orientation.Horizontal;
            this.splitContainer_Outer.SplitterWidth = 5;
            // Explicit Size BEFORE SplitterDistance / Panel*MinSize. The
            // SplitContainer validates each setter against current size, and
            // its design-time default (150x100) is too small to fit
            // Panel2MinSize=200 + SplitterDistance=110, throwing
            // InvalidOperationException at EndInit. The actual runtime size
            // is overridden by Dock=Fill on the first layout pass — the
            // value here just has to satisfy the validator.
            this.splitContainer_Outer.Size = new System.Drawing.Size(1100, 600);
            this.splitContainer_Outer.SplitterDistance = 110;
            this.splitContainer_Outer.Panel1MinSize = 40;
            this.splitContainer_Outer.Panel2MinSize = 200;
            this.splitContainer_Outer.Name = "splitContainer_Outer";
            this.splitContainer_Outer.Panel1.Controls.Add(this.panel_SharedInputs);
            this.splitContainer_Outer.Panel2.Controls.Add(this.splitContainer_Mid);
            this.splitContainer_Outer.TabIndex = 1;
            //
            // splitContainer_Mid — top: tabs, bottom: output + logs
            //
            // Splitter between the tab strip and the lower output/logs area.
            // Initial 220 keeps the 3-row button layout visible without scroll
            // on the typical workspace; users can drag down for more space.
            this.splitContainer_Mid.Dock = DockStyle.Fill;
            this.splitContainer_Mid.Orientation = Orientation.Horizontal;
            this.splitContainer_Mid.SplitterWidth = 5;
            // See splitContainer_Outer for why we explicit-Size before
            // setting SplitterDistance + Panel2MinSize.
            this.splitContainer_Mid.Size = new System.Drawing.Size(1100, 600);
            this.splitContainer_Mid.SplitterDistance = 220;
            this.splitContainer_Mid.Panel1MinSize = 80;
            this.splitContainer_Mid.Panel2MinSize = 120;
            this.splitContainer_Mid.Name = "splitContainer_Mid";
            this.splitContainer_Mid.Panel1.Controls.Add(this.tabControl_Workspace);
            // Order matters for Dock layout: Fill control added FIRST so the
            // later Top-docked toolbar claims the upper strip and the
            // splitContainer_Lower fills the remainder. Reverse order
            // works in WinForms too but reads less obviously.
            this.splitContainer_Mid.Panel2.Controls.Add(this.splitContainer_Lower);
            this.splitContainer_Mid.Panel2.Controls.Add(this.panel_Toolbar);

            //
            // panel_Toolbar -- search bar + view toggles, Dock=Top.
            //
            // Sits between the tab strip and the output area. Single thin
            // band keeps it discoverable without claiming much real estate.
            // The search highlights matches in the console RTB and the
            // results grid; the toggles collapse one of them so the other
            // expands to fill the entire output area.
            this.panel_Toolbar.Dock = DockStyle.Top;
            // Size pinned explicitly BEFORE anchored children are
            // configured. Anchor=Top|Right captures the right-edge
            // distance the moment the child's Anchor is set; if the
            // panel's still at its default Panel size (200x100) at
            // that moment, every right-anchored control ends up with
            // a hugely negative right-distance and renders ~800px
            // off-screen. Dock=Top + matching design width = correct
            // anchor distances + correct runtime layout.
            this.panel_Toolbar.Size = new System.Drawing.Size(1100, 32);
            this.panel_Toolbar.Height = 32;
            this.panel_Toolbar.BackColor = System.Drawing.SystemColors.Control;
            this.panel_Toolbar.Padding = new Padding(8, 4, 8, 4);
            //
            // label_Search
            //
            this.label_Search.AutoSize = true;
            this.label_Search.Location = new System.Drawing.Point(10, 8);
            this.label_Search.Text = "Find:";
            //
            // textBox_Search
            //
            this.textBox_Search.Location = new System.Drawing.Point(48, 5);
            this.textBox_Search.Size = new System.Drawing.Size(280, 22);
            this.textBox_Search.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            this.textBox_Search.KeyDown += new KeyEventHandler(this.textBox_Search_KeyDown);
            //
            // button_FindNext
            //
            this.button_FindNext.Location = new System.Drawing.Point(334, 4);
            this.button_FindNext.Size = new System.Drawing.Size(64, 24);
            this.button_FindNext.Text = "Find";
            this.button_FindNext.UseVisualStyleBackColor = true;
            this.button_FindNext.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            this.button_FindNext.Click += new System.EventHandler(this.button_FindNext_Click);
            //
            // button_ClearFind
            //
            this.button_ClearFind.Location = new System.Drawing.Point(404, 4);
            this.button_ClearFind.Size = new System.Drawing.Size(64, 24);
            this.button_ClearFind.Text = "Clear";
            this.button_ClearFind.UseVisualStyleBackColor = true;
            this.button_ClearFind.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            this.button_ClearFind.Click += new System.EventHandler(this.button_ClearFind_Click);
            //
            // Console/grid action buttons -- 28x24 glyph buttons
            // pinned to the right edge of the toolbar. Each shows a
            // single Segoe MDL2 Assets glyph as Text; the actual
            // meaning surfaces via toolTip_Toolbar on hover (kept
            // terse so the strip stays compact). Same handlers are
            // re-used by the right-click context menus on the RTB
            // and grid.
            //
            // Layout (right-to-left): [Popout 1064] [CSV 1032]
            // [Export console 1000] [Clear console 968]. The Show
            // Console / Show Grid checkboxes sit just to their left
            // (775 / 880). All anchored Top|Right with
            // panel_Toolbar's design width pinned to 1100 above, so
            // the anchor distances stay correct when the form is
            // resized.
            this.button_ClearConsole.Location = new System.Drawing.Point(968, 4);
            this.button_ClearConsole.Size     = new System.Drawing.Size(28, 24);
            this.button_ClearConsole.Font     = new System.Drawing.Font("Segoe MDL2 Assets", 10F);
            this.button_ClearConsole.Text     = ""; // Delete (trash can)
            this.button_ClearConsole.UseVisualStyleBackColor = true;
            this.button_ClearConsole.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            this.button_ClearConsole.TabStop  = false;
            this.button_ClearConsole.Click   += new System.EventHandler(this.button_ClearConsole_Click);

            this.button_ExportConsole.Location = new System.Drawing.Point(1000, 4);
            this.button_ExportConsole.Size     = new System.Drawing.Size(28, 24);
            this.button_ExportConsole.Font     = new System.Drawing.Font("Segoe MDL2 Assets", 10F);
            this.button_ExportConsole.Text     = ""; // Save (disk)
            this.button_ExportConsole.UseVisualStyleBackColor = true;
            this.button_ExportConsole.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            this.button_ExportConsole.TabStop  = false;
            this.button_ExportConsole.Click   += new System.EventHandler(this.button_ExportConsole_Click);

            this.button_ExportGridCsv.Location = new System.Drawing.Point(1032, 4);
            this.button_ExportGridCsv.Size     = new System.Drawing.Size(28, 24);
            this.button_ExportGridCsv.Font     = new System.Drawing.Font("Segoe MDL2 Assets", 10F);
            this.button_ExportGridCsv.Text     = ""; // SaveAs (disk + arrow)
            this.button_ExportGridCsv.UseVisualStyleBackColor = true;
            this.button_ExportGridCsv.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            this.button_ExportGridCsv.TabStop  = false;
            this.button_ExportGridCsv.Click   += new System.EventHandler(this.button_ExportGridCsv_Click);

            this.button_GridPopout.Location = new System.Drawing.Point(1064, 4);
            this.button_GridPopout.Size     = new System.Drawing.Size(28, 24);
            this.button_GridPopout.Font     = new System.Drawing.Font("Segoe MDL2 Assets", 10F);
            this.button_GridPopout.Text     = ""; // OpenInNewWindow
            this.button_GridPopout.UseVisualStyleBackColor = true;
            this.button_GridPopout.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            this.button_GridPopout.TabStop  = false;
            this.button_GridPopout.Click   += new System.EventHandler(this.button_GridPopout_Click);

            // Tooltips. 600ms initial show is the WinForms default; we
            // keep it explicit so a future restyle doesn't accidentally
            // disable hover hints.
            this.toolTip_Toolbar.AutoPopDelay = 6000;
            this.toolTip_Toolbar.InitialDelay = 500;
            this.toolTip_Toolbar.ReshowDelay  = 200;
            this.toolTip_Toolbar.SetToolTip(this.button_ClearConsole,  "Clear console text");
            this.toolTip_Toolbar.SetToolTip(this.button_ExportConsole, "Export console text...");
            this.toolTip_Toolbar.SetToolTip(this.button_ExportGridCsv, "Export grid to CSV...");
            this.toolTip_Toolbar.SetToolTip(this.button_GridPopout,    "Open grid in a separate window");
            //
            // checkBox_ShowConsole / checkBox_ShowGrid
            //
            // Both checked by default. The change handler enforces the
            // "at least one must be checked" invariant by silently re-
            // checking the box the user just unchecked when it would
            // have left zero panels visible.
            // Shifted left to make room for the four glyph buttons
            // that now anchor to the right edge. Reading order on the
            // right side of the toolbar: [Show Console 775] [Show Grid
            // 880] [Clear 968] [Export 1000] [CSV 1032] [Popout 1064].
            this.checkBox_ShowConsole.AutoSize = true;
            this.checkBox_ShowConsole.Checked = true;
            this.checkBox_ShowConsole.Text = "Show Console";
            this.checkBox_ShowConsole.Location = new System.Drawing.Point(775, 7);
            this.checkBox_ShowConsole.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.checkBox_ShowConsole.CheckedChanged += new System.EventHandler(this.checkBox_ShowConsole_CheckedChanged);

            this.checkBox_ShowGrid.AutoSize = true;
            this.checkBox_ShowGrid.Checked = true;
            this.checkBox_ShowGrid.Text = "Show Grid";
            this.checkBox_ShowGrid.Location = new System.Drawing.Point(880, 7);
            this.checkBox_ShowGrid.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.checkBox_ShowGrid.CheckedChanged += new System.EventHandler(this.checkBox_ShowGrid_CheckedChanged);

            this.panel_Toolbar.Controls.Add(this.label_Search);
            this.panel_Toolbar.Controls.Add(this.textBox_Search);
            this.panel_Toolbar.Controls.Add(this.button_FindNext);
            this.panel_Toolbar.Controls.Add(this.button_ClearFind);
            this.panel_Toolbar.Controls.Add(this.button_ClearConsole);
            this.panel_Toolbar.Controls.Add(this.button_ExportConsole);
            this.panel_Toolbar.Controls.Add(this.button_ExportGridCsv);
            this.panel_Toolbar.Controls.Add(this.button_GridPopout);
            this.panel_Toolbar.Controls.Add(this.checkBox_ShowConsole);
            this.panel_Toolbar.Controls.Add(this.checkBox_ShowGrid);
            //
            // splitContainer_Lower — top: output split (console|grid), bottom: logs
            //
            // The existing splitContainer_Output (vertical split between the
            // console RTB and the results grid) becomes Panel1 here. Panel2 is
            // the log RTB. Keeping the existing horizontal output split intact
            // means the console|grid divider behavior is unchanged.
            this.splitContainer_Lower.Dock = DockStyle.Fill;
            this.splitContainer_Lower.Orientation = Orientation.Horizontal;
            this.splitContainer_Lower.SplitterWidth = 5;
            // See splitContainer_Outer for why we explicit-Size before
            // setting SplitterDistance + Panel2MinSize.
            this.splitContainer_Lower.Size = new System.Drawing.Size(1100, 600);
            this.splitContainer_Lower.SplitterDistance = 290;
            this.splitContainer_Lower.Panel1MinSize = 80;
            this.splitContainer_Lower.Panel2MinSize = 40;
            this.splitContainer_Lower.Name = "splitContainer_Lower";
            // Output area is wrapped in a TabControl so the user can flip
            // between the foreground Console+Grid split and the Jobs view
            // without losing either.
            this.splitContainer_Lower.Panel1.Controls.Add(this.tabControl_Output);
            // Bottom band: vertical split between Logs (left) and the
            // Inputs grid (right). See splitContainer_LogsAndInputs
            // section below for the rationale on the 70/30 default split.
            this.splitContainer_Lower.Panel2.Controls.Add(this.splitContainer_LogsAndInputs);

            //
            // splitContainer_LogsAndInputs — left: logs RTB, right: inputs grid
            //
            // Vertical split (splitter bar is vertical, panels side by side).
            // Default 70/30 favors the logs since that's what the user is
            // typically watching; the grid just needs enough width to show
            // an id + short value comfortably. Users can drag to suit.
            this.splitContainer_LogsAndInputs.Dock = DockStyle.Fill;
            this.splitContainer_LogsAndInputs.Orientation = Orientation.Vertical;
            this.splitContainer_LogsAndInputs.SplitterWidth = 5;
            // See splitContainer_Outer for why we explicit-Size before
            // setting SplitterDistance + Panel*MinSize.
            this.splitContainer_LogsAndInputs.Size = new System.Drawing.Size(1100, 300);
            this.splitContainer_LogsAndInputs.SplitterDistance = 770;
            this.splitContainer_LogsAndInputs.Panel1MinSize = 120;
            this.splitContainer_LogsAndInputs.Panel2MinSize = 180;
            this.splitContainer_LogsAndInputs.Name = "splitContainer_LogsAndInputs";
            this.splitContainer_LogsAndInputs.Panel1.Controls.Add(this.richTextBox_Logs);
            this.splitContainer_LogsAndInputs.Panel2.Controls.Add(this.inputsGridPanel);

            //
            // inputsGridPanel
            //
            // UserControl that hosts the Add Static / Add Volatile toolbar
            // plus the grid. Shell wires its events (Add* / Remove /
            // ClearVolatile / VolatileValueEdited) to handlers that
            // mutate either the workspace model (Static) or the session-
            // input dict (Volatile).
            this.inputsGridPanel.Dock = DockStyle.Fill;
            this.inputsGridPanel.Name = "inputsGridPanel";

            //
            // tabControl_Output -- two pages, "Output" (existing
            // console+grid) and "Jobs" (background queue UI).
            //
            this.tabControl_Output.Dock = DockStyle.Fill;
            this.tabControl_Output.TabPages.Add(this.tabPage_Output);
            this.tabControl_Output.TabPages.Add(this.tabPage_Jobs);

            this.tabPage_Output.Text = "Output";
            this.tabPage_Output.UseVisualStyleBackColor = true;
            this.tabPage_Output.Padding = new Padding(0);
            this.tabPage_Output.Controls.Add(this.splitContainer_Output);

            this.tabPage_Jobs.Text = "Jobs";
            this.tabPage_Jobs.UseVisualStyleBackColor = true;
            this.tabPage_Jobs.Padding = new Padding(0);
            // Children added in reverse Z-order: Fill first, Top last,
            // so layout resolves cleanly.
            this.tabPage_Jobs.Controls.Add(this.splitContainer_Jobs);
            this.tabPage_Jobs.Controls.Add(this.panel_JobsToolbar);

            //
            // panel_JobsToolbar -- compact button strip across the top.
            //
            this.panel_JobsToolbar.Dock = DockStyle.Top;
            this.panel_JobsToolbar.Height = 30;
            this.panel_JobsToolbar.BackColor = System.Drawing.SystemColors.Control;
            this.panel_JobsToolbar.Padding = new Padding(6, 3, 6, 3);

            this.button_JobCancel.Text = "Cancel Job";
            this.button_JobCancel.Location = new System.Drawing.Point(8, 3);
            this.button_JobCancel.Size = new System.Drawing.Size(85, 24);
            this.button_JobCancel.UseVisualStyleBackColor = true;
            this.button_JobCancel.Click += new System.EventHandler(this.button_JobCancel_Click);

            this.button_JobSendToConsole.Text = "Send to Console";
            this.button_JobSendToConsole.Location = new System.Drawing.Point(99, 3);
            this.button_JobSendToConsole.Size = new System.Drawing.Size(120, 24);
            this.button_JobSendToConsole.UseVisualStyleBackColor = true;
            this.button_JobSendToConsole.Click += new System.EventHandler(this.button_JobSendToConsole_Click);

            this.button_JobDismiss.Text = "Dismiss";
            this.button_JobDismiss.Location = new System.Drawing.Point(225, 3);
            this.button_JobDismiss.Size = new System.Drawing.Size(80, 24);
            this.button_JobDismiss.UseVisualStyleBackColor = true;
            this.button_JobDismiss.Click += new System.EventHandler(this.button_JobDismiss_Click);

            this.panel_JobsToolbar.Controls.Add(this.button_JobCancel);
            this.panel_JobsToolbar.Controls.Add(this.button_JobSendToConsole);
            this.panel_JobsToolbar.Controls.Add(this.button_JobDismiss);

            //
            // splitContainer_Jobs -- list on top, output RTB on bottom.
            //
            this.splitContainer_Jobs.Dock = DockStyle.Fill;
            this.splitContainer_Jobs.Orientation = Orientation.Horizontal;
            // Explicit Size + sane min sizes so the splitter validator
            // doesn't blow up at EndInit (same trick the other splits
            // use throughout this Designer).
            this.splitContainer_Jobs.Size = new System.Drawing.Size(1100, 400);
            this.splitContainer_Jobs.SplitterDistance = 150;
            this.splitContainer_Jobs.Panel1MinSize = 60;
            this.splitContainer_Jobs.Panel2MinSize = 60;
            this.splitContainer_Jobs.Panel1.Controls.Add(this.dataGridView_Jobs);
            this.splitContainer_Jobs.Panel2.Controls.Add(this.richTextBox_JobOutput);

            this.dataGridView_Jobs.Dock = DockStyle.Fill;
            this.dataGridView_Jobs.AllowUserToAddRows = false;
            this.dataGridView_Jobs.AllowUserToDeleteRows = false;
            this.dataGridView_Jobs.AllowUserToResizeRows = false;
            this.dataGridView_Jobs.RowHeadersVisible = false;
            this.dataGridView_Jobs.MultiSelect = false;
            this.dataGridView_Jobs.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this.dataGridView_Jobs.ReadOnly = true;
            this.dataGridView_Jobs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            this.dataGridView_Jobs.SelectionChanged += new System.EventHandler(this.dataGridView_Jobs_SelectionChanged);

            // Black-on-light-gray output pane mirrors the foreground
            // console RTB so the visual contract stays consistent.
            this.richTextBox_JobOutput.Dock = DockStyle.Fill;
            this.richTextBox_JobOutput.BackColor = System.Drawing.Color.Black;
            this.richTextBox_JobOutput.ForeColor = System.Drawing.Color.LightGray;
            this.richTextBox_JobOutput.Font = new System.Drawing.Font("Consolas", 9.75F);
            this.richTextBox_JobOutput.ReadOnly = true;
            this.richTextBox_JobOutput.WordWrap = false;
            this.richTextBox_JobOutput.ScrollBars = RichTextBoxScrollBars.Both;
            //
            // panel_SharedInputs
            //
            // Black band across the top hosting the workspace's shared
            // input fields. ~120px tall, docked top. FlowLayout so
            // workspace-defined inputs lay out left-to-right and wrap if many.
            // Always visible — when no workspace is loaded it's an empty
            // black band, which looks deliberate rather than "missing UI."
            this.panel_SharedInputs.BackColor = System.Drawing.Color.Black;
            this.panel_SharedInputs.Dock = DockStyle.Fill;
            this.panel_SharedInputs.FlowDirection = FlowDirection.LeftToRight;
            this.panel_SharedInputs.ForeColor = System.Drawing.Color.LightGreen;
            this.panel_SharedInputs.Margin = new Padding(0);
            this.panel_SharedInputs.Name = "panel_SharedInputs";
            this.panel_SharedInputs.Padding = new Padding(8, 6, 8, 6);
            this.panel_SharedInputs.WrapContents = true;
            //
            // tabControl_Workspace
            //
            this.tabControl_Workspace.Dock = DockStyle.Fill;
            this.tabControl_Workspace.Margin = new Padding(4, 0, 4, 4);
            this.tabControl_Workspace.Name = "tabControl_Workspace";
            this.tabControl_Workspace.TabIndex = 2;
            // Add the Welcome placeholder so the tab strip + page area are
            // visible from launch. Phase 2's renderer removes this page
            // when a workspace loads.
            this.tabControl_Workspace.Controls.Add(this.tabPage_Welcome);
            //
            // tabPage_Welcome
            //
            this.tabPage_Welcome.Name = "tabPage_Welcome";
            this.tabPage_Welcome.Text = "Welcome";
            this.tabPage_Welcome.UseVisualStyleBackColor = true;
            this.tabPage_Welcome.Padding = new Padding(12);
            this.tabPage_Welcome.Controls.Add(this.label_WelcomeText);
            //
            // label_WelcomeText
            //
            this.label_WelcomeText.AutoSize = false;
            this.label_WelcomeText.Dock = DockStyle.Fill;
            this.label_WelcomeText.Font = new System.Drawing.Font("Segoe UI", 10F);
            this.label_WelcomeText.Name = "label_WelcomeText";
            this.label_WelcomeText.Text =
                "No workspace loaded.\r\n\r\n" +
                "Open an existing workspace:    File \u2192 Open Workspace  (Ctrl+O)\r\n" +
                "Create a new workspace:         File \u2192 New Workspace   (Ctrl+N)\r\n\r\n" +
                "A workspace defines the tabs, buttons, and shared input fields you see here. " +
                "Each button maps to a PowerShell script, a CMD script, or an executable. " +
                "Output streams into the console below; structured results land in the grid.";
            this.label_WelcomeText.TextAlign = System.Drawing.ContentAlignment.TopLeft;
            //
            // splitContainer_Output
            //
            // Console takes ~60% of horizontal space, grid the rest. Most
            // PS / cmd output is unstructured text, so the console is the
            // wider half by default.
            this.splitContainer_Output.Dock = DockStyle.Fill;
            this.splitContainer_Output.Margin = new Padding(4, 0, 4, 0);
            this.splitContainer_Output.Name = "splitContainer_Output";
            this.splitContainer_Output.Panel1.Controls.Add(this.richTextBox_Console);
            this.splitContainer_Output.Panel2.Controls.Add(this.dataGridView_Results);
            this.splitContainer_Output.Size = new System.Drawing.Size(1092, 290);
            this.splitContainer_Output.SplitterDistance = 660;
            this.splitContainer_Output.TabIndex = 3;
            //
            // richTextBox_Console
            //
            this.richTextBox_Console.BackColor = System.Drawing.Color.Black;
            this.richTextBox_Console.BorderStyle = BorderStyle.FixedSingle;
            this.richTextBox_Console.Dock = DockStyle.Fill;
            this.richTextBox_Console.Font = new System.Drawing.Font("Consolas", 9.75F);
            this.richTextBox_Console.ForeColor = System.Drawing.Color.LightGreen;
            this.richTextBox_Console.HideSelection = false;
            this.richTextBox_Console.Name = "richTextBox_Console";
            this.richTextBox_Console.ReadOnly = true;
            this.richTextBox_Console.WordWrap = false;
            this.richTextBox_Console.DetectUrls = false;
            //
            // dataGridView_Results
            //
            this.dataGridView_Results.AllowUserToAddRows = false;
            this.dataGridView_Results.AllowUserToDeleteRows = false;
            this.dataGridView_Results.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridView_Results.Dock = DockStyle.Fill;
            this.dataGridView_Results.Name = "dataGridView_Results";
            this.dataGridView_Results.ReadOnly = true;
            this.dataGridView_Results.RowHeadersWidth = 24;
            //
            // richTextBox_Logs
            //
            this.richTextBox_Logs.BackColor = System.Drawing.Color.FromArgb(20, 20, 20);
            this.richTextBox_Logs.BorderStyle = BorderStyle.FixedSingle;
            this.richTextBox_Logs.Dock = DockStyle.Fill;
            this.richTextBox_Logs.Font = new System.Drawing.Font("Consolas", 9F);
            this.richTextBox_Logs.ForeColor = System.Drawing.Color.WhiteSmoke;
            this.richTextBox_Logs.Margin = new Padding(4, 4, 4, 4);
            this.richTextBox_Logs.Name = "richTextBox_Logs";
            this.richTextBox_Logs.ReadOnly = true;
            this.richTextBox_Logs.WordWrap = false;
            this.richTextBox_Logs.DetectUrls = false;
            //
            // statusStrip
            //
            this.statusStrip.Items.AddRange(new ToolStripItem[] {
                this.statusLabel_Workspace,
                this.statusLabel_Spring,
                this.statusLabel_Mode
            });
            this.statusStrip.Location = new System.Drawing.Point(0, 678);
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.Size = new System.Drawing.Size(1100, 22);
            this.statusStrip.TabIndex = 4;
            //
            // statusLabel_Workspace
            //
            this.statusLabel_Workspace.Name = "statusLabel_Workspace";
            this.statusLabel_Workspace.Text = "No workspace";
            //
            // statusLabel_Spring (filler so workspace label hugs left, mode hugs right)
            //
            this.statusLabel_Spring.Name = "statusLabel_Spring";
            this.statusLabel_Spring.Spring = true;
            //
            // statusLabel_Mode
            //
            this.statusLabel_Mode.Name = "statusLabel_Mode";
            this.statusLabel_Mode.Text = "Run mode";
            //
            // Shell
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1100, 700);
            this.Controls.Add(this.splitContainer_Outer);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.menuStrip);
            this.MainMenuStrip = this.menuStrip;
            this.Name = "Shell";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "ScriptDeck";
            // KeyPreview lets the form see key events before any focused
            // child consumes them — required for our Escape-to-cancel
            // hotkey since menu shortcuts can't bind bare Escape.
            this.KeyPreview = true;
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.Shell_KeyDown);

            this.menuStrip.ResumeLayout(false);
            this.menuStrip.PerformLayout();
            this.splitContainer_Output.Panel1.ResumeLayout(false);
            this.splitContainer_Output.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Output)).EndInit();
            this.splitContainer_Output.ResumeLayout(false);
            this.tabPage_Output.ResumeLayout(false);
            this.panel_JobsToolbar.ResumeLayout(false);
            this.splitContainer_Jobs.Panel1.ResumeLayout(false);
            this.splitContainer_Jobs.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Jobs)).EndInit();
            this.splitContainer_Jobs.ResumeLayout(false);
            this.tabPage_Jobs.ResumeLayout(false);
            this.tabControl_Output.ResumeLayout(false);
            // Resume the inner Logs|Inputs split BEFORE its parent (splitContainer_Lower.Panel2).
            this.splitContainer_LogsAndInputs.Panel1.ResumeLayout(false);
            this.splitContainer_LogsAndInputs.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_LogsAndInputs)).EndInit();
            this.splitContainer_LogsAndInputs.ResumeLayout(false);
            this.splitContainer_Lower.Panel1.ResumeLayout(false);
            this.splitContainer_Lower.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Lower)).EndInit();
            this.splitContainer_Lower.ResumeLayout(false);
            this.splitContainer_Mid.Panel1.ResumeLayout(false);
            this.splitContainer_Mid.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Mid)).EndInit();
            this.splitContainer_Mid.ResumeLayout(false);
            this.splitContainer_Outer.Panel1.ResumeLayout(false);
            this.splitContainer_Outer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Outer)).EndInit();
            this.splitContainer_Outer.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Results)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Jobs)).EndInit();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
