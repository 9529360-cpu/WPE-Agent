using WpeAgent.RuntimeContracts;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

/// <summary>Read-only, bounded projection of the configured authorization mode and approval queue.</summary>
public sealed class RuntimeAuthorizationStateStore
{
    public const int PendingLimit=50;
    public static readonly TimeSpan StaleAfter=TimeSpan.FromSeconds(30);
    private readonly AgentSettingsStore _settings;
    private readonly AgentSqliteStore _database;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate=new();
    private RuntimeAuthorizationState _current=RuntimeAuthorizationState.Unsupported("Trading authorization state is not connected.");

    public RuntimeAuthorizationStateStore(AgentSettingsStore? settings=null,AgentSqliteStore? database=null):this(settings,database,null){}
    internal RuntimeAuthorizationStateStore(AgentSettingsStore? settings,AgentSqliteStore? database,Func<DateTimeOffset>? utcNow)
    {
        _settings=settings??new AgentSettingsStore();_database=database??new AgentSqliteStore();_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
        RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public RuntimeAuthorizationState Read(){lock(_gate)return _current;}

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var settings=_settings.Load();
            if(_settings.LastLoadDiagnostic is not null){lock(_gate)_current=RuntimeAuthorizationState.Error("Trading authorization settings could not be read.");return;}
            var now=_utcNow().ToUniversalTime();var automatic=new List<RuntimeAutomaticExecutionSummaryV1>();
            foreach(var status in Enum.GetValues<AutomaticExecutionQueueStatus>())automatic.AddRange((await _database.GetAutomaticExecutionQueueAsync(status,PendingLimit,ct)).Select(row=>new RuntimeAutomaticExecutionSummaryV1(SensitiveDataRedactor.MaskIdentifier(row.ExecutionId,"execution"),row.Status.ToString(),row.LastCode,row.AttemptCount,row.UpdatedAtUtc)));
            var historical=settings.AuthorizationMode==TradingAuthorizationMode.Review?(await _database.GetPendingTradingApprovalSummariesAsync(PendingLimit,0,ct)).Select(row=>ToRuntime(row,now)).ToArray():[];
            lock(_gate)_current=new(RuntimeCollectionState.Available,settings.AuthorizationMode.ToString(),historical,automatic.OrderByDescending(x=>x.UpdatedAtUtc).Take(PendingLimit).ToArray(),now,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(_gate)_current=RuntimeAuthorizationState.Error($"Trading approval projection failed: {ex.GetType().Name}.");}
    }

    public void MarkUnsupported(string message){lock(_gate)_current=RuntimeAuthorizationState.Unsupported(message);}
    public void PublishError(string message){lock(_gate)_current=RuntimeAuthorizationState.Error(message);}

    private static RuntimeApprovalSummaryV1 ToRuntime(PersistedTradingApprovalSummary row,DateTimeOffset now)
    {
        var decision=row.Decision;var risk=row.Risk;var symbol=SafeSymbol(decision?.Instrument);var side=decision is null?null:Side(decision.Action);
        var artifactAvailable=decision is not null&&risk is not null&&symbol is not null&&side is not null&&Enum.IsDefined(decision.OrderType)&&risk.PlannedQuantity>0&&decision.EntryPrice>0&&decision.StopLossPrice>0&&decision.TakeProfitPrice>0;
        var status=row.RevokedAtUtc is not null?"Revoked":row.ExpiresAtUtc<=now?"Expired":artifactAvailable?"Pending":"ArtifactUnavailable";
        var reason=status switch{"Revoked"=>"approval.revoked","Expired"=>"approval.expired","ArtifactUnavailable"=>"approval.artifact-unavailable",_=>"approval.historical-read-only"};
        return new(
            SensitiveDataRedactor.MaskIdentifier(row.RequestId,"approval"),
            artifactAvailable?symbol:null,
            artifactAvailable?side:null,
            artifactAvailable?decision!.OrderType.ToString():null,
            artifactAvailable?risk!.PlannedQuantity:null,
            artifactAvailable?decision!.EntryPrice:null,
            artifactAvailable?decision!.StopLossPrice:null,
            artifactAvailable?decision!.TakeProfitPrice:null,
            row.CreatedAtUtc,row.ExpiresAtUtc,status,reason);
    }

    private static string? Side(DecisionAction action)=>action switch
    {
        DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReduceLong or DecisionAction.CloseLong or DecisionAction.ReverseToLong=>"Long",
        DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReduceShort or DecisionAction.CloseShort or DecisionAction.ReverseToShort=>"Short",
        _=>null
    };
    private static string? SafeSymbol(string? value)
    {
        value=value?.Trim().ToUpperInvariant();
        return value is not null&&value.Length is >=2 and <=24&&value.All(char.IsLetterOrDigit)?value:null;
    }
}

public sealed record RuntimeAuthorizationState(RuntimeCollectionState State,string? Mode,IReadOnlyList<RuntimeApprovalSummaryV1> Pending,IReadOnlyList<RuntimeAutomaticExecutionSummaryV1> Automatic,DateTimeOffset? UpdatedAt,string? Message)
{
    public RuntimeAuthorizationState(RuntimeCollectionState state,string? mode,IReadOnlyList<RuntimeApprovalSummaryV1> pending,DateTimeOffset? updatedAt,string? message):this(state,mode,pending,[],updatedAt,message){}
    public static RuntimeAuthorizationState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,null,[],[],null,message);
    public static RuntimeAuthorizationState Error(string message)=>new(RuntimeCollectionState.Error,null,[],[],DateTimeOffset.UtcNow,message);
}
