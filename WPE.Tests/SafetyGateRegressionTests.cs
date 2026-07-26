using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class SafetyGateRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStatus _originalStatus = ServiceLocator.SystemState.Status;

    [Fact]
    public async Task Mainnet_IsRejectedBeforeAnyExchangeMutation()
    {
        ServiceLocator.SystemState.Status = AgentStatus.Running;
        var exchange = new RecordingExchange(ExchangeEnvironment.Mainnet);
        var executor = CreateExecutor(exchange);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle", Intent(reduceOnly: false), 10, true, CancellationToken.None));

        Assert.Contains("Testnet", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, exchange.MutationCount);
    }

    [Theory]
    [InlineData(AgentStatus.Degraded)]
    [InlineData(AgentStatus.Stopped)]
    public async Task DegradedOrStopped_RejectsRiskIncreaseBeforeAnyExchangeMutation(AgentStatus status)
    {
        ServiceLocator.SystemState.Status = status;
        var exchange = new RecordingExchange(ExchangeEnvironment.Testnet);
        var executor = CreateExecutor(exchange);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle", Intent(reduceOnly: false), 10, true, CancellationToken.None));

        Assert.Contains(status.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, exchange.MutationCount);
    }

    [Theory]
    [InlineData(AgentStatus.Degraded)]
    [InlineData(AgentStatus.Stopped)]
    public async Task DegradedOrStopped_AllowsReduceOnly(AgentStatus status)
    {
        ServiceLocator.SystemState.Status = status;
        var exchange = new RecordingExchange(ExchangeEnvironment.Testnet);
        var executor = CreateExecutor(exchange);

        var result = await executor.ExecuteAsync("cycle", Intent(reduceOnly: true), 10, true, CancellationToken.None);

        Assert.Contains("Execution.CloseConfirmed", result, StringComparison.Ordinal);
        Assert.True(exchange.MutationCount > 0);
        Assert.True(exchange.LastMarketRequest?.ReduceOnly);
    }

    [Fact]
    public void NonPositiveEquity_DoesNotGenerateRiskIncreasingIntent()
    {
        var evidence = new EvidencePack
        {
            Completeness = 100,
            Account = new AccountSnapshot(0, 0, 0, DateTime.UtcNow),
            Markets = new Dictionary<string, MarketEvidence>
            {
                ["BTCUSDT"] = new("BTCUSDT", 50_000, 49_000, 51_000, 50, 0, 0, 0, new(0, 0, 0, 0, 0, 0, 0), DateTime.UtcNow)
            }
        };
        var decision = new DecisionPlan
        {
            Action = DecisionAction.OpenLong,
            Instrument = "BTCUSDT",
            TargetTier = 1,
            StopLossPrice = 49_000,
            TakeProfitPrice = 53_000
        };

        var result = new RiskAndPositionPlanner().Plan(
            decision,
            evidence,
            new TradingRule("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m, 125),
            new RiskLimits(),
            0);

        Assert.Empty(result.Intents);
        Assert.Contains("Risk.MarginLimit", result.Result, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        ServiceLocator.SystemState.Status = _originalStatus;
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private ReliableOrderExecutor CreateExecutor(IExchangeAdapter exchange) =>
        new(exchange, new AgentSqliteStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db")));

    private static ExecutionIntent Intent(bool reduceOnly) => new(
        "BTCUSDT",
        PositionSide.Long,
        0.001m,
        reduceOnly,
        reduceOnly ? 0 : 49_000m,
        reduceOnly ? 0 : 51_000m,
        "safety-gate-order",
        "test",
        reduceOnly ? DecisionAction.ReduceLong : DecisionAction.OpenLong);

    private sealed record MarketRequest(bool ReduceOnly);

    private sealed class RecordingExchange(ExchangeEnvironment environment) : IExchangeAdapter
    {
        public ExchangeEnvironment Environment { get; } = environment;
        public int MutationCount { get; private set; }
        public MarketRequest? LastMarketRequest { get; private set; }

        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) { MutationCount++; return Task.CompletedTask; }
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) { MutationCount++; return Task.CompletedTask; }
        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) { MutationCount++; return Task.CompletedTask; }
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct)
        {
            MutationCount++;
            LastMarketRequest = new MarketRequest(reduceOnly);
            return Task.FromResult(new ExchangeOrder(symbol,"42", clientOrderId, "FILLED", quantity, 50_000, "MARKET", side, false, DateTime.UtcNow));
        }
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct) { MutationCount++; return Task.FromResult(new ExchangeOrder(symbol,"43",clientOrderId,"FILLED",quantity,price,"LIMIT",side,false,DateTime.UtcNow)); }
        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct) => Task.FromResult<ExchangeOrder?>(null);
        public Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) { MutationCount++; return Task.CompletedTask; }
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) { MutationCount++; throw new InvalidOperationException("Not expected"); }
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExchangeOrder>>(Array.Empty<ExchangeOrder>());
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
