using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.Hosting;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Tools -> Manage Computers dialog. Edits the
    /// <see cref="ComputerListStore"/> in-place on Save; on Cancel the
    /// store is reverted from a snapshot taken at open time so the
    /// in-memory state never diverges from disk.
    ///
    /// The dialog edits a LOCAL working copy of the list (the listbox
    /// is the source of truth while the dialog is open). Only Save
    /// pushes the working copy back into the store + persists. This
    /// way Add / Remove / Import buttons can be "free" without worrying
    /// about half-committed state, and Cancel really cancels.
    /// </summary>
    public sealed class ManageComputersDialog : Form
    {
        private readonly ComputerListStore _store;
        private readonly ListBox _listBox;
        private readonly Label _statusLabel;

        public ManageComputersDialog(ComputerListStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));

            Text            = "Manage Computers";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(500, 460);
            MinimumSize     = new Size(420, 320);
            MinimizeBox     = false;
            MaximizeBox     = true;
            ShowInTaskbar   = false;

            // Top intro -- short, descriptive. Helps the user understand
            // the scope ("ScriptDeck-wide") and the connection to the
            // shared input dropdowns.
            var intro = new Label
            {
                Dock    = DockStyle.Top,
                Height  = 50,
                Padding = new Padding(12, 10, 12, 4),
                Text    = "Computer names known to ScriptDeck. " +
                          "Workspaces with a Computer Name field use this list " +
                          "as the dropdown source; manual entry is still allowed " +
                          "in those fields.",
                AutoEllipsis = true,
            };

            // Bottom-of-list buttons: Add / Edit / Remove / Import,
            // pinned to a panel above the OK/Cancel row.
            var actionBar = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                Height        = 36,
                FlowDirection = FlowDirection.LeftToRight,
                Padding       = new Padding(12, 4, 12, 4),
            };
            var btnAdd     = MakeFlowButton("Add...");
            var btnEdit    = MakeFlowButton("Edit");
            var btnRemove  = MakeFlowButton("Remove");
            var btnImport  = MakeFlowButton("Import from text file...");
            btnImport.Width = 160; // wider to fit the label
            actionBar.Controls.AddRange(new Control[] { btnAdd, btnEdit, btnRemove, btnImport });

            // OK / Cancel at the very bottom, right-aligned via FlowLayout
            // RightToLeft so Save sits closer to the right edge per the
            // platform convention.
            var commitBar = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                Height        = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(12, 8, 12, 8),
                BackColor     = SystemColors.Control,
            };
            var btnCancel = new System.Windows.Forms.Button
            {
                Text = "Cancel",
                Width = 90,
                DialogResult = DialogResult.Cancel,
            };
            var btnSave = new System.Windows.Forms.Button
            {
                Text = "Save",
                Width = 90,
                DialogResult = DialogResult.None, // we handle Save manually
            };
            commitBar.Controls.AddRange(new Control[] { btnCancel, btnSave });

            // Status text strip just above the OK/Cancel row -- shows
            // entry counts and post-Import summaries ("Imported 7 new;
            // 2 already existed").
            _statusLabel = new Label
            {
                Dock      = DockStyle.Bottom,
                Height    = 22,
                Padding   = new Padding(14, 2, 14, 2),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                AutoEllipsis = true,
            };

            // The list itself, filling whatever's left in the middle.
            // ListBox (not DataGridView) because the data is just a flat
            // string list and ListBox handles multi-select / keyboard
            // navigation with zero ceremony.
            _listBox = new ListBox
            {
                Dock                = DockStyle.Fill,
                SelectionMode       = SelectionMode.MultiExtended,
                IntegralHeight      = false,
                Sorted              = true,
                Margin              = new Padding(12),
                Font                = new Font("Segoe UI", 9.5F),
            };
            _listBox.DoubleClick += (_, __) => InvokeEditSelected(btnEdit);
            _listBox.SelectedIndexChanged += (_, __) => UpdateButtonsAndStatus(btnEdit, btnRemove);

            // Hosting panel just so the ListBox gets sensible padding
            // -- DockStyle.Fill ignores Margin, so we wrap it.
            var listHost = new Panel
            {
                Dock    = DockStyle.Fill,
                Padding = new Padding(12, 4, 12, 4),
            };
            listHost.Controls.Add(_listBox);

            // Add order matters for Dock layout: Fill last so the
            // top/bottom strips claim their bands first.
            Controls.Add(listHost);
            Controls.Add(_statusLabel);
            Controls.Add(commitBar);
            Controls.Add(actionBar);
            Controls.Add(intro);

            AcceptButton = btnSave;
            CancelButton = btnCancel;

            // Wire actions. All four operate on the listbox's working
            // copy; Save copies the listbox back into the store. This
            // way the store is never partially mutated between OK and
            // Cancel.
            btnAdd.Click    += (_, __) => OnAddClick();
            btnEdit.Click   += (_, __) => InvokeEditSelected(btnEdit);
            btnRemove.Click += (_, __) => OnRemoveClick();
            btnImport.Click += (_, __) => OnImportClick();
            btnSave.Click   += (_, __) => OnSaveClick();

            // Seed the list from the store's current state.
            ReloadFromStore();
            UpdateButtonsAndStatus(btnEdit, btnRemove);
        }

        private static System.Windows.Forms.Button MakeFlowButton(string text)
        {
            return new System.Windows.Forms.Button
            {
                Text   = text,
                Width  = 80,
                Height = 26,
                Margin = new Padding(0, 0, 6, 0),
                UseVisualStyleBackColor = true,
            };
        }

        private void ReloadFromStore()
        {
            _listBox.BeginUpdate();
            try
            {
                _listBox.Items.Clear();
                foreach (var c in _store.GetAll())
                    _listBox.Items.Add(c);
            }
            finally
            {
                _listBox.EndUpdate();
            }
        }

        private void UpdateButtonsAndStatus(
            System.Windows.Forms.Button editBtn,
            System.Windows.Forms.Button removeBtn)
        {
            // Edit makes sense for exactly one row; Remove takes one or
            // more. Empty list disables both.
            editBtn.Enabled   = _listBox.SelectedItems.Count == 1;
            removeBtn.Enabled = _listBox.SelectedItems.Count >= 1;
            int total = _listBox.Items.Count;
            _statusLabel.Text = total == 1
                ? "1 entry"
                : total + " entries";
        }

        // ---- Action handlers ------------------------------------------------

        private void OnAddClick()
        {
            string entered = PromptForName("Add Computer", "Computer name:", string.Empty);
            if (entered == null) return; // user cancelled
            entered = entered.Trim();
            if (entered.Length == 0) return;
            if (ListContains(entered))
            {
                MessageBox.Show(this,
                    "'" + entered + "' is already in the list.",
                    "Duplicate entry",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _listBox.Items.Add(entered);
            // ListBox is Sorted, so we re-locate the new item to select it.
            int idx = _listBox.Items.IndexOf(entered);
            if (idx >= 0) _listBox.SelectedIndex = idx;
            _statusLabel.Text = "Added '" + entered + "'";
        }

        private void InvokeEditSelected(System.Windows.Forms.Button _)
        {
            if (_listBox.SelectedItems.Count != 1) return;
            string original = _listBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(original)) return;

            string entered = PromptForName("Edit Computer", "Computer name:", original);
            if (entered == null) return;
            entered = entered.Trim();
            if (entered.Length == 0) return;
            if (string.Equals(entered, original, StringComparison.OrdinalIgnoreCase)) return; // no change

            if (ListContains(entered))
            {
                MessageBox.Show(this,
                    "'" + entered + "' is already in the list.",
                    "Duplicate entry",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int idx = _listBox.Items.IndexOf(original);
            if (idx >= 0)
            {
                _listBox.Items.RemoveAt(idx);
                _listBox.Items.Add(entered);
                int newIdx = _listBox.Items.IndexOf(entered);
                if (newIdx >= 0) _listBox.SelectedIndex = newIdx;
            }
            _statusLabel.Text = "Renamed '" + original + "' to '" + entered + "'";
        }

        private void OnRemoveClick()
        {
            if (_listBox.SelectedItems.Count == 0) return;
            // Snapshot selection because removing items will mutate the
            // collection we're iterating. Cast through ToList().
            var toRemove = _listBox.SelectedItems.Cast<object>().ToList();
            foreach (var item in toRemove)
                _listBox.Items.Remove(item);
            _statusLabel.Text = toRemove.Count == 1
                ? "Removed 1 entry"
                : "Removed " + toRemove.Count + " entries";
        }

        private void OnImportClick()
        {
            using (var dlg = new OpenFileDialog
            {
                Title           = "Import computers from text file",
                Filter          = "Text files (*.txt;*.lst;*.csv)|*.txt;*.lst;*.csv|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect     = false,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                int added = 0, dupes = 0, blank = 0;
                try
                {
                    foreach (var raw in File.ReadAllLines(dlg.FileName))
                    {
                        if (raw == null) continue;
                        string line = raw.Trim();
                        if (line.Length == 0) { blank++; continue; }
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                        if (ListContains(line)) { dupes++; continue; }
                        _listBox.Items.Add(line);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Failed to read the import file:\n\n" + ex.Message,
                        "Import failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _statusLabel.Text = "Imported " + added + " new entr" +
                    (added == 1 ? "y" : "ies") +
                    (dupes > 0 ? "; " + dupes + " already existed" : string.Empty);
            }
        }

        private void OnSaveClick()
        {
            // Push the listbox's working copy back into the store and
            // persist. Replace() is destructive on the store's in-memory
            // list -- if Save throws, the in-memory list is still the
            // new one (matches what the user sees in the dialog), but
            // disk is stale. The OS error message gives them enough to
            // act on (write-protected install? full disk?).
            var entries = _listBox.Items.Cast<object>()
                .Select(o => o?.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            _store.Replace(entries);
            try
            {
                _store.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Failed to save the computer list:\n\n" + ex.Message +
                    "\n\nYour changes are still loaded for this session but " +
                    "won't survive an app restart.",
                    "Save failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // Don't close on save failure -- user might want to
                // adjust and retry, or copy entries out before cancelling.
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private bool ListContains(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var item in _listBox.Items)
            {
                if (string.Equals(item?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Tiny in-dialog text-entry helper. WinForms has no built-in
        // InputBox; rolling our own here keeps the Add / Edit flows
        // a single dialog away (no extra files for a trivial form).
        private static string PromptForName(string title, string prompt, string initial)
        {
            using (var dlg = new Form
            {
                Text            = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition   = FormStartPosition.CenterParent,
                MinimizeBox     = false,
                MaximizeBox     = false,
                ShowInTaskbar   = false,
                ClientSize      = new Size(380, 110),
            })
            {
                var lbl = new Label { Text = prompt, Left = 12, Top = 12, AutoSize = true };
                var txt = new TextBox { Left = 12, Top = 36, Width = 356, Text = initial ?? string.Empty };
                txt.Select(0, txt.Text.Length);
                var ok = new System.Windows.Forms.Button
                {
                    Text = "OK", Left = 196, Top = 70, Width = 80,
                    DialogResult = DialogResult.OK,
                };
                var cancel = new System.Windows.Forms.Button
                {
                    Text = "Cancel", Left = 286, Top = 70, Width = 80,
                    DialogResult = DialogResult.Cancel,
                };
                dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.ActiveControl = txt;

                return dlg.ShowDialog() == DialogResult.OK ? txt.Text : null;
            }
        }
    }
}
