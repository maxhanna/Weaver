
namespace Weaver;

public class AgentRequest
{
    public string Prompt { get; set; } = "";
    public string Project { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int? MaxIterations { get; set; }
    public int? MaxStepsPerBatch { get; set; }
    public string? SteeringContext { get; set; }
    public bool SelfImproving { get; set; }
    public bool IsDecomposing { get; set; }
    public string? CardId { get; set; } 
    public bool CreateTests { get; set; }
    public bool IsBenchmark { get; set; }
    public string? BenchmarkProjectRoot { get; set; }
    public string? BuildCommands { get; set; }
    // Strict verifier (hard-gate) toggle. Three states preserve backward compatibility:
    //  null  → legacy behavior (deterministic findings still force complete=false, but the
    //          LLM verification round and the post-verify repair loop still run).
    //  true  → HARD-GATE: when a deterministic check (template binding, CSS wiring, rename-all,
    //          OS-output, applied-edit-on-disk, browser-test, state-probe mismatch) fires, the
    //          run ends immediately with a FAILED verdict — no LLM verifier round, no repair
    //          loop. Saves the 3× replan churn a compile-time error otherwise triggers.
    //  false → explicitly relaxed: deterministic issues are published as ground truth but do
    //          not force complete=false.
    public bool? StrictVerifier { get; set; }
    public string? EndpointId { get; set; }
    public string? RunId { get; set; }

    /// <summary>Indices of plan steps already completed (0-based).</summary>
    public List<int>? CompletedStepIndices { get; set; }
}

public class ExistingPlanItem
{
    public int Index { get; set; }
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public bool Done { get; set; }
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
}
