using System.Collections.Concurrent;
using System.Text.Json;
using 币安量化机器人.Core.Runtime;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Infrastructure.Runtime;

public sealed class AgentRuntimeSupervisor : IAsyncDisposable
{
    private const string LeaseName = "autonomous-trading-kernel";
    private readonly AgentSqliteStore _database;
    private readonly IAgentEventBus _events;
    private readonly ConcurrentDictionary<string, WorkflowNode> _nodes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _instanceId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    private IDisposable? _eventPersistence;
    private Task? _heartbeat;
    private IReadOnlyList<WorkflowRecovery> _interrupted = Array.Empty<WorkflowRecovery>();
    private long _eventSequence;

    public AgentRuntimeSupervisor(AgentSqliteStore database, IAgentEventBus events)
    {
        _database = database;
        _events = events;
        RunId = Guid.NewGuid().ToString("N");
    }

    public string RunId { get; }
    public RuntimeHealth Health { get; private set; } = new("", "", DateTime.MinValue, "NOT_STARTED", 0);
    public event Action<RuntimeHealth>? HealthChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await _database.TryAcquireRuntimeLeaseAsync(LeaseName, _instanceId, TimeSpan.FromSeconds(30), cancellationToken))
            throw new InvalidOperationException("Another WPE trading kernel owns the active runtime lease.");

        _eventPersistence = _events.Subscribe(async (value, ct) =>
        {
            await _database.RecordRuntimeEventAsync(value, ct);
            Interlocked.Exchange(ref _eventSequence, value.Sequence);
        });
        _interrupted = await _database.GetInterruptedWorkflowsAsync(cancellationToken);
        UpdateHealth(_interrupted.Count == 0 ? "CLEAN_START" : $"RECOVERY_PENDING:{_interrupted.Count}");
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token), CancellationToken.None);
        await PublishAsync("runtime.started", new { RunId, InstanceId = _instanceId, Interrupted = _interrupted.Count }, cancellationToken);
    }

    public async Task RecoverInterruptedAsync(Func<CancellationToken, Task<string>> reconcile, CancellationToken cancellationToken)
    {
        if (_interrupted.Count == 0) return;
        UpdateHealth("RECONCILING");
        var result = await reconcile(cancellationToken);
        foreach (var item in _interrupted)
        {
            var action = item.LastNode == WorkflowNode.Execution
                ? "RECONCILED_EXECUTION_THEN_RESTART_OBSERVATION"
                : "RESTART_OBSERVATION_FROM_FRESH_MARKET_STATE";
            await _database.MarkWorkflowRecoveredAsync(item, action, cancellationToken);
            await PublishAsync("workflow.recovered", new { item.RunId, item.CycleId, item.LastNode, action, result }, cancellationToken);
        }
        _interrupted = Array.Empty<WorkflowRecovery>();
        UpdateHealth("RECOVERED");
    }

    public async Task BeginCycleAsync(string cycleId, object state, CancellationToken cancellationToken)
    {
        _nodes[cycleId] = WorkflowNode.Observation;
        var json = JsonSerializer.Serialize(state);
        await _database.BeginWorkflowRunAsync(RunId, cycleId, json, cancellationToken);
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, WorkflowNode.Observation, CheckpointPhase.Entered, json, DateTime.UtcNow), cancellationToken);
        await PublishAsync("workflow.cycle.started", new { RunId, CycleId = cycleId, Node = WorkflowNode.Observation }, cancellationToken, cycleId);
    }

    public async Task TransitionAsync(string cycleId, WorkflowNode next, object state, CancellationToken cancellationToken)
    {
        if (!_nodes.TryGetValue(cycleId, out var current)) throw new InvalidOperationException($"Workflow cycle {cycleId} was not started.");
        TradingWorkflowGraph.EnsureTransition(current, next);
        var json = JsonSerializer.Serialize(state);
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, current, CheckpointPhase.Completed, json, DateTime.UtcNow), cancellationToken);
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, next, CheckpointPhase.Entered, json, DateTime.UtcNow), cancellationToken);
        _nodes[cycleId] = next;
        await PublishAsync("workflow.node.entered", new { RunId, CycleId = cycleId, Previous = current, Node = next }, cancellationToken, cycleId);
    }

    public async Task CompleteCycleAsync(string cycleId, object state, CancellationToken cancellationToken)
    {
        if (!_nodes.TryGetValue(cycleId, out var current)) return;
        if (current is not WorkflowNode.Reflection)
        {
            TradingWorkflowGraph.EnsureTransition(current, WorkflowNode.Reflection);
            await TransitionAsync(cycleId, WorkflowNode.Reflection, state, cancellationToken);
            current = WorkflowNode.Reflection;
        }
        var json = JsonSerializer.Serialize(state);
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, current, CheckpointPhase.Completed, json, DateTime.UtcNow), cancellationToken);
        await _database.CompleteWorkflowRunAsync(RunId, cycleId, "COMPLETED", null, cancellationToken);
        _nodes.TryRemove(cycleId, out _);
        await PublishAsync("workflow.cycle.completed", new { RunId, CycleId = cycleId }, cancellationToken, cycleId);
    }

    public async Task FailCycleAsync(string cycleId, Exception exception, CancellationToken cancellationToken)
    {
        var current = _nodes.GetValueOrDefault(cycleId, WorkflowNode.Observation);
        var json = JsonSerializer.Serialize(new { exception.Message, Type = exception.GetType().Name });
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, current, CheckpointPhase.Failed, json, DateTime.UtcNow), cancellationToken);
        await _database.CompleteWorkflowRunAsync(RunId, cycleId, "FAILED", exception.ToString(), cancellationToken);
        _nodes.TryRemove(cycleId, out _);
        await PublishAsync("workflow.cycle.failed", new { RunId, CycleId = cycleId, Node = current, exception.Message }, cancellationToken, cycleId);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var renewed = await _database.RenewRuntimeLeaseAsync(LeaseName, _instanceId, TimeSpan.FromSeconds(30), cancellationToken);
                UpdateHealth(renewed ? Health.RecoveryStatus : "LEASE_LOST");
                await PublishAsync("runtime.heartbeat", new { RunId, InstanceId = _instanceId, LeaseRenewed = renewed }, cancellationToken);
                if (!renewed) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private Task PublishAsync(string type, object payload, CancellationToken ct, string? correlationId = null)
        => _events.PublishAsync(AgentRuntimeEvent.Create(correlationId ?? RunId, type, nameof(AgentRuntimeSupervisor), payload), ct).AsTask();

    private void UpdateHealth(string recoveryStatus)
    {
        Health = new(RunId, _instanceId, DateTime.UtcNow, recoveryStatus, Interlocked.Read(ref _eventSequence));
        HealthChanged?.Invoke(Health);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_heartbeat is not null) try { await _heartbeat; } catch (OperationCanceledException) { }
        try { await _database.ReleaseRuntimeLeaseAsync(LeaseName, _instanceId, CancellationToken.None); } catch { }
        _eventPersistence?.Dispose();
        await _events.DisposeAsync();
        _shutdown.Dispose();
    }
}
