using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    partial class EditTabDialog
    {
        private System.ComponentModel.IContainer components = null;

        private Label label_Id;
        private TextBox textBox_Id;
        private Label label_Title;
        private TextBox textBox_Title;
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

            this.label_Id      = new Label();
            this.textBox_Id    = new TextBox();
            this.label_Title   = new Label();
            this.textBox_Title = new TextBox();
            this.button_Ok     = new System.Windows.Forms.Button();
            this.button_Cancel = new System.Windows.Forms.Button();

            this.SuspendLayout();

            //
            // label_Id
            //
            this.label_Id.AutoSize = true;
            this.label_Id.Location = new System.Drawing.Point(12, 15);
            this.label_Id.Text = "Id:";
            //
            // textBox_Id
            //
            this.textBox_Id.Location = new System.Drawing.Point(80, 12);
            this.textBox_Id.Width = 280;
            //
            // label_Title
            //
            this.label_Title.AutoSize = true;
            this.label_Title.Location = new System.Drawing.Point(12, 45);
            this.label_Title.Text = "Title:";
            //
            // textBox_Title
            //
            this.textBox_Title.Location = new System.Drawing.Point(80, 42);
            this.textBox_Title.Width = 280;
            //
            // button_Ok
            //
            this.button_Ok.Location = new System.Drawing.Point(190, 80);
            this.button_Ok.Size = new System.Drawing.Size(85, 28);
            this.button_Ok.Text = "OK";
            this.button_Ok.UseVisualStyleBackColor = true;
            this.button_Ok.DialogResult = DialogResult.OK;
            this.button_Ok.Click += new System.EventHandler(this.Button_Ok_Click);
            //
            // button_Cancel
            //
            this.button_Cancel.Location = new System.Drawing.Point(280, 80);
            this.button_Cancel.Size = new System.Drawing.Size(85, 28);
            this.button_Cancel.Text = "Cancel";
            this.button_Cancel.UseVisualStyleBackColor = true;
            this.button_Cancel.DialogResult = DialogResult.Cancel;
            //
            // EditTabDialog
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(380, 122);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Edit Tab";
            this.AcceptButton = this.button_Ok;
            this.CancelButton = this.button_Cancel;

            this.Controls.Add(this.label_Id);
            this.Controls.Add(this.textBox_Id);
            this.Controls.Add(this.label_Title);
            this.Controls.Add(this.textBox_Title);
            this.Controls.Add(this.button_Ok);
            this.Controls.Add(this.button_Cancel);

            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
