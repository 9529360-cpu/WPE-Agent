using WpeAgent.TradingAuthorization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum AutomaticFactState { Unknown,False,True }
public sealed record AutomaticExecutionRuntimeFacts(
    AutomaticFactState Testnet,
    AutomaticFactState RuntimeReady,
    AutomaticFactState CapabilityAvailable,
    AutomaticFactState MarketFresh,
    string Code);

public interface IAutomaticExecutionReadOnlyValidator
{
    Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct);
}

public enum AutomaticGatewayExecutionState { Succeeded,Rejected,Unknown }
public sealed record AutomaticGatewayExecutionResult(AutomaticGatewayExecutionState State,string Code);
public enum AutomaticGatewayReconciliationState { Succeeded,NotSubmitted,Failed,Unknown }
public sealed record AutomaticGatewayReconciliationResult(AutomaticGatewayReconciliationState State,string Code);

/// <summary>Module gateway port. A later integration adapter must delegate this to the existing execution gateway.</summary>
public interface IAutomaticExecutionGateway
{
    bool IsTestnet { get; }
    Task<AutomaticGatewayExecutionResult> ExecuteAsync(DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,CancellationToken ct);
    Task<AutomaticGatewayReconciliationResult> ReconcileAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct);
}

public sealed record ModelOffExecutionTimelineEntryV1(int Sequence,string From,string To,string Code,string Actor,DateTimeOffset OccurredAtUtc);
public sealed record ModelOffExecutionTimelineV1(int ContractVersion,string ExecutionId,string TimelineSha256,IReadOnlyList<ModelOffExecutionTimelineEntryV1> Entries);
public sealed record ModelOffRecoveryHandoffV1(int ContractVersion,string ExecutionId,string State,string ReasonCode,bool Quarantined,bool ResubmitAllowed,string ArtifactHash,string IntentHash,string HandoffSha256);
public sealed record AutomaticExecutionProcessorResult(bool Handled,string Code,ModelOffExecutionTimelineV1? Timeline=null,ModelOffRecoveryHandoffV1? RecoveryHandoff=null);

public static class ModelOffExecutionRecoveryContractV1
{
    public const int Version=1;

    public static ModelOffExecutionTimelineV1 Timeline(string executionId,IReadOnlyList<PersistedAutomaticExecutionEvent> events)
    {
        var entries=events.OrderBy(x=>x.Sequence).Select(x=>new ModelOffExecutionTimelineEntryV1(
            x.Sequence,x.FromStatus?.ToString()??"None",x.ToStatus.ToString(),x.EventCode,x.ActorKind,x.OccurredAtUtc.ToUniversalTime())).ToArray();
        return new(Version,executionId,Hash(JsonSerializer.Serialize(entries)),entries);
    }

    public static ModelOffRecoveryHandoffV1 Recovery(PersistedAutomaticExecution item,string state,string reason,bool quarantined)
    {
        var payload=new{version=Version,item.ExecutionId,state,reason,quarantined,artifactHash=item.ArtifactHash,intentHash=item.IntentHash};
        return new(Version,item.ExecutionId,state,reason,quarantined,false,item.ArtifactHash,item.IntentHash,Hash(JsonSerializer.Serialize(payload)));
    }

    private static string Hash(string value)=>"sha256:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class AutomaticExecutionProcessor
{
    private static readonly TimeSpan LeaseLifetime=TimeSpan.FromSeconds(30);
    private readonly AgentSqliteStore _store;
    private readonly IAutomaticExecutionReadOnlyValidator _validator;
    private readonly IAutomaticExecutionGateway _gateway;
    private readonly Func<DateTimeOffset> _utcNow;

    public AutomaticExecutionProcessor(AgentSqliteStore store,IAutomaticExecutionReadOnlyValidator validator,IAutomaticExecutionGateway gateway)
        :this(store,validator,gateway,null){}
    internal AutomaticExecutionProcessor(AgentSqliteStore store,IAutomaticExecutionReadOnlyValidator validator,IAutomaticExecutionGateway gateway,Func<DateTimeOffset>? utcNow)
    { _store=store??throw new ArgumentNullException(nameof(store));_validator=validator??throw new ArgumentNullException(nameof(validator));_gateway=gateway??throw new ArgumentNullException(nameof(gateway));_utcNow=utcNow??(()=>DateTimeOffset.UtcNow); }

    public async Task<AutomaticExecutionProcessorResult> ProcessNextAsync(string workerId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(workerId))return Result(false,"automatic.worker-id-invalid");
        PersistedAutomaticExecution? claimed=null;
        foreach(var candidate in await _store.GetAutomaticExecutionQueueAsync(AutomaticExecutionQueueStatus.RiskApproved,100,ct))
        {
            var result=await _store.TryClaimAutomaticExecutionAsync(candidate.ExecutionId,workerId,LeaseLifetime,ct);if(!result.Claimed)continue;claimed=await _store.GetAutomaticExecutionAsync(candidate.ExecutionId,ct);break;
        }
        if(claimed is null)return Result(false,"automatic.no-risk-approved-work");
        if(!ValidArtifactAndReceipt(claimed,_utcNow().ToUniversalTime()))return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.PolicyBlocked,"automatic.claim-revalidation-failed",ct);
        var artifact=claimed.Artifact!;
        if(!_gateway.IsTestnet)return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.PolicyBlocked,"automatic.testnet-required",ct);
        AutomaticExecutionRuntimeFacts facts;
        try{facts=await _validator.ValidateAsync(artifact,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.PolicyBlocked,"automatic.runtime-facts-unknown",ct);}
        if(facts.Testnet!=AutomaticFactState.True)return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.PolicyBlocked,"automatic.testnet-required",ct);
        if(facts.RuntimeReady!=AutomaticFactState.True)return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.PolicyBlocked,"automatic.runtime-unavailable",ct);
        if(facts.CapabilityAvailable!=AutomaticFactState.True)return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.CapabilityUnavailable,"automatic.capability-unavailable",ct);
        if(facts.MarketFresh!=AutomaticFactState.True)return await Block(claimed.ExecutionId,workerId,AutomaticExecutionQueueStatus.MarketStale,"automatic.market-stale",ct);
        var executing=await _store.TryTransitionAutomaticExecutionAsync(claimed.ExecutionId,AutomaticExecutionQueueStatus.Claimed,AutomaticExecutionQueueStatus.Executing,workerId,"automatic.executing",ct);if(!executing.Succeeded)return Result(false,executing.Code);
        AutomaticGatewayExecutionResult execution;
        try{execution=await _gateway.ExecuteAsync(artifact,claimed.RiskReceipt!,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{execution=new(AutomaticGatewayExecutionState.Unknown,"automatic.gateway-unknown");}
        var (next,code)=execution.State switch{AutomaticGatewayExecutionState.Succeeded=>(AutomaticExecutionQueueStatus.Succeeded,"automatic.succeeded"),AutomaticGatewayExecutionState.Rejected=>(AutomaticExecutionQueueStatus.FailedTerminal,"automatic.gateway-rejected"),_=>(AutomaticExecutionQueueStatus.UnknownOutcome,"automatic.unknown-outcome")};
        var transitioned=await _store.TryTransitionAutomaticExecutionAsync(claimed.ExecutionId,AutomaticExecutionQueueStatus.Executing,next,workerId,code,ct);
        return await Complete(claimed.ExecutionId,transitioned.Succeeded,code,null,ct);
    }

    public async Task<AutomaticExecutionProcessorResult> ReconcileNextAsync(string workerId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach(var status in new[]{AutomaticExecutionQueueStatus.UnknownOutcome,AutomaticExecutionQueueStatus.Executing})
        foreach(var candidate in await _store.GetAutomaticExecutionQueueAsync(status,100,ct))
        {
            var claim=await _store.TryClaimAutomaticReconciliationAsync(candidate.ExecutionId,workerId,LeaseLifetime,ct);if(!claim.Claimed)continue;var item=await _store.GetAutomaticExecutionAsync(candidate.ExecutionId,ct);
            if(item is null||!item.ArtifactValid||item.Artifact is null){var invalid=await _store.TryTransitionAutomaticExecutionAsync(candidate.ExecutionId,AutomaticExecutionQueueStatus.Reconciling,AutomaticExecutionQueueStatus.ArtifactInvalid,workerId,"automatic.reconcile-artifact-invalid",ct);return Result(invalid.Succeeded,"automatic.reconcile-artifact-invalid");}
            AutomaticGatewayReconciliationResult reconciliation;try{reconciliation=await _gateway.ReconcileAsync(item.Artifact,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{reconciliation=new(AutomaticGatewayReconciliationState.Unknown,"automatic.reconcile-unknown");}
            var (next,code)=reconciliation.State switch{AutomaticGatewayReconciliationState.Succeeded=>(AutomaticExecutionQueueStatus.Succeeded,"automatic.reconcile-succeeded"),AutomaticGatewayReconciliationState.NotSubmitted=>(AutomaticExecutionQueueStatus.FailedTerminal,"automatic.reconcile-not-submitted"),AutomaticGatewayReconciliationState.Failed=>(AutomaticExecutionQueueStatus.FailedTerminal,"automatic.reconcile-failed"),_=>(AutomaticExecutionQueueStatus.UnknownOutcome,"automatic.reconcile-unknown")};
            var transitioned=await _store.TryTransitionAutomaticExecutionAsync(candidate.ExecutionId,AutomaticExecutionQueueStatus.Reconciling,next,workerId,code,ct);
            var persisted=await _store.GetAutomaticExecutionAsync(candidate.ExecutionId,ct);
            var handoff=persisted is null?null:ModelOffExecutionRecoveryContractV1.Recovery(
                persisted,next.ToString(),code,next==AutomaticExecutionQueueStatus.UnknownOutcome);
            return await Complete(candidate.ExecutionId,transitioned.Succeeded,code,handoff,ct);
        }
        return Result(false,"automatic.no-reconciliation-work");
    }

    private static bool ValidArtifactAndReceipt(PersistedAutomaticExecution item,DateTimeOffset now)
    {
        if(!item.ArtifactValid||!item.RiskReceiptValid||item.Artifact is null||item.RiskReceipt is null)return false;var artifact=item.Artifact;var receipt=item.RiskReceipt;var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        return artifact.ContractVersion==DurableExecutionArtifactV2.Version&&artifact.Environment=="Testnet"&&artifact.CreatedAtUtc<=now&&artifact.ExpiresAtUtc>now&&item.ArtifactHash==hashes.ArtifactHash&&item.IntentHash==hashes.IntentHash&&receipt.Approved&&receipt.RevokedAtUtc is null&&receipt.IssuedAtUtc<=now&&receipt.ExpiresAtUtc>now&&receipt.ExpiresAtUtc>receipt.IssuedAtUtc&&receipt.ExpiresAtUtc-receipt.IssuedAtUtc<=TimeSpan.FromMinutes(2)&&receipt.CorrelationId==artifact.CorrelationId&&receipt.IntentHash==hashes.IntentHash&&receipt.ArtifactHash==hashes.ArtifactHash;
    }
    private async Task<AutomaticExecutionProcessorResult> Block(string id,string worker,AutomaticExecutionQueueStatus status,string code,CancellationToken ct){var transition=await _store.TryTransitionAutomaticExecutionAsync(id,AutomaticExecutionQueueStatus.Claimed,status,worker,code,ct);return await Complete(id,transition.Succeeded,code,null,ct);}
    private async Task<AutomaticExecutionProcessorResult> Complete(string id,bool handled,string code,ModelOffRecoveryHandoffV1? handoff,CancellationToken ct)
    {
        var events=await _store.GetAutomaticExecutionEventsAsync(id,100,ct);
        return new(handled,code,ModelOffExecutionRecoveryContractV1.Timeline(id,events),handoff);
    }
    private static AutomaticExecutionProcessorResult Result(bool handled,string code)=>new(handled,code);
}
