using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

public sealed class RuntimeStrategyRegistryStateStore
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);
    private readonly AgentSqliteStore _database;private readonly object _gate=new();
    private RuntimeStrategyRegistryState _current=RuntimeStrategyRegistryState.Unsupported("Strategy registry is not connected.");
    public RuntimeStrategyRegistryStateStore(AgentSqliteStore database){_database=database;RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();}
    public RuntimeStrategyRegistryState Read(){lock(_gate)return _current;}
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var profiles=await _database.GetStrategiesAsync(ct);var events=await _database.GetRecentStrategyLifecycleEventsAsync(200,ct);
            var items=profiles.OrderBy(x=>x.Symbol,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Id,StringComparer.OrdinalIgnoreCase).Select(x=>new RuntimeStrategyProfileV1(x.Id,x.Version,x.Symbol,x.Family.ToString(),x.Lifecycle.ToString(),x.LastReason,x.StateChangedAtUtc?.ToUniversalTime(),x.QualityScore)).ToArray();
            var lifecycle=events.Select(x=>new RuntimeStrategyLifecycleEventV1(x.Id,x.StrategyId,x.FromState,x.ToState,x.OccurredAtUtc,x.Reason)).ToArray();
            lock(_gate)_current=new(RuntimeCollectionState.Available,items,lifecycle,DateTimeOffset.UtcNow,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(_gate)_current=RuntimeStrategyRegistryState.Error($"Strategy persistence read failed: {ex.Message}");}
    }
    public void PublishError(string message){lock(_gate)_current=RuntimeStrategyRegistryState.Error(message);}
}
public sealed record RuntimeStrategyRegistryState(RuntimeCollectionState State,IReadOnlyList<RuntimeStrategyProfileV1> Profiles,IReadOnlyList<RuntimeStrategyLifecycleEventV1> Events,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeStrategyRegistryState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,[],[],null,message);
    public static RuntimeStrategyRegistryState Error(string message)=>new(RuntimeCollectionState.Error,[],[],DateTimeOffset.UtcNow,message);
}
