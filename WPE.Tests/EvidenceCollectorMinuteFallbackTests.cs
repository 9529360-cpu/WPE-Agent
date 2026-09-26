using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class EvidenceCollectorMinuteFallbackTests
{
    private static readonly DateTime Now=new(2026,9,27,0,0,0,DateTimeKind.Utc);

    [Fact]
    public async Task MissingRealtimeMinutesAreBackfilledFromProvider()
    {
        var fallback=Minutes(6,Now.AddMinutes(-6));
        await using var exchange=new MinuteExchange(fallback);
        var market=Market([]);

        var result=await EvidenceCollector.EnsureMinuteCandlesAsync(exchange,market,CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal("rest-fallback",result.Source);
        Assert.Equal(6,result.Market.Candles1m.Count);
        Assert.Equal(1,exchange.MinuteReads);
        Assert.Equal("1m",exchange.LastInterval);
        Assert.Equal(12,exchange.LastLimit);
    }

    [Fact]
    public async Task FreshRealtimeMinuteWindowAvoidsRedundantProviderRead()
    {
        var realtime=Minutes(4,Now.AddMinutes(-4));
        await using var exchange=new MinuteExchange(throwOnRead:true);
        var market=Market(realtime);

        var result=await EvidenceCollector.EnsureMinuteCandlesAsync(exchange,market,CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal("realtime",result.Source);
        Assert.Equal(4,result.Market.Candles1m.Count);
        Assert.Equal(0,exchange.MinuteReads);
    }

    [Fact]
    public async Task FailedProviderBackfillDoesNotFabricateMicrostructureAvailability()
    {
        var partial=Minutes(2,Now.AddMinutes(-2));
        await using var exchange=new MinuteExchange(throwOnRead:true);
        var market=Market(partial);

        var result=await EvidenceCollector.EnsureMinuteCandlesAsync(exchange,market,CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal("unavailable",result.Source);
        Assert.Equal(2,result.Market.Candles1m.Count);
        Assert.Equal(1,exchange.MinuteReads);
    }

    private static MarketEvidence Market(IReadOnlyList<CandleEvidence> minutes)=>new(
        "ALTUSDT",100m,95m,105m,50,0,0,0,new DerivativesSnapshot(0,1,0,0,0,1,0),Now)
    {
        Candles1m=minutes
    };

    private static IReadOnlyList<CandleEvidence> Minutes(int count,DateTime start)=>
        Enumerable.Range(0,count).Select(i=>
        {
            var open=100m+i*.1m;
            var close=open+.05m;
            return new CandleEvidence(start.AddMinutes(i),open,close+.05m,open-.05m,close,10m,1000m,20,6m);
        }).ToArray();

    private sealed class MinuteExchange:IExchangeAdapter
    {
        private readonly IReadOnlyList<CandleEvidence> _candles;
        private readonly bool _throwOnRead;

        public MinuteExchange(IReadOnlyList<CandleEvidence>? candles=null,bool throwOnRead=false)
        {
            _candles=candles??[];
            _throwOnRead=throwOnRead;
        }

        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public int MinuteReads{get;private set;}
        public string? LastInterval{get;private set;}
        public int LastLimit{get;private set;}

        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)
        {
            MinuteReads++;
            LastInterval=interval;
            LastLimit=limit;
            if(_throwOnRead)throw new InvalidOperationException("minute fallback unavailable");
            return Task.FromResult(_candles);
        }

        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>throw new NotSupportedException();
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)=>throw new NotSupportedException();
    }
}
