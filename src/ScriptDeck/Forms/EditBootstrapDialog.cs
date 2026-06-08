using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ScintillaNET;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Modal dialog for editing any of the three bootstrap helper files
    /// (PowerShell / Python / Bash) that ship next to ScriptDeck.exe.
    /// Combines a language selector at the top, a Scintilla editor in
    /// the middle, and Save / Save+Close / Cancel buttons at the bottom.
    ///
    /// Switching the dropdown mid-session:
    ///   - Prompts to save if the current buffer is dirty
    ///   - Loads the newly-selected file
    ///   - Re-applies the matching ScintillaNET lexer (reused from
    ///     ScriptEditorDialog's internal static configurators)
    ///
    /// Save semantics:
    ///   - PowerShell (.ps1) and Python (.py) saved with native line
    ///     endings (the runtime is happy with either)
    ///   - Bash (.sh) saved with forced LF -- CRLF breaks the shebang
    ///     and `then`/`fi` parsing on Windows-resident bash
    ///
    /// The caller (Shell) reads <see cref="ModifiedBootstraps"/> after
    /// the dialog closes to decide what to do next. PowerShell mods
    /// require resetting the live runspaces (the bootstrap is loaded
    /// once at runspace creation); Python and Bash mods take effect on
    /// the next button click automatically.
    /// </summary>
    public sealed class EditBootstrapDialog : Form
    {
        // ---- Bootstrap registry (path + lexer + line-ending policy) -------

        public enum BootstrapKind { PowerShell, Python, Bash }

        private sealed class BootstrapInfo
        {
            public BootstrapKind Kind   { get; set; }
            public string Display       { get; set; } // human-readable label for the dropdown
            public string FileName      { get; set; } // bare file name (next to ScriptDeck.exe)
            public bool   ForceLfOnSave { get; set; }
        }

        private static readonly BootstrapInfo[] _registry =
        {
            new BootstrapInfo { Kind = BootstrapKind.PowerShell, Display = "PowerShell  (ScriptDeck.Bootstrap.ps1)", FileName = "ScriptDeck.Bootstrap.ps1", ForceLfOnSave = false },
            new BootstrapInfo { Kind = BootstrapKind.Python,     Display = "Python      (scriptdeck_bootstrap.py)",  FileName = "scriptdeck_bootstrap.py",  ForceLfOnSave = false },
            new BootstrapInfo { Kind = BootstrapKind.Bash,       Display = "Bash        (scriptdeck_bootstrap.sh)",  FileName = "scriptdeck_bootstrap.sh",  ForceLfOnSave = true  },
        };

        // ---- UI state ------------------------------------------------------

        private readonly ComboBox     _combo;
        private readonly Scintilla    _editor;
        private readonly Label        _statusLabel;
        private readonly System.Windows.Forms.Button _btnSave;
        private readonly System.Windows.Forms.Button _btnSaveClose;
        private readonly System.Windows.Forms.Button _btnCancel;

        private BootstrapInfo _current;        // the bootstrap currently loaded into the editor
        private string        _currentDiskText;// snapshot of what's on disk for dirty comparison
        private bool          _suppressTextChanged;

        // Caller-facing: which bootstraps actually got a successful Save.
        // Shell uses this to decide whether to prompt for runspace reset.
        public ISet<BootstrapKind> ModifiedBootstraps { get; } =
            new HashSet<BootstrapKind>();

        public EditBootstrapDialog(BootstrapKind initialKind = BootstrapKind.PowerShell)
        {
            Text            = "Edit Bootstrap Helper";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(820, 560);
            MinimumSize     = new Size(560, 360);
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            KeyPreview      = true;

            // ---- Top strip: bootstrap selector ---------------------------
            var topPanel = new Panel
            {
                Dock    = DockStyle.Top,
                Height  = 50,
                Padding = new Padding(12, 10, 12, 8),
            };
            var lbl = new Label
            {
                Text      = "Bootstrap:",
                AutoSize  = true,
                Location  = new Point(0, 8),
                Font      = new Font("Segoe UI", 9F, FontStyle.Bold),
            };
            _combo = new ComboBox
            {
                Location      = new Point(85, 5),
                Width         = 320,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font          = new Font("Segoe UI", 9F),
            };
            foreach (var b in _registry) _combo.Items.Add(b.Display);
            _combo.SelectedIndexChanged += (_, __) => OnComboChanged();

            var hint = new Label
            {
                Text      = "Switching reloads from disk; you'll be prompted to save unsaved edits.",
                AutoSize  = true,
                Location  = new Point(415, 8),
                ForeColor = SystemColors.GrayText,
                Font      = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            };
            topPanel.Controls.AddRange(new Control[] { lbl, _combo, hint });

            // ---- Bottom strip: Save / Save+Close / Cancel + status ------
            var bottomPanel = new Panel
            {
                Dock    = DockStyle.Bottom,
                Height  = 44,
                Padding = new Padding(12, 8, 12, 8),
                BackColor = SystemColors.Control,
            };
            _statusLabel = new Label
            {
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                AutoEllipsis = true,
            };
            _btnCancel = new System.Windows.Forms.Button
            {
                Text  = "Cancel",
                Width = 90,
                Dock  = DockStyle.Right,
                DialogResult = DialogResult.Cancel,
            };
            _btnSaveClose = new System.Windows.Forms.Button
            {
                Text  = "Save && Close",
                Width = 110,
                Dock  = DockStyle.Right,
                UseVisualStyleBackColor = true,
            };
            _btnSaveClose.Click += (_, __) => OnSaveAndClose();
            _btnSave = new System.Windows.Forms.Button
            {
                Text  = "Save",
                Width = 90,
                Dock  = DockStyle.Right,
                UseVisualStyleBackColor = true,
            };
            _btnSave.Click += (_, __) => SaveCurrent(showStatus: true);

            // Right-docked controls layout in reverse-add order, so add
            // Cancel first to put it furthest right.
            bottomPanel.Controls.Add(_statusLabel);
            bottomPanel.Controls.Add(_btnCancel);
            bottomPanel.Controls.Add(_btnSaveClose);
            bottomPanel.Controls.Add(_btnSave);

            // ---- Editor (fill remainder) --------------------------------
            _editor = new Scintilla
            {
                Dock = DockStyle.Fill,
            };
            ConfigureBaseEditor(_editor);
            _editor.TextChanged += (_, __) =>
            {
                if (_suppressTextChanged) return;
                UpdateDirtyStatus();
            };

            // Add order matters with Dock: Fill last so it claims
            // whatever's between the top and bottom strips.
            Controls.Add(_editor);
            Controls.Add(bottomPanel);
            Controls.Add(topPanel);

            // Initial selection -> triggers OnComboChanged which loads
            // the file + applies the lexer.
            _combo.SelectedIndex = Array.FindIndex(_registry, b => b.Kind == initialKind);
            if (_combo.SelectedIndex < 0) _combo.SelectedIndex = 0;

            FormClosing += OnFormClosing;
            KeyDown     += OnKeyDown;
            AcceptButton = null;          // Enter shouldn't auto-trigger Save -- the editor needs it for newlines
            CancelButton = _btnCancel;
        }

        // ---- Editor base configuration -------------------------------------

        // Mirror of the editor setup that ScriptEditorDialog does in
        // ConfigureScintilla, minus the brace-match + bottom-status bits
        // that aren't relevant here. The per-language Configure*Lexer
        // calls happen separately when the selector changes.
        private static void ConfigureBaseEditor(Scintilla s)
        {
            const string font = "Consolas";
            s.Styles[Style.Default].Font = font;
            s.Styles[Style.Default].Size = 10;
            s.StyleClearAll();

            s.Margins[0].Type  = MarginType.Number;
            s.Margins[0].Width = 36;

            s.UseTabs     = false;
            s.TabWidth    = 4;
            s.IndentWidth = 4;

            // Brace match (language-neutral) so { } / [ ] / ( ) light up.
            s.Styles[Style.BraceLight].ForeColor = Color.LimeGreen;
            s.Styles[Style.BraceLight].Bold      = true;
            s.Styles[Style.BraceBad].ForeColor   = Color.Red;
            s.Styles[Style.BraceBad].Bold        = true;
        }

        // ---- Selector change -----------------------------------------------

        private void OnComboChanged()
        {
            var next = _registry[_combo.SelectedIndex];

            // If we already have a current file open and it's dirty,
            // give the user a chance to save before swapping. Cancelling
            // reverts the dropdown to the previous selection.
            if (_current != null && IsDirty())
            {
                var dr = MessageBox.Show(
                    this,
                    "Save changes to " + _current.FileName + " before switching?",
                    "Unsaved changes",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel)
                {
                    // Restore the previous selection without re-entering
                    // this handler. We can't call _combo.SelectedIndex = ...
                    // because that re-fires; null _current's index, set,
                    // then restore _current to avoid the loop.
                    _suppressTextChanged = true;
                    try
                    {
                        int prevIdx = Array.FindIndex(_registry, b => b.Kind == _current.Kind);
                        if (prevIdx >= 0 && prevIdx != _combo.SelectedIndex)
                        {
                            _combo.SelectedIndexChanged -= delegate { };
                            _combo.SelectedIndex = prevIdx;
                        }
                    }
                    finally { _suppressTextChanged = false; }
                    return;
                }
                if (dr == DialogResult.Yes)
                {
                    if (!SaveCurrent(showStatus: false)) return; // save failed -- bail
                }
                // No -> drop changes and proceed
            }

            LoadBootstrap(next);
        }

        private void LoadBootstrap(BootstrapInfo info)
        {
            _current = info;

            string path = ResolvePath(info);
            string disk = "";
            bool exists = File.Exists(path);
            if (exists)
            {
                try { disk = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    disk = "";
                    _statusLabel.Text = "Could not read " + info.FileName + ": " + ex.Message;
                    _statusLabel.ForeColor = Color.Firebrick;
                }
            }

            _currentDiskText = disk;
            _suppressTextChanged = true;
            try
            {
                _editor.Text = disk;
                ApplyLexerFor(info.Kind);
                _editor.EmptyUndoBuffer(); // a freshly-loaded file isn't "an edit"
            }
            finally { _suppressTextChanged = false; }

            // Status: show path + file size, or warn if missing.
            if (!exists)
            {
                _statusLabel.Text = "Not found: " + path + " (Save will create it)";
                _statusLabel.ForeColor = Color.DarkOrange;
            }
            else
            {
                _statusLabel.Text = path;
                _statusLabel.ForeColor = SystemColors.GrayText;
            }

            UpdateTitleAndButtons();
        }

        private void ApplyLexerFor(BootstrapKind kind)
        {
            switch (kind)
            {
                case BootstrapKind.PowerShell:
                    ScriptEditorDialog.ConfigurePowerShellLexer(_editor);
                    break;
                case BootstrapKind.Python:
                    ScriptEditorDialog.ConfigurePythonLexer(_editor);
                    break;
                case BootstrapKind.Bash:
                    ScriptEditorDialog.ConfigureBashLexer(_editor);
                    break;
            }
            _editor.Colorize(0, _editor.TextLength);
        }

        // ---- Save / dirty tracking -----------------------------------------

        private bool IsDirty()
        {
            return _current != null && _editor.Text != _currentDiskText;
        }

        private void UpdateDirtyStatus()
        {
            UpdateTitleAndButtons();
        }

        private void UpdateTitleAndButtons()
        {
            string dirtyMark = IsDirty() ? " *" : "";
            Text = "Edit Bootstrap Helper" + dirtyMark;
            _btnSave.Enabled = _btnSaveClose.Enabled = _current != null;
        }

        // Returns true if save succeeded (or wasn't needed).
        private bool SaveCurrent(bool showStatus)
        {
            if (_current == null) return false;
            if (!IsDirty()) return true;

            string path = ResolvePath(_current);
            try
            {
                // Bash NEEDS LF or the shebang fails on Windows-resident
                // bash with "/bin/bash^M: bad interpreter". Force it.
                string content = _current.ForceLfOnSave
                    ? _editor.Text.Replace("\r\n", "\n")
                    : _editor.Text;

                File.WriteAllText(path, content);
                _currentDiskText = _editor.Text;
                ModifiedBootstraps.Add(_current.Kind);

                if (showStatus)
                {
                    _statusLabel.Text = "Saved " + _current.FileName +
                        " at " + DateTime.Now.ToString("HH:mm:ss");
                    _statusLabel.ForeColor = Color.DarkGreen;
                }
                UpdateTitleAndButtons();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Failed to save " + _current.FileName + ":\n\n" + ex.Message +
                    "\n\nIf ScriptDeck is installed under a write-protected location " +
                    "(Program Files), you'll need to run elevated or copy the bootstrap " +
                    "to a writable spot first.",
                    "Save failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void OnSaveAndClose()
        {
            if (SaveCurrent(showStatus: false))
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        // ---- Form lifecycle ------------------------------------------------

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // Whether the user clicked Cancel or hit the X / Esc, ask
            // about unsaved edits. We don't auto-discard.
            if (IsDirty())
            {
                var dr = MessageBox.Show(this,
                    "Save changes to " + _current.FileName + " before closing?",
                    "Unsaved changes",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel) { e.Cancel = true; return; }
                if (dr == DialogResult.Yes)
                {
                    if (!SaveCurrent(showStatus: false)) { e.Cancel = true; return; }
                }
                // No -> proceed without saving
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+S = Save current. Matches every other editor on the
            // planet so it's worth the small handler.
            if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.S)
            {
                SaveCurrent(showStatus: true);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ---- Path resolution -----------------------------------------------

        private static string ResolvePath(BootstrapInfo info)
        {
            return Path.Combine(AppContext.BaseDirectory, info.FileName);
        }
    }
}
