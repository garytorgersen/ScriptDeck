using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.Workspace;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Modal editor for the workspace's shared input list. The grid
    /// shows id / label / type / default; the user can add, remove, and
    /// reorder rows. On OK the caller pulls the new list via
    /// <see cref="GetEditedList"/> and assigns it to the workspace.
    ///
    /// We don't bind directly to the workspace's collection so Cancel
    /// really cancels — same copy-on-OK pattern the other editors use.
    /// </summary>
    public partial class EditSharedInputsDialog : Form
    {
        // The working copy the grid is bound to. Becomes the OK result.
        private readonly BindingList<SharedInput> _working;

        public EditSharedInputsDialog(IList<SharedInput> source)
        {
            InitializeComponent();

            // Deep-copy each entry so edits in the grid don't bleed into
            // the original workspace list before the user clicks OK.
            _working = new BindingList<SharedInput>(
                (source ?? new List<SharedInput>())
                    .Select(s => new SharedInput
                    {
                        Id = s?.Id,
                        Label = s?.Label,
                        Type = string.IsNullOrEmpty(s?.Type) ? "text" : s.Type,
                        Default = s?.Default ?? string.Empty,
                    })
                    .ToList());

            ConfigureGrid();
            dataGridView_Inputs.DataSource = _working;
        }

        public IList<SharedInput> GetEditedList() => _working.ToList();

        private void ConfigureGrid()
        {
            // Manual column setup so we can constrain "type" to a combo
            // and control widths. AutoGenerateColumns would give us all
            // properties as plain text columns and look messy.
            dataGridView_Inputs.AutoGenerateColumns = false;

            dataGridView_Inputs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Id", HeaderText = "Id", DataPropertyName = "Id", Width = 120,
            });
            dataGridView_Inputs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Label", HeaderText = "Label", DataPropertyName = "Label", Width = 160,
            });
            var typeCol = new DataGridViewComboBoxColumn
            {
                Name = "Type",
                HeaderText = "Type",
                DataPropertyName = "Type",
                Width = 80,
                FlatStyle = FlatStyle.Flat,
            };
            // Today only "text" is honored by the renderer; the dropdown
            // lists the future-supported types so workspaces written today
            // don't have to be edited again to use them.
            typeCol.Items.AddRange("text", "combo", "checkbox", "filepicker", "folderpicker", "secret");
            dataGridView_Inputs.Columns.Add(typeCol);

            dataGridView_Inputs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Default", HeaderText = "Default value", DataPropertyName = "Default", Width = 180,
            });

            // Combo cells throw a DataError when the underlying value
            // doesn't match an item — happens when loading a workspace
            // with a future type. Swallow the visual error and fall back
            // to "text" silently.
            dataGridView_Inputs.DataError += (s, e) => e.ThrowException = false;
        }

        // ---- Toolbar ----

        private void Button_Add_Click(object sender, EventArgs e)
        {
            _working.Add(new SharedInput
            {
                Id = SuggestUniqueId(),
                Label = "New Input",
                Type = "text",
                Default = string.Empty,
            });
            // Move the selection to the new row so the user can type
            // straight into Id without clicking again.
            if (dataGridView_Inputs.Rows.Count > 0)
            {
                var row = dataGridView_Inputs.Rows[dataGridView_Inputs.Rows.Count - 1];
                dataGridView_Inputs.CurrentCell = row.Cells["Id"];
                dataGridView_Inputs.BeginEdit(true);
            }
        }

        private void Button_Remove_Click(object sender, EventArgs e)
        {
            // Delete every selected row (or the current row if none
            // explicitly selected). Reverse-iterate so removing earlier
            // rows doesn't shift the indices of later ones.
            var rows = dataGridView_Inputs.SelectedRows.Cast<DataGridViewRow>().ToList();
            if (rows.Count == 0 && dataGridView_Inputs.CurrentRow != null && !dataGridView_Inputs.CurrentRow.IsNewRow)
                rows.Add(dataGridView_Inputs.CurrentRow);

            foreach (var r in rows.OrderByDescending(r => r.Index))
            {
                if (r.IsNewRow) continue;
                if (r.Index >= 0 && r.Index < _working.Count)
                    _working.RemoveAt(r.Index);
            }
        }

        private void Button_Up_Click(object sender, EventArgs e) => MoveSelected(-1);
        private void Button_Down_Click(object sender, EventArgs e) => MoveSelected(+1);

        private void MoveSelected(int delta)
        {
            var row = dataGridView_Inputs.CurrentRow;
            if (row == null || row.IsNewRow) return;
            int i = row.Index;
            int j = i + delta;
            if (j < 0 || j >= _working.Count) return;

            var item = _working[i];
            _working.RemoveAt(i);
            _working.Insert(j, item);

            dataGridView_Inputs.ClearSelection();
            dataGridView_Inputs.Rows[j].Selected = true;
            dataGridView_Inputs.CurrentCell = dataGridView_Inputs.Rows[j].Cells[0];
        }

        private string SuggestUniqueId()
        {
            // input1, input2, ... — skip ids that already exist (grid may
            // have user-added rows since open). Caps at 999 to avoid an
            // accidental infinite loop on truly malformed data.
            var taken = new HashSet<string>(_working.Select(w => w?.Id ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < 1000; i++)
            {
                var candidate = "input" + i;
                if (!taken.Contains(candidate)) return candidate;
            }
            return "input";
        }

        private void Button_Ok_Click(object sender, EventArgs e)
        {
            // Make sure any in-progress cell edit is committed to the
            // BindingList before we hand it back. Without this the last
            // typed cell can be silently lost on OK.
            dataGridView_Inputs.EndEdit();

            // Reject duplicate or missing ids — token substitution maps by
            // id, so duplicates would have one input silently shadow another.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _working)
            {
                if (string.IsNullOrWhiteSpace(s?.Id))
                {
                    MessageBox.Show(this, "Every shared input needs an Id.", "ScriptDeck",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                if (!seen.Add(s.Id))
                {
                    MessageBox.Show(this, $"Duplicate Id '{s.Id}'. Ids must be unique.",
                        "ScriptDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                // Backfill missing Type to keep the JSON tidy.
                if (string.IsNullOrWhiteSpace(s.Type)) s.Type = "text";
            }
        }
    }
}
