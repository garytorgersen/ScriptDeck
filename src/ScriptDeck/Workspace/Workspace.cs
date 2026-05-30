using System.Collections.Generic;

namespace ScriptDeck.Workspace
{
    /// <summary>
    /// Root JSON document. One workspace = one user-defined application
    /// layout: tabs, buttons, shared inputs, top menus.
    ///
    /// Versioned from v1 so future schema changes can migrate older files
    /// transparently. The loader rejects documents whose <see cref="Version"/>
    /// is newer than the running build.
    /// </summary>
    public sealed class Workspace
    {
        public int Version { get; set; } = 1;

        /// <summary>Display name shown in the title bar and recent-workspaces list.</summary>
        public string Name { get; set; } = "Untitled";

        /// <summary>
        /// Optional. Where relative <c>scriptPath</c>s in buttons resolve from.
        /// Default = directory containing the workspace JSON file (set by the
        /// loader after deserialization).
        /// </summary>
        public string ScriptsRoot { get; set; }

        public IList<SharedInput> SharedInputs { get; set; } = new List<SharedInput>();
        public IList<Tab> Tabs { get; set; } = new List<Tab>();
        public IList<MenuDefinition> Menus { get; set; } = new List<MenuDefinition>();
    }

    /// <summary>
    /// A persistent input field rendered in the top row. Every button can
    /// reference its value via <c>{{id}}</c> in args. This is what makes
    /// "type the computer name once" work across the whole workspace.
    /// </summary>
    public sealed class SharedInput
    {
        /// <summary>Token name for substitution (e.g. "computerName").</summary>
        public string Id { get; set; }

        /// <summary>Label shown next to the field.</summary>
        public string Label { get; set; }

        /// <summary>
        /// "text" (default) — single-line TextBox.
        /// Future: "combo", "checkbox", "filepicker", "folderpicker", "secret".
        /// </summary>
        public string Type { get; set; } = "text";

        /// <summary>Initial value. Empty string if omitted.</summary>
        public string Default { get; set; } = "";

        /// <summary>
        /// Optional normalization rule applied to the value before it's
        /// injected into a script's runspace. Currently supported:
        ///
        ///   "computerName" — empty / whitespace / "." / "localhost"
        ///                    are replaced with <c>Environment.MachineName</c>,
        ///                    so scripts always see a non-empty hostname.
        ///                    Convention: any value that doesn't equal the
        ///                    local machine name means "remote target."
        ///
        /// Null / empty (default) means "no normalization — pass through
        /// the textbox value verbatim." Adding this field is forward-
        /// compatible: older workspaces just don't normalize.
        /// </summary>
        public string Normalize { get; set; }
    }

    public sealed class Tab
    {
        public string Id { get; set; }
        public string Title { get; set; }

        /// <summary>
        /// Buttons on this tab. Positions are absolute (see Button.X/Y/Width/Height).
        /// In edit mode each button can be dragged to move and resized via the
        /// bottom-right corner grip. Order in this list controls z-order on
        /// overlap (later = drawn on top).
        /// </summary>
        public IList<Button> Buttons { get; set; } = new List<Button>();

        /// <summary>
        /// Labeled rectangles ("group boxes") drawn on the tab. Buttons
        /// reference a group by <see cref="Button.GroupId"/>; the group
        /// owns those buttons visually (they parent into the GroupBox
        /// control) so moving the group moves all members.
        /// </summary>
        public IList<ButtonGroup> Groups { get; set; } = new List<ButtonGroup>();
    }

    /// <summary>
    /// A single clickable button. Always references an external script file
    /// (file references only — no inline scripts, by design). Args are an
    /// ordered list because shells care about positional vs named args; we
    /// pass them through verbatim after token substitution.
    /// </summary>
    public sealed class Button
    {
        public string Id { get; set; }
        public string Label { get; set; }

        /// <summary>"powershell" | "cmd" | "process". Case-insensitive.</summary>
        public string Executor { get; set; }

        /// <summary>
        /// Path to the script/executable. Relative paths resolve against the
        /// workspace's <see cref="Workspace.ScriptsRoot"/>; absolute paths
        /// are used as-is.
        /// </summary>
        public string ScriptPath { get; set; }

        /// <summary>Arguments passed to the script. Tokens like <c>{{id}}</c> are substituted at dispatch.</summary>
        public IList<string> Args { get; set; } = new List<string>();

        /// <summary>Override working directory. Null = ScriptsRoot.</summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// Output destinations. Any subset of {"rtb","grid"}. Defaults to
        /// {"rtb"} if omitted in JSON.
        /// </summary>
        public IList<string> Outputs { get; set; } = new List<string> { "rtb" };

        /// <summary>If true, prompt "Run X?" before executing. Useful for destructive scripts.</summary>
        public bool Confirm { get; set; }

        /// <summary>If true, write a log line to the bottom RTB on click + completion.</summary>
        public bool Log { get; set; } = true;

        /// <summary>
        /// PowerShell-only: how structured output (PSObjects with
        /// properties) is rendered into the console RTB.
        ///
        /// Recognized values (case-insensitive):
        ///   "default" / null / empty — obj.ToString() per record (legacy).
        ///   "list"  — Property : Value per line, blank line between records.
        ///   "table" — aligned text columns with header underline. Buffered
        ///             until the script completes since column widths need
        ///             the full result.
        ///   "json"  — pretty-printed JSON array.
        ///   "csv"   — header row + RFC-4180 quoted values.
        ///   "raw"   — hand console rendering entirely to PowerShell's
        ///             default formatter via `Out-String -Stream`. The
        ///             script's own Format-Table / Format-List / custom
        ///             whitespace render as-written. Auto-grid population
        ///             is also disabled in this mode (untagged structured
        ///             objects no longer become rows), but Write-Grid
        ///             STILL works for explicit grid output -- the
        ///             tag-routing tags survive the filter.
        ///
        /// For non-raw modes, grid output receives the structured form
        /// regardless of RtbFormat (independent surface).
        ///
        /// Buffered formats cap at 2000 records to keep the RTB usable;
        /// the executor emits a "(truncated)" warning when exceeded.
        /// </summary>
        public string RtbFormat { get; set; }

        /// <summary>
        /// When true, clicking this button enqueues the script onto the
        /// background job queue instead of running it on the foreground
        /// single-flight path. Output is captured into a per-job buffer
        /// and surfaces in the Jobs tab (not the main console).
        ///
        /// Use for long-running scripts where you want to keep clicking
        /// other buttons while it runs. The foreground gate is
        /// unaffected -- at most one foreground + one background job
        /// run simultaneously; further background submissions queue.
        /// </summary>
        public bool RunInBackground { get; set; }

        // ---- Positioning ----
        // Coordinates are pixels. When GroupId is set, X/Y are RELATIVE
        // to that group's interior origin; otherwise they're relative
        // to the tab page's canvas. Width/Height of 0 fall back to the
        // renderer default (150x36), so a freshly-typed JSON entry
        // without sizes still renders sensibly.

        public int X { get; set; }
        public int Y { get; set; }

        /// <summary>0 = use default (150).</summary>
        public int Width { get; set; }

        /// <summary>0 = use default (36).</summary>
        public int Height { get; set; }

        /// <summary>
        /// Optional. If set and matches a <see cref="ButtonGroup.Id"/> on the
        /// containing tab, this button is rendered inside that group's frame
        /// (X/Y interpreted relative to the group's interior). When the group
        /// is moved or resized, member buttons follow naturally because they
        /// are real WinForms children of the GroupBox.
        /// </summary>
        public string GroupId { get; set; }
    }

    /// <summary>
    /// A labeled rectangle on a free-layout tab — a visual frame around
    /// related buttons, rendered as a WinForms <c>GroupBox</c>. Buttons
    /// reference it via <see cref="Button.GroupId"/>; in JSON they remain
    /// in the tab's flat <c>buttons</c> list so the schema stays simple.
    /// </summary>
    public sealed class ButtonGroup
    {
        /// <summary>
        /// Stable identifier. Auto-generated when groups are added through
        /// the UI; preserved on save. Buttons reference groups by this Id.
        /// </summary>
        public string Id { get; set; }

        /// <summary>Title shown on the frame. Empty = unlabeled rectangle.</summary>
        public string Title { get; set; }

        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; } = 200;
        public int Height { get; set; } = 120;
    }

    /// <summary>
    /// Top-level menu (e.g. "Quick" / "Tools") inserted into the main
    /// MenuStrip between the built-in Edit and Tools menus. Items use
    /// the same Button schema as tab buttons but with no parent tab --
    /// they're "always-visible" shortcuts independent of which tab the
    /// user is currently viewing.
    /// </summary>
    public sealed class MenuDefinition
    {
        public string Title { get; set; }
        public IList<Button> Items { get; set; } = new List<Button>();
    }
}
