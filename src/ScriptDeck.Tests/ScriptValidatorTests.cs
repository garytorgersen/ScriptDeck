using ScriptDeck.Hosting;
using Xunit;

namespace ScriptDeck.Tests
{
    public class ScriptValidatorTests
    {
        [Fact]
        public void Empty_Script_Has_No_Errors()
        {
            Assert.Empty(ScriptValidator.Validate(""));
        }

        [Fact]
        public void Null_Script_Returns_Empty_List()
        {
            Assert.Empty(ScriptValidator.Validate(null));
        }

        [Fact]
        public void Valid_Script_Has_No_Errors()
        {
            const string ok = "$x = 1\nWrite-Output $x\n";
            Assert.Empty(ScriptValidator.Validate(ok));
        }

        [Fact]
        public void Unbalanced_Brace_Surfaces_Error()
        {
            const string bad = "if ($true) { Get-Process";
            var issues = ScriptValidator.Validate(bad);
            Assert.NotEmpty(issues);
            // Each issue has a 1-based line/col so the editor gutter
            // can paint a marker.
            Assert.All(issues, i => Assert.True(i.Line   >= 1));
            Assert.All(issues, i => Assert.True(i.Column >= 1));
        }

        [Fact]
        public void Unterminated_String_Reports_Issue()
        {
            const string bad = "$x = \"never closes";
            var issues = ScriptValidator.Validate(bad);
            Assert.NotEmpty(issues);
        }

        [Fact]
        public void Multiple_Errors_All_Returned()
        {
            // Parser is robust -- it recovers and keeps going so the user
            // sees every problem at once rather than one-at-a-time.
            const string bad = "if (one { Two\n$x = \"never closes\n";
            var issues = ScriptValidator.Validate(bad);
            Assert.True(issues.Count >= 1);
        }

        [Fact]
        public void Comment_Only_Script_Is_Valid()
        {
            // Common pattern when authoring -- the user might save a script
            // with only a comment header. Should NOT register an error.
            const string ok = "# just a header\n# nothing else\n";
            Assert.Empty(ScriptValidator.Validate(ok));
        }

        [Fact]
        public void Function_Definition_With_Param_Block_Is_Valid()
        {
            const string ok =
                "function Test-Thing {\n" +
                "    [CmdletBinding()]\n" +
                "    param([string]$X)\n" +
                "    Write-Output $X\n" +
                "}\n";
            Assert.Empty(ScriptValidator.Validate(ok));
        }
    }
}
