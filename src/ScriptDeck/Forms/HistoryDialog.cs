using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.History;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Read-only dialog that browses the SQLite run history. Top half is a
    /// row-per-run grid; bottom half is the selected row's full details.
    ///
    /// We bind the grid to a plain <see cref="List{RunRecord}"/> via DataSource
    /// rather than BindingList — the list is regenerated on every refresh,
    /// not mutated in place, so change-tracking buys us nothing.
    ///
    /// "Clear..." prompts before deleting, matching every other destructive
    /// action in ScriptDeck (delete tab, delete button).
    /// </summary>
    public partial class HistoryDialog : Form
    {
        private readonly RunHistory _history;
        private List<RunRecord> _rows = new List<RunRecord>();

        // Cap fetched rows. 200 is plenty for a "recent runs" dialog —
        // power users wanting deeper history can crack open the .db
        // directly. Keeping this small also keeps the grid snappy.
        private const int RowLimit = 200;

        public HistoryDialog(RunHistory history)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            InitializeComponent();
            _history = history;

            ConfigureColumns();
            this.Shown += (_, __) => Reload();
        }

        // Programmatic columns — DataPropertyName binds each cell to the
        // matching RunRecord property when DataSource is set. Display values
        // (formatted timestamp, friendly duration) come from non-bound
        // columns we fill manually in PopulateGrid.
        private void ConfigureColumns()
        {
            // The displayed columns are intentionally a curated subset.
            // Workspace path, exit code, error message, etc. live in the
            // details pane only — putting them in the grid would force
            // horizontal scroll on the common case.
            dataGridView_Runs.Columns.Clear();

            dataGridView_Runs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col_Started",
                HeaderText = "Started",
                Width = 150,
            });
            dataGridView_Runs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col_Duration",
                HeaderText = "Duration",
                Width = 80,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
            });
            dataGridView_Runs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col_Status",
                HeaderText = "Status",
                Width = 80,
            });
            dataGridView_Runs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col_Button",
                HeaderText = "Button",
                Width = 200,
            });
            dataGridView_Runs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col_Executor",
                HeaderText = "Executor",
                Width = 90,
            });
            // Last column auto-fills remaining width — workspace name is
            // the most variable-length value, so it gets the spare space.
            dataGridView_Runs.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col_Workspace",
                HeaderText = "Workspace",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            });
        }

        private void Reload()
        {
            _rows = _history.GetRecent(RowLimit).ToList();
            PopulateGrid();
            label_Count.Text = _history.Disabled
                ? $"History disabled: {_history.DisabledReason}"
                : $"{_rows.Count} run(s)" + (_rows.Count >= RowLimit ? $" (showing newest {RowLimit})" : string.Empty);
            ClearDetails();

            // Auto-select the first row so opening the dialog isn't a
            // two-click affair to see anything. Wrapped in a try because
            // an empty grid would throw on row[0].
            if (dataGridView_Runs.Rows.Count > 0)
            {
                try { dataGridView_Runs.Rows[0].Selected = true; } catch { }
            }
        }

        private void PopulateGrid()
        {
            dataGridView_Runs.Rows.Clear();
            foreach (var r in _rows)
            {
                int idx = dataGridView_Runs.Rows.Add(
                    FormatLocal(r.StartedAtUtc),
                    FormatDuration(r.Duration),
                    r.Status ?? string.Empty,
                    r.ButtonLabel ?? string.Empty,
                    r.Executor ?? string.Empty,
                    r.WorkspaceName ?? string.Empty);

                // Stash the record on the row so the selection-changed
                // handler doesn't have to re-index by row number — clearing
                // and repopulating the grid would otherwise desynchronize
                // _rows from row indices on subsequent refreshes.
                dataGridView_Runs.Rows[idx].Tag = r;

                // Color-code the status cell so failures jump out at a
                // glance — same red/yellow/default scheme the console uses.
                var cell = dataGridView_Runs.Rows[idx].Cells["col_Status"];
                switch (r.Status)
                {
                    case "Failed":
                        cell.Style.ForeColor = System.Drawing.Color.Firebrick;
                        break;
                    case "Cancelled":
                        cell.Style.ForeColor = System.Drawing.Color.DarkGoldenrod;
                        break;
                    case "Ok":
                    default:
                        // leave default color — green-on-white is hard to read
                        break;
                }
            }
        }

        private void DataGridView_Runs_SelectionChanged(object sender, EventArgs e)
        {
            if (dataGridView_Runs.SelectedRows.Count == 0)
            {
                ClearDetails();
                return;
            }
            var row = dataGridView_Runs.SelectedRows[0];
            var rec = row.Tag as RunRecord;
            if (rec == null) { ClearDetails(); return; }
            ShowDetails(rec);
        }

        private void ShowDetails(RunRecord r)
        {
            textBox_Status.Text     = r.Status ?? string.Empty;
            textBox_Started.Text    = FormatLocal(r.StartedAtUtc) + "  (UTC: " + r.StartedAtUtc.ToString("u", CultureInfo.InvariantCulture) + ")";
            textBox_Duration.Text   = FormatDuration(r.Duration);
            textBox_Workspace.Text  = string.IsNullOrEmpty(r.WorkspaceName)
                ? (r.WorkspacePath ?? string.Empty)
                : $"{r.WorkspaceName}  \u2014  {r.WorkspacePath}";
            textBox_Button.Text     = string.IsNullOrEmpty(r.ButtonId)
                ? (r.ButtonLabel ?? string.Empty)
                : $"{r.ButtonLabel}  ({r.ButtonId})";
            textBox_Executor.Text   = r.Executor ?? string.Empty;
            textBox_Script.Text     = r.ScriptPath ?? string.Empty;
            textBox_WorkingDir.Text = r.WorkingDirectory ?? string.Empty;
            // Args one-per-line in the multiline box. Mirrors the editor —
            // visually scannable, easy to copy a single arg if needed.
            textBox_Args.Lines      = (r.Args ?? new List<string>()).ToArray();
            textBox_ExitCode.Text   = r.ExitCode.HasValue
                ? r.ExitCode.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            textBox_Error.Text      = r.ErrorMessage ?? string.Empty;
        }

        private void ClearDetails()
        {
            textBox_Status.Text = string.Empty;
            textBox_Started.Text = string.Empty;
            textBox_Duration.Text = string.Empty;
            textBox_Workspace.Text = string.Empty;
            textBox_Button.Text = string.Empty;
            textBox_Executor.Text = string.Empty;
            textBox_Script.Text = string.Empty;
            textBox_WorkingDir.Text = string.Empty;
            textBox_Args.Lines = new string[0];
            textBox_ExitCode.Text = string.Empty;
            textBox_Error.Text = string.Empty;
        }

        private void Button_Refresh_Click(object sender, EventArgs e) => Reload();

        private void Button_Clear_Click(object sender, EventArgs e)
        {
            // Two-step destructive action: prompt with row count so users
            // who accidentally clicked don't blow away weeks of history.
            var dr = MessageBox.Show(this,
                $"Delete all {_rows.Count} run history record(s)?\r\n\r\n" +
                "This cannot be undone.",
                "Clear history",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
            _history.Clear();
            Reload();
        }

        // ---- formatting helpers ----

        private static string FormatLocal(DateTime utc)
        {
            try
            {
                var local = utc.Kind == DateTimeKind.Utc
                    ? utc.ToLocalTime()
                    : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
                // "yyyy-MM-dd HH:mm:ss" sorts as text and reads unambiguously
                // — avoids the M/d/yy ambiguity of the user's regional setting.
                return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch { return utc.ToString("o", CultureInfo.InvariantCulture); }
        }

        private static string FormatDuration(TimeSpan ts)
        {
            // Same scheme as Dispatcher.FormatDuration — kept duplicated
            // (private static) to avoid a bidirectional dependency between
            // Forms and Hosting. The two implementations are tiny and
            // unlikely to drift in any meaningful way.
            if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0} ms";
            if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:F2} s";
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        }
    }
}
