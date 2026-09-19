using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

public interface ITradingMutationExecutor
{
    bool IsTestnet { get; }
    string ProviderId => "unknown";
    string AccountId => "unknown";
    Task<string> ExecutePlanAsync(string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct);
    Task<string> ExecuteReduceOnlyRecoveryAsync(string correlationId,ExecutionIntent intent,CancellationToken ct)=>
        throw new NotSupportedException("The executor does not provide the mutation-minimal recovery path.");
}

public sealed record TradingExecutionCommand(
    string? ApprovalRequestId,
    TradingAuthorizationRequest Authorization,
    IReadOnlyList<ExecutionIntent> Intents,
    int Leverage,
    bool Isolated);

public sealed record TradingExecutionGatewayResult(bool Executed,string Code,string? ExecutionResult=null);

public sealed record DurableReviewExecutionCommand(
    TradingApprovalRequest ApprovalRequest,
    TradingApprovalReceipt ApprovalReceipt,
    DeterministicRiskReceipt RiskReceipt,
    DurableReviewExecutionArtifactV1 Artifact);

public sealed record ManualEmergencyConfirmation(
    string ConfirmationId,
    string CorrelationId,
    string UserId,
    string DeviceId,
    string SessionId,
    DateTimeOffset ConfirmedAtUtc);

public sealed record EmergencyReductionCommand(
    ManualEmergencyConfirmation Confirmation,
    ManagedPosition ObservedPosition,
    ExecutionIntent Intent,
    int Leverage,
    bool Isolated);

public enum ReduceOnlyRecoveryResult
{
    Verified,
    Unknown,
    Stale,
    Conflicting
}

public sealed record ReduceOnlyRecoveryReceipt(
    string ReceiptId,
    string CorrelationId,
    string ProviderId,
    string Environment,
    string AccountId,
    string Symbol,
    PositionSide Side,
    decimal Quantity,
    DateTimeOffset ObservedAtUtc,
    string IntentHash,
    ReduceOnlyRecoveryResult Result,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Signature);

public interface IReduceOnlyRecoveryReceiptVerifier
{
    bool Verify(ReduceOnlyRecoveryReceipt receipt);
}

public sealed record ReduceOnlyRecoveryCommand(
    string CorrelationId,
    ReduceOnlyRecoveryReceipt? Receipt,
    ManagedPosition ObservedPosition,
    ExecutionIntent Intent,
    int Leverage,
    bool Isolated);

public sealed record TestnetSmokeAuthorization(
    string AuthorizationId,
    string CorrelationId,
    string UserId,
    string DeviceId,
    string SessionId,
    DateTimeOffset AuthorizedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record TestnetSmokeCommand(
    TestnetSmokeAuthorization? Authorization,
    ExecutionIntent Intent,
    int Leverage,
    bool Isolated,
    TradingRule? Rule=null,
    MarketEvidence? Market=null,
    ManagedPosition? ObservedPosition=null);

public enum AutomaticMutationPath
{
    StartupRecovery,
    ProtectionAudit,
    PositionManagement,
    AutoMainPlan
}

public sealed record AutomaticMutationAssessment(bool SafeToIncreaseRisk,string Code);

public enum AutomaticPreMutationAuthorityState { Allowed,Missing,Unknown,Stale,Revoked,Conflicting,Unsupported }
public sealed record AutomaticPreMutationAuthorityDecisionV1(
    int ContractVersion,AutomaticPreMutationAuthorityState State,string ArtifactHash,string IntentHash,
    string RiskReceiptId,string PolicyHash,DateTimeOffset AsOfUtc,DateTimeOffset ExpiresAtUtc);

public interface IAutomaticPreMutationAuthority
{
    Task<AutomaticPreMutationAuthorityDecisionV1> RecheckAsync(
        DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,string policyHash,CancellationToken ct);
}

public static class AutomaticMutationPolicyV1
{
    public const int Version=1;
    public const string Canonical="automatic-testnet|risk-gate-required|exact-artifact-intent-receipt|mainnet-disabled|v1";
    public static readonly string Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical))).ToLowerInvariant();
}

public static class AutomaticPreMutationAuthorityContractV1
{
    public static string Evaluate(AutomaticPreMutationAuthorityDecisionV1? authority,DurableExecutionArtifactV2 artifact,DurableExecutionArtifactHashesV2 hashes,DeterministicRiskReceipt receipt,DateTimeOffset now)
    {
        if(!string.Equals(artifact.Environment,"Testnet",StringComparison.Ordinal))return "automatic.testnet-required";
        if(authority is null)return "automatic.authority-missing";
        if(authority.ContractVersion!=AutomaticMutationPolicyV1.Version)return "automatic.authority-unsupported";
        if(authority.State!=AutomaticPreMutationAuthorityState.Allowed)return "automatic.authority-"+authority.State.ToString().ToLowerInvariant();
        now=now.ToUniversalTime();
        if(!receipt.Approved)return "automatic.authority-unknown";
        if(receipt.RevokedAtUtc is not null)return "automatic.authority-revoked";
        if(receipt.IssuedAtUtc>now||receipt.ExpiresAtUtc<=now)return "automatic.authority-stale";
        if(!Fixed(receipt.IntentHash,hashes.IntentHash)||!Fixed(receipt.ArtifactHash,hashes.ArtifactHash))return "automatic.authority-conflicting";
        if(authority.AsOfUtc.Offset!=TimeSpan.Zero||authority.AsOfUtc>now||now-authority.AsOfUtc>TimeSpan.FromSeconds(5)||authority.ExpiresAtUtc<=now)
            return "automatic.authority-stale";
        if(!Fixed(authority.ArtifactHash,hashes.ArtifactHash)||!Fixed(authority.IntentHash,hashes.IntentHash)||
           !string.Equals(authority.RiskReceiptId,receipt.ReceiptId,StringComparison.Ordinal)||!Fixed(authority.PolicyHash,AutomaticMutationPolicyV1.Hash))
            return "automatic.authority-conflicting";
        return "automatic.authority-allowed";
    }

    private static bool Fixed(string? left,string right)
    {
        if(left is null||left.Length!=64||right.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left),Convert.FromHexString(right));}catch(FormatException){return false;}
    }
}

internal sealed class PersistedAutomaticPreMutationAuthority(AgentSqliteStore store,Func<DateTimeOffset> utcNow):IAutomaticPreMutationAuthority
{
    public async Task<AutomaticPreMutationAuthorityDecisionV1> RecheckAsync(DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,string policyHash,CancellationToken ct)
    {
        var now=utcNow().ToUniversalTime();
        var item=await store.GetAutomaticExecutionAsync(artifact.CorrelationId,ct);
        var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        if(item is null)return Decision(AutomaticPreMutationAuthorityState.Missing,hashes,receipt,policyHash,now,now);
        if(!string.Equals(artifact.Environment,"Testnet",StringComparison.Ordinal)||artifact.ContractVersion!=DurableExecutionArtifactV2.Version)
            return Decision(AutomaticPreMutationAuthorityState.Unsupported,hashes,receipt,policyHash,now,now);
        if(!item.ArtifactValid||!item.RiskReceiptValid||item.Artifact is null||item.RiskReceipt is null||item.Status!=AutomaticExecutionQueueStatus.Executing||
           !Fixed(item.ArtifactHash,hashes.ArtifactHash)||!Fixed(item.IntentHash,hashes.IntentHash)||
           !string.Equals(item.RiskReceipt.ReceiptId,receipt.ReceiptId,StringComparison.Ordinal)||!Fixed(item.RiskReceipt.IntentHash,receipt.IntentHash)||
           !Fixed(item.RiskReceipt.ArtifactHash,receipt.ArtifactHash??string.Empty)||item.RiskReceipt.Approved!=receipt.Approved||
           item.RiskReceipt.IssuedAtUtc!=receipt.IssuedAtUtc||item.RiskReceipt.ExpiresAtUtc!=receipt.ExpiresAtUtc)
            return Decision(AutomaticPreMutationAuthorityState.Conflicting,hashes,receipt,policyHash,now,now);
        var current=item.RiskReceipt;
        if(current.RevokedAtUtc is not null)return Decision(AutomaticPreMutationAuthorityState.Revoked,hashes,current,policyHash,now,current.ExpiresAtUtc);
        if(receipt.RevokedAtUtc is not null)return Decision(AutomaticPreMutationAuthorityState.Conflicting,hashes,current,policyHash,now,current.ExpiresAtUtc);
        if(!current.Approved)return Decision(AutomaticPreMutationAuthorityState.Unknown,hashes,current,policyHash,now,current.ExpiresAtUtc);
        if(current.IssuedAtUtc>now||current.ExpiresAtUtc<=now||artifact.ExpiresAtUtc<=now)
            return Decision(AutomaticPreMutationAuthorityState.Stale,hashes,current,policyHash,now,current.ExpiresAtUtc);
        return Decision(AutomaticPreMutationAuthorityState.Allowed,hashes,current,policyHash,now,Min(current.ExpiresAtUtc,artifact.ExpiresAtUtc));
    }

    private static AutomaticPreMutationAuthorityDecisionV1 Decision(AutomaticPreMutationAuthorityState state,DurableExecutionArtifactHashesV2 hashes,DeterministicRiskReceipt receipt,string policyHash,DateTimeOffset asOf,DateTimeOffset expires)=>
        new(AutomaticMutationPolicyV1.Version,state,hashes.ArtifactHash,hashes.IntentHash,receipt.ReceiptId,policyHash,asOf,expires);
    private static DateTimeOffset Min(DateTimeOffset left,DateTimeOffset right)=>left<=right?left:right;
    private static bool Fixed(string? left,string? right)
    {
        if(left is null||right is null||left.Length!=64||right.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left),Convert.FromHexString(right));}catch(FormatException){return false;}
    }
}

public sealed class TradingAutomaticExecutionGateway : IAutomaticExecutionGateway,IAutomaticExecutionOrderEvidenceReader,IAutomaticExecutionRealityEvidenceReader
{
    private readonly TradingExecutionGateway _gateway;
    private readonly IExchangeAdapter _exchange;
    private readonly AgentSqliteStore _store;
    private readonly IAutomaticPreMutationAuthority? _authority;
    private readonly Func<DateTimeOffset> _utcNow;

    public TradingAutomaticExecutionGateway(TradingExecutionGateway gateway,IExchangeAdapter exchange,AgentSqliteStore store)
        :this(gateway,exchange,store,null,null){}
    internal TradingAutomaticExecutionGateway(TradingExecutionGateway gateway,IExchangeAdapter exchange,AgentSqliteStore store,IAutomaticPreMutationAuthority? authority,Func<DateTimeOffset>? utcNow)
    {
        _gateway=gateway??throw new ArgumentNullException(nameof(gateway));_exchange=exchange??throw new ArgumentNullException(nameof(exchange));_store=store??throw new ArgumentNullException(nameof(store));
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);_authority=authority??new PersistedAutomaticPreMutationAuthority(store,_utcNow);
    }

    public bool IsTestnet=>_exchange.Environment==ExchangeEnvironment.Testnet;

    public async Task<AutomaticGatewayExecutionResult> ExecuteAsync(DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,CancellationToken ct)
    {
        if(!IsTestnet)return new(AutomaticGatewayExecutionState.Rejected,"automatic.testnet-required");
        if(_exchange is IExchangeProvider liveProvider
           &&!string.Equals(liveProvider.ProviderId,artifact.ProviderId,StringComparison.Ordinal))
            return new(AutomaticGatewayExecutionState.Rejected,"automatic.provider-mismatch");
        IReadOnlyList<ExecutionIntent> intents;
        DurableExecutionArtifactHashesV2 hashes;
        try{intents=Restore(artifact);hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);}
        catch(ArgumentException){return new(AutomaticGatewayExecutionState.Rejected,"automatic.artifact-invalid");}
        if(_authority is null)return new(AutomaticGatewayExecutionState.Rejected,"automatic.authority-missing");
        AutomaticPreMutationAuthorityDecisionV1 authority;
        try{authority=await _authority.RecheckAsync(artifact,receipt,AutomaticMutationPolicyV1.Hash,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return new(AutomaticGatewayExecutionState.Rejected,"automatic.authority-unknown");}
        var authorityCode=AutomaticPreMutationAuthorityContractV1.Evaluate(authority,artifact,hashes,receipt,_utcNow());
        if(authorityCode!="automatic.authority-allowed")return new(AutomaticGatewayExecutionState.Rejected,authorityCode);
        var gatewayIntentHash=TradingExecutionGateway.ComputeIntentHash(intents,artifact.Leverage,artifact.Isolated);
        var gatewayReceipt=receipt with{IntentHash=gatewayIntentHash};
        var authorization=new TradingAuthorizationRequest(TradingAuthorizationMode.Auto,true,artifact.CorrelationId,gatewayIntentHash,"automatic","automatic","automatic",gatewayReceipt,null);
        var result=await _gateway.ExecutePlanAsync(new(null,authorization,intents,artifact.Leverage,artifact.Isolated),ct);
        return result.Executed?new(AutomaticGatewayExecutionState.Succeeded,result.Code):new(AutomaticGatewayExecutionState.Rejected,result.Code);
    }

    public async Task<AutomaticGatewayReconciliationResult> ReconcileAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct)
    {
        if(!IsTestnet)return new(AutomaticGatewayReconciliationState.Failed,"automatic.testnet-required");
        if(_exchange is IExchangeProvider liveProvider
           &&!string.Equals(liveProvider.ProviderId,artifact.ProviderId,StringComparison.Ordinal))
            return new(AutomaticGatewayReconciliationState.Failed,"automatic.provider-mismatch");
        if(!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid)return new(AutomaticGatewayReconciliationState.Failed,"automatic.reconcile-artifact-invalid");
        var found=0;var journaled=0;
        foreach(var intent in artifact.Intents)
        {
            var journal=await _store.HasExecutionSubmissionJournalAsync(intent.ClientOrderId,ct);if(journal)journaled++;
            var order=await _exchange.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(order is null)continue;found++;
            if(order.ExecutedQuantity<=0&&order.Status is "CANCELED" or "EXPIRED" or "REJECTED")return new(AutomaticGatewayReconciliationState.Failed,"automatic.reconcile-order-terminal");
        }
        if(found==artifact.Intents.Count)return new(AutomaticGatewayReconciliationState.Succeeded,"automatic.reconcile-succeeded");
        if(found==0&&journaled==0)return new(AutomaticGatewayReconciliationState.NotSubmitted,"automatic.reconcile-not-submitted");
        return new(AutomaticGatewayReconciliationState.Unknown,"automatic.reconcile-unknown");
    }

    public async Task<AutomaticExecutionSimulationObservationV1> ObserveSimulationInputAsync(
        DurableExecutionArtifactV2 artifact,
        DurableExecutionIntentSnapshotV1 intent,
        CancellationToken ct)
    {
        var observedAt=_utcNow().ToUniversalTime();
        if(!IsTestnet
           ||!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid
           ||artifact.Environment!="Testnet")
            return new(false,"simulation.testnet-or-artifact-invalid",artifact?.ProviderId??"unknown",artifact?.Environment??"unknown",null,null,observedAt);

        var providerId=_exchange is IExchangeProvider provider?provider.ProviderId:artifact.ProviderId;
        if(!string.Equals(providerId,artifact.ProviderId,StringComparison.Ordinal))
            return new(false,"simulation.provider-mismatch",providerId,"Testnet",null,null,observedAt);

        try
        {
            var market=await _exchange.GetMarketAsync(intent.Symbol,ct);
            var rule=await _exchange.GetRulesAsync(intent.Symbol,ct);
            if(!string.Equals(market.Symbol,intent.Symbol,StringComparison.Ordinal)
               ||!string.Equals(rule.Symbol,intent.Symbol,StringComparison.Ordinal))
                return new(false,"simulation.symbol-mismatch",providerId,"Testnet",null,null,observedAt);

            if(market.Provenance is null)
                market=market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,providerId,"Testnet")};
            else if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)
                    ||!string.Equals(market.Provenance.ProviderId,providerId,StringComparison.Ordinal)
                    ||!string.Equals(market.Provenance.Environment,"Testnet",StringComparison.Ordinal))
                return new(false,"simulation.market-provenance-invalid",providerId,"Testnet",null,null,observedAt);

            return new(true,"simulation.source-available",providerId,"Testnet",market,rule,observedAt);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return new(false,"simulation.source-query-failed",providerId,"Testnet",null,null,observedAt);}
    }

    public async Task<ExchangeOrder?> ObserveOrderAsync(
        DurableExecutionArtifactV2 artifact,
        DurableExecutionIntentSnapshotV1 intent,
        CancellationToken ct)
    {
        if(!IsTestnet
           ||!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid
           ||artifact.Environment!="Testnet")
            return null;
        var providerId=_exchange is IExchangeProvider provider?provider.ProviderId:artifact.ProviderId;
        if(!string.Equals(providerId,artifact.ProviderId,StringComparison.Ordinal))
            return null;
        try{return await _exchange.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return null;}
    }

    public async Task<ModelOffExchangeOrderEvidenceV1> ObserveOrdersAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct)
    {
        var observedAt=_utcNow().ToUniversalTime();
        if(!IsTestnet||!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid)
            return ModelOffExchangeOrderEvidenceContractV1.Create(artifact?.CorrelationId??"invalid",ModelOffExchangeOrderEvidenceStateV1.Unsupported,artifact?.Intents.Count??0,0,observedAt,[]);
        var entries=new List<ModelOffExchangeOrderEvidenceEntryV1>();var found=0;var conflicting=false;
        try
        {
            foreach(var intent in artifact.Intents.OrderBy(x=>x.Sequence))
            {
                var order=await _exchange.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);
                if(order is null){entries.Add(new(intent.Sequence,"missing","none",0));continue;}
                found++;
                var matches=string.Equals(order.Symbol,intent.Symbol,StringComparison.Ordinal)
                    &&string.Equals(order.ClientOrderId,intent.ClientOrderId,StringComparison.Ordinal)
                    &&order.ExecutedQuantity==intent.Quantity
                    &&string.Equals(order.Status,"FILLED",StringComparison.Ordinal);
                if(!matches)conflicting=true;
                entries.Add(new(intent.Sequence,matches?"confirmed":"conflicting",order.Status,order.ExecutedQuantity));
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return ModelOffExchangeOrderEvidenceContractV1.Create(artifact.CorrelationId,ModelOffExchangeOrderEvidenceStateV1.Unknown,artifact.Intents.Count,found,observedAt,entries);}
        var state=conflicting?ModelOffExchangeOrderEvidenceStateV1.Conflicting:
            found==artifact.Intents.Count?ModelOffExchangeOrderEvidenceStateV1.Confirmed:ModelOffExchangeOrderEvidenceStateV1.Missing;
        return ModelOffExchangeOrderEvidenceContractV1.Create(artifact.CorrelationId,state,artifact.Intents.Count,found,observedAt,entries);
    }

    private static IReadOnlyList<ExecutionIntent> Restore(DurableExecutionArtifactV2 artifact)
    {
        if(!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid)throw new ArgumentException("automatic.artifact-invalid",nameof(artifact));
        return artifact.Intents.OrderBy(x=>x.Sequence).Select(x=>new ExecutionIntent(x.Symbol,Enum.Parse<PositionSide>(x.Side),x.Quantity,x.ReduceOnly,x.StopLoss,x.TakeProfit,x.ClientOrderId,x.ReasonCode,Enum.Parse<DecisionAction>(x.Action),Enum.Parse<ExecutionOrderType>(x.OrderType),x.LimitPrice,x.ExpectedPrice)).ToArray();
    }

}

public sealed class TradingExecutionGateway
{
    private static readonly TimeSpan EmergencyConfirmationLifetime=TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumSmokeAuthorizationLifetime=TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaximumRecoveryObservationAge=TimeSpan.FromSeconds(30);
    private readonly ITradingMutationExecutor _executor;
    private readonly AgentSqliteStore _approvals;
    private readonly IReduceOnlyRecoveryReceiptVerifier? _recoveryReceiptVerifier;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _recoveryLocks=new(StringComparer.Ordinal);

    public TradingExecutionGateway(ITradingMutationExecutor executor,AgentSqliteStore approvals):this(executor,approvals,null,null){}
    internal TradingExecutionGateway(ITradingMutationExecutor executor,AgentSqliteStore approvals,Func<DateTimeOffset>? utcNow,IReduceOnlyRecoveryReceiptVerifier? recoveryReceiptVerifier=null)
    {
        _executor=executor??throw new ArgumentNullException(nameof(executor));
        _approvals=approvals??throw new ArgumentNullException(nameof(approvals));
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
        _recoveryReceiptVerifier=recoveryReceiptVerifier;
    }

    public async Task<TradingExecutionGatewayResult> ExecutePlanAsync(TradingExecutionCommand command,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if(command.Authorization is null||command.Intents is null||command.Intents.Count==0)
            return Deny("execution.command-invalid");

        var intentHash=ComputeIntentHash(command.Intents,command.Leverage,command.Isolated);
        if(!string.Equals(command.Authorization.IntentHash,intentHash,StringComparison.Ordinal))
            return Deny("execution.intent-hash-mismatch");

        var authorization=command.Authorization with{IsTestnet=_executor.IsTestnet};
        var decision=TradingAuthorizationPolicy.Evaluate(authorization,_utcNow().ToUniversalTime());
        if(!decision.Allowed)return Deny(decision.Code);

        if(authorization.Mode==TradingAuthorizationMode.Review)
        {
            if(string.IsNullOrWhiteSpace(command.ApprovalRequestId)||authorization.ApprovalReceipt is null)
                return Deny("approval.consume-invalid");
            var receipt=authorization.ApprovalReceipt;
            var consumed=await _approvals.TryConsumeTradingApprovalAsync(new(
                command.ApprovalRequestId,receipt.ReceiptId,authorization.CorrelationId,authorization.IntentHash,
                authorization.UserId,authorization.DeviceId,authorization.SessionId),ct);
            if(!consumed.Consumed)return Deny(consumed.Code);
        }

        var result=await _executor.ExecutePlanAsync(
            authorization.CorrelationId,command.Intents,command.Leverage,command.Isolated,ct);
        return new(true,"execution.submitted",result);
    }

    public async Task<TradingExecutionGatewayResult> ExecuteDurableReviewAsync(DurableReviewExecutionCommand command,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);if(command.ApprovalRequest is null||command.ApprovalReceipt is null||command.RiskReceipt is null||command.Artifact is null)return Deny("review.execution-command-invalid");
        DurableReviewArtifactHashes hashes;IReadOnlyList<ExecutionIntent> intents;
        try{hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(command.Artifact);intents=RestoreDurableIntents(command.Artifact);}
        catch(ArgumentException){return Deny("review.artifact-invalid");}
        var request=command.ApprovalRequest;var approval=command.ApprovalReceipt;var risk=command.RiskReceipt;
        if(request.Mode!=TradingAuthorizationMode.Review||!FixedHash(request.IntentHash,hashes.IntentHash)||!FixedHash(request.ArtifactHash,hashes.ArtifactHash))return Deny("review.request-hash-mismatch");
        if(!FixedHash(approval.IntentHash,hashes.IntentHash)||!FixedHash(approval.ArtifactHash,hashes.ArtifactHash))return Deny("review.approval-hash-mismatch");
        if(!FixedHash(risk.IntentHash,hashes.IntentHash)||!FixedHash(risk.ArtifactHash,hashes.ArtifactHash))return Deny("review.risk-hash-mismatch");
        var now=_utcNow().ToUniversalTime();if(risk.ExpiresAtUtc-risk.IssuedAtUtc>TimeSpan.FromMinutes(2))return Deny("review.risk-lifetime-invalid");
        if(request.RevokedAtUtc is not null||request.ConsumedAtUtc is not null||request.IssuedAtUtc>now||request.ExpiresAtUtc<=now||request.IssuedAtUtc!=command.Artifact.CreatedAtUtc||request.ExpiresAtUtc!=command.Artifact.ExpiresAtUtc)return Deny("review.request-not-valid");
        var authorization=new TradingAuthorizationRequest(TradingAuthorizationMode.Review,_executor.IsTestnet,request.CorrelationId,hashes.IntentHash,request.UserId,request.DeviceId,request.SessionId,risk,approval);
        var decision=TradingAuthorizationPolicy.Evaluate(authorization,now);if(!decision.Allowed)return Deny(decision.Code);
        var consumed=await _approvals.TryConsumeTradingApprovalAsync(new(request.RequestId,approval.ReceiptId,request.CorrelationId,hashes.IntentHash,request.UserId,request.DeviceId,request.SessionId,hashes.ArtifactHash),ct);if(!consumed.Consumed)return Deny(consumed.Code);
        var result=await _executor.ExecutePlanAsync(request.CorrelationId,intents,command.Artifact.Leverage,command.Artifact.Isolated,ct);return new(true,"review.execution-submitted",result);
    }

    public async Task<TradingExecutionGatewayResult> ExecuteEmergencyReductionAsync(EmergencyReductionCommand command,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now=_utcNow().ToUniversalTime();
        var confirmation=command.Confirmation;
        if(confirmation is null||string.IsNullOrWhiteSpace(confirmation.ConfirmationId)||
           string.IsNullOrWhiteSpace(confirmation.CorrelationId)||string.IsNullOrWhiteSpace(confirmation.UserId)||
           string.IsNullOrWhiteSpace(confirmation.DeviceId)||string.IsNullOrWhiteSpace(confirmation.SessionId))
            return Deny("emergency.confirmation-invalid");
        var confirmedAt=confirmation.ConfirmedAtUtc.ToUniversalTime();
        if(confirmedAt>now||confirmedAt.Add(EmergencyConfirmationLifetime)<=now)
            return Deny("emergency.confirmation-expired");
        if(!_executor.IsTestnet)return Deny("authorization.testnet-required");
        if(!IsConfirmedReduction(command.ObservedPosition,command.Intent))
            return Deny("emergency.reduction-invalid");

        var intentHash=ComputeIntentHash([command.Intent],command.Leverage,command.Isolated);
        var stableKey=$"{confirmation.ConfirmationId}\u001f{intentHash}";
        var requestId=StableId("emergency-request-",stableKey);
        var receiptId=StableId("emergency-receipt-",stableKey);
        var riskId=StableId("emergency-risk-",stableKey);
        var expiresAt=confirmedAt.Add(EmergencyConfirmationLifetime);
        var request=new TradingApprovalRequest(
            requestId,TradingAuthorizationMode.Review,confirmation.CorrelationId,intentHash,
            confirmation.UserId,confirmation.DeviceId,confirmation.SessionId,confirmedAt,expiresAt);
        var persistedRequest=await _approvals.SaveTradingApprovalRequestAsync(request,ct);
        if(!persistedRequest.Succeeded)return Deny(persistedRequest.Code);
        var receipt=new TradingApprovalReceipt(
            receiptId,confirmation.CorrelationId,intentHash,confirmation.UserId,confirmation.DeviceId,
            confirmation.SessionId,true,confirmedAt,expiresAt);
        var persistedReceipt=await _approvals.SaveTradingApprovalReceiptAsync(requestId,receipt,ct);
        if(!persistedReceipt.Succeeded)return Deny(persistedReceipt.Code);
        var risk=new DeterministicRiskReceipt(
            riskId,confirmation.CorrelationId,intentHash,true,now,expiresAt);
        var authorization=new TradingAuthorizationRequest(
            TradingAuthorizationMode.Review,true,confirmation.CorrelationId,intentHash,
            confirmation.UserId,confirmation.DeviceId,confirmation.SessionId,risk,receipt);
        return await ExecutePlanAsync(new(requestId,authorization,[command.Intent],command.Leverage,command.Isolated),ct);
    }

    public async Task<TradingExecutionGatewayResult> ExecuteReduceOnlyRecoveryAsync(ReduceOnlyRecoveryCommand command,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if(!_executor.IsTestnet)return Deny("recovery.testnet-required");
        if(string.IsNullOrWhiteSpace(command.CorrelationId)||command.ObservedPosition is null||command.Intent is null)
            return Deny("recovery.command-invalid");

        var now=_utcNow().ToUniversalTime();
        var receipt=command.Receipt;
        if(receipt is null||_recoveryReceiptVerifier is null||!_recoveryReceiptVerifier.Verify(receipt))
            return Deny("recovery.receipt-invalid");
        if(receipt.Result!=ReduceOnlyRecoveryResult.Verified)
            return Deny($"recovery.reconciliation-{receipt.Result.ToString().ToLowerInvariant()}");
        var observedAt=receipt.ObservedAtUtc.ToUniversalTime();
        if(observedAt>now||now-observedAt>MaximumRecoveryObservationAge)
            return Deny("recovery.observation-stale");
        if(receipt.IssuedAtUtc>now||receipt.ExpiresAtUtc<=now||receipt.ExpiresAtUtc-receipt.IssuedAtUtc>MaximumRecoveryObservationAge)
            return Deny("recovery.receipt-expired");
        var intentHash=ComputeIntentHash([command.Intent],command.Leverage,command.Isolated);
        if(!FixedHash(receipt.IntentHash,intentHash)||
           !string.Equals(receipt.CorrelationId,command.CorrelationId,StringComparison.Ordinal)||
           !string.Equals(receipt.ProviderId,_executor.ProviderId,StringComparison.Ordinal)||
           !string.Equals(receipt.Environment,"Testnet",StringComparison.Ordinal)||
           !string.Equals(receipt.AccountId,_executor.AccountId,StringComparison.Ordinal)||
           !string.Equals(receipt.Symbol,command.ObservedPosition.Symbol,StringComparison.Ordinal)||
           receipt.Side!=command.ObservedPosition.Side||
           receipt.Quantity!=command.ObservedPosition.Quantity)
            return Deny("recovery.receipt-mismatch");
        if(command.Leverage<=0||command.ObservedPosition.Leverage!=command.Leverage||
           command.ObservedPosition.Isolated!=command.Isolated)
            return Deny("recovery.account-settings-conflict");
        if(!IsConfirmedReduction(command.ObservedPosition,command.Intent))
            return Deny("recovery.reduction-invalid");

        var gate=_recoveryLocks.GetOrAdd(command.Intent.ClientOrderId,_=>new SemaphoreSlim(1,1));
        await gate.WaitAsync(ct);
        try
        {
            // Any durable state means submission may already have happened. Reconcile it; never replay it.
            var status=await _approvals.GetOrderIntentStatusAsync(command.Intent.ClientOrderId,ct);
            if(status is not null||await _approvals.HasExecutionSubmissionJournalAsync(command.Intent.ClientOrderId,ct))
                return Deny("recovery.reconcile-existing");
            var result=await _executor.ExecuteReduceOnlyRecoveryAsync(command.CorrelationId,command.Intent,ct);
            return new(true,"recovery.reduce-only-submitted",result);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TradingExecutionGatewayResult> ExecuteTestnetSmokeAsync(TestnetSmokeCommand command,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var authorization=command.Authorization;
        if(authorization is null||string.IsNullOrWhiteSpace(authorization.AuthorizationId)||
           string.IsNullOrWhiteSpace(authorization.CorrelationId)||string.IsNullOrWhiteSpace(authorization.UserId)||
           string.IsNullOrWhiteSpace(authorization.DeviceId)||string.IsNullOrWhiteSpace(authorization.SessionId))
            return Deny("smoke.authorization-required");
        var now=_utcNow().ToUniversalTime();var issued=authorization.AuthorizedAtUtc.ToUniversalTime();var expires=authorization.ExpiresAtUtc.ToUniversalTime();
        if(issued>now||expires<=now||expires<=issued||expires-issued>MaximumSmokeAuthorizationLifetime)
            return Deny("smoke.authorization-expired");
        if(!_executor.IsTestnet)return Deny("authorization.testnet-required");
        var valid=command.Intent.ReduceOnly
            ?command.ObservedPosition is not null&&IsConfirmedReduction(command.ObservedPosition,command.Intent)
            :IsVerifiedSmokeOpening(command);
        if(!valid)return Deny("smoke.intent-invalid");

        var intentHash=ComputeIntentHash([command.Intent],command.Leverage,command.Isolated);
        var stableKey=$"{authorization.AuthorizationId}\u001f{intentHash}";
        var requestId=StableId("smoke-request-",stableKey);
        var receiptId=StableId("smoke-receipt-",stableKey);
        var riskId=StableId("smoke-risk-",stableKey);
        var request=new TradingApprovalRequest(
            requestId,TradingAuthorizationMode.Review,authorization.CorrelationId,intentHash,
            authorization.UserId,authorization.DeviceId,authorization.SessionId,issued,expires);
        var persistedRequest=await _approvals.SaveTradingApprovalRequestAsync(request,ct);
        if(!persistedRequest.Succeeded)return Deny(persistedRequest.Code);
        var receipt=new TradingApprovalReceipt(
            receiptId,authorization.CorrelationId,intentHash,authorization.UserId,authorization.DeviceId,
            authorization.SessionId,true,issued,expires);
        var persistedReceipt=await _approvals.SaveTradingApprovalReceiptAsync(requestId,receipt,ct);
        if(!persistedReceipt.Succeeded)return Deny(persistedReceipt.Code);
        var risk=new DeterministicRiskReceipt(riskId,authorization.CorrelationId,intentHash,true,now,expires);
        var review=new TradingAuthorizationRequest(
            TradingAuthorizationMode.Review,true,authorization.CorrelationId,intentHash,
            authorization.UserId,authorization.DeviceId,authorization.SessionId,risk,receipt);
        return await ExecutePlanAsync(new(requestId,review,[command.Intent],command.Leverage,command.Isolated),ct);
    }

    public AutomaticMutationAssessment AssessUnverifiedAutomaticMutation(AutomaticMutationPath path,int itemCount)
    {
        if(itemCount<=0&&path is AutomaticMutationPath.StartupRecovery or AutomaticMutationPath.ProtectionAudit)
            return new(true,"authorization.no-mutation-required");
        return path switch
        {
            AutomaticMutationPath.StartupRecovery=>new(false,"authorization.recovery-approval-chain-required"),
            AutomaticMutationPath.ProtectionAudit=>new(false,"authorization.protection-approval-chain-required"),
            AutomaticMutationPath.PositionManagement=>new(false,"authorization.position-management-approval-required"),
            _=>new(false,"authorization.auto-risk-receipt-unavailable")
        };
    }

    public static TradingApprovalRequest CreateReviewRequest(
        string correlationId,string intentHash,string userId,string deviceId,string sessionId,
        DateTimeOffset issuedAtUtc,TimeSpan lifetime)
    {
        if(string.IsNullOrWhiteSpace(correlationId)||string.IsNullOrWhiteSpace(intentHash)||
           string.IsNullOrWhiteSpace(userId)||string.IsNullOrWhiteSpace(deviceId)||string.IsNullOrWhiteSpace(sessionId)||
           lifetime<=TimeSpan.Zero)
            throw new ArgumentException("Review approval context is incomplete.");
        var issued=issuedAtUtc.ToUniversalTime();
        return new(Guid.NewGuid().ToString("N"),TradingAuthorizationMode.Review,correlationId,intentHash,
            userId,deviceId,sessionId,issued,issued.Add(lifetime));
    }

    public static string ComputeIntentHash(IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated)
    {
        ArgumentNullException.ThrowIfNull(intents);
        var payload=JsonSerializer.SerializeToUtf8Bytes(new IntentHashPayload(leverage,isolated,intents));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static TradingExecutionGatewayResult Deny(string code)=>new(false,code);
    private static IReadOnlyList<ExecutionIntent> RestoreDurableIntents(DurableReviewExecutionArtifactV1 artifact)
    {
        var intents=new List<ExecutionIntent>(artifact.Intents.Count);foreach(var value in artifact.Intents)
        {
            if(!Enum.TryParse<PositionSide>(value.Side,false,out var side)||!Enum.IsDefined(side)||!Enum.TryParse<DecisionAction>(value.Action,false,out var action)||!Enum.IsDefined(action)||!Enum.TryParse<ExecutionOrderType>(value.OrderType,false,out var orderType)||!Enum.IsDefined(orderType))throw new ArgumentException("review.artifact-intent-invalid",nameof(artifact));
            intents.Add(new(value.Symbol,side,value.Quantity,value.ReduceOnly,value.StopLoss,value.TakeProfit,value.ClientOrderId,value.ReasonCode,action,orderType,value.LimitPrice,value.ExpectedPrice));
        }return intents;
    }
    private static bool FixedHash(string? left,string right)
    {
        if(left is null||left.Length!=64||right.Length!=64)return false;try{return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left),Convert.FromHexString(right));}catch(FormatException){return false;}
    }
    private static bool IsConfirmedReduction(ManagedPosition position,ExecutionIntent intent)
    {
        if(position is null||intent is null||position.Quantity<=0||intent.Quantity<=0||intent.Quantity>position.Quantity)
            return false;
        if(!intent.ReduceOnly||intent.OrderType!=ExecutionOrderType.Market||intent.StopLoss!=0||intent.TakeProfit!=0)
            return false;
        if(!string.Equals(position.Symbol,intent.Symbol,StringComparison.OrdinalIgnoreCase)||position.Side!=intent.Side)
            return false;
        return position.Side switch
        {
            PositionSide.Long=>intent.Action is DecisionAction.ReduceLong or DecisionAction.CloseLong,
            PositionSide.Short=>intent.Action is DecisionAction.ReduceShort or DecisionAction.CloseShort,
            _=>false
        };
    }
    private static bool IsVerifiedSmokeOpening(TestnetSmokeCommand command)
    {
        var intent=command.Intent;var rule=command.Rule;var market=command.Market;
        if(rule is null||market is null||command.ObservedPosition is not null||!command.Isolated)
            return false;
        if(intent.ReduceOnly||intent.OrderType!=ExecutionOrderType.Market||intent.Quantity<=0||
           intent.StopLoss<=0||intent.TakeProfit<=0||intent.ExpectedPrice<=0||string.IsNullOrWhiteSpace(intent.ClientOrderId))
            return false;
        if(!string.Equals(intent.Symbol,rule.Symbol,StringComparison.OrdinalIgnoreCase)||
           !string.Equals(intent.Symbol,market.Symbol,StringComparison.OrdinalIgnoreCase)||market.Price<=0||intent.ExpectedPrice!=market.Price)
            return false;
        if(command.Leverage<=0||command.Leverage>Math.Min(10,rule.MaxLeverage)||rule.StepSize<=0||rule.MinQuantity<=0||rule.MinNotional<=0)
            return false;
        var minimum=CeilingToStep(Math.Max(rule.MinQuantity,rule.MinNotional*1.10m/market.Price),rule.StepSize);
        if(intent.Quantity!=minimum)return false;
        return intent.Side switch
        {
            PositionSide.Long=>intent.Action==DecisionAction.OpenLong&&intent.StopLoss<market.Price&&intent.TakeProfit>market.Price,
            PositionSide.Short=>intent.Action==DecisionAction.OpenShort&&intent.StopLoss>market.Price&&intent.TakeProfit<market.Price,
            _=>false
        };
    }
    private static decimal CeilingToStep(decimal value,decimal step)=>step<=0?value:Math.Ceiling(value/step)*step;
    private static string StableId(string prefix,string value)
    {
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return prefix+hash[..24];
    }
    private sealed record IntentHashPayload(int Leverage,bool Isolated,IReadOnlyList<ExecutionIntent> Intents);
}
