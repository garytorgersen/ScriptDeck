using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScintillaNET;
using ScriptDeck.Hosting;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// In-app PowerShell editor with Run-Test-against-the-live-runspace.
    ///
    /// Goals:
    ///   - Let users compose a .ps1 without leaving ScriptDeck.
    ///   - "Run Test" exercises the script in the SAME long-lived
    ///     runspace button clicks use, so the user sees identical
    ///     behavior (shared inputs, bootstrap helpers, $global state).
    ///   - History recording is suppressed for tests via
    ///     <c>ExecutionRequest.SkipHistory</c> -- iterating on a script
    ///     shouldn't fill the audit trail with scratch rows.
    ///   - Always-on syntax check via <see cref="ScriptValidator"/>;
    ///     debounced so we don't reparse on every keystroke.
    ///
    /// Phase 1 scope: PowerShell only. Format / lint live in Phase 2.
    /// </summary>
    public partial class ScriptEditorDialog : Form
    {
        private readonly Dispatcher _dispatcher;
        private readonly string _scriptsRoot;

        // The output sink is constructed lazily in the constructor so we
        // can hand it directly to the dispatcher's per-call sink override.
        private readonly RtbSink _testSink;

        // Snapshot of the workspace's shared inputs at the moment the
        // dialog opened, plus their normalize rules. The grid lets the
        // user override values for tests; on Run Test we apply the same
        // normalization a real button click would, so test runs and real
        // runs see identical $variable values.
        private readonly List<SharedInputSnapshot> _inputSnapshot;

        // Path the script will save to. Empty when this is a new script
        // that hasn't been Save-As'd yet. Updated on Save / Save As.
        private string _currentPath;

        // Snapshot of the editor text at the last successful Save or
        // initial Load, so we can detect "modified since" for the title
        // asterisk and the close-prompt.
        private string _savedText = string.Empty;

        // Debounce timer for syntax validation. ScriptValidator is fast
        // (single-digit ms for typical scripts) but reparsing on every
        // key event still makes the editor feel jumpy on long files.
        private readonly Timer _validateTimer;

        // True between Run Test click and dispatcher completion. Drives
        // button enable/disable in UpdateButtons().
        private volatile bool _testRunning;

        // Backward-compatible 3-arg ctor: callers that don't yet know
        // about shared-input snapshots get an empty grid (no test
        // overrides available, but everything else still works).
        public ScriptEditorDialog(Dispatcher dispatcher, string scriptsRoot, string initialPath)
            : this(dispatcher, scriptsRoot, initialPath, sharedInputs: null) { }

        public ScriptEditorDialog(
            Dispatcher dispatcher,
            string scriptsRoot,
            string initialPath,
            IList<SharedInputSnapshot> sharedInputs)
        {
            _dispatcher  = dispatcher  ?? throw new ArgumentNullException(nameof(dispatcher));
            _scriptsRoot = scriptsRoot;
            _currentPath = initialPath ?? string.Empty;
            _inputSnapshot = sharedInputs == null
                ? new List<SharedInputSnapshot>()
                : new List<SharedInputSnapshot>(sharedInputs);

            InitializeComponent();
            ConfigureScintilla();
            ConfigureInputsGrid();

            _testSink = new RtbSink(richTextBox_Output);

            _validateTimer = new Timer { Interval = 350 };
            _validateTimer.Tick += (_, __) =>
            {
                _validateTimer.Stop();
                RunSyntaxCheck();
            };

            // Hook the editor's text-changed event AFTER configuration so
            // the initial style assignment doesn't flag "modified".
            scintilla_Editor.TextChanged += (_, __) =>
            {
                _validateTimer.Stop();
                _validateTimer.Start();
                UpdateTitle();
            };

            textBox_Path.Text = _currentPath;
            LoadInitialContent();
            UpdateTitle();
            UpdateButtons();
            RunSyntaxCheck();

            // Esc cancels a running test (similar to the main Shell's
            // KeyPreview wiring); when nothing is running, Esc closes.
            // The CancelButton hookup handles the close case for free.
            this.KeyDown += ScriptEditorDialog_KeyDown;

            // Subscribe to dispatcher busy changes so a Run started here
            // (or anywhere) updates the button state correctly. The main
            // Shell still owns the dispatcher; we just observe.
            _dispatcher.BusyChanged += Dispatcher_BusyChanged;
        }

        public string SavedPath => _currentPath;

        // ----------------------------------------------------------------
        // Editor configuration
        // ----------------------------------------------------------------

        // Style indices used below. ScintillaNET's lexer tags tokens with
        // numeric "style" ids (0..STYLE_MAX); we set foreground colors
        // for the ones the lexer emits. Names match ScintillaNET's
        // Style.Cpp constants for readability, even though we point the
        // lexer at the nearer-fit "Cpp" lexer rather than a true PS one.
        private void ConfigureScintilla()
        {
            var s = scintilla_Editor;

            // Monospace font, sensible size.
            var font = "Consolas";
            s.Styles[Style.Default].Font = font;
            s.Styles[Style.Default].Size = 10;
            s.StyleClearAll(); // propagate the default to every style slot

            // Line numbers in margin 0. Width auto-sizes when document
            // grows past 999 lines (rare for a single-button script).
            s.Margins[0].Type = MarginType.Number;
            s.Margins[0].Width = 36;

            // Tab = 4 spaces, no auto-indent magic. PowerShell convention
            // is mixed but 4-space tabs render predictably.
            s.UseTabs = false;
            s.TabWidth = 4;
            s.IndentWidth = 4;

            // Brace match on { } [ ] ( ) -- indispensable in any PS
            // script with nested scriptblocks.
            s.UpdateUI += (_, e) =>
            {
                if ((e.Change & UpdateChange.Selection) == 0) return;
                int caret = s.CurrentPosition;
                int charBefore = caret > 0 ? s.GetCharAt(caret - 1) : 0;
                int matchPos = -1;
                if (IsBraceChar(charBefore))
                {
                    matchPos = s.BraceMatch(caret - 1);
                    if (matchPos != -1) s.BraceHighlight(caret - 1, matchPos);
                    else s.BraceBadLight(caret - 1);
                }
                else
                {
                    s.BraceHighlight(-1, -1);
                }
            };

            // Lexer + keyword + style setup is language-dependent. We
            // probe the open file's extension once at construction to
            // decide. Switching languages mid-session (the user does
            // not get an Open/Save-As that retargets the editor in v1)
            // isn't supported -- close + reopen to switch.
            if (IsPythonPath(_currentPath))
                ConfigurePythonLexer(s);
            else if (IsBashPath(_currentPath))
                ConfigureBashLexer(s);
            else
                ConfigurePowerShellLexer(s);

            // Brace match colors apply to all languages (language-
            // neutral Scintilla styles). Lives here after the lexer
            // dispatch so it isn't accidentally scoped to one lexer.
            s.Styles[Style.BraceLight].ForeColor = Color.LimeGreen;
            s.Styles[Style.BraceLight].Bold      = true;
            s.Styles[Style.BraceBad].ForeColor   = Color.Red;
            s.Styles[Style.BraceBad].Bold        = true;
        }

        private static bool IsPythonPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".py",  StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".pyw", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBashPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".sh",   StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bash", StringComparison.OrdinalIgnoreCase);
        }

        // Re-applies the lexer + keyword sets + style colors to match
        // the current _currentPath's extension. Used when the open file
        // changes after construction (Browse to a different file, Save
        // As crossing a .ps1 <-> .py boundary). Does NOT touch margins,
        // font, brace-match colors, etc. -- those are set once in
        // ConfigureScintilla and never need to change.
        private void ReapplyLexerForCurrentPath()
        {
            if (IsPythonPath(_currentPath))
                ConfigurePythonLexer(scintilla_Editor);
            else if (IsBashPath(_currentPath))
                ConfigureBashLexer(scintilla_Editor);
            else
                ConfigurePowerShellLexer(scintilla_Editor);
            // Force a colorise pass over the whole buffer so the new
            // styles render immediately rather than only on the next
            // edit.
            scintilla_Editor.Colorize(0, scintilla_Editor.TextLength);
        }

        internal static void ConfigurePowerShellLexer(ScintillaNET.Scintilla s)
        {
            // ScintillaNET's PowerShell lexer is the natural fit. We
            // set keyword set 0 (language keywords) and 1 (common
            // cmdlet verbs) and let the rest fall back to default style.
            s.Lexer = Lexer.PowerShell;
            s.SetKeywords(0,
                "if else elseif while do for foreach in switch break continue " +
                "return throw try catch finally function param begin process end " +
                "filter class enum hidden static public private using module " +
                "trap data dynamicparam exit");
            s.SetKeywords(1,
                "Get Set New Remove Add Clear Find Format Out " +
                "Import Export ConvertFrom ConvertTo Invoke Start Stop Restart " +
                "Test Update Write Read Select Where ForEach Sort Group Measure " +
                "Enable Disable Resolve Search");

            s.Styles[Style.PowerShell.Default].ForeColor       = Color.Black;
            s.Styles[Style.PowerShell.Comment].ForeColor       = Color.FromArgb(0, 128, 0);
            s.Styles[Style.PowerShell.String].ForeColor        = Color.FromArgb(163, 21, 21);
            s.Styles[Style.PowerShell.Character].ForeColor     = Color.FromArgb(163, 21, 21);
            s.Styles[Style.PowerShell.Number].ForeColor        = Color.FromArgb(0, 0, 200);
            s.Styles[Style.PowerShell.Variable].ForeColor      = Color.FromArgb(128, 0, 128);
            s.Styles[Style.PowerShell.Operator].ForeColor      = Color.FromArgb(80, 80, 80);
            s.Styles[Style.PowerShell.Identifier].ForeColor    = Color.Black;
            s.Styles[Style.PowerShell.Keyword].ForeColor       = Color.Blue;
            s.Styles[Style.PowerShell.Cmdlet].ForeColor        = Color.FromArgb(0, 96, 160);
            s.Styles[Style.PowerShell.Alias].ForeColor         = Color.FromArgb(0, 96, 160);
            s.Styles[Style.PowerShell.CommentStream].ForeColor = Color.FromArgb(0, 128, 0);
            s.Styles[Style.PowerShell.HereString].ForeColor    = Color.FromArgb(163, 21, 21);
            s.Styles[Style.PowerShell.HereCharacter].ForeColor = Color.FromArgb(163, 21, 21);
            s.Styles[Style.PowerShell.CommentDocKeyword].ForeColor = Color.FromArgb(0, 96, 160);
        }

        internal static void ConfigurePythonLexer(ScintillaNET.Scintilla s)
        {
            // ScintillaNET's Python lexer covers keywords, strings,
            // triple-quoted strings, numbers, decorators, and class /
            // def headers. Style ids live under Style.Python.*.
            s.Lexer = Lexer.Python;

            // Keyword set 0 = language keywords (full PEP 3131 list as
            // of Python 3.12). Set 1 = common builtins + types so
            // print / len / dict / etc. read as identifiers, not
            // generic names. The lexer doesn't bind these to a
            // separate style on every build, but providing both makes
            // ScintillaNET's word-classifier behave correctly.
            s.SetKeywords(0,
                "False None True and as assert async await break class continue " +
                "def del elif else except finally for from global if import in is " +
                "lambda nonlocal not or pass raise return try while with yield match case");
            s.SetKeywords(1,
                "abs all any bin bool bytes callable chr classmethod compile complex " +
                "dict dir divmod enumerate eval exec filter float format frozenset " +
                "getattr globals hasattr hash help hex id input int isinstance issubclass " +
                "iter len list locals map max memoryview min next object oct open ord " +
                "pow print property range repr reversed round set setattr slice sorted " +
                "staticmethod str sum super tuple type vars zip __import__ self cls");

            s.Styles[Style.Python.Default].ForeColor          = Color.Black;
            s.Styles[Style.Python.CommentLine].ForeColor      = Color.FromArgb(0, 128, 0);
            s.Styles[Style.Python.Number].ForeColor           = Color.FromArgb(0, 0, 200);
            s.Styles[Style.Python.String].ForeColor           = Color.FromArgb(163, 21, 21);
            s.Styles[Style.Python.Character].ForeColor        = Color.FromArgb(163, 21, 21);
            s.Styles[Style.Python.Word].ForeColor             = Color.Blue;   // keywords
            s.Styles[Style.Python.Triple].ForeColor           = Color.FromArgb(163, 21, 21);
            s.Styles[Style.Python.TripleDouble].ForeColor     = Color.FromArgb(163, 21, 21);
            s.Styles[Style.Python.ClassName].ForeColor        = Color.FromArgb(0, 96, 160);
            s.Styles[Style.Python.ClassName].Bold             = true;
            s.Styles[Style.Python.DefName].ForeColor          = Color.FromArgb(0, 96, 160);
            s.Styles[Style.Python.DefName].Bold               = true;
            s.Styles[Style.Python.Operator].ForeColor         = Color.FromArgb(80, 80, 80);
            s.Styles[Style.Python.Identifier].ForeColor       = Color.Black;
            s.Styles[Style.Python.CommentBlock].ForeColor     = Color.FromArgb(0, 128, 0);
            s.Styles[Style.Python.StringEol].ForeColor        = Color.Red;
            s.Styles[Style.Python.Word2].ForeColor            = Color.FromArgb(0, 96, 160); // builtins
            s.Styles[Style.Python.Decorator].ForeColor        = Color.FromArgb(128, 0, 128);
        }

        internal static void ConfigureBashLexer(ScintillaNET.Scintilla s)
        {
            // ScintillaNET 3.6.3's Lexer enum doesn't include a Bash
            // entry, but the underlying Scintilla library DOES support
            // it (SCLEX_BASH = 62). Cast the raw int value -- the
            // wrapper forwards it unchanged to SCI_SETLEXER.
            // Newer ScintillaNET (4.x+) has Lexer.Bash as a named
            // value; this cast continues to work on those too because
            // the underlying constant is stable.
            s.Lexer = (Lexer)62;

            // Keyword set 0 = shell keywords + common builtins. The
            // bash lexer doesn't separate user-defined commands from
            // shell builtins via styles, but providing a known set
            // helps token classification.
            s.SetKeywords(0,
                "if then else elif fi case esac for while until do done " +
                "function return break continue in select time " +
                "true false test exit source declare local readonly export " +
                "echo printf read cd pwd pushd popd dirs alias unalias " +
                "set unset shift trap eval exec wait hash kill jobs bg fg " +
                "command type which builtin getopts getopt let " +
                "shopt complete compgen ulimit umask history fc " +
                "scriptdeck_path scriptdeck_to_unix_path scriptdeck_to_win_path " +
                "scriptdeck_write_rtb scriptdeck_write_grid_row " +
                "scriptdeck_set_shared_input scriptdeck_remove_shared_input");

            // ScintillaNET 3.6.3 doesn't expose Style.Bash.* named
            // constants, so we use the raw Scintilla SCE_SH_* style
            // ids directly. From scintilla/include/SciLexer.h:
            //   0 Default, 1 Error, 2 Comment, 3 Number, 4 Word,
            //   5 String, 6 Character, 7 Operator, 8 Identifier,
            //   9 Scalar ($var), 10 ParamExpand (${var}),
            //   11 Backticks, 12 HereDelim, 13 HereString.
            s.Styles[0].ForeColor  = Color.Black;
            s.Styles[2].ForeColor  = Color.FromArgb(0, 128, 0);   // comment
            s.Styles[3].ForeColor  = Color.FromArgb(0, 0, 200);   // number
            s.Styles[4].ForeColor  = Color.Blue;                  // keywords
            s.Styles[5].ForeColor  = Color.FromArgb(163, 21, 21); // string
            s.Styles[6].ForeColor  = Color.FromArgb(163, 21, 21); // character
            s.Styles[7].ForeColor  = Color.FromArgb(80, 80, 80);  // operator
            s.Styles[8].ForeColor  = Color.Black;                 // identifier
            s.Styles[9].ForeColor  = Color.FromArgb(128, 0, 128); // scalar $var
            s.Styles[10].ForeColor = Color.FromArgb(128, 0, 128); // ${var}
            s.Styles[11].ForeColor = Color.FromArgb(163, 21, 21); // `cmd`
            s.Styles[12].ForeColor = Color.FromArgb(0, 96, 160);  // here-delim
            s.Styles[13].ForeColor = Color.FromArgb(163, 21, 21); // here-string
        }

        private static bool IsBraceChar(int c) =>
            c == '{' || c == '}' || c == '(' || c == ')' || c == '[' || c == ']';

        // ----------------------------------------------------------------
        // Shared-input grid
        // ----------------------------------------------------------------

        private void ConfigureInputsGrid()
        {
            var g = dataGridView_Inputs;

            // Two columns: id (read-only, narrow) and value (editable,
            // takes remaining width). We DO NOT data-bind to the
            // snapshot list because the user often doesn't select rows
            // -- they tab in, type, tab out -- and a binding would force
            // us to re-read the bound value on cell-change events. A
            // direct cell read on Run Test is simpler and more robust.
            var colId = new DataGridViewTextBoxColumn
            {
                Name = "Id",
                HeaderText = "id",
                ReadOnly = true,
                Width = 160,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            };
            var colVal = new DataGridViewTextBoxColumn
            {
                Name = "Value",
                HeaderText = "value",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            };
            g.Columns.Add(colId);
            g.Columns.Add(colVal);

            if (_inputSnapshot.Count == 0)
            {
                // Empty state: dim the label so users don't think the
                // grid is broken, just absent. Keeps the UI predictable
                // when the editor is launched with no workspace open.
                label_Inputs.Text = "Test inputs: (no shared inputs in this workspace)";
                g.Enabled = false;
                return;
            }

            foreach (var snap in _inputSnapshot)
            {
                int rowIdx = g.Rows.Add(snap.Id ?? string.Empty, snap.Value ?? string.Empty);
                // Stash the snapshot on the row so the Run Test path can
                // pull the Normalize flag back out without a parallel
                // dictionary lookup. Tag is `object` so we can put any
                // ref type here without ceremony.
                g.Rows[rowIdx].Tag = snap;
                // Tooltip shows the human label + normalization hint --
                // the id alone often isn't self-describing.
                if (!string.IsNullOrEmpty(snap.Label))
                    g.Rows[rowIdx].Cells[0].ToolTipText = snap.Label;
            }
        }

        // Build a name -> value dictionary from the grid's current
        // contents and apply the same normalization rules Shell uses on
        // a real button click. Exposed as a method (not a property) to
        // emphasise that it's read-time, not load-time -- the user may
        // edit the grid mid-session.
        private IDictionary<string, string> CollectInputsForTest()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in dataGridView_Inputs.Rows)
            {
                if (row.IsNewRow) continue;
                string id = row.Cells["Id"].Value?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                string val = row.Cells["Value"].Value?.ToString() ?? string.Empty;

                // Apply normalize rule (same as Shell.NormalizeSharedInputs).
                // Detection mirrors WorkspaceRenderer + Shell: explicit
                // Normalize is canonical, id=="computerName" is the
                // implicit fallback so workspaces missing the hint still
                // get the same resolution behavior here.
                var snap = row.Tag as SharedInputSnapshot;
                bool isComputerNameField = snap != null && (
                    string.Equals(snap.Normalize, "computerName", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(snap.Id,    "computerName", StringComparison.OrdinalIgnoreCase));
                if (isComputerNameField)
                {
                    if (string.IsNullOrWhiteSpace(val)
                        || string.Equals(val, ".",         StringComparison.OrdinalIgnoreCase)
                        || string.Equals(val, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        val = Environment.MachineName;
                    }
                }
                dict[id] = val;
            }
            return dict;
        }

        // ----------------------------------------------------------------
        // Load / Save
        // ----------------------------------------------------------------

        private void LoadInitialContent()
        {
            if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
            {
                _savedText = string.Empty;
                scintilla_Editor.Text = string.Empty;
                return;
            }
            try
            {
                _savedText = File.ReadAllText(_currentPath);
                scintilla_Editor.Text = _savedText;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not read file:\n{ex.Message}",
                    "Open Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _savedText = string.Empty;
                scintilla_Editor.Text = string.Empty;
            }
        }

        private bool TrySave(string path)
        {
            try
            {
                File.WriteAllText(path, scintilla_Editor.Text);
                _currentPath = path;
                textBox_Path.Text = path;
                _savedText = scintilla_Editor.Text;
                UpdateTitle();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not save file:\n{ex.Message}",
                    "Save Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool IsDirty => !string.Equals(scintilla_Editor.Text ?? string.Empty, _savedText ?? string.Empty, StringComparison.Ordinal);

        private void UpdateTitle()
        {
            string baseTitle = "Script Editor";
            string fileName = string.IsNullOrEmpty(_currentPath) ? "(new)" : Path.GetFileName(_currentPath);
            this.Text = $"{baseTitle} - {fileName}{(IsDirty ? " *" : string.Empty)}";
        }

        // ----------------------------------------------------------------
        // Syntax check (runs on a debounce timer hooked to TextChanged)
        // ----------------------------------------------------------------

        private void RunSyntaxCheck()
        {
            // For Python files we shell out to `python -c "import ast; ..."`
            // because there's no in-process Python parser on net48. For
            // Bash, `bash -n` is the parse-only mode -- exits 0 on
            // success, prints errors to stderr otherwise. For PowerShell
            // we use the existing ScriptValidator (which uses
            // System.Management.Automation's parser in-process).
            if (IsPythonPath(_currentPath))
            {
                RunPythonSyntaxCheck();
                return;
            }
            if (IsBashPath(_currentPath))
            {
                RunBashSyntaxCheck();
                return;
            }

            var issues = ScriptValidator.Validate(scintilla_Editor.Text);
            if (issues.Count == 0)
            {
                statusLabel_Syntax.Text = "Syntax: OK";
                statusLabel_Syntax.ForeColor = SystemColors.ControlText;
                return;
            }

            // Show the count + the first error inline so the user gets
            // something actionable without opening a side panel. Lint /
            // a richer error list belongs to Phase 2.
            var first = issues[0];
            statusLabel_Syntax.Text =
                $"Syntax: {issues.Count} error(s) -- line {first.Line}: {Truncate(first.Message, 90)}";
            statusLabel_Syntax.ForeColor = Color.Firebrick;
        }

        // Python syntax check via `python -c "import ast; ast.parse(...)"`.
        // Best-effort: if python isn't on PATH we surface a soft "not
        // checked" status rather than fail the editor. Stdin is the
        // editor text, avoiding a scratch file.
        private void RunPythonSyntaxCheck()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "python",
                    Arguments              = "-c \"import sys, ast\nast.parse(sys.stdin.read())\"",
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null)
                    {
                        statusLabel_Syntax.Text = "Syntax: not checked (no interpreter)";
                        statusLabel_Syntax.ForeColor = SystemColors.GrayText;
                        return;
                    }
                    p.StandardInput.Write(scintilla_Editor.Text);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        statusLabel_Syntax.Text = "Syntax: check timed out";
                        statusLabel_Syntax.ForeColor = Color.Firebrick;
                        return;
                    }

                    if (p.ExitCode == 0)
                    {
                        statusLabel_Syntax.Text = "Syntax: OK";
                        statusLabel_Syntax.ForeColor = SystemColors.ControlText;
                        return;
                    }

                    // SyntaxError on stderr: last line is the most
                    // actionable ("SyntaxError: ..."). Show that + try
                    // to extract a line number from the traceback.
                    string err = p.StandardError.ReadToEnd() ?? string.Empty;
                    string firstError = err.Split(new[] { '\r', '\n' },
                                                  StringSplitOptions.RemoveEmptyEntries)
                                            .LastOrDefault() ?? "(unknown)";
                    statusLabel_Syntax.Text = "Syntax: " + Truncate(firstError, 120);
                    statusLabel_Syntax.ForeColor = Color.Firebrick;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // python.exe not on PATH. Don't treat as an error; the
                // user can still run via the configured interpreter.
                statusLabel_Syntax.Text = "Syntax: not checked (python not on PATH)";
                statusLabel_Syntax.ForeColor = SystemColors.GrayText;
            }
            catch (Exception ex)
            {
                statusLabel_Syntax.Text = "Syntax: check failed -- " + Truncate(ex.Message, 100);
                statusLabel_Syntax.ForeColor = Color.Firebrick;
            }
        }

        // Bash syntax check via `bash -n` (parse-only mode). We pass
        // the editor content via stdin so we don't have to write a
        // scratch file just for validation. Same soft-fail behavior
        // as Python: missing bash -> "not checked" status.
        private void RunBashSyntaxCheck()
        {
            try
            {
                // Resolve bash via the executor's fallback logic --
                // bare bash on PATH, then canonical Git Bash paths.
                string bashPath = ResolveBashForSyntaxCheck();
                if (string.IsNullOrEmpty(bashPath))
                {
                    statusLabel_Syntax.Text = "Syntax: not checked (bash not found)";
                    statusLabel_Syntax.ForeColor = SystemColors.GrayText;
                    return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = bashPath,
                    Arguments              = "-n -s",  // -s reads from stdin
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null)
                    {
                        statusLabel_Syntax.Text = "Syntax: not checked (no interpreter)";
                        statusLabel_Syntax.ForeColor = SystemColors.GrayText;
                        return;
                    }
                    // bash on Windows reads stdin as bytes; LF line
                    // endings are mandatory or the parser hiccups on
                    // \r in the middle of statements.
                    string source = scintilla_Editor.Text.Replace("\r\n", "\n");
                    p.StandardInput.Write(source);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        statusLabel_Syntax.Text = "Syntax: check timed out";
                        statusLabel_Syntax.ForeColor = Color.Firebrick;
                        return;
                    }
                    if (p.ExitCode == 0)
                    {
                        statusLabel_Syntax.Text = "Syntax: OK";
                        statusLabel_Syntax.ForeColor = SystemColors.ControlText;
                        return;
                    }
                    // bash prints "bash: line N: <error>" on stderr.
                    // First line is the most actionable.
                    string err = p.StandardError.ReadToEnd() ?? string.Empty;
                    string firstError = err.Split(new[] { '\r', '\n' },
                                                  StringSplitOptions.RemoveEmptyEntries)
                                            .FirstOrDefault() ?? "(unknown)";
                    statusLabel_Syntax.Text = "Syntax: " + Truncate(firstError, 120);
                    statusLabel_Syntax.ForeColor = Color.Firebrick;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                statusLabel_Syntax.Text = "Syntax: not checked (bash not on PATH)";
                statusLabel_Syntax.ForeColor = SystemColors.GrayText;
            }
            catch (Exception ex)
            {
                statusLabel_Syntax.Text = "Syntax: check failed -- " + Truncate(ex.Message, 100);
                statusLabel_Syntax.ForeColor = Color.Firebrick;
            }
        }

        // Mirror BashExecutor.ResolveInterpreter for the syntax-check
        // path. Keep light: bare "bash" first, then canonical Git Bash.
        private static string ResolveBashForSyntaxCheck()
        {
            string[] candidates =
            {
                "bash",
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
            };
            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) return c;
                if (c == "bash") return c; // let Process.Start probe PATH
            }
            return null;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // ----------------------------------------------------------------
        // Test execution
        // ----------------------------------------------------------------

        private async void Button_RunTest_Click(object sender, EventArgs e)
        {
            if (_testRunning) return;

            bool isPython = IsPythonPath(_currentPath);
            bool isBash   = IsBashPath(_currentPath);

            // Refuse to run if there are syntax errors. For PowerShell
            // we have ScriptValidator (in-process AST parser). For
            // Python and Bash we skip the pre-flight check because
            // spawning a separate validator adds startup cost on
            // every Run; the executor itself will surface SyntaxError
            // on stderr if the user has one.
            if (!isPython && !isBash)
            {
                var issues = ScriptValidator.Validate(scintilla_Editor.Text);
                if (issues.Count > 0)
                {
                    richTextBox_Output.Clear();
                    _testSink.WriteError(
                        $"Refusing to run: {issues.Count} syntax error(s).\r\n" +
                        $"  Line {issues[0].Line}, Col {issues[0].Column}: {issues[0].Message}\r\n");
                    return;
                }
            }

            // Write the editor text to a scratch file so the executor
            // can run it. Extension matches the language so the
            // executor + any shebang-style behavior do the right thing
            // (scratch.ps1, scratch.py, scratch.sh).
            string scratchDir = Path.Combine(Path.GetTempPath(), "ScriptDeck");
            string scratchPath;
            if (isPython)    scratchPath = Path.Combine(scratchDir, "scratch.py");
            else if (isBash) scratchPath = Path.Combine(scratchDir, "scratch.sh");
            else             scratchPath = Path.Combine(scratchDir, "scratch.ps1");

            try
            {
                Directory.CreateDirectory(scratchDir);
                // Bash absolutely requires LF line endings. CRLF in a
                // .sh breaks the shebang ("/bin/bash^M: bad interpreter")
                // and confuses the parser mid-statement. Force LF for
                // the bash scratch file; other languages don't care
                // and we use the platform default (CRLF on Windows).
                string content = isBash
                    ? scintilla_Editor.Text.Replace("\r\n", "\n")
                    : scintilla_Editor.Text;
                File.WriteAllText(scratchPath, content);
            }
            catch (Exception ex)
            {
                _testSink.WriteError($"Could not write scratch file: {ex.Message}\r\n");
                return;
            }

            // Clear the output pane between runs. The dispatcher writes a
            // visual separator after every dispatch, but starting clean
            // is friendlier when the previous run produced a wall of text.
            richTextBox_Output.Clear();

            _testRunning = true;
            UpdateButtons();

            try
            {
                string format = (comboBox_Format.SelectedItem as string) ?? "default";

                var req = new ExecutionRequest
                {
                    ScriptPath    = scratchPath,
                    Args          = new List<string>(),
                    ButtonLabel   = "(Test from editor)",
                    OutputTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rtb" },
                    SkipHistory   = true,
                    // Pull live values out of the inputs grid, normalized
                    // the same way Shell normalizes them for real button
                    // clicks. A test run sees identical $variable values.
                    SharedInputs  = CollectInputsForTest(),
                    // Editor-local format override -- doesn't touch the
                    // saved button (if any). Lets users preview list /
                    // table / json / csv side-by-side without committing.
                    RtbFormat     = string.Equals(format, "default", StringComparison.OrdinalIgnoreCase) ? null : format,
                };

                string executorKind = isPython ? "python"
                                     : isBash   ? "bash"
                                                : "powershell";
                await _dispatcher.ExecuteAsync(req, executorKind, _testSink).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _testSink.WriteError($"Test dispatch failed: {ex.Message}\r\n");
            }
            finally
            {
                _testRunning = false;
                UpdateButtons();
            }
        }

        private void Button_CancelTest_Click(object sender, EventArgs e)
        {
            // Same plumbing as the Shell's Esc handler -- the dispatcher
            // owns cancellation; we just ask it to cancel whatever's
            // active. (If the user somehow started a button-click run
            // from the main window while the test was in flight, the
            // dispatcher's single-flight gate prevents that, so this
            // will only ever cancel our own test.)
            try { _dispatcher.CancelActive(); } catch { /* swallow */ }
        }

        private void Dispatcher_BusyChanged(object sender, EventArgs e)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke((Action)UpdateButtons); } catch { /* form closing */ }
            }
            else
            {
                UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            bool busy = _dispatcher.IsBusy;
            button_RunTest.Enabled = !busy;
            button_CancelTest.Enabled = busy && _testRunning;
            button_Save.Enabled = !busy;
            button_SaveAs.Enabled = !busy;
            statusLabel_RunState.Text = busy ? (_testRunning ? "Running test..." : "Busy") : "Idle";
            statusLabel_RunState.ForeColor = busy ? Color.DeepSkyBlue : SystemColors.ControlText;
        }

        // ----------------------------------------------------------------
        // Save / Save As / Browse / Insert Template / Close
        // ----------------------------------------------------------------

        private void Button_Save_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath))
            {
                Button_SaveAs_Click(sender, e);
                return;
            }
            TrySave(_currentPath);
        }

        private void Button_SaveAs_Click(object sender, EventArgs e)
        {
            // Bias the dialog to whatever language the editor is
            // currently holding. The combined filter ("Script files")
            // is listed first so users picking from any language see
            // the full set without scrolling.
            bool curIsPython = IsPythonPath(_currentPath);
            bool curIsBash   = IsBashPath(_currentPath);
            // FilterIndex is 1-based. 1=combined, 2=ps1, 3=py, 4=sh.
            int idx = curIsPython ? 3 : curIsBash ? 4 : 2;
            string defExt = curIsPython ? "py" : curIsBash ? "sh" : "ps1";
            string newName = curIsPython ? "NewScript.py"
                           : curIsBash   ? "NewScript.sh"
                                         : "NewScript.ps1";
            using (var dlg = new SaveFileDialog
            {
                Title = "Save Script As",
                Filter = "Script files (*.ps1;*.py;*.sh)|*.ps1;*.py;*.sh|"
                       + "PowerShell scripts (*.ps1)|*.ps1|"
                       + "Python scripts (*.py)|*.py|"
                       + "Bash scripts (*.sh)|*.sh|"
                       + "All files (*.*)|*.*",
                FilterIndex = idx,
                DefaultExt = defExt,
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = !string.IsNullOrEmpty(_scriptsRoot) && Directory.Exists(_scriptsRoot)
                    ? _scriptsRoot
                    : (string.IsNullOrEmpty(_currentPath) ? null : Path.GetDirectoryName(_currentPath)),
                FileName = string.IsNullOrEmpty(_currentPath) ? newName : Path.GetFileName(_currentPath),
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (TrySave(dlg.FileName))
                {
                    // If the user saved across languages (.ps1 -> .py
                    // or vice versa), retarget the editor's lexer so
                    // highlighting matches the new file's language.
                    if (IsPythonPath(dlg.FileName) != IsPythonPath(_currentPath))
                    {
                        _currentPath = dlg.FileName;
                        ReapplyLexerForCurrentPath();
                    }
                    // Mark the dialog as "successful save happened" so
                    // the parent (e.g. EditButtonDialog) can read
                    // SavedPath afterwards. We DON'T close on Save -- the
                    // user often saves mid-edit then keeps iterating.
                    this.DialogResult = DialogResult.OK;
                }
            }
        }

        private void Button_Browse_Click(object sender, EventArgs e)
        {
            // Open an existing script from disk. If the editor is dirty,
            // prompt before discarding.
            if (IsDirty && !ConfirmDiscard("open another script")) return;

            using (var dlg = new OpenFileDialog
            {
                Title = "Open Script",
                Filter = "Script files (*.ps1;*.py;*.sh)|*.ps1;*.py;*.sh|"
                       + "PowerShell scripts (*.ps1)|*.ps1|"
                       + "Python scripts (*.py)|*.py|"
                       + "Bash scripts (*.sh)|*.sh|"
                       + "All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = !string.IsNullOrEmpty(_scriptsRoot) && Directory.Exists(_scriptsRoot)
                    ? _scriptsRoot
                    : null,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _currentPath = dlg.FileName;
                textBox_Path.Text = _currentPath;
                LoadInitialContent();
                // Critical when switching languages mid-session: the
                // ctor-time ConfigureScintilla decided the lexer based
                // on the path the dialog was opened with. Re-pointing
                // the editor at a different file means re-applying the
                // correct lexer + keyword set, or .py code shows up
                // with PowerShell highlighting (and vice versa).
                ReapplyLexerForCurrentPath();
                UpdateTitle();
                RunSyntaxCheck();
            }
        }

        private void Button_InsertTemplate_Click(object sender, EventArgs e)
        {
            // Replace the editor contents with the starter template only
            // if the editor is empty. Otherwise insert at caret position
            // -- the user might want to drop the boilerplate inside an
            // already-partial script.
            string template =
                "# <Description of what this button does>\r\n" +
                "# Shared inputs available as variables: $ComputerName (and any\r\n" +
                "# others defined in the workspace's sharedInputs).\r\n" +
                "\r\n" +
                "if (Test-IsLocalTarget) {\r\n" +
                "    # Local code path\r\n" +
                "\r\n" +
                "} else {\r\n" +
                "    # Remote code path\r\n" +
                "    Invoke-Command -ComputerName $ComputerName -ScriptBlock {\r\n" +
                "\r\n" +
                "    }\r\n" +
                "}\r\n";

            if (string.IsNullOrWhiteSpace(scintilla_Editor.Text))
            {
                scintilla_Editor.Text = template;
                scintilla_Editor.GotoPosition(template.Length);
            }
            else
            {
                scintilla_Editor.InsertText(scintilla_Editor.CurrentPosition, template);
            }
        }

        private void Button_Close_Click(object sender, EventArgs e)
        {
            // Form.DialogResult = Cancel from the Designer drives the
            // close. The FormClosing handler does the dirty-check.
            this.Close();
        }

        private void ScriptEditorDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_testRunning)
            {
                var dr = MessageBox.Show(this,
                    "A test is still running. Close anyway?",
                    "Close Editor",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes) { e.Cancel = true; return; }
                try { _dispatcher.CancelActive(); } catch { /* swallow */ }
            }

            if (IsDirty)
            {
                var dr = MessageBox.Show(this,
                    "You have unsaved changes. Save before closing?",
                    "Close Editor",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (dr == DialogResult.Cancel) { e.Cancel = true; return; }
                if (dr == DialogResult.Yes)
                {
                    if (string.IsNullOrEmpty(_currentPath))
                    {
                        Button_SaveAs_Click(sender, EventArgs.Empty);
                        // If the user cancelled the Save As, IsDirty is
                        // still true -- bail out of the close so the
                        // user can try again.
                        if (IsDirty) { e.Cancel = true; return; }
                    }
                    else
                    {
                        if (!TrySave(_currentPath)) { e.Cancel = true; return; }
                    }
                }
            }

            try { _dispatcher.BusyChanged -= Dispatcher_BusyChanged; } catch { }
            try { _validateTimer?.Dispose(); } catch { }
        }

        private void ScriptEditorDialog_KeyDown(object sender, KeyEventArgs e)
        {
            // Esc -> cancel test if running, otherwise let the form's
            // CancelButton (button_Close) handle it.
            if (e.KeyCode == Keys.Escape && _testRunning)
            {
                try { _dispatcher.CancelActive(); } catch { }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            // Ctrl+S -> Save (matches the rest of the app's shortcut convention).
            else if (e.Control && e.KeyCode == Keys.S)
            {
                Button_Save_Click(sender, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            // F5 -> Run Test (industry standard for "run").
            else if (e.KeyCode == Keys.F5 && !_testRunning)
            {
                Button_RunTest_Click(sender, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private bool ConfirmDiscard(string action)
        {
            var dr = MessageBox.Show(this,
                $"You have unsaved changes. Discard and {action}?",
                "Unsaved Changes",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return dr == DialogResult.Yes;
        }

        // ----------------------------------------------------------------
        // RtbSink -- IOutputSink that pipes into the dialog's output RTB.
        // ----------------------------------------------------------------

        /// <summary>
        /// Lightweight IOutputSink for the editor's output pane. Thread-safe
        /// via the standard WinForms Invoke/BeginInvoke marshal -- the
        /// dispatcher writes from the executor's thread, the RTB lives on
        /// the UI thread.
        ///
        /// Grid methods are no-ops: the editor doesn't render structured
        /// data. If a tested script emits PSObjects, only their ToString
        /// representation lands here -- which is fine for "did it run"
        /// validation. For the full structured view, save and click the
        /// real button.
        /// </summary>
        private sealed class RtbSink : IOutputSink
        {
            private readonly RichTextBox _rtb;

            public RtbSink(RichTextBox rtb) { _rtb = rtb; }

            public void WriteOutput (string text) => Append(text, Color.LightGray);
            public void WriteError  (string text) => Append(text, Color.OrangeRed);
            public void WriteWarning(string text) => Append(text, Color.Goldenrod);
            public void WriteInfo   (string text) => Append(text, Color.DeepSkyBlue);
            public void WriteVerbose(string text) => Append(text, Color.MediumPurple);
            public void WriteDebug  (string text) => Append(text, Color.LightSlateGray);
            // The editor's output background is white, so a pure-white
            // header would be invisible. Use a strong dark color that
            // reads as a banner without colliding with stream colors.
            public void WriteHeader (string text) => Append(text, Color.Black);

            public void Log(string message)
            {
                // Match the main sink's "[HH:mm:ss] - <text>" prefix so
                // log lines look identical regardless of which sink
                // emits them.
                Append($"[{DateTime.Now:HH:mm:ss}] - {message}{Environment.NewLine}", Color.Gainsboro);
            }

            public void SetColumns(IList<string> columns) { /* no grid here */ }
            public void AppendRow(params object[] cells)  { /* no grid here */ }
            public void ClearOutput() { /* never called -- editor controls clear via Run Test path */ }
            public void ClearGrid()   { /* no grid here */ }

            private void Append(string text, Color color)
            {
                if (string.IsNullOrEmpty(text)) return;
                if (_rtb.IsDisposed) return;
                if (_rtb.InvokeRequired)
                {
                    try { _rtb.BeginInvoke((Action)(() => Append(text, color))); }
                    catch { /* form closing */ }
                    return;
                }
                _rtb.SelectionStart = _rtb.TextLength;
                _rtb.SelectionLength = 0;
                _rtb.SelectionColor = color;
                _rtb.AppendText(text);
                _rtb.SelectionColor = _rtb.ForeColor;
                _rtb.ScrollToCaret();
            }
        }
    }
}
