namespace ScriptDeck.Hosting
{
    /// <summary>
    /// One row of shared-input state at the moment the Script Editor
    /// opens: the input's id (which also becomes its variable name), its
    /// human label, the live textbox value, and any per-input
    /// normalization rule. The Script Editor renders these in a small
    /// grid so the user can see (and override) what variables a test
    /// run will see.
    ///
    /// Carrying the Normalize flag through to the editor lets the
    /// editor's Run Test apply the same rules a real button click would
    /// (e.g. computerName: empty / "." / "localhost" -> machine name).
    /// Without this, test runs and real runs could see subtly different
    /// values for the same input, which is exactly the kind of thing
    /// that wastes hours of debugging.
    /// </summary>
    public sealed class SharedInputSnapshot
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Value { get; set; }
        public string Normalize { get; set; }
    }
}
