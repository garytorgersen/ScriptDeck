using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Modal dialog for creating a new shared input from the Inputs grid.
    /// Used for both Static and Volatile; the caller passes the scope
    /// label so the title bar and validation messages read correctly.
    ///
    /// Validation:
    ///   * Name required, must be a valid PowerShell identifier
    ///     ([A-Za-z_][A-Za-z0-9_]*) so it works as $variableName
    ///   * Name must not collide with any existing input (Static OR
    ///     Volatile) -- the caller passes the existing-id set
    ///   * Value can be empty
    ///   * Label can be empty (the grid falls back to Name)
    ///
    /// On OK, callers read <see cref="EnteredName"/>, <see cref="EnteredValue"/>,
    /// <see cref="EnteredLabel"/>.
    /// </summary>
    public sealed class AddInputDialog : Form
    {
        // PS identifier rule: leading letter/underscore, then alnum/underscore.
        // Mirrors the rule PowerShellExecutor.ValidIdentifier uses.
        private static readonly Regex IdentRx = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private readonly System.Collections.Generic.ISet<string> _existingIds;
        private readonly TextBox _txtName;
        private readonly TextBox _txtValue;
        private readonly TextBox _txtLabel;

        public string EnteredName  { get; private set; }
        public string EnteredValue { get; private set; }
        public string EnteredLabel { get; private set; }

        public AddInputDialog(string scopeLabel,
                              System.Collections.Generic.ISet<string> existingIds)
        {
            _existingIds = existingIds
                ?? new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Form chrome -- compact dialog box, no min/max, centered
            // on parent. Sized so the labels and three textboxes fit
            // comfortably without horizontal scroll.
            Text            = "Add " + scopeLabel + " Input";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ShowInTaskbar   = false;
            ClientSize      = new System.Drawing.Size(380, 165);

            // Layout uses absolute positions because there are only 3
            // rows; a TableLayoutPanel would be overkill.
            var lblName  = new Label { Text = "Name:",  Left = 12, Top = 14, AutoSize = true };
            _txtName     = new TextBox { Left = 90, Top = 11, Width = 270 };

            var lblValue = new Label { Text = "Value:", Left = 12, Top = 45, AutoSize = true };
            _txtValue    = new TextBox { Left = 90, Top = 42, Width = 270 };

            var lblLabel = new Label { Text = "Label:", Left = 12, Top = 76, AutoSize = true };
            _txtLabel    = new TextBox { Left = 90, Top = 73, Width = 270 };

            var lblHint  = new Label {
                Text = "Label is optional; the Name is used in the grid when blank.",
                Left = 90, Top = 99, AutoSize = true,
                ForeColor = System.Drawing.SystemColors.GrayText,
                Font = new System.Drawing.Font("Segoe UI", 8F),
            };

            var btnOk = new System.Windows.Forms.Button
            {
                Text = "OK",
                Left = 196,
                Top  = 128,
                Width = 80,
                DialogResult = DialogResult.None, // we control closing
            };
            btnOk.Click += OnOkClick;

            var btnCancel = new System.Windows.Forms.Button
            {
                Text = "Cancel",
                Left = 282,
                Top  = 128,
                Width = 80,
                DialogResult = DialogResult.Cancel,
            };

            Controls.Add(lblName);  Controls.Add(_txtName);
            Controls.Add(lblValue); Controls.Add(_txtValue);
            Controls.Add(lblLabel); Controls.Add(_txtLabel);
            Controls.Add(lblHint);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // Land the user in the Name field. Enter -> AcceptButton ->
            // OnOkClick (with full validation). Esc -> CancelButton.
            ActiveControl = _txtName;
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            string name  = _txtName.Text?.Trim();
            string value = _txtValue.Text ?? string.Empty;
            string label = _txtLabel.Text?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                Complain("Name is required.");
                _txtName.Focus();
                return;
            }
            if (!IdentRx.IsMatch(name))
            {
                Complain(
                    "Name must be a valid PowerShell identifier:\n" +
                    "letters, digits, underscores; first character a letter or underscore.\n" +
                    "(So scripts can reference it as $" + name + ".)");
                _txtName.Focus();
                return;
            }
            if (_existingIds.Contains(name))
            {
                Complain("An input named '" + name + "' already exists. " +
                         "Pick a different name (or edit the existing one from the grid).");
                _txtName.Focus();
                return;
            }

            EnteredName  = name;
            EnteredValue = value;
            EnteredLabel = string.IsNullOrEmpty(label) ? null : label;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Complain(string message)
        {
            MessageBox.Show(this, message, "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
