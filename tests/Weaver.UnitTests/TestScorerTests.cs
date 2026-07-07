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

    static Dictionary<string, object?> StepWithOrigin(string type, string status, string origin, string? path = null)
    {
        var d = Step(type, status, path);
        d["origin"] = origin;
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

    [Fact]
    public void Score_NoBenchmarkManifest_GatesThatNeedItAreUnmeasuredAndNotPerfect()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "done", "b.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "a.cs", "b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Null(r.Gates.ExactStepCount);
        Assert.Null(r.Gates.StructurePreserved);
        Assert.True(r.Gates.PermissionsRespected);
        Assert.True(r.Gates.NoReplan);
        // Unmeasured gates count as not-perfect even though the run itself passed.
        Assert.True(r.Passed);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_OriginalPlanOnly_MatchingManifest_IsPerfectPass()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/a.cs"),
            Step("edit", "done", "src/b.cs"),
        };

        var benchmark = new BenchmarkManifest
        {
            ExpectedSteps = 2,
            AllowedPaths = new List<string> { "src/**" }
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/a.cs", "src/b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6", benchmark: benchmark);

        Assert.True(r.Gates.ExactStepCount);
        Assert.True(r.Gates.StructurePreserved);
        Assert.True(r.Gates.NoReplan);
        Assert.Equal(2, r.PlannedSteps);
        Assert.Equal(2, r.ExpectedSteps);
        // FormattingClean is untouched by the sync Score() overload — still null (unmeasured).
        Assert.Null(r.Gates.FormattingClean);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_ReplanOriginStep_FailsNoReplanGateEvenIfComplete()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/a.cs"),
            StepWithOrigin("edit", "done", "replan", "src/b.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/a.cs", "src/b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { ExpectedSteps = 2, AllowedPaths = new List<string> { "src/**" } });

        Assert.False(r.Gates.NoReplan);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_RepairOriginStep_FailsNoReplanGate()
    {
        var steps = new List<object>
        {
            StepWithOrigin("edit", "done", "repair", "src/a.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(1), complete: true,
            filesEdited: new[] { "src/a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { ExpectedSteps = 1, AllowedPaths = new List<string> { "src/**" } });

        Assert.False(r.Gates.NoReplan);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_PlanExceedsExpectedSteps_FailsExactStepCountGate()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "done", "b.cs"),
            Step("edit", "done", "c.cs"),
            Step("edit", "done", "d.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(4), complete: true,
            filesEdited: new[] { "a.cs", "b.cs", "c.cs", "d.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { ExpectedSteps = 3 });

        Assert.False(r.Gates.ExactStepCount);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_FileOutsideAllowedPaths_FailsStructurePreservedGate()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/a.cs"),
            Step("create_file", "done", "../outside/escape.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/a.cs", "../outside/escape.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = new List<string> { "src/**" } });

        Assert.False(r.Gates.StructurePreserved);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_MidPatternDoubleStarGlob_MatchesNestedPathAndRejectsSibling()
    {
        // Regression test for the allowedPaths glob matcher: a mid-pattern "**" (not
        // just a trailing one) must match arbitrarily-nested paths under it and must
        // NOT degrade into matching everything.
        var steps = new List<object>
        {
            Step("edit", "done", "src/deep/nested/dir/tests/CalcTests.cs"),
            Step("create_file", "done", "src/other/not-allowed.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/deep/nested/dir/tests/CalcTests.cs", "src/other/not-allowed.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = new List<string> { "src/**/tests/*.cs" } });

        // One matched, one didn't -> overall gate is false, proving the matcher
        // discriminates rather than matching (or rejecting) everything uniformly.
        Assert.False(r.Gates.StructurePreserved);
    }

    [Fact]
    public async Task ScoreAsync_FormattingModeNone_LeavesFormattingGateUnmeasured()
    {
        var steps = new List<object> { Step("edit", "done", "a.cs") };
        var benchmark = new BenchmarkManifest
        {
            ExpectedSteps = 1,
            AllowedPaths = new List<string> { "*.cs" },
            Formatting = new BenchmarkFormatting { Mode = "none" }
        };

        var r = await TestScorer.ScoreAsync("t", null, steps, PlanOf(1), complete: true,
            filesEdited: new[] { "a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            projectRoot: Path.GetTempPath(), benchmark: benchmark);

        Assert.Null(r.Gates.FormattingClean);
        Assert.False(r.PerfectPass);
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
