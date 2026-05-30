using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Modeless popout window showing a snapshot of the main results grid.
    /// Opened from the toolbar glyph button or the grid's right-click menu;
    /// the snapshot is taken at open time and never changes -- the user can
    /// keep one or more historical results visible while re-running the
    /// script or loading a different workspace.
    ///
    /// Includes its own "Export CSV" button so a popout can be saved out
    /// even after the main grid has moved on. The DataGridView is ReadOnly
    /// (matches the source grid's behavior) and uses a dedicated copy of
    /// the column/row data -- no live binding back to the source.
    /// </summary>
    public sealed class GridOutForm : Form
    {
        private readonly DataGridView _grid;
        private readonly Label _statusLabel;

        public GridOutForm(DataGridView source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // Window chrome. Sized to comfortably fit a handful of
            // columns; the user can resize. Min-size keeps the export
            // button + status label visible even when shrunk.
            Text             = BuildTitle(source);
            StartPosition    = FormStartPosition.CenterParent;
            ClientSize       = new Size(900, 500);
            MinimumSize      = new Size(420, 200);
            ShowInTaskbar    = true;
            KeyPreview       = true;

            // Bottom panel: small status label on the left, export
            // button on the right. Fixed height -- nothing else lives
            // here so a Panel beats a TableLayoutPanel.
            var bottom = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 36,
                Padding   = new Padding(8, 6, 8, 6),
                BackColor = SystemColors.Control,
            };
            _statusLabel = new Label
            {
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize  = false,
            };
            var exportBtn = new System.Windows.Forms.Button
            {
                Dock = DockStyle.Right,
                Text = "Export CSV...",
                Width = 110,
                UseVisualStyleBackColor = true,
            };
            exportBtn.Click += (_, __) => ExportCsv();
            bottom.Controls.Add(_statusLabel);
            bottom.Controls.Add(exportBtn);

            // Snapshot grid. Same look as the source: ReadOnly,
            // no add-row stub, auto-fit columns.
            _grid = new DataGridView
            {
                Dock                       = DockStyle.Fill,
                ReadOnly                   = true,
                AllowUserToAddRows         = false,
                AllowUserToDeleteRows      = false,
                AllowUserToResizeRows      = false,
                RowHeadersVisible          = false,
                AutoSizeColumnsMode        = DataGridViewAutoSizeColumnsMode.AllCells,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                SelectionMode              = DataGridViewSelectionMode.CellSelect,
                ClipboardCopyMode          = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
            };

            CloneFromSource(source);

            Controls.Add(_grid);    // fill
            Controls.Add(bottom);   // bottom -- added second so Dock=Fill takes the remainder

            // Esc closes the popout -- it's a glance-and-go window so
            // matching dialog ergonomics keeps the muscle memory simple.
            KeyDown += (_, ev) =>
            {
                if (ev.KeyCode == Keys.Escape) { Close(); ev.Handled = true; }
            };
        }

        // Copy column shape + every (non-new-row) cell value into our
        // internal grid. We don't share columns with the source -- a
        // DataGridViewColumn can only belong to one grid at a time, so
        // the snapshot has to be a deep-copy of the visible state.
        private void CloneFromSource(DataGridView source)
        {
            var srcCols = source.Columns.Cast<DataGridViewColumn>()
                .Where(c => c.Visible)
                .OrderBy(c => c.DisplayIndex)
                .ToList();

            foreach (var sc in srcCols)
            {
                var nc = new DataGridViewTextBoxColumn
                {
                    Name        = sc.Name,
                    HeaderText  = sc.HeaderText,
                    SortMode    = DataGridViewColumnSortMode.Automatic,
                };
                _grid.Columns.Add(nc);
            }

            int rowCount = 0;
            foreach (DataGridViewRow row in source.Rows)
            {
                if (row.IsNewRow) continue;
                var values = srcCols.Select(c =>
                {
                    var cell = row.Cells[c.Index];
                    var v = cell?.Value;
                    return v == null || v == DBNull.Value ? string.Empty : v.ToString();
                }).ToArray();
                _grid.Rows.Add(values);
                rowCount++;
            }

            _statusLabel.Text = $"Snapshot: {rowCount} row{(rowCount == 1 ? "" : "s")}, {srcCols.Count} column{(srcCols.Count == 1 ? "" : "s")}  -  captured {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }

        // Title shows the snapshot's row count up-front so the user can
        // tell two popouts apart at a glance via the taskbar / Alt+Tab.
        private static string BuildTitle(DataGridView source)
        {
            int rows = source.Rows.Cast<DataGridViewRow>().Count(r => !r.IsNewRow);
            return $"Grid-Out  ({rows} row{(rows == 1 ? "" : "s")})  -  {DateTime.Now:HH:mm:ss}";
        }

        // Same CSV semantics as the main toolbar's Export button --
        // RFC 4180-ish, UTF-8 with BOM so Excel auto-detects encoding.
        // Duplicated logic kept tiny + local to avoid coupling the
        // popout back to Shell.cs.
        private void ExportCsv()
        {
            if (_grid.Columns.Count == 0 || _grid.Rows.Count == 0)
            {
                MessageBox.Show(this,
                    "The snapshot is empty -- nothing to export.",
                    "Nothing to export",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Title    = "Export snapshot to CSV",
                Filter   = "CSV (Comma-delimited) (*.csv)|*.csv",
                FileName = "ScriptDeck-grid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv",
                AddExtension    = true,
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var utf8WithBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                    using (var sw = new StreamWriter(dlg.FileName, append: false, encoding: utf8WithBom))
                    {
                        var cols = _grid.Columns.Cast<DataGridViewColumn>()
                            .OrderBy(c => c.DisplayIndex)
                            .ToList();
                        sw.WriteLine(string.Join(",", cols.Select(c => CsvEscape(c.HeaderText ?? c.Name ?? string.Empty))));
                        foreach (DataGridViewRow row in _grid.Rows)
                        {
                            if (row.IsNewRow) continue;
                            var values = cols.Select(c =>
                            {
                                var v = row.Cells[c.Index]?.Value;
                                return v == null || v == DBNull.Value ? string.Empty : v.ToString();
                            });
                            sw.WriteLine(string.Join(",", values.Select(CsvEscape)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Failed to write the CSV:\n\n" + ex.Message,
                        "Export failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            bool needsQuotes = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!needsQuotes) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
