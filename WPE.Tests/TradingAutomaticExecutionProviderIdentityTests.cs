using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class TradingAutomaticExecutionProviderIdentityTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,9,19,2,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-auto-provider-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task OrderObserverRejectsArtifactBoundToDifferentProvider()
    {
        var store=new AgentSqliteStore(Database,()=>Now);
        var provider=new RecordingProvider("provider-b")
        {
            ObservedOrder=new ExchangeOrder(
                "BTCUSDT","order-other-provider","WPE-PROVIDER-MISMATCH","FILLED",
                .001m,50_000m,"MARKET",PositionSide.Long,false,Now.UtcDateTime)
        };
        var gateway=new TradingExecutionGateway(new NoopExecutor(),store,()=>Now);
        var automatic=new TradingAutomaticExecutionGateway(gateway,provider,store);
        var artifact=Artifact();

        var observed=await automatic.ObserveOrderAsync(artifact,artifact.Intents.Single(),default);

        Assert.Null(observed);
    }

    [Fact]
    public async Task AutomaticExecutionAllowsArtifactBoundToSameProvider()
    {
        var store=new AgentSqliteStore(Database,()=>Now);
        var provider=new RecordingProvider("provider-a");
        var executor=new RecordingExecutor();
        var gateway=new TradingExecutionGateway(executor,store,()=>Now);
        var automatic=new TradingAutomaticExecutionGateway(
            gateway,provider,store,new AllowedAuthority(),()=>Now);
        var artifact=Artifact();
        var receipt=Receipt(artifact);

        var result=await automatic.ExecuteAsync(artifact,receipt,default);

        Assert.True(result.State==AutomaticGatewayExecutionState.Succeeded,result.Code);
        Assert.Equal(1,executor.MutationCount);
    }

    [Fact]
    public async Task AutomaticExecutionRejectsArtifactBoundToDifferentProvider()
    {
        var store=new AgentSqliteStore(Database,()=>Now);
        var provider=new RecordingProvider("provider-b");
        var executor=new RecordingExecutor();
        var gateway=new TradingExecutionGateway(executor,store,()=>Now);
        var automatic=new TradingAutomaticExecutionGateway(
            gateway,provider,store,new AllowedAuthority(),()=>Now);
        var artifact=Artifact();
        var receipt=Receipt(artifact);

        var result=await automatic.ExecuteAsync(artifact,receipt,default);

        Assert.Equal(AutomaticGatewayExecutionState.Rejected,result.State);
        Assert.Equal("automatic.provider-mismatch",result.Code);
        Assert.Equal(0,executor.MutationCount);
    }

    [Fact]
    public async Task AutomaticReconciliationRejectsArtifactBoundToDifferentProvider()
    {
        var store=new AgentSqliteStore(Database,()=>Now);
        var provider=new RecordingProvider("provider-b");
        var gateway=new TradingExecutionGateway(new NoopExecutor(),store,()=>Now);
        var automatic=new TradingAutomaticExecutionGateway(gateway,provider,store);

        var result=await automatic.ReconcileAsync(Artifact(),default);

        Assert.Equal(AutomaticGatewayReconciliationState.Failed,result.State);
        Assert.Equal("automatic.provider-mismatch",result.Code);
    }

    private static DurableExecutionArtifactV2 Artifact()=>new(
        DurableExecutionArtifactV2.Version,
        "provider-mismatch",
        [new(0,"BTCUSDT","Long",.001m,false,49_000m,51_000m,
            "WPE-PROVIDER-MISMATCH","strategy.entry","OpenLong","Market",0,50_000m)],
        5,true,"provider-a","Testnet","strategy","v1",
        Now.AddSeconds(-20),"market-v1",Now.AddSeconds(-10),Now.AddMinutes(2));

    private static DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact)
    {
        var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        return new("risk-provider-mismatch",artifact.CorrelationId,hashes.IntentHash,true,
            Now.AddSeconds(-1),Now.AddMinutes(1),null,hashes.ArtifactHash);
    }

    private sealed class AllowedAuthority:IAutomaticPreMutationAuthority
    {
        public Task<AutomaticPreMutationAuthorityDecisionV1> RecheckAsync(
            DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,string policyHash,CancellationToken ct)
        {
            var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
            return Task.FromResult(new AutomaticPreMutationAuthorityDecisionV1(
                AutomaticMutationPolicyV1.Version,AutomaticPreMutationAuthorityState.Allowed,
                hashes.ArtifactHash,hashes.IntentHash,receipt.ReceiptId,policyHash,
                Now,Now.AddMinutes(1)));
        }
    }

    private sealed class RecordingExecutor:ITradingMutationExecutor
    {
        public bool IsTestnet=>true;
        public int MutationCount{get;private set;}
        public Task<string> ExecutePlanAsync(
            string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)
        {
            MutationCount++;
            return Task.FromResult("submitted");
        }
    }

    private sealed class NoopExecutor:ITradingMutationExecutor
    {
        public bool IsTestnet=>true;
        public Task<string> ExecutePlanAsync(string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)=>
            Task.FromResult("unused");
    }
    private sealed class RecordingProvider(string providerId):IExchangeProvider,IMarketDataProvider,IBrokerProvider
    {
        public int MutationCount{get;private set;}
        public ExchangeOrder? ObservedOrder{get;init;}
        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public string ConnectionId=>"test-account";
        public string ProviderId{get;}=providerId;
        public ExchangeProviderDescriptor Descriptor=>new(ProviderId,"Test",ExchangeAssetClass.CryptoCex,true,false,[],new HashSet<string>());
        public ISymbolMapper Symbols=>new ConventionSymbolMapper(ProviderId);
        public IMarketDataProvider MarketData=>this;
        public IBrokerProvider Broker=>this;
        public Task<bool> PingAsync(CancellationToken ct)=>Task.FromResult(true);
        public Task<DateTime> GetServerTimeAsync(CancellationToken ct)=>Task.FromResult(DateTime.UtcNow);
        public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)=>
            Task.FromResult(new ExchangePermissionSnapshot(true,true,false,ConnectionId,[]));
        public Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)=>
            Task.FromResult(new ExchangeHealthSnapshot(true,1,0,"ok",DateTime.UtcNow));
        public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> symbols,AgentSqliteStore database)=>null;
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>
            Task.FromResult(new AccountSnapshot(1_000,1_000,1_000,DateTime.UtcNow));
        public Task<MarginSnapshot> GetMarginAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ManagedPosition>>([]);
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ExchangeOrder>>([]);
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)=>
            Task.FromResult(new TradingRule(symbol,.001m,.1m,.001m,5m,20));
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)=>
            Task.FromResult(new MarketEvidence(symbol,50_000m,49_000m,51_000m,50,0,0,0,
                new(0,1,1,1,1,1,0),DateTime.UtcNow){Quality=new(){QualityScore=100,LiquidityScore=1,SpreadBps=1,AtrPercent=.01}});
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DerivativesSnapshot>>([]);
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandleEvidence>>([]);
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandleEvidence>>([]);
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)
        {MutationCount++;return Task.FromResult(new ExchangeOrder(symbol,"order-1",clientOrderId,"FILLED",quantity,50_000m,"MARKET",side,reduceOnly,DateTime.UtcNow));}
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>PlaceMarketAsync(symbol,side,quantity,clientOrderId,reduceOnly,ct);
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide side,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)=>Task.FromResult(new ExchangeOrder(symbol,"protection-1",groupId,"NEW",0,0,"OCO",side,true,DateTime.UtcNow));
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>Task.FromResult(ObservedOrder);
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
