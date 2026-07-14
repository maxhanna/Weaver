namespace Weaver.Services;

public sealed record MetaPlanGateDecision(bool UseMetaPlan, int Score, string Reason)
{
    public string Route => UseMetaPlan ? "meta-plan" : "incremental";
}

public sealed record PlannerRoutingCase(
    string Id, string Category, string Prompt, bool ExpectedMetaPlan, string Rationale);

public sealed class PlannerRoutingCaseResult
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public bool ExpectedMetaPlan { get; init; }
    public bool ActualMetaPlan { get; init; }
    public int Score { get; init; }
    public string Route { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool Correct => ExpectedMetaPlan == ActualMetaPlan;
}

public sealed class PlannerRoutingCalibrationReport
{
    public int Threshold { get; init; }
    public int Total { get; init; }
    public int Correct { get; init; }
    public double AccuracyPercent { get; init; }
    public int TruePositives { get; init; }
    public int TrueNegatives { get; init; }
    public int FalsePositives { get; init; }
    public int FalseNegatives { get; init; }
    public double PrecisionPercent { get; init; }
    public double RecallPercent { get; init; }
    public List<PlannerRoutingCaseResult> Cases { get; init; } = new();
}

public static class PlannerRoutingCalibrationService
{
    public static IReadOnlyList<PlannerRoutingCase> Corpus { get; } =
    [
        new("atomic-value", "atomic", "Change RetryCount from 3 to 5 in AppSettings.cs.", false, "One targeted value replacement."),
        new("atomic-method", "atomic", "Add a ClampToZero method to PriceService.cs.", false, "One method in one known file."),
        new("verbose-atomic", "deceptive", "Please carefully update the existing MaxEntries property in CacheOptions.cs from 100 to 250, preserve all surrounding behavior, avoid duplicates, validate the syntax, and make no unrelated changes.", false, "Verbose wording does not create architectural complexity."),
        new("atomic-endpoint-table", "deceptive", "Add an endpoint method that inserts a row into the audit table.", false, "Explicit deterministic atomic exception."),
        new("two-known-files", "medium", "Change Greeter.Greet in Greeter.cs and update its caller in Program.cs.", false, "Small coordinated edit with explicit files."),
        new("single-component", "medium", "Create a reusable date picker component in DatePicker.tsx with styles in DatePicker.css.", false, "Self-contained component across two explicit files."),
        new("crud-cross-layer", "complex", "Implement full CRUD for projects with a backend controller, service, database endpoint, and frontend component.", true, "Cross-layer feature with several architectural stages."),
        new("auth-end-to-end", "complex", "Build end-to-end authentication across the backend service, controller endpoints, frontend module, and login component.", true, "Cross-cutting security feature."),
        new("scaffold-module", "complex", "Scaffold an orders module with OrdersController.cs, OrdersService.cs, orders.ts, orders.html, and orders.css.", true, "Five explicit files and multiple layers."),
        new("migration-feature", "complex", "Create a migration, implement a backend service and endpoint, then build the frontend component for managing notification preferences.", true, "Ordered database, API, and UI stages."),
        new("multi-system", "complex", "Build a reporting module that aggregates billing and usage data through a service and controller and exposes it in a frontend dashboard component.", true, "Multiple systems and layers."),
        new("short-complex", "deceptive", "Implement end-to-end password reset.", true, "Short prompt but inherently cross-layer workflow.")
    ];

    public static PlannerRoutingCalibrationReport Run()
    {
        var cases = Corpus.Select(c =>
        {
            var decision = AgentUtilities.EvaluateMetaPlanGate(c.Prompt);
            return new PlannerRoutingCaseResult
            {
                Id = c.Id,
                Category = c.Category,
                ExpectedMetaPlan = c.ExpectedMetaPlan,
                ActualMetaPlan = decision.UseMetaPlan,
                Score = decision.Score,
                Route = decision.Route,
                Reason = decision.Reason
            };
        }).ToList();
        var tp = cases.Count(c => c.ExpectedMetaPlan && c.ActualMetaPlan);
        var tn = cases.Count(c => !c.ExpectedMetaPlan && !c.ActualMetaPlan);
        var fp = cases.Count(c => !c.ExpectedMetaPlan && c.ActualMetaPlan);
        var fn = cases.Count(c => c.ExpectedMetaPlan && !c.ActualMetaPlan);
        return new PlannerRoutingCalibrationReport
        {
            Threshold = AgentUtilities.MetaPlanScoreThreshold,
            Total = cases.Count,
            Correct = tp + tn,
            AccuracyPercent = Percent(tp + tn, cases.Count),
            TruePositives = tp,
            TrueNegatives = tn,
            FalsePositives = fp,
            FalseNegatives = fn,
            PrecisionPercent = Percent(tp, tp + fp),
            RecallPercent = Percent(tp, tp + fn),
            Cases = cases
        };
    }

    private static double Percent(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round(numerator * 100.0 / denominator, 1);
}
