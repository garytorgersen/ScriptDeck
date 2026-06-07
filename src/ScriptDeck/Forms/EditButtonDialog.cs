using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.Hosting;
using ScriptDeck.Workspace;

// See WorkspaceRenderer.cs for the rationale on these aliases — Workspace.Button
// vs WinForms.Button collide on the bare 'Button' name.
using WsButton = ScriptDeck.Workspace.Button;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Modal editor for a single workspace <see cref="WsButton"/>. The
    /// dialog mutates a copy of the button — only on OK do we copy the
    /// edits back onto the caller's instance via <see cref="ApplyTo"/>.
    /// That way Cancel really cancels, even after the user has typed
    /// into every field.
    ///
    /// Used both for editing existing buttons (right-click → Edit) and
    /// for creating new ones (right-click tab → Add Button), distinguished
    /// purely by the dialog title the caller supplies.
    /// </summary>
    public partial class EditButtonDialog : Form
    {
        // ScriptsRoot is captured so the file-picker can default to the
        // workspace's scripts folder and so we can offer "make this path
        // relative" if the user picks a file under the root.
        private readonly string _scriptsRoot;

        // Optional. When supplied, the "Edit Script..." button is enabled
        // for PowerShell buttons and opens ScriptEditorDialog against the
        // current script path. When null (e.g. unit tests, future
        // headless callers), the button is disabled with a tooltip.
        private readonly Dispatcher _dispatcher;

        // Optional shared-input snapshot, passed through to the Script
        // Editor when the user clicks Edit Script. Lets the editor's
        // Test Inputs grid reflect the workspace's current inputs even
        // though the Edit Button dialog itself is modal over Shell.
        private readonly IList<SharedInputSnapshot> _sharedInputs;

        // Captured ref to the button we're editing so the interpreter
        // row can swap the displayed value between PythonInterpreter
        // and BashInterpreter when the user flips the Executor combo.
        // Mutating the source directly happens only in ApplyTo on OK.
        private WsButton _sourceButton;

        public EditButtonDialog(WsButton source, string scriptsRoot)
            : this(source, scriptsRoot, dispatcher: null, sharedInputs: null) { }

        public EditButtonDialog(WsButton source, string scriptsRoot, Dispatcher dispatcher)
            : this(source, scriptsRoot, dispatcher, sharedInputs: null) { }

        public EditButtonDialog(
            WsButton source,
            string scriptsRoot,
            Dispatcher dispatcher,
            IList<SharedInputSnapshot> sharedInputs)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            InitializeComponent();
            _scriptsRoot  = scriptsRoot;
            _dispatcher   = dispatcher;
            _sharedInputs = sharedInputs;

            // Hydrate fields from the source. We never bind directly to
            // the source — see class-level comment on cancel semantics.
            textBox_Id.Text          = source.Id ?? string.Empty;
            textBox_Label.Text       = source.Label ?? string.Empty;
            comboBox_Executor.Text   = string.IsNullOrEmpty(source.Executor) ? "powershell" : source.Executor;
            textBox_ScriptPath.Text  = source.ScriptPath ?? string.Empty;
            textBox_WorkingDir.Text  = source.WorkingDirectory ?? string.Empty;

            // Args render one-per-line so users can edit positional args
            // visually. Empty lines are dropped on ApplyTo to keep the
            // serialized form tidy.
            textBox_Args.Lines = (source.Args ?? new List<string>()).ToArray();

            var outputs = source.Outputs ?? new List<string>();
            checkBox_OutputRtb.Checked  = outputs.Any(o => string.Equals(o, "rtb", StringComparison.OrdinalIgnoreCase));
            checkBox_OutputGrid.Checked = outputs.Any(o => string.Equals(o, "grid", StringComparison.OrdinalIgnoreCase));

            checkBox_Confirm.Checked = source.Confirm;
            checkBox_Log.Checked     = source.Log;
            checkBox_RunInBackground.Checked = source.RunInBackground;

            // Per-language interpreter override. The same row is reused
            // for python and bash -- its label + bound value swap based
            // on the selected executor. UpdatePythonRowVisibility (now
            // more aptly named UpdateInterpreterRowVisibility, but
            // renaming would churn the Designer hookup) decides which
            // value to display + persist.
            _sourceButton = source;
            textBox_PythonInterpreter.Text = source.PythonInterpreter ?? string.Empty;

            // RTB format: select the saved value if it matches an item;
            // otherwise default to "default" (the legacy behavior). This
            // way an unrecognized value (future format we don't know
            // about, typo) doesn't lose data — ApplyTo will keep the
            // original string if the user doesn't change the dropdown.
            string fmt = string.IsNullOrWhiteSpace(source.RtbFormat) ? "default" : source.RtbFormat.Trim().ToLowerInvariant();
            int idx = comboBox_RtbFormat.Items.IndexOf(fmt);
            comboBox_RtbFormat.SelectedIndex = idx >= 0 ? idx : 0;

            // Show/hide the Python interpreter row based on current
            // executor. Also wire the change handler so flipping the
            // executor dropdown toggles the row.
            UpdatePythonRowVisibility();
            comboBox_Executor.TextChanged += (_, __) => UpdatePythonRowVisibility();
        }

        private string _lastInterpreterExecutor; // tracks which language's value is currently in the textbox
        private void UpdatePythonRowVisibility()
        {
            // The single interpreter row serves both python and bash.
            // The label switches ("Python:" / "Bash:") and the textbox
            // value swaps to the matching field on the source button
            // when the user flips the dropdown. This way they can
            // configure both interpreters on the same button (e.g.
            // a python override + a bash override) without losing
            // values when toggling executors mid-edit.
            string executor = (comboBox_Executor.Text ?? string.Empty).Trim();
            bool isPython = string.Equals(executor, "python", StringComparison.OrdinalIgnoreCase);
            bool isBash   = string.Equals(executor, "bash",   StringComparison.OrdinalIgnoreCase);

            // Persist the currently-displayed value to whichever field
            // it belongs to BEFORE swapping (otherwise edits made while
            // python was selected would be lost when the user toggled
            // to bash and back).
            if (_sourceButton != null && _lastInterpreterExecutor != null)
            {
                if (string.Equals(_lastInterpreterExecutor, "python", StringComparison.OrdinalIgnoreCase))
                    _sourceButton.PythonInterpreter = textBox_PythonInterpreter.Text;
                else if (string.Equals(_lastInterpreterExecutor, "bash", StringComparison.OrdinalIgnoreCase))
                    _sourceButton.BashInterpreter = textBox_PythonInterpreter.Text;
            }

            // Show + label + (re)load value for the new executor.
            bool showRow = isPython || isBash;
            label_PythonInterpreter.Visible   = showRow;
            textBox_PythonInterpreter.Visible = showRow;
            button_BrowsePython.Visible       = showRow;

            if (showRow)
            {
                label_PythonInterpreter.Text = isPython ? "Python:" : "Bash:";
                if (_sourceButton != null)
                {
                    textBox_PythonInterpreter.Text = isPython
                        ? (_sourceButton.PythonInterpreter ?? string.Empty)
                        : (_sourceButton.BashInterpreter   ?? string.Empty);
                }
                _lastInterpreterExecutor = isPython ? "python" : "bash";
            }
            else
            {
                _lastInterpreterExecutor = null;
            }
        }

        private void Button_BrowsePython_Click(object sender, EventArgs e)
        {
            // Filter and title swap based on which language's row is
            // active (the same Browse button serves both python and
            // bash interpreter selection).
            bool isBash = string.Equals(
                (comboBox_Executor.Text ?? string.Empty).Trim(),
                "bash", StringComparison.OrdinalIgnoreCase);
            using (var dlg = new OpenFileDialog
            {
                Title           = isBash ? "Select Bash interpreter" : "Select Python interpreter",
                Filter          = isBash
                    ? "Bash executable (bash*.exe;wsl.exe)|bash*.exe;wsl.exe|All files (*.*)|*.*"
                    : "Python executable (python*.exe)|python*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
            })
            {
                // Seed from existing value if any (so reopening lands
                // back in the same folder).
                if (!string.IsNullOrWhiteSpace(textBox_PythonInterpreter.Text))
                {
                    try
                    {
                        var dir = System.IO.Path.GetDirectoryName(textBox_PythonInterpreter.Text);
                        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                            dlg.InitialDirectory = dir;
                    }
                    catch { /* invalid path -- ignore seeding */ }
                }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    textBox_PythonInterpreter.Text = dlg.FileName;
            }
        }

        /// <summary>
        /// Push the edited values into the caller-supplied target.
        /// Called by the parent ONLY after ShowDialog returns OK.
        /// </summary>
        public void ApplyTo(WsButton target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Id               = textBox_Id.Text.Trim();
            target.Label            = textBox_Label.Text;
            target.Executor         = (comboBox_Executor.Text ?? "powershell").Trim().ToLowerInvariant();
            target.ScriptPath       = textBox_ScriptPath.Text;
            target.WorkingDirectory = string.IsNullOrWhiteSpace(textBox_WorkingDir.Text) ? null : textBox_WorkingDir.Text;

            // Drop empty/whitespace lines — users tend to leave a blank at
            // the bottom of multi-line text boxes, and shells don't want
            // empty positional args.
            target.Args = (textBox_Args.Lines ?? new string[0])
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var outputs = new List<string>();
            if (checkBox_OutputRtb.Checked)  outputs.Add("rtb");
            if (checkBox_OutputGrid.Checked) outputs.Add("grid");
            target.Outputs = outputs;

            target.Confirm = checkBox_Confirm.Checked;
            target.Log     = checkBox_Log.Checked;
            target.RunInBackground = checkBox_RunInBackground.Checked;

            // Persist BOTH interpreter overrides. The currently-visible
            // textbox holds the value for whichever executor is selected;
            // the OTHER language's value lives on the source button
            // (last sync'd via UpdatePythonRowVisibility's flush). Only
            // persist the matching field when the executor is that
            // language to avoid cluttering JSON for non-applicable
            // executors.
            bool isPython = string.Equals(
                target.Executor, "python", StringComparison.OrdinalIgnoreCase);
            bool isBash = string.Equals(
                target.Executor, "bash", StringComparison.OrdinalIgnoreCase);
            string current = textBox_PythonInterpreter.Text?.Trim();

            if (isPython)
            {
                target.PythonInterpreter = string.IsNullOrEmpty(current) ? null : current;
                target.BashInterpreter   = _sourceButton?.BashInterpreter; // preserved across flips
            }
            else if (isBash)
            {
                target.BashInterpreter   = string.IsNullOrEmpty(current) ? null : current;
                target.PythonInterpreter = _sourceButton?.PythonInterpreter;
            }
            else
            {
                // Non-python, non-bash -- clear both to keep JSON tidy.
                target.PythonInterpreter = null;
                target.BashInterpreter   = null;
            }

            // RTB format: persist null when "default" is selected so the
            // JSON stays minimal for buttons that didn't opt in. Skip
            // persistence entirely when the executor isn't powershell —
            // the field has no meaning for cmd / process and we don't
            // want stale state cluttering the file.
            string selected = (comboBox_RtbFormat.SelectedItem as string) ?? "default";
            if (string.Equals(target.Executor, "powershell", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(selected, "default", StringComparison.OrdinalIgnoreCase))
            {
                target.RtbFormat = selected;
            }
            else
            {
                target.RtbFormat = null;
            }
        }

        // ---- UI handlers ----

        private void Button_EditScript_Click(object sender, EventArgs e)
        {
            // Phase 1 limitation: the editor speaks PowerShell. Cmd/batch
            // and process buttons still get the Browse path; we just
            // prevent the editor from opening a .cmd it can't lint.
            string executor = (comboBox_Executor.Text ?? string.Empty).Trim();
            if (!string.Equals(executor, "powershell", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "The Script Editor currently supports PowerShell scripts only.\r\n\r\n" +
                    "Set Executor to 'powershell' to edit this script in-app, or use Browse... to point at an existing file.",
                    "Script Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_dispatcher == null)
            {
                // Caller used the legacy 2-arg ctor (no dispatcher). The
                // editor needs a runspace for Run Test, so we can't open
                // it; the user can still edit the script in any external
                // editor and Browse... back to it.
                MessageBox.Show(this,
                    "The Script Editor isn't available in this context.",
                    "Script Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Resolve the current path: relative entries get joined with
            // ScriptsRoot so the editor opens the actual file. Empty path
            // means "new script" — the editor opens with a blank canvas
            // and a Save As prompt on first save.
            string current = textBox_ScriptPath.Text?.Trim() ?? string.Empty;
            string resolved = current;
            if (!string.IsNullOrEmpty(current) && !Path.IsPathRooted(current)
                && !string.IsNullOrEmpty(_scriptsRoot))
            {
                try { resolved = Path.GetFullPath(Path.Combine(_scriptsRoot, current)); }
                catch { resolved = current; }
            }

            using (var dlg = new ScriptEditorDialog(_dispatcher, _scriptsRoot, resolved, _sharedInputs))
            {
                dlg.ShowDialog(this);

                // If the editor saved to a new path (e.g. Save As from a
                // blank-new flow), reflect that back into the Script Path
                // field. Try to relativize against the scripts root so
                // the workspace stays portable.
                string saved = dlg.SavedPath;
                if (!string.IsNullOrEmpty(saved)
                    && !string.Equals(saved, resolved, StringComparison.OrdinalIgnoreCase))
                {
                    string toShow = saved;
                    if (!string.IsNullOrEmpty(_scriptsRoot) && Directory.Exists(_scriptsRoot))
                    {
                        var rel = TryMakeRelative(_scriptsRoot, saved);
                        if (rel != null && !rel.StartsWith("..", StringComparison.Ordinal))
                            toShow = rel;
                    }
                    textBox_ScriptPath.Text = toShow;
                }
            }
        }

        private void Button_Browse_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Select script or executable",
                Filter =
                    "Common (*.ps1;*.cmd;*.bat;*.exe;*.py;*.sh)|*.ps1;*.cmd;*.bat;*.exe;*.py;*.sh|" +
                    "PowerShell (*.ps1)|*.ps1|" +
                    "CMD/BAT (*.cmd;*.bat)|*.cmd;*.bat|" +
                    "Python (*.py)|*.py|" +
                    "Bash (*.sh)|*.sh|" +
                    "Executables (*.exe)|*.exe|" +
                    "All files (*.*)|*.*",
                CheckFileExists = true,
            })
            {
                // Default to the scripts root so picking a script in the
                // workspace folder is one click — same convention the
                // sample workspace uses.
                if (!string.IsNullOrEmpty(_scriptsRoot) && Directory.Exists(_scriptsRoot))
                    dlg.InitialDirectory = _scriptsRoot;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var picked = dlg.FileName;

                // Offer to relativize against the workspace scripts root.
                // Workspaces are portable when paths are relative; absolute
                // paths break the moment the workspace moves between
                // machines. We ask rather than do-silently because the
                // user might genuinely want an absolute path (e.g. a
                // system32 utility).
                if (!string.IsNullOrEmpty(_scriptsRoot) && Directory.Exists(_scriptsRoot))
                {
                    var rel = TryMakeRelative(_scriptsRoot, picked);
                    if (rel != null && !rel.StartsWith("..", StringComparison.Ordinal))
                    {
                        var dr = MessageBox.Show(this,
                            $"Use the path relative to the workspace?\r\n\r\nRelative: {rel}\r\nAbsolute: {picked}",
                            "Path style",
                            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                        if (dr == DialogResult.Cancel) return;
                        textBox_ScriptPath.Text = (dr == DialogResult.Yes) ? rel : picked;
                        AutoSelectExecutorFromExtension(picked);
                        return;
                    }
                }

                textBox_ScriptPath.Text = picked;
                AutoSelectExecutorFromExtension(picked);
            }
        }

        private void AutoSelectExecutorFromExtension(string path)
        {
            // Soft hint: switch the executor to match the file's
            // extension. We never overwrite a deliberately chosen
            // executor that disagrees with the extension -- if you've
            // set "process" and pick a .py, we leave it alone. The
            // ext->kind map is the canonical mapping the dispatcher
            // also uses to validate.
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            string suggest = null;
            switch (ext)
            {
                case ".ps1":  suggest = "powershell"; break;
                case ".cmd":
                case ".bat":  suggest = "cmd"; break;
                case ".exe":  suggest = "process"; break;
                case ".py":   suggest = "python"; break;
                case ".sh":
                case ".bash": suggest = "bash"; break;
            }
            if (suggest != null) comboBox_Executor.Text = suggest;
        }

        private static string TryMakeRelative(string baseDir, string fullPath)
        {
            try
            {
                // Path.GetRelativePath is .NET Core+; on net48 we roll
                // our own using Uri's MakeRelativeUri. Trailing slash on
                // baseDir is required for Uri to treat it as a folder.
                if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                    !baseDir.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    baseDir += Path.DirectorySeparatorChar;
                }
                var baseUri = new Uri(baseDir);
                var fileUri = new Uri(fullPath);
                if (baseUri.Scheme != fileUri.Scheme) return null;
                var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
                return rel;
            }
            catch { return null; }
        }

        private void Button_Ok_Click(object sender, EventArgs e)
        {
            // Minimal validation: a button with no executor or no script
            // path will fail at click-time anyway; reject up-front so the
            // user notices in the editor instead of debugging an empty
            // run later.
            if (string.IsNullOrWhiteSpace(textBox_Label.Text))
            {
                MessageBox.Show(this, "Label is required.", "ScriptDeck",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                textBox_Label.Focus();
                this.DialogResult = DialogResult.None;
                return;
            }
            if (string.IsNullOrWhiteSpace(comboBox_Executor.Text))
            {
                MessageBox.Show(this, "Executor is required.", "ScriptDeck",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                comboBox_Executor.Focus();
                this.DialogResult = DialogResult.None;
                return;
            }
            if (string.IsNullOrWhiteSpace(textBox_ScriptPath.Text))
            {
                MessageBox.Show(this, "Script path is required.", "ScriptDeck",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                textBox_ScriptPath.Focus();
                this.DialogResult = DialogResult.None;
                return;
            }
            // Auto-fill an Id if blank — saves the user a step. We slug
            // from the label since it's required and human-readable.
            if (string.IsNullOrWhiteSpace(textBox_Id.Text))
                textBox_Id.Text = SlugifyId(textBox_Label.Text);
        }

        private static string SlugifyId(string label)
        {
            if (string.IsNullOrEmpty(label)) return "btn";
            var chars = label.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return string.IsNullOrEmpty(slug) ? "btn" : "btn-" + slug;
        }
    }
}
