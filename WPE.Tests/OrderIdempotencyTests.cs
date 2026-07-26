using 币安量化机器人.Services.Agent;
using Microsoft.Data.Sqlite;

namespace WPE.Tests;

public sealed class OrderIdempotencyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReplayingSameClientOrderId_DoesNotSubmitSecondMarketOrder()
    {
        var exchange = new FakeExchangeAdapter();
        var store = new AgentSqliteStore(Path.Combine(_directory, "agent.db"));
        var executor = new ReliableOrderExecutor(exchange, store);
        var intent = TestIntentFactory.Opening("stable-client-id");

        await executor.ExecuteAsync("cycle-1", intent, 10, true, CancellationToken.None);
        await executor.ExecuteAsync("cycle-1", intent, 10, true, CancellationToken.None);

        Assert.Equal(1, exchange.MarketOrderSubmissions);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeExchangeAdapter : IExchangeAdapter
    {
        private readonly Dictionary<string, ExchangeOrder> _orders = new(StringComparer.Ordinal);
        public int MarketOrderSubmissions { get; private set; }
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;

        public Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct)
        {
            MarketOrderSubmissions++;
            var order = new ExchangeOrder(symbol, MarketOrderSubmissions.ToString(), clientOrderId, "FILLED", quantity, 50_000m, "MARKET", side, false, DateTime.UtcNow);
            _orders[clientOrderId] = order;
            return Task.FromResult(order);
        }
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct) => PlaceMarketAsync(symbol,side,quantity,clientOrderId,reduceOnly,ct);

        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            _orders.TryGetValue(clientOrderId, out var order);
            return Task.FromResult(order);
        }

        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) =>
            Task.FromResult(new ExchangeOrder(symbol,"100", groupId + "-SL", "NEW", 0, 0, "STOP_MARKET", sideToClose, true, DateTime.UtcNow));

        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) => Task.CompletedTask;
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) => Task.CompletedTask;
        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) => Task.CompletedTask;
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExchangeOrder>>(Array.Empty<ExchangeOrder>());
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => Task.FromResult(new MarketEvidence(symbol,50_000m,49_000m,51_000m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow){Quality=new(){QualityScore=100,LiquidityScore=1,SpreadBps=1,AtrPercent=.01}});
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
