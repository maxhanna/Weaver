using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

public class AgentUtilitiesEdgeCasesTests
{
    [Fact]
    public void GeneratePlanJsonCandidates_RepairsUnquotedKeysAndTruncation()
    {
        var raw = "{ plan: [{ file: \"a.cs\", change: \"add\" }";
        var candidates = AgentUtilities.GeneratePlanJsonCandidates(raw).ToList();

        Assert.Contains(candidates, c => c.Contains("\"plan\""));
        Assert.Contains(candidates, c => c.Contains("\"file\""));
    }

    [Fact]
    public void GeneratePlanJsonCandidates_RepairsNewlinesInStrings()
    {
        var raw = "{\"plan\":[{\"file\":\"a.cs\",\"change\":\"line1\nline2\"}]}";
        var candidates = AgentUtilities.GeneratePlanJsonCandidates(raw).ToList();

        Assert.Contains(candidates, c => c.Contains("line1\\nline2") || c.Contains("line1\\r\\nline2"));
    }

    [Fact]
    public void TryRepairTruncatedPlanJson_AppendsClosingQuoteAndBrackets()
    {
        var truncated = "{\"plan\":[{\"file\":\"a.cs\",\"change\":\"missing end";
        var repaired = AgentUtilities.TryRepairTruncatedPlanJson(truncated);

        Assert.NotNull(repaired);
        Assert.EndsWith("]}", repaired);
    }

    [Fact]
    public void ExtractEditFromCodeGen_IgnoresLeadingMarkdownAndMissingCodeFence()
    {
        var raw = "Here you go:\noldString: \"before\"\nnewString: \"after\"";
        var (oldString, newString, error) = AgentUtilities.ExtractEditFromCodeGen(raw);

        Assert.NotNull(error);
        Assert.Contains("parse", error!, StringComparison.OrdinalIgnoreCase);
    }
}
