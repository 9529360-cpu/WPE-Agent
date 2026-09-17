using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

public sealed class AgentRoleRuntimeRegistry
{
    private static readonly string[] Roles = ["market", "research", "strategy", "risk", "execution", "recovery", "audit"];
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, RuntimeAgentOperationV1> states = new(StringComparer.OrdinalIgnoreCase);

    public static AgentRoleRuntimeRegistry Shared { get; } = new();

    public void Publish(string roleId, string status, string activity, DateTime? occurredAtUtc = null)
    {
        if (!Roles.Contains(roleId, StringComparer.OrdinalIgnoreCase)) throw new ArgumentOutOfRangeException(nameof(roleId));
        if (status is not ("running" or "monitoring" or "waiting" or "degraded" or "stopped")) throw new ArgumentOutOfRangeException(nameof(status));
        var at = (occurredAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        lock (gate) states[roleId] = new(roleId, status, at, activity, "Local Only");
    }

    public IReadOnlyDictionary<string, RuntimeAgentOperationV1> Read()
    {
        var now = DateTime.UtcNow;
        lock (gate) return states.ToDictionary(x => x.Key, x => ProjectLiveness(x.Value, now), StringComparer.OrdinalIgnoreCase);
    }

    public void StopAll(string activity)
    {
        PublishAll("stopped", activity);
    }

    public void DegradeAll(string activity) => PublishAll("degraded", activity);

    private void PublishAll(string status, string activity)
    {
        var now = DateTime.UtcNow;
        foreach (var role in Roles) Publish(role, status, activity, now);
    }

    private static RuntimeAgentOperationV1 ProjectLiveness(RuntimeAgentOperationV1 state, DateTime nowUtc)
    {
        if (state.Status is not ("running" or "monitoring" or "waiting") || state.LastActivityAtUtc is null) return state;
        var lastActivity = state.LastActivityAtUtc.Value.ToUniversalTime();
        if (lastActivity > nowUtc.AddMinutes(1))
            return state with { Status = "degraded", Activity = "Role activity time is invalid; worker liveness cannot be proven." };
        if (nowUtc - lastActivity > StaleAfter)
            return state with { Status = "degraded", Activity = "Role activity is stale; worker liveness cannot be proven." };
        return state;
    }
}
