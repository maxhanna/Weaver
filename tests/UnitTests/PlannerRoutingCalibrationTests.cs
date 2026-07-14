using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public class PlannerRoutingCalibrationTests
{
    [Theory]
    [InlineData("Change RetryCount in AppSettings.cs from 3 to 5.", false)]
    [InlineData("Add an endpoint method that inserts a row into the audit table.", false)]
    [InlineData("Implement full CRUD with a backend controller, service, endpoint, and frontend component.", true)]
    public void ProductionGate_ReturnsExpectedRoute(string prompt, bool expectedMetaPlan)
    {
        Assert.Equal(expectedMetaPlan, AgentUtilities.EvaluateMetaPlanGate(prompt).UseMetaPlan);
    }

    [Fact]
    public void CalibrationCorpus_CoversAtomicDeceptiveAndComplexPrompts()
    {
        Assert.True(PlannerRoutingCalibrationService.Corpus.Count >= 12);
        Assert.Contains(PlannerRoutingCalibrationService.Corpus, c => c.Category == "atomic");
        Assert.Contains(PlannerRoutingCalibrationService.Corpus, c => c.Category == "deceptive");
        Assert.Contains(PlannerRoutingCalibrationService.Corpus, c => c.Category == "complex");
        Assert.Contains(PlannerRoutingCalibrationService.Corpus, c => c.ExpectedMetaPlan);
        Assert.Contains(PlannerRoutingCalibrationService.Corpus, c => !c.ExpectedMetaPlan);
    }

    [Fact]
    public void CalibrationReport_ExposesConfusionMatrixAndCaseEvidence()
    {
        var report = PlannerRoutingCalibrationService.Run();

        Assert.Equal(report.Total, report.TruePositives + report.TrueNegatives + report.FalsePositives + report.FalseNegatives);
        Assert.Equal(report.Total, report.Cases.Count);
        Assert.Equal(AgentUtilities.MetaPlanScoreThreshold, report.Threshold);
        Assert.All(report.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
        Assert.Equal(100, report.AccuracyPercent);
        Assert.Equal(0, report.FalsePositives);
        Assert.Equal(0, report.FalseNegatives);
    }
}
