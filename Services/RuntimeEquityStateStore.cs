using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

/// <summary>Read-only projection of real provider account observations persisted by AutoTradingAgent.</summary>
public sealed class RuntimeEquityStateStore
{
    public static readonly TimeSpan Window=TimeSpan.FromDays(7);
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(35);
    private readonly AgentSqliteStore _database;private readonly object _gate=new();
    private RuntimeEquityState _current=RuntimeEquityState.Unsupported("Equity persistence is not connected.");
    public RuntimeEquityStateStore(AgentSqliteStore database){_database=database;RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();}
    public RuntimeEquityState Read(){lock(_gate)return _current;}
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var rows=await _database.GetRecentEquitySnapshotsAsync(DateTime.UtcNow-Window,500,ct);
            var items=rows.Select(x=>new RuntimeEquityPointV1(x.ObservedAtUtc,x.Equity,x.AvailableBalance,x.Environment,x.ProviderId)).ToArray();
            var updatedAt=rows.Count==0?DateTimeOffset.UtcNow:new DateTimeOffset(rows[^1].ObservedAtUtc.ToUniversalTime());
            lock(_gate)_current=new(RuntimeCollectionState.Available,items,updatedAt,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(_gate)_current=RuntimeEquityState.Error($"Equity persistence read failed: {ex.Message}");}
    }
    public void MarkUnsupported(string message){lock(_gate)_current=RuntimeEquityState.Unsupported(message);}
    public void PublishError(string message){lock(_gate)_current=RuntimeEquityState.Error(message);}
}

public sealed record RuntimeEquityState(RuntimeCollectionState State,IReadOnlyList<RuntimeEquityPointV1> Items,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeEquityState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,[],null,message);
    public static RuntimeEquityState Error(string message)=>new(RuntimeCollectionState.Error,[],DateTimeOffset.UtcNow,message);
}
