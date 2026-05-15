using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    partial class EditSharedInputsDialog
    {
        private System.ComponentModel.IContainer components = null;

        private DataGridView dataGridView_Inputs;
        private System.Windows.Forms.Button button_Add;
        private System.Windows.Forms.Button button_Remove;
        private System.Windows.Forms.Button button_Up;
        private System.Windows.Forms.Button button_Down;
        private System.Windows.Forms.Button button_Ok;
        private System.Windows.Forms.Button button_Cancel;
        private Label label_Hint;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            this.dataGridView_Inputs = new DataGridView();
            this.button_Add          = new System.Windows.Forms.Button();
            this.button_Remove       = new System.Windows.Forms.Button();
            this.button_Up           = new System.Windows.Forms.Button();
            this.button_Down         = new System.Windows.Forms.Button();
            this.button_Ok           = new System.Windows.Forms.Button();
            this.button_Cancel       = new System.Windows.Forms.Button();
            this.label_Hint          = new Label();

            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Inputs)).BeginInit();
            this.SuspendLayout();

            //
            // dataGridView_Inputs
            //
            this.dataGridView_Inputs.Location = new System.Drawing.Point(12, 12);
            this.dataGridView_Inputs.Size = new System.Drawing.Size(600, 260);
            this.dataGridView_Inputs.AllowUserToAddRows = false;
            this.dataGridView_Inputs.AllowUserToResizeRows = false;
            this.dataGridView_Inputs.RowHeadersWidth = 24;
            this.dataGridView_Inputs.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this.dataGridView_Inputs.MultiSelect = false;
            //
            // button_Add
            //
            this.button_Add.Location = new System.Drawing.Point(12, 282);
            this.button_Add.Size = new System.Drawing.Size(80, 26);
            this.button_Add.Text = "Add";
            this.button_Add.UseVisualStyleBackColor = true;
            this.button_Add.Click += new System.EventHandler(this.Button_Add_Click);
            //
            // button_Remove
            //
            this.button_Remove.Location = new System.Drawing.Point(96, 282);
            this.button_Remove.Size = new System.Drawing.Size(80, 26);
            this.button_Remove.Text = "Remove";
            this.button_Remove.UseVisualStyleBackColor = true;
            this.button_Remove.Click += new System.EventHandler(this.Button_Remove_Click);
            //
            // button_Up
            //
            this.button_Up.Location = new System.Drawing.Point(184, 282);
            this.button_Up.Size = new System.Drawing.Size(60, 26);
            this.button_Up.Text = "Up";
            this.button_Up.UseVisualStyleBackColor = true;
            this.button_Up.Click += new System.EventHandler(this.Button_Up_Click);
            //
            // button_Down
            //
            this.button_Down.Location = new System.Drawing.Point(248, 282);
            this.button_Down.Size = new System.Drawing.Size(60, 26);
            this.button_Down.Text = "Down";
            this.button_Down.UseVisualStyleBackColor = true;
            this.button_Down.Click += new System.EventHandler(this.Button_Down_Click);
            //
            // label_Hint
            //
            this.label_Hint.AutoSize = true;
            this.label_Hint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.label_Hint.Location = new System.Drawing.Point(12, 318);
            this.label_Hint.Text = "Ids are referenced from button args as {{id}} for substitution.";
            //
            // button_Ok
            //
            this.button_Ok.Location = new System.Drawing.Point(437, 320);
            this.button_Ok.Size = new System.Drawing.Size(85, 28);
            this.button_Ok.Text = "OK";
            this.button_Ok.UseVisualStyleBackColor = true;
            this.button_Ok.DialogResult = DialogResult.OK;
            this.button_Ok.Click += new System.EventHandler(this.Button_Ok_Click);
            //
            // button_Cancel
            //
            this.button_Cancel.Location = new System.Drawing.Point(527, 320);
            this.button_Cancel.Size = new System.Drawing.Size(85, 28);
            this.button_Cancel.Text = "Cancel";
            this.button_Cancel.UseVisualStyleBackColor = true;
            this.button_Cancel.DialogResult = DialogResult.Cancel;
            //
            // EditSharedInputsDialog
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(624, 360);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Shared Inputs";
            this.AcceptButton = this.button_Ok;
            this.CancelButton = this.button_Cancel;

            this.Controls.Add(this.dataGridView_Inputs);
            this.Controls.Add(this.button_Add);
            this.Controls.Add(this.button_Remove);
            this.Controls.Add(this.button_Up);
            this.Controls.Add(this.button_Down);
            this.Controls.Add(this.label_Hint);
            this.Controls.Add(this.button_Ok);
            this.Controls.Add(this.button_Cancel);

            ((System.ComponentModel.ISupportInitialize)(this.dataGridView_Inputs)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
