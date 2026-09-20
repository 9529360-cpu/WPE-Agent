using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Core.Runtime;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services;

namespace 币安量化机器人.Infrastructure.Runtime;

public sealed class AgentRuntimeSupervisor : IAsyncDisposable
{
    private const string LeaseName = "autonomous-trading-kernel";
    private static readonly TimeSpan DefaultRuntimeLeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRuntimeLeaseAcquireTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultRuntimeLeaseRetryInterval = TimeSpan.FromSeconds(1);
    private readonly AgentSqliteStore _database;
    private readonly IAgentEventBus _events;
    private readonly ConcurrentDictionary<string, WorkflowNode> _nodes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _instanceId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly string _processLeasePath;
    private readonly TimeSpan _runtimeLeaseTtl;
    private readonly TimeSpan _runtimeLeaseAcquireTimeout;
    private readonly TimeSpan _runtimeLeaseRetryInterval;
    private IDisposable? _eventPersistence;
    private FileStream? _processLease;
    private bool _databaseLeaseAcquired;
    private bool _leaseLost;
    private Task? _heartbeat;
    private IReadOnlyList<WorkflowRecovery> _interrupted = Array.Empty<WorkflowRecovery>();
    private long _eventSequence;

    public AgentRuntimeSupervisor(AgentSqliteStore database, IAgentEventBus events)
        : this(database, events, AppDataPaths.RuntimeFile(LeaseName + ".lock"))
    {
    }

    internal AgentRuntimeSupervisor(AgentSqliteStore database, IAgentEventBus events, string processLeasePath)
        : this(database, events, processLeasePath, DefaultRuntimeLeaseTtl, DefaultRuntimeLeaseAcquireTimeout, DefaultRuntimeLeaseRetryInterval)
    {
    }

    internal AgentRuntimeSupervisor(
        AgentSqliteStore database,
        IAgentEventBus events,
        string processLeasePath,
        TimeSpan runtimeLeaseTtl,
        TimeSpan runtimeLeaseAcquireTimeout,
        TimeSpan runtimeLeaseRetryInterval)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        if (string.IsNullOrWhiteSpace(processLeasePath)) throw new ArgumentException("Runtime process lease path is required.", nameof(processLeasePath));
        if (runtimeLeaseTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(runtimeLeaseTtl));
        if (runtimeLeaseAcquireTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(runtimeLeaseAcquireTimeout));
        if (runtimeLeaseRetryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(runtimeLeaseRetryInterval));
        _processLeasePath = Path.GetFullPath(processLeasePath);
        _runtimeLeaseTtl = runtimeLeaseTtl;
        _runtimeLeaseAcquireTimeout = runtimeLeaseAcquireTimeout;
        _runtimeLeaseRetryInterval = runtimeLeaseRetryInterval;
        RunId = Guid.NewGuid().ToString("N");
    }

    public string RunId { get; }
    public RuntimeHealth Health { get; private set; } = new("", "", DateTime.MinValue, "NOT_STARTED", 0);
    public event Action<RuntimeHealth>? HealthChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_processLease is not null) throw new InvalidOperationException("The WPE trading kernel runtime is already started.");
        _processLease = AcquireProcessLease();
        try
        {
            if (!await TryAcquireDatabaseLeaseAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Another WPE trading kernel owns the active runtime lease.");
            _databaseLeaseAcquired = true;
            _leaseLost = false;

            _eventPersistence = _events.Subscribe(async (value, ct) =>
            {
                await _database.RecordRuntimeEventAsync(value, ct);
                Interlocked.Exchange(ref _eventSequence, value.Sequence);
            });
            _interrupted = await _database.GetInterruptedWorkflowsAsync(cancellationToken);
            UpdateHealth(_interrupted.Count == 0 ? "CLEAN_START" : $"RECOVERY_PENDING:{_interrupted.Count}");
            await PublishAsync("runtime.started", new { RunId, InstanceId = _instanceId, Interrupted = _interrupted.Count }, cancellationToken);
            _heartbeat = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token), CancellationToken.None);
        }
        catch
        {
            _eventPersistence?.Dispose();
            _eventPersistence = null;
            if (_databaseLeaseAcquired)
            {
                try { await _database.ReleaseRuntimeLeaseAsync(LeaseName, _instanceId, CancellationToken.None); } catch { }
                _databaseLeaseAcquired = false;
            }
            ReleaseProcessLease();
            throw;
        }
    }

    public async Task RecoverInterruptedAsync(Func<CancellationToken, Task<string>> reconcile, CancellationToken cancellationToken)
    {
        EnsureRuntimeAuthority();
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
        EnsureRuntimeAuthority();
        _nodes[cycleId] = WorkflowNode.Observation;
        var json = JsonSerializer.Serialize(state);
        await _database.BeginWorkflowRunAsync(RunId, cycleId, json, cancellationToken);
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, WorkflowNode.Observation, CheckpointPhase.Entered, json, DateTime.UtcNow), cancellationToken);
        await PublishAsync("workflow.cycle.started", new { RunId, CycleId = cycleId, Node = WorkflowNode.Observation }, cancellationToken, cycleId);
    }

    public async Task TransitionAsync(string cycleId, WorkflowNode next, object state, CancellationToken cancellationToken)
    {
        EnsureRuntimeAuthority();
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
        var safeMessage=SensitiveDataRedactor.ForLog(exception.Message);
        var json = JsonSerializer.Serialize(new { Message=safeMessage, Type = exception.GetType().Name });
        await _database.SaveWorkflowCheckpointAsync(new(RunId, cycleId, current, CheckpointPhase.Failed, json, DateTime.UtcNow), cancellationToken);
        await _database.CompleteWorkflowRunAsync(RunId, cycleId, "FAILED", safeMessage, cancellationToken);
        _nodes.TryRemove(cycleId, out _);
        await PublishAsync("workflow.cycle.failed", new { RunId, CycleId = cycleId, Node = current, Message=safeMessage }, cancellationToken, cycleId);
    }

    private async Task<bool> TryAcquireDatabaseLeaseAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _runtimeLeaseAcquireTimeout;
        while (true)
        {
            if (await _database.TryAcquireRuntimeLeaseAsync(LeaseName, _instanceId, _runtimeLeaseTtl, cancellationToken).ConfigureAwait(false))
                return true;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            var delay = remaining < _runtimeLeaseRetryInterval ? remaining : _runtimeLeaseRetryInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var renewed = await _database.RenewRuntimeLeaseAsync(LeaseName, _instanceId, _runtimeLeaseTtl, cancellationToken);
                if (!renewed)
                {
                    await FailClosedLeaseAsync("runtime.lease-renewal-rejected");
                    break;
                }
                UpdateHealth(Health.RecoveryStatus);
                await PublishAsync("runtime.heartbeat", new { RunId, InstanceId = _instanceId, LeaseRenewed = true }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            await FailClosedLeaseAsync("runtime.lease-renewal-failed");
        }
    }

    private FileStream AcquireProcessLease()
    {
        var directory = Path.GetDirectoryName(_processLeasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        try
        {
            var stream = new FileStream(_processLeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 256, FileOptions.WriteThrough);
            var identity = Encoding.UTF8.GetBytes(_instanceId);
            stream.SetLength(0);
            stream.Write(identity);
            stream.Flush(true);
            stream.Position = 0;
            return stream;
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The WPE trading kernel process lease is unavailable; another instance may already be active.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The WPE trading kernel process lease is unavailable; another instance may already be active.");
        }
    }

    private void EnsureRuntimeAuthority()
    {
        if (_processLease is null || !_databaseLeaseAcquired || _leaseLost)
            throw new InvalidOperationException("The WPE trading kernel no longer owns runtime authority.");
    }

    private async Task FailClosedLeaseAsync(string reasonCode)
    {
        if (_leaseLost) return;
        _leaseLost = true;
        UpdateHealth("LEASE_LOST");
        AutoTradingAgent.Pause();
        try
        {
            await PublishAsync("runtime.lease-lost", new { RunId, InstanceId = _instanceId, ReasonCode = reasonCode }, CancellationToken.None);
        }
        catch { }
    }

    private Task PublishAsync(string type, object payload, CancellationToken ct, string? correlationId = null)
        => _events.PublishAsync(AgentRuntimeEvent.Create(correlationId ?? RunId, type, nameof(AgentRuntimeSupervisor), payload), ct).AsTask();

    private void UpdateHealth(string recoveryStatus)
    {
        Health = new(RunId, _instanceId, DateTime.UtcNow, recoveryStatus, Interlocked.Read(ref _eventSequence));
        HealthChanged?.Invoke(Health);
    }

    private void ReleaseProcessLease()
    {
        var lease = Interlocked.Exchange(ref _processLease, null);
        if (lease is null) return;
        try { lease.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_heartbeat is not null) try { await _heartbeat; } catch (OperationCanceledException) { }
        if (_databaseLeaseAcquired)
        {
            try { await _database.ReleaseRuntimeLeaseAsync(LeaseName, _instanceId, CancellationToken.None); } catch { }
            _databaseLeaseAcquired = false;
        }
        _eventPersistence?.Dispose();
        ReleaseProcessLease();
        await _events.DisposeAsync();
        _shutdown.Dispose();
    }
}
