using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

/// <summary>Thread-safe, read-only projection of persisted deterministic backtest runs.</summary>
public sealed class RuntimeBacktestStateStore
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    private readonly AgentSqliteStore _database;
    private readonly object _gate = new();
    private RuntimeBacktestState _current = RuntimeBacktestState.Unsupported("Backtest persistence is not connected.");

    public RuntimeBacktestStateStore(AgentSqliteStore database)
    {
        _database = database;
        RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public RuntimeBacktestState Read(){lock(_gate)return _current;}

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var rows=await _database.GetRecentBacktestRunsAsync(100,ct);
            var items=rows.Select(x=>new RuntimeBacktestV1(x.Id,x.StrategyId,x.StrategyVersion,x.Symbol,x.CompletedAtUtc,x.Status,x.CoverageDays,x.Trades,x.OutOfSampleReturn,x.MaxDrawdown,x.Sharpe)).ToArray();
            var updatedAt=rows.Count==0?DateTimeOffset.UtcNow:new DateTimeOffset(rows.Max(x=>x.CompletedAtUtc.ToUniversalTime()));
            lock(_gate)_current=new(RuntimeCollectionState.Available,items,updatedAt,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(_gate)_current=RuntimeBacktestState.Error($"Backtest persistence read failed: {ex.Message}");}
    }

    public void MarkUnsupported(string message){lock(_gate)_current=RuntimeBacktestState.Unsupported(message);}
    public void PublishError(string message){lock(_gate)_current=RuntimeBacktestState.Error(message);}
}

public sealed record RuntimeBacktestState(RuntimeCollectionState State,IReadOnlyList<RuntimeBacktestV1> Items,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeBacktestState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,[],null,message);
    public static RuntimeBacktestState Error(string message)=>new(RuntimeCollectionState.Error,[],DateTimeOffset.UtcNow,message);
}
