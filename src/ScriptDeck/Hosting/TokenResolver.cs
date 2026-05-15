using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScriptDeck.Hosting
{
    /// <summary>
    /// Outcome of resolving every <c>{{token}}</c> in a button's args and
    /// working directory against the current shared-input values.
    ///
    /// Errors are hard stops — the dispatcher won't run a button that
    /// references a non-existent shared input, because silently leaving
    /// "{{computerName}}" as a literal arg would be wildly worse than
    /// blocking the click. Warnings are informational (an input is empty
    /// and the author didn't supply an inline default).
    /// </summary>
    public sealed class TokenResolutionResult
    {
        public IList<string> ResolvedArgs { get; set; } = new List<string>();
        public string ResolvedWorkingDirectory { get; set; }
        public IList<string> Errors { get; } = new List<string>();
        public IList<string> Warnings { get; } = new List<string>();
        public bool HasErrors => Errors.Count > 0;
    }

    /// <summary>
    /// Substitutes <c>{{id}}</c> tokens in workspace strings using
    /// shared-input values. Phase 5 hardening over the Phase 2 stopgap:
    ///
    ///   - Unknown tokens (no matching shared input) become errors so the
    ///     dispatcher can refuse to run rather than pass a literal
    ///     "{{foo}}" to a script.
    ///   - <c>{{id|default}}</c> supplies an inline fallback when the
    ///     shared input is present but empty. The default text may not
    ///     contain '}'.
    ///   - <c>{{{{</c> / <c>}}}}</c> are literal braces, mirroring how
    ///     C# format strings escape braces — familiar and unambiguous.
    ///   - Empty value with no inline default raises a warning, not an
    ///     error: an empty optional flag is a legitimate use case.
    ///
    /// The token id grammar matches a typical config identifier:
    /// <c>[A-Za-z_][A-Za-z0-9_-]*</c>. Spaces and dots aren't allowed —
    /// that keeps "{{ stuff }}" in a literal sentence from being
    /// accidentally interpreted as a token reference.
    /// </summary>
    public static class TokenResolver
    {
        // Anchored, single-line. The default group is greedy until the
        // first '}}' — '}' isn't allowed inside a default to keep the
        // regex unambiguous.
        private static readonly Regex TokenRx = new Regex(
            @"\{\{(?<id>[A-Za-z_][A-Za-z0-9_\-]*)(?:\|(?<def>[^}]*))?\}\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Sentinels used to hide literal {{/}} sequences from the regex
        // pass. Must be characters that cannot appear in user input or
        // shared-input values — U+E000-U+F8FF is the Unicode private use
        // area, perfect for this kind of in-band protocol.
        private const string LiteralOpenSentinel  = "\uE000<<{{>>\uE000";
        private const string LiteralCloseSentinel = "\uE000<<}}>>\uE000";

        /// <summary>
        /// Resolve every token in <paramref name="args"/> and
        /// <paramref name="workingDirectory"/>. The result aggregates
        /// errors/warnings across all inputs so the user sees ALL the
        /// problems at once rather than fixing them one click at a time.
        /// </summary>
        public static TokenResolutionResult Resolve(
            IList<string> args,
            string workingDirectory,
            IDictionary<string, string> values)
        {
            var result = new TokenResolutionResult();
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emptyWithoutDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            result.ResolvedArgs = (args ?? new List<string>())
                .Select(a => SubstituteOne(a, values, unknown, emptyWithoutDefault))
                .ToList();

            result.ResolvedWorkingDirectory =
                SubstituteOne(workingDirectory, values, unknown, emptyWithoutDefault);

            foreach (var id in unknown.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                result.Errors.Add($"Unknown token '{{{{{id}}}}}' — no shared input has id '{id}'.");

            foreach (var id in emptyWithoutDefault.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                result.Warnings.Add(
                    $"Shared input '{id}' is empty and no inline default was given. " +
                    $"Use '{{{{{id}|fallback}}}}' to supply one.");

            return result;
        }

        /// <summary>
        /// Single-string convenience used by callers that don't need the
        /// rich result (e.g. tests, future label-formatting). Throws on
        /// unknown tokens because there's no out-of-band place for the
        /// caller to receive errors.
        /// </summary>
        public static string Substitute(string input, IDictionary<string, string> values)
        {
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var s = SubstituteOne(input, values, unknown, empty);
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    "Unknown token(s): " + string.Join(", ", unknown));
            return s;
        }

        // ---- Internals ----

        private static string SubstituteOne(
            string input,
            IDictionary<string, string> values,
            HashSet<string> unknown,
            HashSet<string> emptyWithoutDefault)
        {
            if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
            if (values == null) values = new Dictionary<string, string>();

            // Stage 1: hide literal-brace escapes behind sentinels so the
            // regex can't misinterpret "{{{{name}}}}" as a token wrapped
            // in literal braces. Order matters — replace 4-char escapes
            // first so they don't get partially eaten by a 2-char match
            // later.
            var s = input
                .Replace("{{{{", LiteralOpenSentinel)
                .Replace("}}}}", LiteralCloseSentinel);

            // Stage 2: regex-substitute real tokens.
            s = TokenRx.Replace(s, m => ResolveMatch(m, values, unknown, emptyWithoutDefault));

            // Stage 3: restore escaped literals. The user wrote "{{{{" to
            // produce "{{", so unescape exactly that.
            return s
                .Replace(LiteralOpenSentinel, "{{")
                .Replace(LiteralCloseSentinel, "}}");
        }

        private static string ResolveMatch(
            Match m,
            IDictionary<string, string> values,
            HashSet<string> unknown,
            HashSet<string> emptyWithoutDefault)
        {
            var id = m.Groups["id"].Value;
            var hasDefault = m.Groups["def"].Success;
            var inlineDefault = hasDefault ? m.Groups["def"].Value : null;

            if (!values.TryGetValue(id, out var val))
            {
                // Unknown token. Record it and leave the literal in place
                // so the user sees what failed in any error-side output.
                unknown.Add(id);
                return m.Value;
            }

            if (string.IsNullOrEmpty(val))
            {
                // Empty value: prefer the inline default if the author
                // supplied one. Otherwise emit empty + warn — silently
                // emitting "" would mask configuration mistakes.
                if (hasDefault) return inlineDefault;
                emptyWithoutDefault.Add(id);
                return string.Empty;
            }

            return val;
        }
    }
}
