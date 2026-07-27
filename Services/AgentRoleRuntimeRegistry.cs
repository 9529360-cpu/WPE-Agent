using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

public sealed class AgentRoleRuntimeRegistry
{
    private static readonly string[] Roles = ["market", "research", "strategy", "risk", "execution", "recovery", "audit"];
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
        lock (gate) return states.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
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
}
