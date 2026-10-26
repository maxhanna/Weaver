using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the expiry of the process-wide LLM connectivity cache. <c>_nextConnectivityCheck</c>
/// holds the instant the cached verdict expires; the verdict must be reused only BEFORE that
/// instant. A previous guard computed <c>utcNow - expiry &lt; 5 min</c>; with expiry set to
/// now + 5 min that stayed true until now + 10 min, so every verdict was reused for twice
/// its intended lifetime (a cached "down" verdict blocked runs for 10 minutes, not 5).
/// </summary>
public class ConnectivityCacheTests
{
    [Fact]
    public void Cache_IsFresh_BeforeExpiry()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(AgentController.IsConnectivityCacheFresh(now.AddMinutes(5), now));
        Assert.True(AgentController.IsConnectivityCacheFresh(now.AddMinutes(5), now.AddMinutes(4)));
    }

    [Fact]
    public void Cache_IsStale_AfterExpiry()
    {
        // The regression: five minutes past expiry must re-probe, not reuse the old verdict.
        var expiry = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(AgentController.IsConnectivityCacheFresh(expiry, expiry.AddMinutes(1)));
        Assert.False(AgentController.IsConnectivityCacheFresh(expiry, expiry.AddHours(3)));
    }

    [Fact]
    public void Cache_IsStale_WhenNeverProbed()
    {
        Assert.False(AgentController.IsConnectivityCacheFresh(DateTime.MinValue, DateTime.UtcNow));
    }
}
