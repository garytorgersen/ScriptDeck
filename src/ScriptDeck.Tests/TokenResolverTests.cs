using System.Collections.Generic;
using System.Linq;
using ScriptDeck.Hosting;
using Xunit;

namespace ScriptDeck.Tests
{
    public class TokenResolverTests
    {
        private static IDictionary<string, string> Vals(params (string, string)[] kv)
        {
            var d = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Fact]
        public void Substitute_Replaces_Simple_Token()
        {
            var args = new[] { "-Name", "{{computerName}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("computerName", "MYBOX")));
            Assert.Equal(new[] { "-Name", "MYBOX" }, result.ResolvedArgs);
            Assert.False(result.HasErrors);
        }

        [Fact]
        public void Substitute_Empty_Value_Emits_Empty_With_Warning()
        {
            var args = new[] { "{{user}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("user", "")));
            Assert.Equal(new[] { "" }, result.ResolvedArgs);
            Assert.False(result.HasErrors);
            Assert.Single(result.Warnings);
            Assert.Contains("user", result.Warnings[0]);
        }

        [Fact]
        public void Substitute_Empty_Value_With_Inline_Default_Uses_Default()
        {
            var args = new[] { "{{user|guest}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("user", "")));
            Assert.Equal(new[] { "guest" }, result.ResolvedArgs);
            Assert.False(result.HasErrors);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void Substitute_Unknown_Token_Becomes_Error()
        {
            var args = new[] { "{{nope}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("computerName", "X")));
            Assert.True(result.HasErrors);
            Assert.Single(result.Errors);
            Assert.Contains("nope", result.Errors[0]);
        }

        [Fact]
        public void Substitute_Unknown_Token_Leaves_Literal_In_Output()
        {
            // Even though it's an error, the resolved arg keeps the literal
            // so the user can see what failed in any error-side display.
            var args = new[] { "{{nope}}" };
            var result = TokenResolver.Resolve(args, null, new Dictionary<string, string>());
            Assert.Equal(new[] { "{{nope}}" }, result.ResolvedArgs);
        }

        [Fact]
        public void Substitute_Literal_Braces_Via_Quad_Escape()
        {
            var args = new[] { "{{{{name}}}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("name", "X")));
            Assert.Equal(new[] { "{{name}}" }, result.ResolvedArgs);
            Assert.False(result.HasErrors);
        }

        [Fact]
        public void Substitute_Multiple_Tokens_In_Same_String()
        {
            var args = new[] { "{{a}}-{{b}}-{{c}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("a", "1"), ("b", "2"), ("c", "3")));
            Assert.Equal(new[] { "1-2-3" }, result.ResolvedArgs);
        }

        [Fact]
        public void Substitute_WorkingDirectory_Resolved()
        {
            var result = TokenResolver.Resolve(
                new List<string>(), "C:\\{{root}}\\sub", Vals(("root", "data")));
            Assert.Equal("C:\\data\\sub", result.ResolvedWorkingDirectory);
        }

        [Fact]
        public void Substitute_Null_Args_Treated_As_Empty()
        {
            var result = TokenResolver.Resolve(null, null, null);
            Assert.Empty(result.ResolvedArgs);
            Assert.Equal(string.Empty, result.ResolvedWorkingDirectory);
            Assert.False(result.HasErrors);
        }

        [Fact]
        public void Substitute_Case_Insensitive_Lookup()
        {
            var args = new[] { "{{ComputerName}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("computername", "X")));
            Assert.Equal(new[] { "X" }, result.ResolvedArgs);
        }

        [Fact]
        public void Substitute_Aggregates_Errors_And_Warnings_Across_Args()
        {
            var args = new[] { "{{unknown1}}", "{{unknown2}}", "{{empty}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("empty", "")));
            // Two unknown errors, one empty warning. Errors are deduped /
            // sorted by id so the user gets a clean list.
            Assert.Equal(2, result.Errors.Count);
            Assert.Single(result.Warnings);
        }

        [Fact]
        public void Substitute_Static_Convenience_Throws_On_Unknown()
        {
            Assert.Throws<System.InvalidOperationException>(
                () => TokenResolver.Substitute("{{nope}}", new Dictionary<string, string>()));
        }

        [Fact]
        public void Substitute_Inline_Default_With_Pipes_Allowed_In_Default_Region()
        {
            // The default region accepts everything except '}' so pipes
            // inside it are fine. Pinning this so a future regex tweak
            // doesn't accidentally tighten it.
            var args = new[] { "{{x|a|b}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("x", "")));
            Assert.Equal(new[] { "a|b" }, result.ResolvedArgs);
        }

        [Fact]
        public void Substitute_Underscore_And_Hyphen_In_Identifier()
        {
            var args = new[] { "{{my_input-name}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("my_input-name", "ok")));
            Assert.Equal(new[] { "ok" }, result.ResolvedArgs);
        }

        [Fact]
        public void Substitute_Spaces_In_Token_Are_Not_Recognized_As_Token()
        {
            // "{{ stuff }}" in plain text -- our regex requires no spaces,
            // so this is treated as a literal.
            var args = new[] { "see {{ stuff }} here" };
            var result = TokenResolver.Resolve(args, null, new Dictionary<string, string>());
            Assert.Equal(new[] { "see {{ stuff }} here" }, result.ResolvedArgs);
            Assert.False(result.HasErrors);
        }

        [Fact]
        public void Substitute_Mixed_Literal_Escape_And_Token()
        {
            var args = new[] { "{{{{open}}}} {{name}} {{{{close}}}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("name", "VAL")));
            Assert.Equal(new[] { "{{open}} VAL {{close}}" }, result.ResolvedArgs);
        }

        [Fact]
        public void Substitute_Inline_Default_Not_Used_When_Value_Present()
        {
            var args = new[] { "{{x|fallback}}" };
            var result = TokenResolver.Resolve(args, null, Vals(("x", "real")));
            Assert.Equal(new[] { "real" }, result.ResolvedArgs);
        }
    }
}
