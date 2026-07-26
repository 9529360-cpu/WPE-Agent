using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class UnknownOrderRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecoverPending_UnknownExchangeStateDoesNotSubmitReplacementOrder()
    {
        var exchange = new UnknownOrderExchange();
        var store = new AgentSqliteStore(Path.Combine(_directory, "agent.db"));
        var intent = TestIntentFactory.Opening("unknown-client-id");
        await store.SaveIntentAsync("cycle-1", intent, "INTENT", null, CancellationToken.None);
        var executor = new ReliableOrderExecutor(exchange, store);

        var result = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.NotEmpty(result.Messages);
        Assert.Equal(0, exchange.MarketOrderSubmissions);
        Assert.Equal(1, exchange.FindOrderCalls);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class UnknownOrderExchange : IExchangeAdapter
    {
        public int FindOrderCalls { get; private set; }
        public int MarketOrderSubmissions { get; private set; }
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;

        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            FindOrderCalls++;
            return Task.FromResult<ExchangeOrder?>(null);
        }

        public Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct)
        {
            MarketOrderSubmissions++;
            throw new InvalidOperationException("Recovery must not submit a replacement order");
        }
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct) => throw new NotSupportedException();

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) => throw new NotSupportedException();
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) => throw new NotSupportedException();
        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
