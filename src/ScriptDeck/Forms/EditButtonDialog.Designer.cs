using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    partial class EditButtonDialog
    {
        private System.ComponentModel.IContainer components = null;

        private Label label_Id;
        private TextBox textBox_Id;
        private Label label_LabelText;
        private TextBox textBox_Label;
        private Label label_Executor;
        private ComboBox comboBox_Executor;
        private Label label_ScriptPath;
        private TextBox textBox_ScriptPath;
        private System.Windows.Forms.Button button_Browse;
        private System.Windows.Forms.Button button_EditScript;
        private Label label_Args;
        private TextBox textBox_Args;
        private Label label_ArgsHint;
        private Label label_WorkingDir;
        private TextBox textBox_WorkingDir;
        private GroupBox groupBox_Outputs;
        private CheckBox checkBox_OutputRtb;
        private CheckBox checkBox_OutputGrid;
        private CheckBox checkBox_Confirm;
        private CheckBox checkBox_Log;
        private CheckBox checkBox_RunInBackground;
        private Label label_RtbFormat;
        private ComboBox comboBox_RtbFormat;
        private System.Windows.Forms.Button button_Ok;
        private System.Windows.Forms.Button button_Cancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            this.label_Id            = new Label();
            this.textBox_Id          = new TextBox();
            this.label_LabelText     = new Label();
            this.textBox_Label       = new TextBox();
            this.label_Executor      = new Label();
            this.comboBox_Executor   = new ComboBox();
            this.label_ScriptPath    = new Label();
            this.textBox_ScriptPath  = new TextBox();
            this.button_Browse       = new System.Windows.Forms.Button();
            this.label_Args          = new Label();
            this.textBox_Args        = new TextBox();
            this.label_ArgsHint      = new Label();
            this.label_WorkingDir    = new Label();
            this.textBox_WorkingDir  = new TextBox();
            this.groupBox_Outputs    = new GroupBox();
            this.checkBox_OutputRtb  = new CheckBox();
            this.checkBox_OutputGrid = new CheckBox();
            this.checkBox_Confirm    = new CheckBox();
            this.checkBox_Log        = new CheckBox();
            this.checkBox_RunInBackground = new CheckBox();
            this.label_RtbFormat       = new Label();
            this.comboBox_RtbFormat    = new ComboBox();
            this.button_Ok           = new System.Windows.Forms.Button();
            this.button_Cancel       = new System.Windows.Forms.Button();

            this.SuspendLayout();
            this.groupBox_Outputs.SuspendLayout();

            //
            // label_Id
            //
            this.label_Id.AutoSize = true;
            this.label_Id.Location = new System.Drawing.Point(12, 15);
            this.label_Id.Text = "Id:";
            //
            // textBox_Id
            //
            this.textBox_Id.Location = new System.Drawing.Point(110, 12);
            this.textBox_Id.Width = 360;
            //
            // label_LabelText
            //
            this.label_LabelText.AutoSize = true;
            this.label_LabelText.Location = new System.Drawing.Point(12, 45);
            this.label_LabelText.Text = "Label:";
            //
            // textBox_Label
            //
            this.textBox_Label.Location = new System.Drawing.Point(110, 42);
            this.textBox_Label.Width = 360;
            //
            // label_Executor
            //
            this.label_Executor.AutoSize = true;
            this.label_Executor.Location = new System.Drawing.Point(12, 75);
            this.label_Executor.Text = "Executor:";
            //
            // comboBox_Executor
            //
            this.comboBox_Executor.Location = new System.Drawing.Point(110, 72);
            this.comboBox_Executor.Width = 200;
            this.comboBox_Executor.DropDownStyle = ComboBoxStyle.DropDownList;
            this.comboBox_Executor.Items.AddRange(new object[] { "powershell", "cmd", "process" });
            //
            // label_ScriptPath
            //
            this.label_ScriptPath.AutoSize = true;
            this.label_ScriptPath.Location = new System.Drawing.Point(12, 105);
            this.label_ScriptPath.Text = "Script path:";
            //
            // textBox_ScriptPath -- shrunk from 280 to 220 to make room
            // for the Edit Script... button on the same row.
            //
            this.textBox_ScriptPath.Location = new System.Drawing.Point(110, 102);
            this.textBox_ScriptPath.Width = 220;
            //
            // button_Browse
            //
            this.button_Browse.Location = new System.Drawing.Point(336, 100);
            this.button_Browse.Size = new System.Drawing.Size(70, 24);
            this.button_Browse.Text = "Browse...";
            this.button_Browse.UseVisualStyleBackColor = true;
            this.button_Browse.Click += new System.EventHandler(this.Button_Browse_Click);
            //
            // button_EditScript -- inline Script Editor launcher.
            //
            // Sits to the right of Browse so the path field's three actions
            // (type / pick / edit) read left-to-right. Disabled for non-PS
            // executors at runtime since the editor is PowerShell-only in
            // Phase 1.
            this.button_EditScript = new System.Windows.Forms.Button();
            this.button_EditScript.Location = new System.Drawing.Point(410, 100);
            this.button_EditScript.Size = new System.Drawing.Size(70, 24);
            this.button_EditScript.Text = "Edit...";
            this.button_EditScript.UseVisualStyleBackColor = true;
            this.button_EditScript.Click += new System.EventHandler(this.Button_EditScript_Click);
            //
            // label_Args
            //
            this.label_Args.AutoSize = true;
            this.label_Args.Location = new System.Drawing.Point(12, 135);
            this.label_Args.Text = "Args:";
            //
            // textBox_Args
            //
            this.textBox_Args.Location = new System.Drawing.Point(110, 132);
            this.textBox_Args.Multiline = true;
            this.textBox_Args.AcceptsReturn = true;
            this.textBox_Args.ScrollBars = ScrollBars.Vertical;
            this.textBox_Args.Width = 360;
            this.textBox_Args.Height = 90;
            this.textBox_Args.Font = new System.Drawing.Font("Consolas", 9F);
            //
            // label_ArgsHint
            //
            this.label_ArgsHint.AutoSize = true;
            this.label_ArgsHint.Location = new System.Drawing.Point(110, 224);
            this.label_ArgsHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.label_ArgsHint.Text = "One arg per line. Tokens like {{computerName}} substitute from shared inputs.";
            //
            // label_WorkingDir
            //
            this.label_WorkingDir.AutoSize = true;
            this.label_WorkingDir.Location = new System.Drawing.Point(12, 250);
            this.label_WorkingDir.Text = "Working dir:";
            //
            // textBox_WorkingDir
            //
            this.textBox_WorkingDir.Location = new System.Drawing.Point(110, 247);
            this.textBox_WorkingDir.Width = 360;
            //
            // groupBox_Outputs
            //
            // Holds the destination toggles plus the RTB-format combo
            // (since both affect how output is presented). Compact box
            // -- two checkboxes and one labelled combobox row.
            this.groupBox_Outputs.Location = new System.Drawing.Point(12, 280);
            this.groupBox_Outputs.Size = new System.Drawing.Size(458, 90);
            this.groupBox_Outputs.Text = "Output destinations";
            this.groupBox_Outputs.Controls.Add(this.checkBox_OutputRtb);
            this.groupBox_Outputs.Controls.Add(this.checkBox_OutputGrid);
            this.groupBox_Outputs.Controls.Add(this.label_RtbFormat);
            this.groupBox_Outputs.Controls.Add(this.comboBox_RtbFormat);
            //
            // checkBox_OutputRtb
            //
            this.checkBox_OutputRtb.AutoSize = true;
            this.checkBox_OutputRtb.Location = new System.Drawing.Point(12, 25);
            this.checkBox_OutputRtb.Text = "Console (RTB)";
            //
            // checkBox_OutputGrid
            //
            this.checkBox_OutputGrid.AutoSize = true;
            this.checkBox_OutputGrid.Location = new System.Drawing.Point(160, 25);
            this.checkBox_OutputGrid.Text = "Grid (structured rows)";
            //
            // label_RtbFormat / comboBox_RtbFormat
            //
            // PowerShell-only. Controls how structured records render in
            // the console RTB ("default" = obj.ToString(); list/table/json/csv
            // produce friendlier text; "raw" hands console rendering
            // entirely to PowerShell's default formatter so Format-Table
            // / Format-List / custom whitespace render as the script
            // wrote them, and disables auto-grid-population -- Write-Grid
            // still works in raw mode for explicit grid output).
            this.label_RtbFormat.AutoSize = true;
            this.label_RtbFormat.Location = new System.Drawing.Point(12, 55);
            this.label_RtbFormat.Text = "RTB format:";
            this.comboBox_RtbFormat.Location = new System.Drawing.Point(96, 51);
            this.comboBox_RtbFormat.Width = 130;
            this.comboBox_RtbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            this.comboBox_RtbFormat.Items.AddRange(new object[] {
                "default", "list", "table", "json", "raw"
            });
            //
            // checkBox_Confirm
            //
            this.checkBox_Confirm.AutoSize = true;
            this.checkBox_Confirm.Location = new System.Drawing.Point(15, 405);
            this.checkBox_Confirm.Text = "Prompt before running (good for destructive scripts)";
            //
            // checkBox_Log
            //
            this.checkBox_Log.AutoSize = true;
            this.checkBox_Log.Location = new System.Drawing.Point(15, 429);
            this.checkBox_Log.Text = "Write start/done lines to log";
            //
            // checkBox_RunInBackground
            //
            // When checked, clicking the button enqueues onto the
            // background job queue instead of running on the foreground
            // single-flight gate. Output appears in the Jobs tab.
            this.checkBox_RunInBackground.AutoSize = true;
            this.checkBox_RunInBackground.Location = new System.Drawing.Point(15, 453);
            this.checkBox_RunInBackground.Text = "Run in background (long-running -- output appears in Jobs tab)";
            //
            // button_Ok
            //
            this.button_Ok.Location = new System.Drawing.Point(295, 489);
            this.button_Ok.Size = new System.Drawing.Size(85, 28);
            this.button_Ok.Text = "OK";
            this.button_Ok.UseVisualStyleBackColor = true;
            this.button_Ok.DialogResult = DialogResult.OK;
            this.button_Ok.Click += new System.EventHandler(this.Button_Ok_Click);
            //
            // button_Cancel
            //
            this.button_Cancel.Location = new System.Drawing.Point(385, 489);
            this.button_Cancel.Size = new System.Drawing.Size(85, 28);
            this.button_Cancel.Text = "Cancel";
            this.button_Cancel.UseVisualStyleBackColor = true;
            this.button_Cancel.DialogResult = DialogResult.Cancel;
            //
            // EditButtonDialog
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(484, 530);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Edit Button";
            this.AcceptButton = this.button_Ok;
            this.CancelButton = this.button_Cancel;

            this.Controls.Add(this.label_Id);
            this.Controls.Add(this.textBox_Id);
            this.Controls.Add(this.label_LabelText);
            this.Controls.Add(this.textBox_Label);
            this.Controls.Add(this.label_Executor);
            this.Controls.Add(this.comboBox_Executor);
            this.Controls.Add(this.label_ScriptPath);
            this.Controls.Add(this.textBox_ScriptPath);
            this.Controls.Add(this.button_Browse);
            this.Controls.Add(this.button_EditScript);
            this.Controls.Add(this.label_Args);
            this.Controls.Add(this.textBox_Args);
            this.Controls.Add(this.label_ArgsHint);
            this.Controls.Add(this.label_WorkingDir);
            this.Controls.Add(this.textBox_WorkingDir);
            this.Controls.Add(this.groupBox_Outputs);
            this.Controls.Add(this.checkBox_Confirm);
            this.Controls.Add(this.checkBox_Log);
            this.Controls.Add(this.checkBox_RunInBackground);
            this.Controls.Add(this.button_Ok);
            this.Controls.Add(this.button_Cancel);

            this.groupBox_Outputs.ResumeLayout(false);
            this.groupBox_Outputs.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
