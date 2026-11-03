namespace Weaver;

/// <summary>
/// Mutable state owned by one agent execution.
///
/// This state must not live on <see cref="Controllers.AgentController"/>: an execution
/// spans several pipeline phases and may overlap another execution. Keeping it in an
/// explicit object makes the ownership and lifetime of run state visible at call sites.
/// </summary>
internal sealed class AgentRunContext
{
    public bool LastConnectionCheckResult { get; set; } = true;
    public bool GracefulStop { get; set; }
    public int SkeletonContextChars { get; set; }
    public string? RequirementChecklist { get; set; }
    public int TaskPromptContextChars { get; set; }
    public int StepLlmPromptTokens { get; private set; }
    public int StepLlmResponseTokens { get; private set; }
    public int StepLlmCalls { get; private set; }
    public int RunLlmPromptTokens { get; private set; }
    public int RunLlmResponseTokens { get; private set; }
    public int RunLlmCalls { get; private set; }
    public List<object> DiscoverySteps { get; set; } = new();

    public void Reset()
    {
        LastConnectionCheckResult = true;
        GracefulStop = false;
        SkeletonContextChars = 0;
        RequirementChecklist = null;
        TaskPromptContextChars = 0;
        StepLlmPromptTokens = 0;
        StepLlmResponseTokens = 0;
        StepLlmCalls = 0;
        RunLlmPromptTokens = 0;
        RunLlmResponseTokens = 0;
        RunLlmCalls = 0;
        DiscoverySteps = new List<object>();
    }

    public void RecordLlmRound(int promptTokens, int responseTokens)
    {
        StepLlmPromptTokens += promptTokens;
        StepLlmResponseTokens += responseTokens;
        StepLlmCalls++;
        RunLlmPromptTokens += promptTokens;
        RunLlmResponseTokens += responseTokens;
        RunLlmCalls++;
    }

    public object? SnapshotRunLlmSpend()
    {
        if (RunLlmCalls == 0) return null;
        return new
        {
            calls = RunLlmCalls,
            promptTokens = RunLlmPromptTokens,
            responseTokens = RunLlmResponseTokens,
            totalTokens = RunLlmPromptTokens + RunLlmResponseTokens
        };
    }

    public object? TakeStepLlmMetrics()
    {
        if (StepLlmCalls == 0) return null;
        var metrics = new
        {
            calls = StepLlmCalls,
            promptTokens = StepLlmPromptTokens,
            responseTokens = StepLlmResponseTokens,
            totalTokens = StepLlmPromptTokens + StepLlmResponseTokens
        };
        StepLlmCalls = 0;
        StepLlmPromptTokens = 0;
        StepLlmResponseTokens = 0;
        return metrics;
    }
}
