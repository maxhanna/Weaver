public class PlanAuditResult
{
    public List<AuditPlanStepResult> Steps { get; set; } = new();
}

public class AuditPlanStepResult
{
    public int Index { get; set; }
    public bool AlreadyDone { get; set; }
    public bool NeedsDecoupling { get; set; }
    public string? Reason { get; set; }
    public List<PlanStep>? DecoupledSteps { get; set; }
}

public class DecoupledStep
{
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public string? Details { get; set; }
}
