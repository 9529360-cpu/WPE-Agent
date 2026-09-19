using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class AutomaticExecutionProcessorTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-automatic-processor-"+Guid.NewGuid().ToString("N"));
    private readonly Clock _clock=new(new(2026,7,22,6,0,0,TimeSpan.Zero));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task OnlyRiskApprovedRowsCanReachValidatorOrGateway()
    {
        var store=Store();await store.SaveAutomaticExecutionAsync("proposed",Artifact(),CancellationToken.None);var validator=new Validator();var gateway=new Gateway();var result=await Processor(store,validator,gateway).ProcessNextAsync("worker",CancellationToken.None);
        Assert.False(result.Handled);Assert.Equal("automatic.no-risk-approved-work",result.Code);Assert.Equal(0,validator.CallCount);Assert.Equal(0,gateway.ExecuteCount);
    }

    [Theory]
    [InlineData(AutomaticFactState.Unknown,AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,AutomaticExecutionQueueStatus.PolicyBlocked)]
    [InlineData(AutomaticFactState.True,AutomaticFactState.Unknown,AutomaticFactState.True,AutomaticFactState.True,AutomaticExecutionQueueStatus.PolicyBlocked)]
    [InlineData(AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.Unknown,AutomaticFactState.True,AutomaticExecutionQueueStatus.CapabilityUnavailable)]
    [InlineData(AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.Unknown,AutomaticExecutionQueueStatus.MarketStale)]
    [InlineData(AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.False,AutomaticExecutionQueueStatus.MarketStale)]
    public async Task UnknownOrStaleFacts_BlockWithZeroMutation(AutomaticFactState testnet,AutomaticFactState runtime,AutomaticFactState capability,AutomaticFactState market,AutomaticExecutionQueueStatus expected)
    {
        var store=Store();await Approve(store,"facts",Artifact());var validator=new Validator{Facts=new(testnet,runtime,capability,market,"fake")};var gateway=new Gateway();var result=await Processor(store,validator,gateway).ProcessNextAsync("worker",CancellationToken.None);
        Assert.True(result.Handled);Assert.Equal(0,gateway.ExecuteCount);Assert.Equal(expected,(await store.GetAutomaticExecutionAsync("facts",CancellationToken.None))!.Status);Assert.Contains((await store.GetAutomaticExecutionEventsAsync("facts",100,CancellationToken.None)),x=>x.ToStatus==expected);
    }

    [Fact]
    public async Task MainnetGateway_BlocksWithZeroMutation()
    {
        var store=Store();await Approve(store,"mainnet",Artifact());var gateway=new Gateway{IsTestnet=false};var result=await Processor(store,new Validator(),gateway).ProcessNextAsync("worker",CancellationToken.None);
        Assert.True(result.Handled);Assert.Equal("automatic.testnet-required",result.Code);Assert.Equal(0,gateway.ExecuteCount);Assert.Equal(AutomaticExecutionQueueStatus.PolicyBlocked,(await store.GetAutomaticExecutionAsync("mainnet",CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task MismatchedPersistedReceipt_BlocksAfterClaimWithZeroMutation()
    {
        var store=Store();var artifact=Artifact();await Approve(store,"mismatch",artifact);var valid=Receipt(artifact);var mismatch=valid with{CorrelationId="different-correlation"};var bytes=DurableExecutionArtifactCanonicalizerV2.SerializeRiskReceipt(mismatch);await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE automatic_execution_queue SET risk_receipt_bytes=$bytes,risk_receipt_hash=$hash WHERE execution_id='mismatch'";q.Parameters.AddWithValue("$bytes",bytes);q.Parameters.AddWithValue("$hash",DurableReviewArtifactCanonicalizer.Sha256Hex(bytes));await q.ExecuteNonQueryAsync();}
        var gateway=new Gateway();var result=await Processor(store,new Validator(),gateway).ProcessNextAsync("worker",CancellationToken.None);
        Assert.True(result.Handled);Assert.Equal("automatic.claim-revalidation-failed",result.Code);Assert.Equal(0,gateway.ExecuteCount);Assert.Equal(AutomaticExecutionQueueStatus.PolicyBlocked,(await store.GetAutomaticExecutionAsync("mismatch",CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ExpiredArtifactOrReceipt_BlocksAfterClaimWithZeroMutation()
    {
        var store=Store();await Approve(store,"expired",Artifact(expires:_clock.Now.AddSeconds(20)));_clock.Now=_clock.Now.AddMinutes(2);var gateway=new Gateway();var result=await Processor(store,new Validator(),gateway).ProcessNextAsync("worker",CancellationToken.None);
        Assert.True(result.Handled);Assert.Equal(0,gateway.ExecuteCount);Assert.Equal(AutomaticExecutionQueueStatus.PolicyBlocked,(await store.GetAutomaticExecutionAsync("expired",CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ValidFacts_DelegateOnceAndAppendSucceededEvent()
    {
        var store=Store();await Approve(store,"success",Artifact());var gateway=new Gateway();var result=await Processor(store,new Validator(),gateway).ProcessNextAsync("worker",CancellationToken.None);
        Assert.True(result.Handled);Assert.Equal(1,gateway.ExecuteCount);Assert.Equal(AutomaticExecutionQueueStatus.Succeeded,(await store.GetAutomaticExecutionAsync("success",CancellationToken.None))!.Status);var events=await store.GetAutomaticExecutionEventsAsync("success",100,CancellationToken.None);Assert.Contains(events,x=>x.ToStatus==AutomaticExecutionQueueStatus.Executing);Assert.Contains(events,x=>x.ToStatus==AutomaticExecutionQueueStatus.Succeeded);
    }

    [Fact]
    public async Task RejectedGatewayPreservesSafeDiagnosticCode()
    {
        var store=Store();await Approve(store,"rejected",Artifact());
        var gateway=new Gateway{Execution=new(AutomaticGatewayExecutionState.Rejected,"execution.intent-hash-mismatch")};

        var result=await Processor(store,new Validator(),gateway).ProcessNextAsync("worker",CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("execution.intent-hash-mismatch",result.Code);
        Assert.Equal(AutomaticExecutionQueueStatus.FailedTerminal,(await store.GetAutomaticExecutionAsync("rejected",CancellationToken.None))!.Status);
        Assert.Contains(await store.GetAutomaticExecutionEventsAsync("rejected",100,CancellationToken.None),x=>x.EventCode=="execution.intent-hash-mismatch");
    }

    [Fact]
    public async Task UnknownOutcome_ReconcilesByQueryAndNeverResubmits()
    {
        var store=Store();await Approve(store,"unknown",Artifact());var gateway=new Gateway{Execution=new(AutomaticGatewayExecutionState.Unknown,"unknown"),Reconciliation=new(AutomaticGatewayReconciliationState.Unknown,"unknown")};var processor=Processor(store,new Validator(),gateway);
        Assert.Equal("automatic.unknown-outcome",(await processor.ProcessNextAsync("worker",CancellationToken.None)).Code);Assert.Equal(1,gateway.ExecuteCount);Assert.Equal("automatic.no-risk-approved-work",(await processor.ProcessNextAsync("worker",CancellationToken.None)).Code);Assert.Equal(1,gateway.ExecuteCount);
        _clock.Now=_clock.Now.AddMinutes(1);var worker=new AutomaticExecutionWorker(processor);Assert.Equal("automatic.reconcile-unknown",(await worker.RunOnceAsync("reconciler",CancellationToken.None)).Code);Assert.Equal(1,gateway.ExecuteCount);Assert.Equal(1,gateway.ReconcileCount);Assert.Equal(AutomaticExecutionQueueStatus.UnknownOutcome,(await store.GetAutomaticExecutionAsync("unknown",CancellationToken.None))!.Status);
        var statuses=(await store.GetAutomaticExecutionEventsAsync("unknown",100,CancellationToken.None)).Select(x=>x.ToStatus).ToArray();Assert.Contains(AutomaticExecutionQueueStatus.Reconciling,statuses);Assert.Equal(2,statuses.Count(x=>x==AutomaticExecutionQueueStatus.UnknownOutcome));
    }

    [Fact]
    public void ProcessorAndWorker_HaveCorrectUtf8NamespaceAndNoDirectMutationDependency()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));foreach(var name in new[]{"AutomaticExecutionProcessor.cs","AutomaticExecutionWorker.cs"}){var bytes=File.ReadAllBytes(Path.Combine(root,"Services","Agent",name));var source=new System.Text.UTF8Encoding(false,true).GetString(bytes);Assert.Contains("namespace 币安量化机器人.Services.Agent;",source,StringComparison.Ordinal);Assert.DoesNotContain("IExchangeAdapter",source,StringComparison.Ordinal);Assert.DoesNotContain("ReliableOrderExecutor",source,StringComparison.Ordinal);Assert.DoesNotContain("ITradingMutationExecutor",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceMarket",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceLimit",source,StringComparison.Ordinal);Assert.DoesNotContain("CancelOrder",source,StringComparison.Ordinal);}
    }

    private AgentSqliteStore Store()=>new(DatabasePath,()=>_clock.Now);
    private AutomaticExecutionProcessor Processor(AgentSqliteStore store,Validator validator,Gateway gateway)=>new(store,validator,gateway,()=>_clock.Now);
    private async Task Approve(AgentSqliteStore store,string id,DurableExecutionArtifactV2 artifact){Assert.True((await store.SaveAutomaticExecutionAsync(id,artifact,CancellationToken.None)).Succeeded);Assert.True((await store.RecordAutomaticRiskDecisionAsync(id,Receipt(artifact),CancellationToken.None)).Succeeded);}
    private DurableExecutionArtifactV2 Artifact(DateTimeOffset? expires=null)=>new(2,"cycle-auto",[new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-AUTO-PROC","strategy.entry","OpenLong","Limit",50_000m,50_100m)],5,true,"binance","Testnet","strategy-alpha","v2",_clock.Now.AddSeconds(-20),"book-v5",_clock.Now.AddSeconds(-10),expires??_clock.Now.AddMinutes(5));
    private DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact){var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);return new("risk-auto",artifact.CorrelationId,hashes.IntentHash,true,_clock.Now,_clock.Now.AddMinutes(1),null,hashes.ArtifactHash);}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    private sealed class Clock(DateTimeOffset value){public DateTimeOffset Now{get;set;}=value;}
    private sealed class Validator:IAutomaticExecutionReadOnlyValidator{public AutomaticExecutionRuntimeFacts Facts{get;set;}=new(AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,AutomaticFactState.True,"ok");public int CallCount{get;private set;}public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct){CallCount++;return Task.FromResult(Facts);}}
    private sealed class Gateway:IAutomaticExecutionGateway
    {
        public bool IsTestnet{get;set;}=true;public AutomaticGatewayExecutionResult Execution{get;set;}=new(AutomaticGatewayExecutionState.Succeeded,"ok");public AutomaticGatewayReconciliationResult Reconciliation{get;set;}=new(AutomaticGatewayReconciliationState.Succeeded,"ok");public int ExecuteCount{get;private set;}public int ReconcileCount{get;private set;}
        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,CancellationToken ct){ExecuteCount++;return Task.FromResult(Execution);}public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct){ReconcileCount++;return Task.FromResult(Reconciliation);}
    }
}
