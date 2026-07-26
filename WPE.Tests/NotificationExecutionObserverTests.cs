using Microsoft.Data.Sqlite;
using WpeAgent.Notifications;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class NotificationExecutionObserverTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-notify-exec-"+Guid.NewGuid().ToString("N"));
    private readonly AgentStatus _originalStatus=ServiceLocator.SystemState.Status;

    public NotificationExecutionObserverTests()
    {
        Directory.CreateDirectory(_directory);
        ServiceLocator.SystemState.Status=AgentStatus.Running;
    }

    [Fact]
    public async Task ConfirmedOpenAndProtectionPublishTruthEvents()
    {
        var exchange=new RecordingExchange("FILLED",1m);
        var observer=new RecordingObserver();
        var executor=Executor(exchange,observer);

        var result=await executor.ExecuteAsync("cycle-1",Intent(),5,true,default);

        Assert.Contains("Protected",result,StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            observer.Events,
            value=>Assert.Equal(NotificationEventKind.PositionOpened,value.Kind),
            value=>Assert.Equal(NotificationEventKind.ProtectionPlaced,value.Kind));
        Assert.All(observer.Events,value=>
        {
            Assert.Equal("SOLUSDT",value.Symbol);
            Assert.Equal("Testnet",value.Environment);
        });
        Assert.Equal(1m,observer.Events[0].Quantity);
    }

    [Fact]
    public async Task RejectedUnfilledOrderDoesNotPublishTradeNotification()
    {
        var exchange=new RecordingExchange("REJECTED",0);
        var observer=new RecordingObserver();
        var executor=Executor(exchange,observer);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            executor.ExecuteAsync("cycle-rejected",Intent(),5,true,default));

        Assert.Empty(observer.Events);
    }

    [Fact]
    public async Task ThrowingObserverCannotChangeSuccessfulExecution()
    {
        var exchange=new RecordingExchange("FILLED",1m);
        var executor=Executor(exchange,new ThrowingObserver());

        var result=await executor.ExecuteAsync("cycle-observer-fail",Intent(),5,true,default);

        Assert.Contains("Protected",result,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1,exchange.PlaceCount);
    }

    [Fact]
    public async Task PersistedPreflightBlockPublishesRiskBlockedWithoutTrade()
    {
        var exchange=new RecordingExchange("FILLED",1m)
        {
            MarketQuality=new(){QualityScore=20,LiquidityScore=.1,SpreadBps=50,AtrPercent=.2}
        };
        var observer=new RecordingObserver();
        var executor=Executor(exchange,observer);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            executor.ExecuteAsync("cycle-risk",Intent(),5,true,default));

        Assert.Equal(0,exchange.PlaceCount);
        Assert.Equal(NotificationEventKind.RiskBlocked,Assert.Single(observer.Events).Kind);
    }

    private ReliableOrderExecutor Executor(IExchangeAdapter exchange,IConfirmedNotificationObserver observer)
    {
        var db=new AgentSqliteStore(Path.Combine(_directory,Guid.NewGuid().ToString("N")+".db"));
        var capability=new ExchangeCapability(
            "test","test","SOLUSDT","SOLUSDT",MarketType.Perpetual,
            CapabilityStatus.Available,true,true,true,DateTimeOffset.UtcNow);
        return new(exchange,db,new RiskLimits(),SystemOrderPollScheduler.Instance,
            new Dictionary<string,ExchangeCapability>{{"SOLUSDT",capability}},true,observer);
    }

    private static ExecutionIntent Intent()=>new(
        "SOLUSDT",PositionSide.Long,1m,false,90m,120m,"notify-open","test",
        DecisionAction.OpenLong,ExpectedPrice:100m);

    public void Dispose()
    {
        ServiceLocator.SystemState.Status=_originalStatus;
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch{}
    }

    private sealed class RecordingObserver:IConfirmedNotificationObserver
    {
        public List<ConfirmedNotificationEvent> Events{get;}=[];
        public Task ObserveAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)
        {
            Events.Add(notificationEvent);
            return Task.CompletedTask;
        }
    }
    private sealed class ThrowingObserver:IConfirmedNotificationObserver
    {
        public Task ObserveAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)=>
            throw new IOException("notification outbox failed");
    }

    private sealed class RecordingExchange(string status,decimal executed):IExchangeAdapter
    {
        private ExchangeOrder? _order;
        public MarketQualityEvidence MarketQuality{get;init;}=new(){QualityScore=95,LiquidityScore=.9,SpreadBps=1,AtrPercent=.01};
        public int PlaceCount{get;private set;}
        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>Task.FromResult(new AccountSnapshot(1000,900,1000,DateTime.UtcNow));
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ManagedPosition>>([]);
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ExchangeOrder>>([]);
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)=>Task.FromResult(new TradingRule(symbol,.001m,.1m,.001m,5m,20));
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)=>Task.FromResult(
            new MarketEvidence(symbol,100m,90m,120m,55,.1,.2,.3,new(0,1,0,0,0,0,0),DateTime.UtcNow)
            {
                Quality=MarketQuality
            });
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DerivativesSnapshot>>([]);
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandleEvidence>>([]);
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CandleEvidence>>([]);
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct)=>Task.CompletedTask;
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct)=>Task.CompletedTask;
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>Task.CompletedTask;
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>Task.FromResult(_order);
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)
        {
            PlaceCount++;
            _order=new(symbol,"provider-order",clientOrderId,status,executed,executed>0?100m:0,"MARKET",side,false,DateTime.UtcNow);
            return Task.FromResult(_order);
        }
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>
            PlaceMarketAsync(symbol,side,quantity,clientOrderId,reduceOnly,ct);
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)=>
            Task.FromResult(new ExchangeOrder(symbol,"protection",groupId,"NEW",0,0,"OCO",sideToClose,true,DateTime.UtcNow));
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
