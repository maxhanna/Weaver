using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Ratchet on the size of the AgentController partial-class split. The agent pipeline
/// lives in ~20 files that all share one class (and its per-run state), so new logic
/// added there is the main maintainability risk. New behavior belongs in Services/
/// (stateless, unit-testable). This budget stops the controller from growing while the
/// extraction work proceeds.
///
/// When a change SHRINKS the total, lower <see cref="BaselineLines"/> to the new value in
/// the same commit so the ratchet keeps tightening. Raising it requires a deliberate,
/// reviewed reason, not a passing test.
/// </summary>
public class AgentControllerSizeBudgetTests
{
    // Total lines across Controllers/AgentController*.cs at the time the ratchet was added,
    // counted the same way the test counts (File.ReadAllLines, not wc -l).
    private const int BaselineLines = 28403;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Weaver.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the Weaver repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void AgentControllerPartials_TotalLines_DoNotExceedBudget()
    {
        var controllers = Path.Combine(FindRepoRoot(), "Controllers");
        var files = Directory.GetFiles(controllers, "AgentController*.cs");
        Assert.NotEmpty(files);

        var total = files.Sum(f => File.ReadAllLines(f).Length);

        Assert.True(total <= BaselineLines,
            $"AgentController partials total {total} lines, over the {BaselineLines}-line budget. " +
            "Put new logic in Services/ instead, or lower the budget if this change removed lines.");
    }
}
