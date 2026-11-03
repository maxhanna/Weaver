using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the deterministic half of the DUMMY-FILE-FOR-FOLDER guard: when a weak planner
/// cannot create a directory directly (its mkdir/_create_directory attempt failed or was
/// rejected), it falls back to planning a placeholder file whose only purpose is to
/// materialize the folder — the benchmark-23 run wrote "benchmark_test_23/placeholder.txt"
/// containing "Placeholder file for directory creation". <c>AgentTextUtilities.IsDirectoryScaffoldPlaceholder</c>
/// detects those steps so the executor can skip the junk file and create the directory the
/// step implies instead. Real files (long content, or short content without placeholder
/// signals) must never be classified as scaffolding.
/// </summary>
public class PlaceholderCreateFileDetectionTests
{
    // ── The regression: the exact benchmark-23 log case ────────────────────

    [Fact]
    public void PlaceholderTxt_WithDirectoryCreationContent_IsScaffold()
    {
        Assert.True(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(
            "placeholder.txt", "Placeholder file for directory creation"));
    }

    [Fact]
    public void NestedPlaceholderTxt_IsScaffold()
    {
        Assert.True(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(
            "benchmark_test_23/placeholder.txt", "Placeholder file for directory creation"));
    }

    // ── Filename signals ────────────────────────────────────────────────────

    [Theory]
    [InlineData("placeholder.txt", null)]
    [InlineData("dummy.txt", null)]
    [InlineData(".gitkeep", null)]
    [InlineData(".keep", "")]
    [InlineData("keep", null)]
    [InlineData(".placeholder", null)]
    [InlineData("scaffold.txt", null)]
    [InlineData("temp", null)]
    [InlineData("PLACEHOLDER.md", null)]
    public void ClassicPlaceholderFilenames_AreScaffold(string fileName, string? content)
    {
        Assert.True(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(fileName, content),
            $"'{fileName}' should be detected as folder scaffolding");
    }

    // ── Short-content phrase signals ────────────────────────────────────────

    [Theory]
    [InlineData("x.txt", "just to create the folder")]
    [InlineData("x.txt", "create the directory")]
    [InlineData("x.txt", "Placeholder file for directory creation")]
    [InlineData("x.txt", "dummy file to establish the folder")]
    [InlineData("x.txt", "empty file to create the folder")]
    public void ShortFolderScaffoldingContent_IsScaffold(string fileName, string content)
    {
        Assert.True(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(fileName, content));
    }

    // ── Real files must never be classified as scaffolding ─────────────────

    [Fact]
    public void LongRealFile_WithPlaceholderWord_IsNotScaffold()
    {
        var html = "<!DOCTYPE html>\n<html>\n<head><title>Benchmark 23</title></head>\n" +
                   "<body><h1>Benchmark 23</h1><p>placeholder text here</p><canvas id=\"c\"></canvas></body></html>\n" +
                   "<script>window.legCount = 4;</script>\n";
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("benchmark_test_23/index.html", html));
    }

    [Fact]
    public void Gitignore_WithNodeModules_IsNotScaffold()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(".gitignore", "node_modules\n/dist\n"));
    }

    [Fact]
    public void EmptyJsonObject_IsNotScaffold()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("package.json", "{}"));
    }

    [Fact]
    public void Npmrc_IsNotScaffold()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(".npmrc", "registry=https://registry.npmjs.org/"));
    }

    [Fact]
    public void TinyRealScript_IsNotScaffold()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("server.js", "const http = require('http');"));
    }

    [Fact]
    public void ShortHeadingHtml_IsNotScaffold()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("benchmark_test_23/index.html", "Benchmark23"));
    }

    // ── The hint gate (LLM-fallback precondition) ───────────────────────────

    [Fact]
    public void ShortRealReadme_HasNoHint_SoNoLlmCall()
    {
        // The tool-selection corpus case: short but plainly real content must NOT even hint,
        // otherwise the guard would burn an LLM round-trip on every ordinary short file.
        Assert.False(AgentTextUtilities.HasPlaceholderHint(
            "docs/README.md", "# Demo project\n\nA sandbox fixture used by tool-selection tests.\n"));
    }

    [Fact]
    public void ShortHelperFunction_HasNoHint()
    {
        Assert.False(AgentTextUtilities.HasPlaceholderHint(
            "src/utils/format.ts", "export function formatNumber(n: number): string { return n.toLocaleString(); }\n"));
    }

    [Theory]
    [InlineData("x.txt", "just to create the folder")]
    [InlineData("dummy.txt", "whatever")]
    [InlineData("notes.txt", "placeholder file for the demo module")]
    [InlineData("x.txt", "needed for folder creation")]
    [InlineData("temp", null)]
    public void PlaceholderHints_AreDetected(string fileName, string? content)
    {
        Assert.True(AgentTextUtilities.HasPlaceholderHint(fileName, content));
    }

    [Fact]
    public void BareWordSentences_AreNotHints_AfterPhrasesOnlyTightening()
    {
        // The hint gate matches explicit placeholder/folder-creation PHRASES only. Bare words
        // ("keep", "placeholder", "tmp", …) were deliberately dropped from the content regex
        // because real code and comments contain them constantly (e.g. `return "keep"`), and
        // every ordinary short _create_file would then burn an LLM round-trip. Sentences built
        // from those words are therefore NOT hints anymore — the junk files they describe are
        // only caught when the content carries an explicit creation/placeholder phrase or the
        // FILENAME is a classic placeholder name (IsPlaceholderStem).
        Assert.False(AgentTextUtilities.HasPlaceholderHint("x.txt", "keep this file for now"));
        Assert.False(AgentTextUtilities.HasPlaceholderHint("notes.txt", "placeholder to be replaced later"));
    }

    [Fact]
    public void LongContent_WithPlaceholderWord_HasNoHint()
    {
        var longContent = "placeholder\n" + string.Concat(Enumerable.Repeat("filler line of real content\n", 20));
        Assert.False(AgentTextUtilities.HasPlaceholderHint("x.txt", longContent));
    }

    [Fact]
    public void EmptyFileName_WithRealContent_IsNotScaffold()
    {
        // Content has no placeholder signals — the missing filename must not flip it to true.
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("", "const http = require('http');"));
    }

    [Fact]
    public void NullFileName_WithRealContent_IsNotScaffold()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder(null!, "const http = require('http');"));
    }
}
