using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffExecutionRecoveryContractTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-mo07-"+Guid.NewGuid().ToString("N"));
    private readonly Clock _clock=new(new(2026,7,23,13,0,0,TimeSpan.Zero));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Theory]
    [InlineData(AutomaticPreMutationAuthorityState.Missing,"automatic.authority-missing")]
    [InlineData(AutomaticPreMutationAuthorityState.Unknown,"automatic.authority-unknown")]
    [InlineData(AutomaticPreMutationAuthorityState.Stale,"automatic.authority-stale")]
    [InlineData(AutomaticPreMutationAuthorityState.Revoked,"automatic.authority-revoked")]
    [InlineData(AutomaticPreMutationAuthorityState.Conflicting,"automatic.authority-conflicting")]
    [InlineData(AutomaticPreMutationAuthorityState.Unsupported,"automatic.authority-unsupported")]
    public void EveryNonAllowedAuthorityStateFailsClosed(AutomaticPreMutationAuthorityState state,string expected)
    {
        var artifact=Artifact();var receipt=Receipt(artifact);var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        var decision=Decision(state,artifact,receipt);
        Assert.Equal(expected,AutomaticPreMutationAuthorityContractV1.Evaluate(decision,artifact,hashes,receipt,_clock.Now));
    }

    [Fact]
    public void MissingMainnetExpiredRevokedAndConflictingInputsFailClosed()
    {
        var artifact=Artifact();var receipt=Receipt(artifact);var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);var allowed=Decision(AutomaticPreMutationAuthorityState.Allowed,artifact,receipt);
        Assert.Equal("automatic.authority-missing",AutomaticPreMutationAuthorityContractV1.Evaluate(null,artifact,hashes,receipt,_clock.Now));
        Assert.Equal("automatic.testnet-required",AutomaticPreMutationAuthorityContractV1.Evaluate(allowed,artifact with{Environment="Mainnet"},hashes,receipt,_clock.Now));
        Assert.Equal("automatic.authority-stale",AutomaticPreMutationAuthorityContractV1.Evaluate(allowed with{AsOfUtc=_clock.Now.AddSeconds(-6)},artifact,hashes,receipt,_clock.Now));
        Assert.Equal("automatic.authority-revoked",AutomaticPreMutationAuthorityContractV1.Evaluate(allowed,artifact,hashes,receipt with{RevokedAtUtc=_clock.Now},_clock.Now));
        Assert.Equal("automatic.authority-conflicting",AutomaticPreMutationAuthorityContractV1.Evaluate(allowed with{PolicyHash=new string('0',64)},artifact,hashes,receipt,_clock.Now));
    }

    [Fact]
    public async Task RevocationBetweenClaimAndMutationIsObservedByPersistedAuthority()
    {
        var store=Store();var artifact=Artifact();var receipt=Receipt(artifact);await ApproveAndExecuteState(store,artifact,receipt);
        var revoked=receipt with{RevokedAtUtc=_clock.Now};var bytes=DurableExecutionArtifactCanonicalizerV2.SerializeRiskReceipt(revoked);
        await using(var connection=new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();await using var command=connection.CreateCommand();
            command.CommandText="UPDATE automatic_execution_queue SET risk_receipt_bytes=$bytes,risk_receipt_hash=$hash WHERE execution_id=$id";
            command.Parameters.AddWithValue("$bytes",bytes);command.Parameters.AddWithValue("$hash",DurableReviewArtifactCanonicalizer.Sha256Hex(bytes));command.Parameters.AddWithValue("$id",artifact.CorrelationId);
            Assert.Equal(1,await command.ExecuteNonQueryAsync());
        }
        var authority=new PersistedAutomaticPreMutationAuthority(store,()=>_clock.Now);
        var decision=await authority.RecheckAsync(artifact,receipt,AutomaticMutationPolicyV1.Hash,CancellationToken.None);
        Assert.Equal(AutomaticPreMutationAuthorityState.Revoked,decision.State);
    }

    [Fact]
    public async Task ReceiptTimeDriftBetweenClaimAndMutationIsConflicting()
    {
        var store=Store();var artifact=Artifact();var receipt=Receipt(artifact);await ApproveAndExecuteState(store,artifact,receipt);
        var authority=new PersistedAutomaticPreMutationAuthority(store,()=>_clock.Now);
        var decision=await authority.RecheckAsync(artifact,receipt with{ExpiresAtUtc=receipt.ExpiresAtUtc.AddSeconds(1)},AutomaticMutationPolicyV1.Hash,CancellationToken.None);
        Assert.Equal(AutomaticPreMutationAuthorityState.Conflicting,decision.State);
    }

    [Fact]
    public async Task UnknownOutcomeAfterRestartIsQuarantinedAndNeverResubmitted()
    {
        var store=Store();var artifact=Artifact();await Approve(store,artifact,Receipt(artifact));var firstGateway=new Gateway{Execution=new(AutomaticGatewayExecutionState.Unknown,"unknown")};
        var first=new AutomaticExecutionProcessor(store,new Validator(),firstGateway,()=>_clock.Now);
        Assert.Equal("automatic.unknown-outcome",(await first.ProcessNextAsync("worker",CancellationToken.None)).Code);Assert.Equal(1,firstGateway.ExecuteCount);
        _clock.Now=_clock.Now.AddMinutes(1);var restartedGateway=new Gateway{Reconciliation=new(AutomaticGatewayReconciliationState.Unknown,"unknown")};var restarted=new AutomaticExecutionProcessor(store,new Validator(),restartedGateway,()=>_clock.Now);
        var result=await restarted.ReconcileNextAsync("restart-worker",CancellationToken.None);
        Assert.Equal(0,restartedGateway.ExecuteCount);Assert.Equal(1,restartedGateway.ReconcileCount);Assert.NotNull(result.RecoveryHandoff);
        Assert.True(result.RecoveryHandoff!.Quarantined);Assert.False(result.RecoveryHandoff.ResubmitAllowed);Assert.Equal("UnknownOutcome",result.RecoveryHandoff.State);
    }

    [Fact]
    public void TimelineAndQuarantineHashesAreDeterministicAndOrdered()
    {
        var artifact=Artifact();var events=new[]{
            new PersistedAutomaticExecutionEvent(artifact.CorrelationId,2,_clock.Now.AddSeconds(2),AutomaticExecutionQueueStatus.Claimed,AutomaticExecutionQueueStatus.Executing,"automatic.executing","worker"),
            new PersistedAutomaticExecutionEvent(artifact.CorrelationId,1,_clock.Now,AutomaticExecutionQueueStatus.RiskApproved,AutomaticExecutionQueueStatus.Claimed,"automatic.claimed","worker")};
        var first=ModelOffExecutionRecoveryContractV1.Timeline(artifact.CorrelationId,events);var second=ModelOffExecutionRecoveryContractV1.Timeline(artifact.CorrelationId,events.Reverse().ToArray());
        Assert.Equal(first.TimelineSha256,second.TimelineSha256);Assert.Equal(new[]{1,2},first.Entries.Select(x=>x.Sequence));
    }

    [Fact]
    public void ProductionMutationChainAndRecoveryContainNoResubmitPath()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var gateway=File.ReadAllText(Path.Combine(root,"Services","Agent","TradingExecutionGateway.cs"));var processor=File.ReadAllText(Path.Combine(root,"Services","Agent","AutomaticExecutionProcessor.cs"));
        Assert.Contains("_gateway.ExecutePlanAsync",gateway,StringComparison.Ordinal);Assert.Contains("_executor.ExecutePlanAsync",gateway,StringComparison.Ordinal);
        var reconcile=processor[processor.IndexOf("public async Task<AutomaticExecutionProcessorResult> ReconcileNextAsync",StringComparison.Ordinal)..];
        Assert.Contains("_gateway.ReconcileAsync",reconcile,StringComparison.Ordinal);Assert.DoesNotContain("_gateway.ExecuteAsync",reconcile,StringComparison.Ordinal);
    }

    private AgentSqliteStore Store()=>new(DatabasePath,()=>_clock.Now);
    private async Task Approve(AgentSqliteStore store,DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt){Assert.True((await store.SaveAutomaticExecutionAsync(artifact.CorrelationId,artifact,CancellationToken.None)).Succeeded);Assert.True((await store.RecordAutomaticRiskDecisionAsync(artifact.CorrelationId,receipt,CancellationToken.None)).Succeeded);}
    private async Task ApproveAndExecuteState(AgentSqliteStore store,DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt){await Approve(store,artifact,receipt);Assert.True((await store.TryClaimAutomaticExecutionAsync(artifact.CorrelationId,"worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);Assert.True((await store.TryTransitionAutomaticExecutionAsync(artifact.CorrelationId,AutomaticExecutionQueueStatus.Claimed,AutomaticExecutionQueueStatus.Executing,"worker","automatic.executing",CancellationToken.None)).Succeeded);}
    private DurableExecutionArtifactV2 Artifact()=>new(2,"cycle-mo07",[new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-MO07","strategy.entry","OpenLong","Limit",50_000m,50_100m)],5,true,"binance","Testnet","strategy","v1",_clock.Now.AddSeconds(-20),"market-v1",_clock.Now.AddSeconds(-10),_clock.Now.AddMinutes(2));
    private DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact){var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);return new("risk-mo07",artifact.CorrelationId,hashes.IntentHash,true,_clock.Now.AddSeconds(-1),_clock.Now.AddMinutes(1),null,hashes.ArtifactHash);}
    private AutomaticPreMutationAuthorityDecisionV1 Decision(AutomaticPreMutationAuthorityState state,DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt){var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);return new(1,state,hashes.ArtifactHash,hashes.IntentHash,receipt.ReceiptId,AutomaticMutationPolicyV1.Hash,_clock.Now,_clock.Now.AddMinutes(1));}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    private sealed class Clock(DateTimeOffset now){public DateTimeOffset Now{get;set;}=now;}
    private sealed class Validator:IAutomaticExecutionReadOnlyValidator{public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct)=>Task.FromResult(new AutomaticExecutionRuntimeFacts(AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,"ok"));}
    private sealed class Gateway:IAutomaticExecutionGateway
    {
        public bool IsTestnet=>true;public AutomaticGatewayExecutionResult Execution{get;set;}=new(AutomaticGatewayExecutionState.Succeeded,"ok");public AutomaticGatewayReconciliationResult Reconciliation{get;set;}=new(AutomaticGatewayReconciliationState.Succeeded,"ok");public int ExecuteCount{get;private set;}public int ReconcileCount{get;private set;}
        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,CancellationToken ct){ExecuteCount++;return Task.FromResult(Execution);}public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct){ReconcileCount++;return Task.FromResult(Reconciliation);}
    }
}
