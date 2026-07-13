namespace Weaver.Controllers;

partial class AgentController
{
    private const int MAX_INCREMENTAL_STEPS = 24;
    private const int MAX_INCREMENTAL_SUBPLANS = 8;
    private const int MAX_STEP_REGEN_ATTEMPTS = 3;
    private const int MAX_COMMAND_ITERATIONS = 30;
    private const int PLAN_SCORE_THRESHOLD = 65;
    private const int MAX_PLANNING_ITERATIONS = 3;
    private const int MAX_LINES_PER_DISCOVERY_FILE = 10000;
    private const int MAX_DISCOVERY_FILES = 20;
    public const string D_OLD = "<<<OLD>>>";
    public const string D_OLD_END = "<<<END_OLD>>>";
    public const string D_NEW = "<<<NEW>>>";
    public const string D_NEW_END = "<<<END_NEW>>>";
    public const string D_FULL = "<<<FULL_FILE>>>";
    public const string D_FULL_END = "<<<END_FULL_FILE>>>";
    public const string D_DONE = "<<<ALREADY_DONE>>>";
}
