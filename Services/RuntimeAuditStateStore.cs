using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

/// <summary>Read-only, provider-neutral projection of persisted audit records.</summary>
public sealed class RuntimeAuditStateStore
{
    public const int TimelineLimit = 100;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private readonly AgentSqliteStore _database;
    private readonly object _gate = new();
    private RuntimeAuditState _current = RuntimeAuditState.Unsupported("Audit persistence is not connected.");

    public RuntimeAuditStateStore(AgentSqliteStore database)
    {
        _database = database;
        RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public RuntimeAuditState Read(){lock(_gate)return _current;}

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var rows=await _database.GetRecentAuditEventsAsync(TimelineLimit,ct);
            var items=rows.Select(x=>new RuntimeAuditEventV1(x.Id,x.TimeUtc,x.Category,x.Source,x.CorrelationId,x.Status,x.Summary)).ToArray();
            lock(_gate)_current=new(RuntimeCollectionState.Available,items,DateTimeOffset.UtcNow,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(_gate)_current=RuntimeAuditState.Error($"Audit persistence read failed: {ex.GetType().Name}.");}
    }

    public void MarkUnsupported(string message){lock(_gate)_current=RuntimeAuditState.Unsupported(message);}
    public void PublishError(string message){lock(_gate)_current=RuntimeAuditState.Error(message);}
}

public sealed record RuntimeAuditState(RuntimeCollectionState State,IReadOnlyList<RuntimeAuditEventV1> Items,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeAuditState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,[],null,message);
    public static RuntimeAuditState Error(string message)=>new(RuntimeCollectionState.Error,[],DateTimeOffset.UtcNow,message);
}
