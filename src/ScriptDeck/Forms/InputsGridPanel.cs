using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Panel that lives to the right of the Logs RTB, showing all known
    /// shared inputs -- workspace-defined (Static) and runtime-created
    /// (Volatile) -- in a single grid.
    ///
    /// Scope column distinguishes the two so the user can tell at a
    /// glance which inputs survive a workspace switch and which don't.
    ///
    /// User actions surface as events; this panel owns NO state. The
    /// Shell is the single source of truth and re-pushes the full row
    /// set via <see cref="LoadData"/> whenever something changes.
    ///
    /// Editing semantics:
    ///   * Static rows are read-only here. The user changes those values
    ///     via the top-bar textboxes (which are the live source). The
    ///     grid mirrors them so the full Static + Volatile picture is
    ///     visible in one place.
    ///   * Volatile rows have an editable Value cell. Commit fires
    ///     <see cref="VolatileValueEdited"/> which the Shell uses to
    ///     update its session-input dict.
    /// </summary>
    public sealed class InputsGridPanel : UserControl
    {
        public const string ScopeStatic   = "Static";
        public const string ScopeVolatile = "Volatile";

        private readonly Panel        _toolbar;
        private readonly Button       _btnAddStatic;
        private readonly Button       _btnAddVolatile;
        private readonly DataGridView _grid;
        private readonly ContextMenuStrip _rowMenu;
        private readonly ToolStripMenuItem _miAddStatic;
        private readonly ToolStripMenuItem _miAddVolatile;
        private readonly ToolStripSeparator _miSep1;
        private readonly ToolStripMenuItem _miRemove;
        private readonly ToolStripMenuItem _miClearVolatile;

        // True while LoadData is rewriting rows -- suppresses the
        // CellValueChanged handler so a bulk refresh doesn't echo back
        // as a flood of (no-op) VolatileValueEdited events.
        private bool _loading;

        /// <summary>User clicked "+ Static" (toolbar or context menu).</summary>
        public event EventHandler AddStaticRequested;
        /// <summary>User clicked "+ Volatile" (toolbar or context menu).</summary>
        public event EventHandler AddVolatileRequested;
        /// <summary>
        /// User chose Remove on a row. Arg = the row's input id. Shell
        /// decides whether to refuse (Static) or process (Volatile).
        /// </summary>
        public event EventHandler<string> RemoveRequested;
        /// <summary>User chose "Clear All Volatile" from the context menu.</summary>
        public event EventHandler ClearVolatileRequested;
        /// <summary>
        /// User edited the Value cell of a Volatile row. Args = (id, new value).
        /// Static rows do not raise this event (they're read-only).
        /// </summary>
        public event EventHandler<VolatileValueEditedEventArgs> VolatileValueEdited;

        public InputsGridPanel()
        {
            Dock = DockStyle.Fill;

            // ---- Toolbar (Dock=Top) ----
            //
            // Tight band with the two Add buttons. Right-click context
            // menu on the grid duplicates these so right-click flow is
            // also viable for power users.
            _toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = System.Drawing.SystemColors.Control,
                Padding = new Padding(6, 3, 6, 3),
            };

            _btnAddStatic = new Button
            {
                Text = "+ Static",
                Left = 6, Top = 3, Width = 90, Height = 24,
                UseVisualStyleBackColor = true,
            };
            _btnAddStatic.Click += (s, e) => AddStaticRequested?.Invoke(this, EventArgs.Empty);

            _btnAddVolatile = new Button
            {
                Text = "+ Volatile",
                Left = 102, Top = 3, Width = 90, Height = 24,
                UseVisualStyleBackColor = true,
            };
            _btnAddVolatile.Click += (s, e) => AddVolatileRequested?.Invoke(this, EventArgs.Empty);

            _toolbar.Controls.Add(_btnAddStatic);
            _toolbar.Controls.Add(_btnAddVolatile);

            // ---- Grid (Dock=Fill) ----
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                BackgroundColor = System.Drawing.SystemColors.Window,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            };

            // Columns: Name (40%), Value (40%), Scope (20%).
            // Static rows make Value read-only in CellBeginEdit.
            var colName = new DataGridViewTextBoxColumn
            {
                Name = "Name",
                HeaderText = "Name",
                ReadOnly = true,
                FillWeight = 40,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            var colValue = new DataGridViewTextBoxColumn
            {
                Name = "Value",
                HeaderText = "Value",
                FillWeight = 40,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            var colScope = new DataGridViewTextBoxColumn
            {
                Name = "Scope",
                HeaderText = "Scope",
                ReadOnly = true,
                FillWeight = 20,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            _grid.Columns.Add(colName);
            _grid.Columns.Add(colValue);
            _grid.Columns.Add(colScope);

            // Static rows refuse the edit before it opens -- avoids the
            // misleading caret + Esc-to-cancel UX. Volatile rows fall
            // through to the default editor.
            _grid.CellBeginEdit += OnCellBeginEdit;
            _grid.CellValueChanged += OnCellValueChanged;
            _grid.MouseDown += OnGridMouseDown;

            // ---- Context menu (right-click on grid) ----
            //
            // Same actions as the toolbar plus Remove / Clear-All-Volatile.
            // Enablement is computed in Opening based on what's selected.
            _rowMenu = new ContextMenuStrip();
            _miAddStatic   = new ToolStripMenuItem("Add Static Input...");
            _miAddVolatile = new ToolStripMenuItem("Add Volatile Input...");
            _miSep1        = new ToolStripSeparator();
            _miRemove      = new ToolStripMenuItem("Remove Selected");
            _miClearVolatile = new ToolStripMenuItem("Clear All Volatile");

            _miAddStatic.Click   += (s, e) => AddStaticRequested?.Invoke(this, EventArgs.Empty);
            _miAddVolatile.Click += (s, e) => AddVolatileRequested?.Invoke(this, EventArgs.Empty);
            _miRemove.Click      += OnRemoveClicked;
            _miClearVolatile.Click += (s, e) => ClearVolatileRequested?.Invoke(this, EventArgs.Empty);

            _rowMenu.Items.Add(_miAddStatic);
            _rowMenu.Items.Add(_miAddVolatile);
            _rowMenu.Items.Add(_miSep1);
            _rowMenu.Items.Add(_miRemove);
            _rowMenu.Items.Add(_miClearVolatile);
            _rowMenu.Opening += OnContextMenuOpening;

            _grid.ContextMenuStrip = _rowMenu;

            // Add Fill child FIRST then Top -- WinForms resolves Dock in
            // reverse z-order. Same trick the rest of Shell.Designer uses.
            Controls.Add(_grid);
            Controls.Add(_toolbar);
        }

        /// <summary>
        /// Replace all rows. Pass the full Static + Volatile picture in
        /// one shot -- the grid does not do incremental diffs.
        /// </summary>
        public void LoadData(IEnumerable<InputRow> rows)
        {
            _loading = true;
            try
            {
                // Capture selected id so we can restore selection after rebuild.
                string selectedId = null;
                if (_grid.SelectedRows.Count > 0)
                    selectedId = _grid.SelectedRows[0].Cells["Name"].Value as string;

                _grid.Rows.Clear();
                if (rows == null) return;

                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    int idx = _grid.Rows.Add(r.Id, r.Value ?? string.Empty, r.Scope ?? string.Empty);
                    var row = _grid.Rows[idx];
                    // Static rows: gray the Value cell so the read-only
                    // state reads visually, not just on attempted edit.
                    if (string.Equals(r.Scope, ScopeStatic, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Cells["Value"].Style.BackColor = System.Drawing.SystemColors.Control;
                        row.Cells["Value"].Style.ForeColor = System.Drawing.SystemColors.GrayText;
                    }
                    if (selectedId != null
                        && string.Equals(r.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                    }
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnCellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var row = _grid.Rows[e.RowIndex];
            string scope = row.Cells["Scope"].Value as string;
            // Block the edit on Static rows -- those are read-only here;
            // the top-bar textbox is the source of truth.
            if (string.Equals(scope, ScopeStatic, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        }

        private void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_loading) return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "Value") return;

            var row   = _grid.Rows[e.RowIndex];
            string id    = row.Cells["Name"].Value as string;
            string value = row.Cells["Value"].Value as string ?? string.Empty;
            string scope = row.Cells["Scope"].Value as string;
            // Defense in depth: even if a Static row somehow committed
            // (it shouldn't, see CellBeginEdit), do not echo the change
            // out as a volatile mutation.
            if (string.Equals(scope, ScopeStatic, StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrEmpty(id)) return;

            VolatileValueEdited?.Invoke(this, new VolatileValueEditedEventArgs(id, value));
        }

        // Right-click selects the row under the cursor BEFORE the menu
        // pops. Without this, the menu would target whichever row was
        // previously selected -- almost never what the user expects.
        private void OnGridMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[hit.RowIndex].Selected = true;
            }
        }

        private void OnContextMenuOpening(object sender, CancelEventArgs e)
        {
            // Enable Remove only when a Volatile row is selected.
            // (Static rows are managed via Edit -> Shared Inputs.)
            bool removable = false;
            if (_grid.SelectedRows.Count > 0)
            {
                string scope = _grid.SelectedRows[0].Cells["Scope"].Value as string;
                removable = string.Equals(scope, ScopeVolatile, StringComparison.OrdinalIgnoreCase);
            }
            _miRemove.Enabled = removable;

            // Clear All Volatile is only meaningful if any volatile row exists.
            bool anyVolatile = false;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (string.Equals(r.Cells["Scope"].Value as string,
                                  ScopeVolatile, StringComparison.OrdinalIgnoreCase))
                {
                    anyVolatile = true; break;
                }
            }
            _miClearVolatile.Enabled = anyVolatile;
        }

        private void OnRemoveClicked(object sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            string id    = row.Cells["Name"].Value as string;
            string scope = row.Cells["Scope"].Value as string;
            // Mirror the context-menu enablement logic -- belt-and-suspenders.
            if (!string.Equals(scope, ScopeVolatile, StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrEmpty(id)) return;
            RemoveRequested?.Invoke(this, id);
        }

        /// <summary>One row's worth of data passed into LoadData.</summary>
        public sealed class InputRow
        {
            public string Id    { get; set; }
            public string Value { get; set; }
            public string Scope { get; set; }   // "Static" or "Volatile"
        }

        public sealed class VolatileValueEditedEventArgs : EventArgs
        {
            public string Id    { get; }
            public string Value { get; }
            public VolatileValueEditedEventArgs(string id, string value)
            {
                Id = id; Value = value;
            }
        }
    }
}
