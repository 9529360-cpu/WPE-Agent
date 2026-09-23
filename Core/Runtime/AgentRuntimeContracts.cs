using System.Text.Json;

namespace 币安量化机器人.Core.Runtime;

public enum WorkflowNode
{
    Boot,
    Recovery,
    Observation,
    Research,
    PositionManagement,
    SafetyExecution,
    Aggregation,
    Planner,
    Critic,
    Risk,
    Execution,
    Audit,
    Waiting,
    Paused,
    Completed,
    Failed
}

public enum CheckpointPhase { Entered, Completed, Failed, Recovered }

public sealed record AgentRuntimeEvent(
    string EventId,
    string CorrelationId,
    string? CausationId,
    string EventType,
    string Source,
    string PayloadJson,
    DateTime OccurredAtUtc,
    long Sequence = 0)
{
    public static AgentRuntimeEvent Create<T>(string correlationId, string eventType, string source, T payload, string? causationId = null)
        => new(Guid.NewGuid().ToString("N"), correlationId, causationId, eventType, source,
            JsonSerializer.Serialize(payload), DateTime.UtcNow);
}

public sealed record WorkflowCheckpoint(
    string RunId,
    string CycleId,
    WorkflowNode Node,
    CheckpointPhase Phase,
    string StateJson,
    DateTime CreatedAtUtc,
    int Attempt = 1);

public sealed record WorkflowRecovery(
    string RunId,
    string CycleId,
    WorkflowNode LastNode,
    CheckpointPhase LastPhase,
    string StateJson,
    DateTime UpdatedAtUtc,
    string RecoveryAction);

public sealed record RuntimeHealth(
    string RunId,
    string InstanceId,
    DateTime HeartbeatAtUtc,
    string RecoveryStatus,
    long EventSequence);

public interface IAgentEventBus : IAsyncDisposable
{
    IDisposable Subscribe(Func<AgentRuntimeEvent, CancellationToken, ValueTask> handler);
    ValueTask PublishAsync(AgentRuntimeEvent value, CancellationToken cancellationToken = default);
}

public static class TradingWorkflowGraph
{
    private static readonly IReadOnlyDictionary<WorkflowNode, IReadOnlySet<WorkflowNode>> Edges =
        new Dictionary<WorkflowNode, IReadOnlySet<WorkflowNode>>
        {
            [WorkflowNode.Boot] = Set(WorkflowNode.Recovery),
            [WorkflowNode.Recovery] = Set(WorkflowNode.Observation, WorkflowNode.Failed),
            [WorkflowNode.Observation] = Set(WorkflowNode.Research, WorkflowNode.Failed),
            [WorkflowNode.Research] = Set(WorkflowNode.PositionManagement, WorkflowNode.Failed),
            [WorkflowNode.PositionManagement] = Set(WorkflowNode.SafetyExecution, WorkflowNode.Aggregation, WorkflowNode.Failed),
            [WorkflowNode.SafetyExecution] = Set(WorkflowNode.Aggregation, WorkflowNode.Failed),
            [WorkflowNode.Aggregation] = Set(WorkflowNode.Planner, WorkflowNode.Failed),
            [WorkflowNode.Planner] = Set(WorkflowNode.Critic, WorkflowNode.Failed),
            [WorkflowNode.Critic] = Set(WorkflowNode.Risk, WorkflowNode.Failed),
            [WorkflowNode.Risk] = Set(WorkflowNode.Execution, WorkflowNode.Audit, WorkflowNode.Failed),
            [WorkflowNode.Execution] = Set(WorkflowNode.Audit, WorkflowNode.Failed),
            [WorkflowNode.Audit] = Set(WorkflowNode.Waiting, WorkflowNode.Completed, WorkflowNode.Failed),
            [WorkflowNode.Waiting] = Set(WorkflowNode.Observation, WorkflowNode.Paused, WorkflowNode.Completed),
            [WorkflowNode.Paused] = Set(WorkflowNode.Observation, WorkflowNode.Completed),
            [WorkflowNode.Failed] = Set(WorkflowNode.Recovery, WorkflowNode.Completed),
            [WorkflowNode.Completed] = Set()
        };

    public static bool CanTransition(WorkflowNode from, WorkflowNode to)
        => from == to || (Edges.TryGetValue(from, out var targets) && targets.Contains(to));

    public static void EnsureTransition(WorkflowNode from, WorkflowNode to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid workflow transition: {from} -> {to}");
    }

    public static WorkflowNode FromUiNode(string node) => node.ToUpperInvariant() switch
    {
        "OBSERVATION" => WorkflowNode.Observation,
        "PLANNER" => WorkflowNode.Planner,
        "CRITIC" or "REVIEWER" => WorkflowNode.Critic,
        "RISK" => WorkflowNode.Risk,
        "EXECUTION" => WorkflowNode.Execution,
        "REFLECTION" => WorkflowNode.Reflection,
        "SYSTEM" => WorkflowNode.Paused,
        _ => WorkflowNode.Observation
    };

    private static IReadOnlySet<WorkflowNode> Set(params WorkflowNode[] nodes) => nodes.ToHashSet();
}
