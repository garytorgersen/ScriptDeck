using System.Windows.Forms;
using ScintillaNET;

namespace ScriptDeck.Forms
{
    partial class ScriptEditorDialog
    {
        private System.ComponentModel.IContainer components = null;

        // Top bar -- path field + browse
        private Label label_Path;
        private TextBox textBox_Path;
        private Button button_Browse;

        // Editor (top half) and Output (bottom half), split vertically
        private SplitContainer splitContainer_EditorOutput;
        private Scintilla scintilla_Editor;
        private Panel panel_OutputContainer;
        private Label label_Inputs;
        private DataGridView dataGridView_Inputs;
        private RichTextBox richTextBox_Output;

        // Action row at the bottom
        private Button button_RunTest;
        private Button button_CancelTest;
        private Button button_InsertTemplate;
        private Label  label_Format;
        private ComboBox comboBox_Format;
        private Button button_Save;
        private Button button_SaveAs;
        private Button button_Close;

        // Status strip -- syntax error count + run state
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel_Syntax;
        private ToolStripStatusLabel statusLabel_Spring;
        private ToolStripStatusLabel statusLabel_RunState;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.label_Path     = new Label();
            this.textBox_Path   = new TextBox();
            this.button_Browse  = new Button();

            this.splitContainer_EditorOutput = new SplitContainer();
            this.scintilla_Editor      = new Scintilla();
            this.panel_OutputContainer = new Panel();
            this.label_Inputs          = new Label();
            this.dataGridView_Inputs   = new DataGridView();
            this.richTextBox_Output    = new RichTextBox();

            this.button_RunTest        = new Button();
            this.button_CancelTest     = new Button();
            this.button_InsertTemplate = new Button();
            this.label_Format          = new Label();
            this.comboBox_Format       = new ComboBox();
            this.button_Save           = new Button();
            this.button_SaveAs         = new Button();
            this.button_Close          = new Button();

            this.statusStrip          = new StatusStrip();
            this.statusLabel_Syntax   = new ToolStripStatusLabel();
            this.statusLabel_Spring   = new ToolStripStatusLabel();
            this.statusLabel_RunState = new ToolStripStatusLabel();

            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_EditorOutput)).BeginInit();
            this.splitContainer_EditorOutput.Panel1.SuspendLayout();
            this.splitContainer_EditorOutput.Panel2.SuspendLayout();
            this.splitContainer_EditorOutput.SuspendLayout();
            this.panel_OutputContainer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Inputs)).BeginInit();
            this.statusStrip.SuspendLayout();
            this.SuspendLayout();

            //
            // label_Path
            //
            this.label_Path.AutoSize = true;
            this.label_Path.Location = new System.Drawing.Point(12, 14);
            this.label_Path.Text = "Script path:";
            //
            // textBox_Path
            //
            // Anchored top+left+right so the path field grows with the
            // dialog width but the Browse button stays glued to the right.
            this.textBox_Path.Location = new System.Drawing.Point(90, 11);
            this.textBox_Path.Size = new System.Drawing.Size(700, 22);
            this.textBox_Path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            //
            // button_Browse
            //
            this.button_Browse.Location = new System.Drawing.Point(796, 9);
            this.button_Browse.Size = new System.Drawing.Size(80, 26);
            this.button_Browse.Text = "Browse...";
            this.button_Browse.UseVisualStyleBackColor = true;
            this.button_Browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.button_Browse.Click += new System.EventHandler(this.Button_Browse_Click);

            //
            // splitContainer_EditorOutput
            //
            // Editor on top, Output on bottom. Explicit Size before
            // SplitterDistance / Panel2MinSize -- see Shell.Designer.cs
            // for the rationale: SplitContainer's design-time default is
            // 150x100 which violates Panel2MinSize=120 + SplitterDistance=380.
            this.splitContainer_EditorOutput.Location = new System.Drawing.Point(12, 45);
            this.splitContainer_EditorOutput.Size = new System.Drawing.Size(864, 540);
            this.splitContainer_EditorOutput.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                                                       AnchorStyles.Left | AnchorStyles.Right;
            this.splitContainer_EditorOutput.Orientation = Orientation.Horizontal;
            this.splitContainer_EditorOutput.SplitterDistance = 360;
            this.splitContainer_EditorOutput.Panel1MinSize = 120;
            this.splitContainer_EditorOutput.Panel2MinSize = 80;
            //
            // scintilla_Editor (in splitContainer.Panel1)
            //
            // ScintillaNET defaults to a usable monospaced font and 0px
            // line gutter. Real configuration (lexer, keywords, styles)
            // happens in code-behind so we can keep all the styling in
            // one place rather than scattered through Designer init.
            this.scintilla_Editor.Dock = DockStyle.Fill;
            this.splitContainer_EditorOutput.Panel1.Controls.Add(this.scintilla_Editor);
            //
            // panel_OutputContainer (in splitContainer.Panel2)
            //
            // Hosts the Test Inputs label + grid as a band docked Top,
            // with the output RTB filling the rest. We can't dock both
            // directly into Panel2 because Z-order + Dock fights would
            // make the layout brittle on resize.
            this.panel_OutputContainer.Dock = DockStyle.Fill;
            this.splitContainer_EditorOutput.Panel2.Controls.Add(this.panel_OutputContainer);
            //
            // label_Inputs
            //
            this.label_Inputs.Dock = DockStyle.Top;
            this.label_Inputs.Height = 18;
            this.label_Inputs.Padding = new Padding(4, 2, 0, 0);
            this.label_Inputs.Text = "Test inputs (these populate $variables for Run Test):";
            this.label_Inputs.ForeColor = System.Drawing.SystemColors.GrayText;
            //
            // dataGridView_Inputs
            //
            // Two columns: id (read-only), value (editable). Compact
            // height so the bulk of Panel2 belongs to the output RTB.
            // EditMode = OnEnter so a single click into a value cell
            // immediately starts editing -- one fewer click between
            // user intent and result.
            this.dataGridView_Inputs.Dock = DockStyle.Top;
            this.dataGridView_Inputs.Height = 92;
            this.dataGridView_Inputs.AllowUserToAddRows = false;
            this.dataGridView_Inputs.AllowUserToDeleteRows = false;
            this.dataGridView_Inputs.AllowUserToResizeRows = false;
            this.dataGridView_Inputs.RowHeadersVisible = false;
            this.dataGridView_Inputs.MultiSelect = false;
            this.dataGridView_Inputs.SelectionMode = DataGridViewSelectionMode.CellSelect;
            this.dataGridView_Inputs.EditMode = DataGridViewEditMode.EditOnEnter;
            this.dataGridView_Inputs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            this.dataGridView_Inputs.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridView_Inputs.BackgroundColor = System.Drawing.SystemColors.Window;
            //
            // richTextBox_Output
            //
            // Black background mirrors the main console RTB so the visual
            // contract ("dark = output stream") is consistent across the
            // app. ReadOnly because writing here belongs to the sink.
            this.richTextBox_Output.Dock = DockStyle.Fill;
            this.richTextBox_Output.BackColor = System.Drawing.Color.Black;
            this.richTextBox_Output.ForeColor = System.Drawing.Color.LightGray;
            this.richTextBox_Output.Font = new System.Drawing.Font("Consolas", 9.75F);
            this.richTextBox_Output.ReadOnly = true;
            this.richTextBox_Output.WordWrap = false;
            this.richTextBox_Output.ScrollBars = RichTextBoxScrollBars.Both;
            // Order matters: Fill first so it claims the residual space,
            // THEN the docked-Top controls layer on top in reverse Z-order.
            this.panel_OutputContainer.Controls.Add(this.richTextBox_Output);
            this.panel_OutputContainer.Controls.Add(this.dataGridView_Inputs);
            this.panel_OutputContainer.Controls.Add(this.label_Inputs);

            //
            // Action buttons (anchored to the bottom-left / bottom-right
            // halves so a dialog resize keeps them where users expect).
            //
            int btnY = 595;
            int btnH = 28;

            this.button_RunTest.Text = "Run Test";
            this.button_RunTest.Location = new System.Drawing.Point(12, btnY);
            this.button_RunTest.Size = new System.Drawing.Size(90, btnH);
            this.button_RunTest.UseVisualStyleBackColor = true;
            this.button_RunTest.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.button_RunTest.Click += new System.EventHandler(this.Button_RunTest_Click);

            this.button_CancelTest.Text = "Cancel Test";
            this.button_CancelTest.Location = new System.Drawing.Point(108, btnY);
            this.button_CancelTest.Size = new System.Drawing.Size(90, btnH);
            this.button_CancelTest.UseVisualStyleBackColor = true;
            this.button_CancelTest.Enabled = false;
            this.button_CancelTest.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.button_CancelTest.Click += new System.EventHandler(this.Button_CancelTest_Click);

            this.button_InsertTemplate.Text = "Insert Template";
            this.button_InsertTemplate.Location = new System.Drawing.Point(204, btnY);
            this.button_InsertTemplate.Size = new System.Drawing.Size(120, btnH);
            this.button_InsertTemplate.UseVisualStyleBackColor = true;
            this.button_InsertTemplate.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.button_InsertTemplate.Click += new System.EventHandler(this.Button_InsertTemplate_Click);

            //
            // RTB format dropdown -- editor-local override for Run Test
            // output rendering. Doesn't change anything saved to disk;
            // it just steers the in-dialog test output.
            //
            this.label_Format.Text = "Format:";
            this.label_Format.AutoSize = true;
            this.label_Format.Location = new System.Drawing.Point(334, btnY + 6);
            this.label_Format.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            this.comboBox_Format.Location = new System.Drawing.Point(384, btnY + 3);
            this.comboBox_Format.Size = new System.Drawing.Size(110, btnH);
            this.comboBox_Format.DropDownStyle = ComboBoxStyle.DropDownList;
            this.comboBox_Format.Items.AddRange(new object[] {
                "default", "list", "table", "json"
            });
            this.comboBox_Format.SelectedIndex = 0;
            this.comboBox_Format.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Right-aligned cluster: Save / Save As / Close.
            // Coordinates resolved against the dialog's initial width so the
            // anchor math reads like "stick to the right".
            this.button_Save.Text = "Save";
            this.button_Save.Location = new System.Drawing.Point(620, btnY);
            this.button_Save.Size = new System.Drawing.Size(80, btnH);
            this.button_Save.UseVisualStyleBackColor = true;
            this.button_Save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.button_Save.Click += new System.EventHandler(this.Button_Save_Click);

            this.button_SaveAs.Text = "Save As...";
            this.button_SaveAs.Location = new System.Drawing.Point(706, btnY);
            this.button_SaveAs.Size = new System.Drawing.Size(90, btnH);
            this.button_SaveAs.UseVisualStyleBackColor = true;
            this.button_SaveAs.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.button_SaveAs.Click += new System.EventHandler(this.Button_SaveAs_Click);

            this.button_Close.Text = "Close";
            this.button_Close.Location = new System.Drawing.Point(802, btnY);
            this.button_Close.Size = new System.Drawing.Size(80, btnH);
            this.button_Close.UseVisualStyleBackColor = true;
            this.button_Close.DialogResult = DialogResult.Cancel;
            this.button_Close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.button_Close.Click += new System.EventHandler(this.Button_Close_Click);

            //
            // statusStrip
            //
            this.statusStrip.Items.AddRange(new ToolStripItem[] {
                this.statusLabel_Syntax,
                this.statusLabel_Spring,
                this.statusLabel_RunState
            });
            this.statusLabel_Syntax.Name = "statusLabel_Syntax";
            this.statusLabel_Syntax.Text = "Syntax: OK";
            this.statusLabel_Spring.Spring = true;
            this.statusLabel_RunState.Name = "statusLabel_RunState";
            this.statusLabel_RunState.Text = "Idle";

            //
            // ScriptEditorDialog
            //
            this.AcceptButton = null; // Enter shouldn't trigger Save -- it inserts a newline in the editor
            this.CancelButton = this.button_Close;
            this.ClientSize = new System.Drawing.Size(890, 660);
            this.MinimumSize = new System.Drawing.Size(640, 480);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Script Editor";
            this.KeyPreview = true;
            this.Controls.Add(this.label_Path);
            this.Controls.Add(this.textBox_Path);
            this.Controls.Add(this.button_Browse);
            this.Controls.Add(this.splitContainer_EditorOutput);
            this.Controls.Add(this.button_RunTest);
            this.Controls.Add(this.button_CancelTest);
            this.Controls.Add(this.button_InsertTemplate);
            this.Controls.Add(this.label_Format);
            this.Controls.Add(this.comboBox_Format);
            this.Controls.Add(this.button_Save);
            this.Controls.Add(this.button_SaveAs);
            this.Controls.Add(this.button_Close);
            this.Controls.Add(this.statusStrip);
            this.FormClosing += new FormClosingEventHandler(this.ScriptEditorDialog_FormClosing);

            this.splitContainer_EditorOutput.Panel1.ResumeLayout(false);
            this.splitContainer_EditorOutput.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_EditorOutput)).EndInit();
            this.splitContainer_EditorOutput.ResumeLayout(false);
            this.panel_OutputContainer.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Inputs)).EndInit();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
