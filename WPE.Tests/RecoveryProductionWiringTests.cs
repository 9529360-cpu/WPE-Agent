using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;
using Xunit;

namespace WPE.Tests;

public sealed class RecoveryProductionWiringTests
{
    [Fact]
    public async Task ProductionComposition_UsesProviderTruth_AndOnlyReduceOnlyMutation()
    {
        if(!OperatingSystem.IsWindows())return;
        var root=Path.Combine(Path.GetTempPath(),"wpe-recovery-wiring",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider=new RecordingProvider();
            var store=new AgentSqliteStore(Path.Combine(root,"agent.db"));
            var executor=new ReliableOrderExecutor(provider,store,new RiskLimits(),SystemOrderPollScheduler.Instance,
                new Dictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SOLUSDT"]=new("test","test-provider","SOLUSDT","SOLUSDT",WpeAgent.RuntimeContracts.MarketType.Perpetual,
                        WpeAgent.RuntimeContracts.CapabilityStatus.Available,true,true,true,DateTimeOffset.UtcNow,"ok")
                },true);
            var services=await ProductionRecoveryComposition.CreateAsync(provider,executor,store,Path.Combine(root,"receipt-key.json"));
            var intent=new ExecutionIntent("SOLUSDT",PositionSide.Long,1m,true,0,0,"recovery-1","recovery",
                DecisionAction.CloseLong,ExecutionOrderType.Market,ExpectedPrice:150m);

            var result=await services.Recovery.ExecuteAsync("correlation-1",intent,5,true,CancellationToken.None);

            Assert.True(result.Executed);
            Assert.Equal(1,provider.MutationCount);
            Assert.True(provider.LastReduceOnly);
            Assert.True(File.Exists(Path.Combine(root,"receipt-key.json")));
            Assert.DoesNotContain(nameof(ITrustedRecoveryReconciler),typeof(ProductionRecoveryServices).GetProperties().Select(x=>x.PropertyType.Name));
            Assert.DoesNotContain(nameof(IRecoverySigningKeyStore),typeof(ProductionRecoveryServices).GetProperties().Select(x=>x.PropertyType.Name));
        }
        finally{TryDelete(root);}
    }

    [Fact]
    public async Task AutoTradingAgent_RealRecoveryEntry_ExecutesAndObservesProductionFacade()
    {
        if(!OperatingSystem.IsWindows())return;
        var root=Path.Combine(Path.GetTempPath(),"wpe-recovery-entry",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider=new RecordingProvider();
            var store=new AgentSqliteStore(Path.Combine(root,"agent.db"));
            var executor=new ReliableOrderExecutor(provider,store,new RiskLimits(),SystemOrderPollScheduler.Instance,
                new Dictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SOLUSDT"]=new("test","test-provider","SOLUSDT","SOLUSDT",WpeAgent.RuntimeContracts.MarketType.Perpetual,
                        WpeAgent.RuntimeContracts.CapabilityStatus.Available,true,true,true,DateTimeOffset.UtcNow,"ok")
                },true);
            var services=await ProductionRecoveryComposition.CreateAsync(provider,executor,store,Path.Combine(root,"receipt-key.json"));
            var position=(await provider.GetPositionsAsync(CancellationToken.None)).Single();
            var intent=new ExecutionIntent("SOLUSDT",PositionSide.Long,1m,true,0,0,"auto-entry-1","position management",
                DecisionAction.CloseLong,ExecutionOrderType.Market,ExpectedPrice:150m);

            var completed=await 币安量化机器人.Services.AutoTradingAgent.ExecutePositionManagementRecoveryAsync(
                services.Recovery,"auto-correlation-1",[intent],[position],CancellationToken.None);

            Assert.Equal(1,completed);
            Assert.Equal(1,provider.MutationCount);
            Assert.True(provider.LastReduceOnly);
        }
        finally{TryDelete(root);}
    }

    [Fact]
    public async Task ProductionComposition_FailsClosed_WhenProviderObservationIsNotTrusted()
    {
        if(!OperatingSystem.IsWindows())return;
        var root=Path.Combine(Path.GetTempPath(),"wpe-recovery-wiring",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider=new RecordingProvider{CanRead=false};
            var store=new AgentSqliteStore(Path.Combine(root,"agent.db"));
            var executor=new RecordingExecutor();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ProductionRecoveryComposition.CreateAsync(
                provider,executor,store,Path.Combine(root,"receipt-key.json")));
            Assert.Equal(0,executor.MutationCount);
        }
        finally{TryDelete(root);}
    }

    private static void TryDelete(string path)
    {
        try{Directory.Delete(path,true);}catch(IOException){}
    }

    private sealed class RecordingExecutor:ITradingMutationExecutor
    {
        public bool IsTestnet=>true;
        public string ProviderId=>"test-provider";
        public string AccountId=>"test-account";
        public int MutationCount{get;private set;}
        public Task<string> ExecutePlanAsync(string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<string> ExecuteReduceOnlyRecoveryAsync(string correlationId,ExecutionIntent intent,CancellationToken ct){MutationCount++;return Task.FromResult("ok");}
    }

    private sealed class RecordingProvider:IExchangeProvider,IMarketDataProvider,IBrokerProvider
    {
        public bool CanRead{get;init;}=true;
        public int MutationCount{get;private set;}
        public bool LastReduceOnly{get;private set;}
        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public string ConnectionId=>"test-account";
        public string ProviderId=>"test-provider";
        public ExchangeProviderDescriptor Descriptor=>new("test-provider","Test",ExchangeAssetClass.CryptoCex,true,false,[],new HashSet<string>());
        public ISymbolMapper Symbols=>new ConventionSymbolMapper(ProviderId);
        public IMarketDataProvider MarketData=>this;
        public IBrokerProvider Broker=>this;
        public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)=>Task.FromResult(new ExchangePermissionSnapshot(CanRead,true,false,ConnectionId,[]));
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ManagedPosition>>([new("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m)]);
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>Task.FromResult<ExchangeOrder?>(null);
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct){MutationCount++;LastReduceOnly=reduceOnly;return Task.FromResult(new ExchangeOrder(symbol,"order-1",clientOrderId,"FILLED",quantity,150m,"MARKET",side,reduceOnly,DateTime.UtcNow));}
        public Task<bool> PingAsync(CancellationToken ct)=>Task.FromResult(true);
        public Task<DateTime> GetServerTimeAsync(CancellationToken ct)=>Task.FromResult(DateTime.UtcNow);
        public Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)=>Task.FromResult(new ExchangeHealthSnapshot(true,1,0,"ok",DateTime.UtcNow));
        public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> symbols,AgentSqliteStore database)=>null;
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<MarginSnapshot> GetMarginAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExchangeOrder>>([]);
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct)=>throw new InvalidOperationException();
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct)=>throw new InvalidOperationException();
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide side,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)=>throw new InvalidOperationException();
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
