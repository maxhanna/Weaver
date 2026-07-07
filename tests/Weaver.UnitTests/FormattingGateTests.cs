using Xunit;
using Weaver;

namespace Weaver.UnitTests;

public class FormattingGateTests
{
    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        var tokens = FormattingGate.Tokenize("dotnet format --verify-no-changes --include {file}");
        Assert.Equal(new[] { "dotnet", "format", "--verify-no-changes", "--include", "{file}" }, tokens);
    }

    [Fact]
    public void Tokenize_RespectsQuotedSegments()
    {
        var tokens = FormattingGate.Tokenize("prettier --config \"my config.json\" {file}");
        Assert.Equal(new[] { "prettier", "--config", "my config.json", "{file}" }, tokens);
    }

    [Theory]
    [InlineData("a & calc.exe")]
    [InlineData("evil; rm -rf ~")]
    [InlineData("$(echo pwned)")]
    [InlineData("`echo pwned`")]
    [InlineData("a|b>c<d^e")]
    public void FilePlaceholderSubstitution_PreservesShellMetacharactersAsOneAtomicArgument(string maliciousLookingPath)
    {
        // {file} must survive substitution as a single ArgumentList element regardless of
        // embedded shell metacharacters. This is the actual fix: file paths come from
        // agent-authored file names (and cards may be shared via the BugHosted
        // leaderboard), so the substituted value must never be handed to a shell for
        // re-parsing. Passing it as one argv element (rather than interpolating into a
        // "cmd.exe /c ..." string) means characters like & | ; ` $() are inert.
        var tokens = FormattingGate.Tokenize("dotnet format --include {file}");
        var fileTokenIndex = tokens.IndexOf("{file}");
        Assert.True(fileTokenIndex >= 0);

        var substituted = tokens.Select(t => t == "{file}" ? maliciousLookingPath : t).ToList();

        Assert.Equal(tokens.Count, substituted.Count); // no extra tokens were produced
        Assert.Equal(maliciousLookingPath, substituted[fileTokenIndex]); // preserved verbatim, unsplit
    }

    [Fact]
    public async Task CheckAsync_ModeNone_ReturnsNull()
    {
        var result = await FormattingGate.CheckAsync(Path.GetTempPath(), new[] { "a.cs" },
            new BenchmarkFormatting { Mode = "none" });
        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_NoConfiguredExtension_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.py"), "print(1)");
            var result = await FormattingGate.CheckAsync(dir, new[] { "a.py" },
                new BenchmarkFormatting { Mode = "formatter", Commands = new() { ["cs"] = "cmd.exe /c exit 0" } });
            Assert.Null(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_ConfiguredCommandExitsZero_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "class A {}");
            var result = await FormattingGate.CheckAsync(dir, new[] { "a.cs" },
                new BenchmarkFormatting { Mode = "formatter", Commands = new() { ["cs"] = "cmd.exe /c exit 0" } });
            Assert.True(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_ConfiguredCommandExitsNonZero_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "class A {}");
            var result = await FormattingGate.CheckAsync(dir, new[] { "a.cs" },
                new BenchmarkFormatting { Mode = "formatter", Commands = new() { ["cs"] = "cmd.exe /c exit 1" } });
            Assert.False(result);
        }
        finally { Directory.Delete(dir, true); }
    }
}
