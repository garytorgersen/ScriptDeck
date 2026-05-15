using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    partial class HistoryDialog
    {
        private System.ComponentModel.IContainer components = null;

        private SplitContainer splitContainer_Main;
        private DataGridView dataGridView_Runs;
        private TableLayoutPanel tableLayout_Details;
        private Label label_Status;
        private TextBox textBox_Status;
        private Label label_Started;
        private TextBox textBox_Started;
        private Label label_Duration;
        private TextBox textBox_Duration;
        private Label label_Workspace;
        private TextBox textBox_Workspace;
        private Label label_Button;
        private TextBox textBox_Button;
        private Label label_Executor;
        private TextBox textBox_Executor;
        private Label label_Script;
        private TextBox textBox_Script;
        private Label label_WorkingDir;
        private TextBox textBox_WorkingDir;
        private Label label_Args;
        private TextBox textBox_Args;
        private Label label_ExitCode;
        private TextBox textBox_ExitCode;
        private Label label_Error;
        private TextBox textBox_Error;

        private FlowLayoutPanel panel_Buttons;
        private System.Windows.Forms.Button button_Refresh;
        private System.Windows.Forms.Button button_Clear;
        private System.Windows.Forms.Button button_Close;
        private Label label_Count;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            this.splitContainer_Main = new SplitContainer();
            this.dataGridView_Runs = new DataGridView();
            this.tableLayout_Details = new TableLayoutPanel();

            this.label_Status      = new Label();
            this.textBox_Status    = new TextBox();
            this.label_Started     = new Label();
            this.textBox_Started   = new TextBox();
            this.label_Duration    = new Label();
            this.textBox_Duration  = new TextBox();
            this.label_Workspace   = new Label();
            this.textBox_Workspace = new TextBox();
            this.label_Button      = new Label();
            this.textBox_Button    = new TextBox();
            this.label_Executor    = new Label();
            this.textBox_Executor  = new TextBox();
            this.label_Script      = new Label();
            this.textBox_Script    = new TextBox();
            this.label_WorkingDir  = new Label();
            this.textBox_WorkingDir= new TextBox();
            this.label_Args        = new Label();
            this.textBox_Args      = new TextBox();
            this.label_ExitCode    = new Label();
            this.textBox_ExitCode  = new TextBox();
            this.label_Error       = new Label();
            this.textBox_Error     = new TextBox();

            this.panel_Buttons   = new FlowLayoutPanel();
            this.button_Refresh  = new System.Windows.Forms.Button();
            this.button_Clear    = new System.Windows.Forms.Button();
            this.button_Close    = new System.Windows.Forms.Button();
            this.label_Count     = new Label();

            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Main)).BeginInit();
            this.splitContainer_Main.Panel1.SuspendLayout();
            this.splitContainer_Main.Panel2.SuspendLayout();
            this.splitContainer_Main.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Runs)).BeginInit();
            this.tableLayout_Details.SuspendLayout();
            this.panel_Buttons.SuspendLayout();
            this.SuspendLayout();

            //
            // splitContainer_Main
            //
            // Vertical split: grid on top, details below. The details pane
            // is intentionally not auto-sized — a fixed lower band keeps
            // tall scripts (long arg lists) from pushing the grid off-screen.
            this.splitContainer_Main.Dock = DockStyle.Fill;
            this.splitContainer_Main.Orientation = Orientation.Horizontal;
            this.splitContainer_Main.Panel1.Controls.Add(this.dataGridView_Runs);
            this.splitContainer_Main.Panel2.Controls.Add(this.tableLayout_Details);
            // Explicit Size before SplitterDistance — the SplitContainer's
            // default 150x100 design size is shorter than SplitterDistance(280)
            // + default Panel2MinSize(25) and the validator throws at EndInit.
            // Dock=Fill takes over on first layout.
            this.splitContainer_Main.Size = new System.Drawing.Size(800, 500);
            this.splitContainer_Main.SplitterDistance = 280;
            //
            // dataGridView_Runs
            //
            // Read-only grid with row-select (full row highlights on click)
            // and the standard "fill remaining space on the last column"
            // behavior. Columns are populated programmatically in code-behind
            // so we can use BindingList<RunRecord> with custom display values.
            this.dataGridView_Runs.AllowUserToAddRows = false;
            this.dataGridView_Runs.AllowUserToDeleteRows = false;
            this.dataGridView_Runs.AllowUserToResizeRows = false;
            this.dataGridView_Runs.AutoGenerateColumns = false;
            this.dataGridView_Runs.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridView_Runs.Dock = DockStyle.Fill;
            this.dataGridView_Runs.MultiSelect = false;
            this.dataGridView_Runs.ReadOnly = true;
            this.dataGridView_Runs.RowHeadersVisible = false;
            this.dataGridView_Runs.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this.dataGridView_Runs.Name = "dataGridView_Runs";
            this.dataGridView_Runs.SelectionChanged += new System.EventHandler(this.DataGridView_Runs_SelectionChanged);
            //
            // tableLayout_Details
            //
            // 2-column grid: labels (auto width), values (fill). 11 rows for
            // the 11 detail fields. AutoScroll so a long arg list inside the
            // pane scrolls without resizing siblings.
            this.tableLayout_Details.ColumnCount = 2;
            this.tableLayout_Details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            this.tableLayout_Details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            this.tableLayout_Details.RowCount = 11;
            for (int i = 0; i < 11; i++)
                this.tableLayout_Details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            this.tableLayout_Details.Dock = DockStyle.Fill;
            this.tableLayout_Details.AutoScroll = true;
            this.tableLayout_Details.Padding = new Padding(8, 6, 8, 6);

            ConfigureDetailRow(0, this.label_Status,     "Status:",      this.textBox_Status);
            ConfigureDetailRow(1, this.label_Started,    "Started:",     this.textBox_Started);
            ConfigureDetailRow(2, this.label_Duration,   "Duration:",    this.textBox_Duration);
            ConfigureDetailRow(3, this.label_Workspace,  "Workspace:",   this.textBox_Workspace);
            ConfigureDetailRow(4, this.label_Button,     "Button:",      this.textBox_Button);
            ConfigureDetailRow(5, this.label_Executor,   "Executor:",    this.textBox_Executor);
            ConfigureDetailRow(6, this.label_Script,     "Script:",      this.textBox_Script);
            ConfigureDetailRow(7, this.label_WorkingDir, "Working dir:", this.textBox_WorkingDir);
            ConfigureDetailRow(8, this.label_Args,       "Args:",        this.textBox_Args, multiline: true, height: 70);
            ConfigureDetailRow(9, this.label_ExitCode,   "Exit code:",   this.textBox_ExitCode);
            ConfigureDetailRow(10, this.label_Error,     "Error:",       this.textBox_Error, multiline: true, height: 60);

            //
            // panel_Buttons
            //
            // Bottom action bar: count label on the left, action buttons on
            // the right. Right-to-left flow puts Close as the rightmost
            // button (the conventional position for a dismiss action).
            this.panel_Buttons.Dock = DockStyle.Bottom;
            this.panel_Buttons.FlowDirection = FlowDirection.RightToLeft;
            this.panel_Buttons.Height = 38;
            this.panel_Buttons.Padding = new Padding(8, 6, 8, 6);
            this.panel_Buttons.Controls.Add(this.button_Close);
            this.panel_Buttons.Controls.Add(this.button_Refresh);
            this.panel_Buttons.Controls.Add(this.button_Clear);
            this.panel_Buttons.Controls.Add(this.label_Count);

            this.label_Count.AutoSize = true;
            this.label_Count.Anchor = AnchorStyles.Left;
            this.label_Count.Padding = new Padding(8, 8, 0, 0);
            this.label_Count.ForeColor = System.Drawing.SystemColors.GrayText;
            this.label_Count.Text = string.Empty;

            this.button_Close.Text = "Close";
            this.button_Close.Width = 90;
            this.button_Close.DialogResult = DialogResult.Cancel;

            this.button_Refresh.Text = "Refresh";
            this.button_Refresh.Width = 90;
            this.button_Refresh.Click += new System.EventHandler(this.Button_Refresh_Click);

            this.button_Clear.Text = "Clear...";
            this.button_Clear.Width = 90;
            this.button_Clear.Click += new System.EventHandler(this.Button_Clear_Click);

            //
            // HistoryDialog
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(900, 600);
            this.Controls.Add(this.splitContainer_Main);
            this.Controls.Add(this.panel_Buttons);
            this.MinimumSize = new System.Drawing.Size(640, 400);
            this.Name = "HistoryDialog";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Recent Runs";
            this.ShowInTaskbar = false;
            this.CancelButton = this.button_Close;

            this.splitContainer_Main.Panel1.ResumeLayout(false);
            this.splitContainer_Main.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Main)).EndInit();
            this.splitContainer_Main.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Runs)).EndInit();
            this.tableLayout_Details.ResumeLayout(false);
            this.tableLayout_Details.PerformLayout();
            this.panel_Buttons.ResumeLayout(false);
            this.panel_Buttons.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        // Helper: wire one row of the details grid. Each value box is
        // ReadOnly (display-only, but selectable so the user can copy).
        // Multiline is opt-in for fields that legitimately need vertical
        // space (Args, Error). The multiline branch sets a fixed height
        // and Anchor so the value grows with the dialog.
        private void ConfigureDetailRow(
            int row, Label label, string labelText, TextBox value,
            bool multiline = false, int height = 0)
        {
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            label.Padding = new Padding(0, 4, 0, 0);
            label.Text = labelText;

            value.ReadOnly = true;
            value.BorderStyle = BorderStyle.FixedSingle;
            value.BackColor = System.Drawing.SystemColors.Window;
            value.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            value.Margin = new Padding(0, 2, 0, 4);
            if (multiline)
            {
                value.Multiline = true;
                value.Height = height > 0 ? height : 60;
                value.ScrollBars = ScrollBars.Vertical;
                value.Font = new System.Drawing.Font("Consolas", 9F);
                value.WordWrap = false;
            }

            this.tableLayout_Details.Controls.Add(label, 0, row);
            this.tableLayout_Details.Controls.Add(value, 1, row);
        }
    }
}
