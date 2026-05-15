using System.Collections.Generic;
using System.Management.Automation.Language;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Wrapper around the PowerShell parser for the Script Editor's
    /// always-on syntax check. The parser ships with PowerShell itself
    /// (no PSScriptAnalyzer dependency) and is fast enough to run on
    /// every keystroke once the call is debounced — typical scripts
    /// parse in well under 10 ms.
    ///
    /// The wrapper exists mostly to keep the dialog code free of
    /// System.Management.Automation imports and to flatten the parser's
    /// rich AST/error API into a small POCO list the UI can paint.
    /// </summary>
    public static class ScriptValidator
    {
        /// <summary>
        /// Parse <paramref name="text"/> as PowerShell source and return
        /// a flat list of syntax errors. An empty list means the script
        /// parses cleanly — note that "parses cleanly" does NOT imply
        /// "runs cleanly"; runtime failures (cmdlet typos, missing
        /// modules) only surface during actual execution.
        ///
        /// The parser is robust: it tolerates trailing junk, unfinished
        /// strings, mismatched braces, etc., and reports each as a
        /// separate diagnostic, so the UI gets a list rather than just
        /// "first error wins."
        /// </summary>
        public static IList<SyntaxIssue> Validate(string text)
        {
            var result = new List<SyntaxIssue>();
            if (text == null) return result;

            // ParseInput returns the AST + tokens; we only care about
            // errors here. The parser never throws for syntax issues —
            // those land in the out-array.
            Parser.ParseInput(text, out _, out ParseError[] errors);

            if (errors == null) return result;
            foreach (var e in errors)
            {
                result.Add(new SyntaxIssue
                {
                    Message    = e.Message,
                    Line       = e.Extent?.StartLineNumber  ?? 1,
                    Column     = e.Extent?.StartColumnNumber ?? 1,
                    ErrorId    = e.ErrorId,
                });
            }
            return result;
        }
    }

    /// <summary>
    /// Flat representation of a PowerShell parse error. 1-based line and
    /// column to match what users (and the editor's gutter) see.
    /// </summary>
    public sealed class SyntaxIssue
    {
        public string Message { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string ErrorId { get; set; }
    }
}
