using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

/// <summary>Non-sensitive projection of the latest host-owned access readiness check.</summary>
public sealed class RuntimeConnectionStateStore
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(30);
    private readonly object _gate=new();
    private RuntimeConnectionState _current=RuntimeConnectionState.Unsupported("Access readiness has not been checked.");
    public RuntimeConnectionState Read(){lock(_gate)return _current;}
    public void Publish(AccessReadinessReport report,AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(report);ArgumentNullException.ThrowIfNull(settings);
        var profile=settings.Exchanges.FirstOrDefault(x=>x.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase))
            ??settings.Exchanges.FirstOrDefault(x=>x.Enabled&&x.ExecutionEnabled);
        if(profile is null){lock(_gate)_current=RuntimeConnectionState.Unsupported("No execution connection is selected.");return;}
        bool? Permission(string key)=>report.Checks.Any(x=>x.Key==key)?report.Checks.First(x=>x.Key==key).Passed:null;
        var withdrawalSafe=Permission("withdraw_permission");
        var value=new RuntimeConnectionStatusV1(
            profile.Id,profile.DisplayName,profile.ProviderId,profile.IsTestnet?"Testnet":"Mainnet",
            report.Checks.Any(x=>x.Key=="credentials"&&x.Passed)?"configured":"missing",
            report.Checks.Any(x=>x.Key=="provider"&&x.Passed)?"ready":"unavailable",
            report.CheckedAtUtc.ToUniversalTime(),report.Ready,
            report.Checks.Any(x=>x.Key=="exchange"&&x.Passed),Permission("trade_permission"),
            withdrawalSafe is null?null:!withdrawalSafe.Value,
            withdrawalSafe==false?"Withdrawal permission is enabled; disable it for this trading key.":null);
        lock(_gate)_current=new(RuntimeCollectionState.Available,value,new DateTimeOffset(report.CheckedAtUtc.ToUniversalTime()),null);
    }
    public void PublishError(string message){lock(_gate)_current=RuntimeConnectionState.Error(message);}
}

public sealed record RuntimeConnectionState(RuntimeCollectionState State,RuntimeConnectionStatusV1? Value,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeConnectionState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,null,null,message);
    public static RuntimeConnectionState Error(string message)=>new(RuntimeCollectionState.Error,null,DateTimeOffset.UtcNow,message);
}
