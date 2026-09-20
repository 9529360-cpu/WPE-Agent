using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ProtectionFillReconciliationTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-protection-fill-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task ProvenStopFillBackfillsReduceOnlyLedgerAndPostTradeOutcomeIdempotently()
    {
        var store=new AgentSqliteStore(Database);
        var parent=Opening("WPE-parent-1234567890");
        await RecordOpening(store,"cycle-1",parent);
        var stop=new ExchangeOrder("BTCUSDT","stop-order",parent.ClientOrderId+"-SL","FILLED",1m,90m,"MARKET",PositionSide.Long,true,DateTime.UtcNow);
        await using var exchange=new FakeExchange([stop]);

        var first=await ProtectionFillReconciliationServiceV1.ReconcileAsync(exchange,store,[],CancellationToken.None);
        var second=await ProtectionFillReconciliationServiceV1.ReconcileAsync(exchange,store,[],CancellationToken.None);

        Assert.Equal("protection-fill.reconciled",first.Code);
        Assert.Equal(1,first.Recorded);
        Assert.Equal(0,first.RemainingConflicts);
        Assert.Equal(0m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
        Assert.Equal("cycle-1",review.CycleId);
        Assert.Equal(90m,review.ExitPrice);
        Assert.Equal("loss",review.Outcome);
        Assert.Equal("protection.fill-reconciled.stop-loss",review.ExitReason);
        Assert.Equal("unavailable",review.ExcursionBasis);
        Assert.Equal(0,second.Recorded);
        Assert.Equal("protection-fill.no-conflict",second.Code);
    }

    [Fact]
    public async Task NoMatchingExchangeProofLeavesLocalConflictUntouched()
    {
        var store=new AgentSqliteStore(Database);
        var parent=Opening("WPE-parent-1234567890");
        await RecordOpening(store,"cycle-2",parent);
        var unrelated=new ExchangeOrder("BTCUSDT","other","different-parent-SL","FILLED",1m,90m,"MARKET",PositionSide.Long,true,DateTime.UtcNow);
        await using var exchange=new FakeExchange([unrelated]);

        var result=await ProtectionFillReconciliationServiceV1.ReconcileAsync(exchange,store,[],CancellationToken.None);

        Assert.Equal(0,result.Recorded);
        Assert.Equal(1,result.RemainingConflicts);
        Assert.Equal(1m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
        Assert.Empty(await store.GetRecentPostTradeReviewsAsync(10,default));
    }

    [Fact]
    public async Task OversizedProtectionFillCannotExplainSmallerLedgerDiscrepancy()
    {
        var store=new AgentSqliteStore(Database);
        var parent=Opening("WPE-parent-1234567890");
        await RecordOpening(store,"cycle-3",parent);
        var oversized=new ExchangeOrder("BTCUSDT","stop-order",parent.ClientOrderId+"-SL","FILLED",2m,90m,"MARKET",PositionSide.Long,true,DateTime.UtcNow);
        await using var exchange=new FakeExchange([oversized]);

        var result=await ProtectionFillReconciliationServiceV1.ReconcileAsync(exchange,store,[],CancellationToken.None);

        Assert.Equal(0,result.Recorded);
        Assert.Equal(1,result.RemainingConflicts);
        Assert.Equal(1m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
    }

    [Fact]
    public void BinanceProtectionIdentityRecognizesTruncatedStopAndTakeProfitChildren()
    {
        const string parent="WPE-260920002413-Ope-d8af96d077644e7";
        var stop=Order("WPE-260920002413-Ope-d8af96d07764-SL");
        var take=Order("WPE-260920002413-Ope-d8af96d07764-TP");

        Assert.True(BinanceFuturesAdapter.TryMatchProtectionFillIdentity(parent,stop,out var stopKind));
        Assert.Equal(ProtectionFillKind.StopLoss,stopKind);
        Assert.True(BinanceFuturesAdapter.TryMatchProtectionFillIdentity(parent,take,out var takeKind));
        Assert.Equal(ProtectionFillKind.TakeProfit,takeKind);
        Assert.True(BinanceFuturesAdapter.LooksLikeProtectionClientOrderId(stop.ClientOrderId));
        Assert.False(BinanceFuturesAdapter.TryMatchProtectionFillIdentity("WPE-other-parent-1234567890",stop,out _));
    }

    private static ExchangeOrder Order(string clientId)=>
        new("BTCUSDT","order",clientId,"FILLED",.0095m,80947.6m,"MARKET",PositionSide.Long,false,DateTime.UtcNow);

    private static ExecutionIntent Opening(string clientId)=>
        new("BTCUSDT",PositionSide.Long,1m,false,90m,120m,clientId,"automatic.risk-approved",DecisionAction.OpenLong,ExecutionOrderType.Limit,100m,100m);

    private static async Task RecordOpening(AgentSqliteStore store,string cycle,ExecutionIntent parent)
    {
        var order=new ExchangeOrder(parent.Symbol,"entry-order",parent.ClientOrderId,"FILLED",parent.Quantity,100m,"LIMIT",parent.Side,false,DateTime.UtcNow);
        await store.RecordExecutionAsync(cycle,parent,order,"hypothesis-v1",default);
        await store.SaveIntentAsync(cycle,parent,"PROTECTED",order.OrderId,default);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }

    private sealed class FakeExchange(IReadOnlyList<ExchangeOrder> recent):IExchangeAdapter,IProtectionFillEvidenceProvider
    {
        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        public Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(string canonicalSymbol,int limit,CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ExchangeOrder>>(recent.Where(x=>x.Symbol==canonicalSymbol).Take(limit).ToArray());
        public bool TryMatchProtectionFill(string parentClientOrderId,ExchangeOrder order,out ProtectionFillKind kind)
        {
            kind=default;
            if(order.ClientOrderId==parentClientOrderId+"-SL"){kind=ProtectionFillKind.StopLoss;return order.Status=="FILLED"&&order.ExecutedQuantity>0;}
            if(order.ClientOrderId==parentClientOrderId+"-TP"){kind=ProtectionFillKind.TakeProfit;return order.Status=="FILLED"&&order.ExecutedQuantity>0;}
            return false;
        }
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ManagedPosition>>([]);
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExchangeOrder>>([]);
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>Task.FromResult<ExchangeOrder?>(null);
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)=>throw new NotSupportedException();
    }
}
