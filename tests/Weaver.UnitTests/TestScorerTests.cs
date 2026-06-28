using Xunit;
using Weaver;

namespace Weaver.UnitTests;

public class TestScorerTests
{
    static Dictionary<string, object?> Step(string type, string status, string? path = null, string? extraKey = null, string? extraVal = null)
    {
        var d = new Dictionary<string, object?> { ["type"] = type, ["status"] = status };
        if (path != null) d["path"] = path;
        if (extraKey != null) d[extraKey] = extraVal;
        return d;
    }

    static AgentPlan PlanOf(int stepCount)
    {
        var p = new AgentPlan();
        for (var i = 0; i < stepCount; i++)
            p.Plan.Add(new PlanStep { File = $"file{i}.cs", Change = "do thing" });
        return p;
    }

    [Fact]
    public void Score_AllStepsDone_IsPerfectAndPassing()
    {
        var steps = new List<object>
        {
            Step("create_file", "done", "tests.md"),
            Step("edit", "done", "tests.md"),
            Step("edit", "done", "src/a.cs"),
        };

        var r = TestScorer.Score("starter", "card1", steps, PlanOf(3), complete: true,
            filesEdited: new[] { "tests.md", "src/a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(3, r.TotalSteps);
        Assert.Equal(3, r.StepsPassed);
        Assert.Equal(100, r.Score);
        Assert.True(r.Passed);
        Assert.Null(r.FailedStep);
    }

    [Fact]
    public void Score_HaltedMidway_ScoresPartialProgressAndFails()
    {
        var steps = new List<object>
        {
            Step("create_file", "done", "tests.md"),
            Step("edit", "done", "tests.md"),
            new Dictionary<string, object?>
            {
                ["type"] = "plan_halted",
                ["status"] = "error",
                ["reason"] = "Fatal step failure: could not apply edit",
                ["failedFile"] = "src/hard.cs",
                ["remainingSteps"] = 2,
            },
        };

        var r = TestScorer.Score("starter", "card1", steps, PlanOf(4), complete: false,
            filesEdited: new[] { "tests.md" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(4, r.TotalSteps);
        Assert.Equal(2, r.StepsPassed);
        Assert.Equal(50, r.Score);
        Assert.False(r.Passed);
        Assert.Equal("src/hard.cs", r.FailedStep);
        Assert.Contains("could not apply edit", r.FailureReason);
    }

    [Fact]
    public void Score_ErroredStepWithoutHaltMarker_StillCountsAsFailure()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "error", "b.cs", extraKey: "error", extraVal: "boom"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: false,
            filesEdited: new[] { "a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.False(r.Passed);
        Assert.Equal(1, r.StepsPassed);
        Assert.Equal(50, r.Score);
        Assert.Equal("b.cs", r.FailedStep);
        Assert.Equal("boom", r.FailureReason);
    }

    [Fact]
    public void Score_NoPlan_FallsBackToObservedStepCount()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "done", "b.cs"),
        };

        var r = TestScorer.Score("t", null, steps, plan: null, complete: true,
            filesEdited: new[] { "a.cs", "b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(2, r.TotalSteps);
        Assert.Equal(100, r.Score);
        Assert.True(r.Passed);
    }

    [Fact]
    public void Score_IdentifiesWrittenTestsAndCodeFile()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/calc.cs"),
            Step("create_file", "done", "tests/CalcTests.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/calc.cs", "tests/CalcTests.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal("src/calc.cs", r.CodeFile);
        Assert.Contains("tests/CalcTests.cs", r.WrittenTests);
        Assert.DoesNotContain("src/calc.cs", r.WrittenTests);
    }
}

public class WeaverVersionTests
{
    [Fact]
    public void Read_PrefersProvidedDirOverFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".weaver-version.txt"), "42\n");
            Assert.Equal("42", WeaverVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_FindsDotWeaverVersionWithoutTxtExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".weaver-version"), "7");
            Assert.Equal("7", WeaverVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_EmptyProvidedDir_FallsThroughToNonEmptyValue()
    {
        // An empty provided dir must fall through to a fallback (base dir / LocalAppData
        // self-update copy / "0") rather than throwing or returning empty.
        var dir = Path.Combine(Path.GetTempPath(), "weaver-ver-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.False(string.IsNullOrWhiteSpace(WeaverVersion.Read(dir))); }
        finally { Directory.Delete(dir, true); }
    }
}

public class EnvironmentMetadataTests
{
    [Fact]
    public void Collect_PopulatesCoreFields()
    {
        var m = EnvironmentMetadata.Collect();
        Assert.False(string.IsNullOrWhiteSpace(m.Os));
        Assert.True(m.CpuCores > 0);
        Assert.False(string.IsNullOrWhiteSpace(m.Runtime));
    }
}
