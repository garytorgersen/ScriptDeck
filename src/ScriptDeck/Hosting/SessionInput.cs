namespace ScriptDeck.Hosting
{
    /// <summary>
    /// In-memory volatile shared input. Created at runtime by either a
    /// script (via the bootstrap helper <c>Set-SharedInput</c>) or the
    /// user (via the Inputs grid). Lives in <c>Shell._sessionInputs</c>;
    /// never written to the workspace JSON. Cleared whenever the
    /// workspace changes (open / close / switch) or the app exits.
    ///
    /// Coexists with <see cref="ScriptDeck.Workspace.SharedInput"/>
    /// instances from the workspace JSON. At dispatch time the merged
    /// view (volatile wins on id collision -- though the
    /// duplicate-prevention rules in Shell normally keep them from
    /// colliding in the first place) is what scripts see as
    /// <c>$variables</c>.
    ///
    /// Kept deliberately small: just enough state to render a grid row
    /// and produce a variable binding. Anything more elaborate (typed
    /// values, validation rules, normalization) is handled by the
    /// existing static-input plumbing -- volatile inputs are
    /// intentionally simple.
    /// </summary>
    public sealed class SessionInput
    {
        /// <summary>Unique identifier; also the script-visible variable name.</summary>
        public string Id { get; set; }

        /// <summary>Optional human-readable label. Falls back to Id when empty.</summary>
        public string Label { get; set; }

        /// <summary>Current value. Strings only (matches the static-input contract).</summary>
        public string Value { get; set; }
    }
}
