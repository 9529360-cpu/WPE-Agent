using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class SqliteResilienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");
    private string ConnectionString => $"Data Source={DatabasePath}";

    [Fact]
    public async Task CommittedIntent_SurvivesStoreLossAndIsRecoverableAfterRestart()
    {
        var intent = Intent("crash-after-intent");
        var beforeCrash = new AgentSqliteStore(DatabasePath);
        await beforeCrash.SaveIntentAsync("cycle-1", intent, "INTENT", null, CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var afterRestart = new AgentSqliteStore(DatabasePath);
        var pending = await afterRestart.GetRecoverableIntentsAsync(CancellationToken.None);

        var recovered = Assert.Single(pending);
        Assert.Equal("INTENT", recovered.Status);
        Assert.Equal(intent, recovered.Intent);
    }

    [Fact]
    public async Task ExchangeFilledBeforeLocalStatusUpdate_IsReconciledWithoutSecondOrder()
    {
        var intent = Intent("filled-before-status-update");
        var beforeCrash = new AgentSqliteStore(DatabasePath);
        await beforeCrash.SaveIntentAsync("cycle-1", intent, "INTENT", null, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var afterRestart = new AgentSqliteStore(DatabasePath);
        var exchange = new FilledOrderExchange(intent);
        var executor = new ReliableOrderExecutor(exchange, afterRestart);

        var result = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.True(result.SafeToIncreaseRisk);
        Assert.Equal(1, exchange.FindCalls);
        Assert.Equal(0, exchange.MarketOrderSubmissions);
        Assert.Equal(1, exchange.ProtectionSubmissions);
        Assert.Empty(await afterRestart.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WriterBlockedByTransaction_CompletesAfterLockIsReleased()
    {
        var store = new AgentSqliteStore(DatabasePath);
        await using var blocker = new SqliteConnection(ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO agent_state(key,value,updated_at) VALUES('lock-test','held','now')";
            await command.ExecuteNonQueryAsync();
        }

        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var blockedWrite = Task.Run(async () =>
        {
            writerEntered.SetResult();
            await store.SaveIntentAsync("cycle-1", Intent("waits-for-lock"), "INTENT", null, CancellationToken.None);
            return stopwatch.Elapsed;
        });
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);

        await transaction.CommitAsync();
        var completionTime = await blockedWrite.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(completionTime, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WalModeAndCommittedDataSurviveConnectionPoolReset()
    {
        var store = new AgentSqliteStore(DatabasePath);
        await store.SaveIntentAsync("cycle-1", Intent("wal-reopen"), "INTENT", null, CancellationToken.None);

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            var mode = (await command.ExecuteScalarAsync())?.ToString();
            Assert.Equal("wal", mode, ignoreCase: true);
        }

        SqliteConnection.ClearAllPools();
        var reopened = new AgentSqliteStore(DatabasePath);

        Assert.Single(await reopened.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIntentWrites_PersistEveryDistinctIntent()
    {
        const int count = 40;
        var store = new AgentSqliteStore(DatabasePath);

        await Task.WhenAll(Enumerable.Range(0, count).Select(index =>
            store.SaveIntentAsync("cycle-concurrent", Intent($"concurrent-{index:D2}"), "INTENT", null, CancellationToken.None)));

        SqliteConnection.ClearAllPools();
        var reopened = new AgentSqliteStore(DatabasePath);
        var persisted = await reopened.GetRecoverableIntentsAsync(CancellationToken.None);

        Assert.Equal(count, persisted.Count);
        Assert.Equal(count, persisted.Select(item => item.Intent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ExecutionIntent Intent(string clientOrderId) => new(
        "BTCUSDT", PositionSide.Long, 0.001m, false, 49_000m, 51_000m, clientOrderId, "sqlite resilience", DecisionAction.OpenLong);

    private sealed class FilledOrderExchange(ExecutionIntent intent) : IExchangeAdapter
    {
        public int FindCalls { get; private set; }
        public int MarketOrderSubmissions { get; private set; }
        public int ProtectionSubmissions { get; private set; }
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;

        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            FindCalls++;
            ExchangeOrder order = new(symbol, "42", clientOrderId, "FILLED", intent.Quantity, 50_000m, "MARKET", intent.Side, false, DateTime.UtcNow);
            return Task.FromResult<ExchangeOrder?>(order);
        }

        public Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct)
        {
            MarketOrderSubmissions++;
            throw new InvalidOperationException("Recovery must reconcile the accepted order instead of submitting again");
        }

        public Task<ExchangeOrder> PlaceLimitAsync(string symbol, PositionSide side, decimal quantity, decimal price, string clientOrderId, bool reduceOnly, CancellationToken ct)
            => PlaceMarketAsync(symbol, side, quantity, clientOrderId, reduceOnly, ct);

        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct)
        {
            ProtectionSubmissions++;
            return Task.FromResult(new ExchangeOrder(symbol, "100", groupId + "-SL", "NEW", 0, 0, "STOP_MARKET", sideToClose, true, DateTime.UtcNow));
        }

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol, string interval, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol, string interval, DateTime start, DateTime end, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) => throw new NotSupportedException();
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) => throw new NotSupportedException();
        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
