using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ExecutionTopOfBookSimulationV1Tests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-topbook-sim-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");

    [Fact]
    public void MarketBuyUsesOnlyProvenBestAskQuantity()
    {
        var bundle = Bundle(
            Artifact(quantity:2m),
            Snapshot(askQuantity:3m, bidQuantity:4m),
            Rule(),
            Now);

        Assert.Equal(ExecutionSimulationFillStateV1.Filled, bundle.Fill.State);
        Assert.Equal(2m, bundle.Fill.ExecutedQuantity);
        Assert.Equal(101m, bundle.Fill.AveragePrice);
        Assert.Equal(ExecutionSimulationFeeRoleV1.Unavailable, bundle.Fill.FeeRole);
        Assert.False(bundle.Fill.LatencyModeled);
        Assert.Equal("top-of-book-full", bundle.Fill.ReasonCode);
        Assert.True(ExecutionSimulationFillCanonicalizerV1.IsCanonical(bundle.Fill));
        Assert.True(ExecutionTopOfBookSimulationV1.IsCanonical(bundle.Source));
    }

    [Fact]
    public void QuantityBeyondTopLevelIsPartialRatherThanOptimisticFullFill()
    {
        var bundle = Bundle(
            Artifact(quantity:2m),
            Snapshot(askQuantity:.75m, bidQuantity:5m),
            Rule(),
            Now);

        Assert.Equal(ExecutionSimulationFillStateV1.Partial, bundle.Fill.State);
        Assert.Equal(.75m, bundle.Fill.ExecutedQuantity);
        Assert.Equal(101m, bundle.Fill.AveragePrice);
        Assert.Equal("top-of-book-partial", bundle.Fill.ReasonCode);
    }

    [Fact]
    public void SellSideUsesBestBidAndBidQuantity()
    {
        var artifact = Artifact(
            quantity:2m,
            side:"Short",
            reduceOnly:false);
        var bundle = Bundle(
            artifact,
            Snapshot(askQuantity:5m, bidQuantity:1.25m),
            Rule(),
            Now);

        Assert.Equal(ExecutionSimulationFillStateV1.Partial, bundle.Fill.State);
        Assert.Equal(1.25m, bundle.Fill.ExecutedQuantity);
        Assert.Equal(99m, bundle.Fill.AveragePrice);
    }

    [Fact]
    public void LimitRuleMissingOrStaleMarketFailsClosedAsUnsupported()
    {
        var limit = Bundle(
            Artifact(quantity:1m, orderType:"Limit", limitPrice:100m),
            Snapshot(),
            Rule(),
            Now);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported, limit.Fill.State);
        Assert.Equal("order-type-unsupported", limit.Fill.ReasonCode);

        var missingRule = Bundle(
            Artifact(quantity:1m),
            Snapshot(),
            null,
            Now);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported, missingRule.Fill.State);
        Assert.Equal("venue-rule-unavailable", missingRule.Fill.ReasonCode);

        var stale = Bundle(
            Artifact(quantity:1m),
            Snapshot(updatedAt:Now.AddSeconds(-16)),
            Rule(),
            Now);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported, stale.Fill.State);
        Assert.Equal("top-of-book-stale", stale.Fill.ReasonCode);
    }

    [Fact]
    public void VenueRuleViolationIsUnsupportedNotRoundedIntoA DifferentOrder()
    {
        var bundle = Bundle(
            Artifact(quantity:0.15m),
            Snapshot(),
            new TradingRule("BTCUSDT", .1m, .1m, .1m, 5m, 20),
            Now);

        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported, bundle.Fill.State);
        Assert.Equal("venue-rule-rejected", bundle.Fill.ReasonCode);
        Assert.Equal(0m, bundle.Fill.ExecutedQuantity);
    }

    [Fact]
    public void SourceHashAndReplayDetectTampering()
    {
        var bundle = Bundle(Artifact(quantity:1m), Snapshot(), Rule(), Now);

        Assert.Equal(
            bundle.Fill.CanonicalSha256,
            ExecutionTopOfBookSimulationV1.ReplayFill(bundle.Source).CanonicalSha256);
        Assert.False(ExecutionTopOfBookSimulationV1.IsCanonical(
            bundle.Source with { AskQuantity = 999m }));
        Assert.False(ExecutionTopOfBookSimulationV1.IsCanonical(
            bundle.Source with { ArtifactSha256 = new string('0', 64) }));
    }

    [Fact]
    public async Task BundlePersistenceIsAtomicIdempotentAndSingleSourcePerDurableIntent()
    {
        var store = new AgentSqliteStore(Database);
        var artifact = Artifact(quantity:1m);
        var first = Bundle(artifact, Snapshot(), Rule(), Now);

        var saved = await store.SaveExecutionSimulationBundleAsync(first.Source, first.Fill, default);
        var replay = await new AgentSqliteStore(Database).SaveExecutionSimulationBundleAsync(first.Source, first.Fill, default);

        Assert.True(saved.Succeeded);
        Assert.False(saved.Idempotent);
        Assert.True(replay.Succeeded);
        Assert.True(replay.Idempotent);

        var changed = Bundle(
            artifact,
            Snapshot(bestAsk:102m),
            Rule(),
            Now.AddSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionSimulationBundleAsync(changed.Source, changed.Fill, default));

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM execution_simulation_sources";
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task SimulationSourceLedgerIsAppendOnly()
    {
        var store = new AgentSqliteStore(Database);
        var bundle = Bundle(Artifact(quantity:1m), Snapshot(), Rule(), Now);
        await store.SaveExecutionSimulationBundleAsync(bundle.Source, bundle.Fill, default);

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "UPDATE execution_simulation_sources SET provider_id='tampered'",
            "DELETE FROM execution_simulation_sources"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task ProducerPersistsUnsupportedEvidenceWhenRealtimeSnapshotIsUnavailable()
    {
        var store = new AgentSqliteStore(Database);
        var exchange = new FakeExchange();
        var feed = new FakeFeed(null);
        var producer = new ExecutionTopOfBookSimulationProducerV1(
            "fake-provider",
            exchange,
            feed,
            store,
            utcNow:() => Now);

        var result = await producer.CaptureAsync(Artifact(quantity:1m), default);

        Assert.True(result.Recorded);
        Assert.Equal(1, result.FillCount);
        Assert.Equal(0, result.SupportedFillCount);

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT simulated_fill_sha256 FROM execution_simulation_sources LIMIT 1";
        Assert.Equal(result.FillCanonicalSha256s[0], (await query.ExecuteScalarAsync())?.ToString());
    }

    [Fact]
    public void ProductionCompositionInjectsRealtimeSimulationProducerIntoAutomaticGateway()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "AutoTradingAgent.cs"));

        Assert.Contains(
            "new ExecutionTopOfBookSimulationProducerV1(exchange.ProviderId,exchange,realtime,Db)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new TradingAutomaticExecutionGateway(executionGateway,exchange,Db,executionSimulationProducer)",
            source,
            StringComparison.Ordinal);
    }

    private static ExecutionSimulationBundleV1 Bundle(
        DurableExecutionArtifactV2 artifact,
        RealtimeMarketSnapshot? snapshot,
        TradingRule? rule,
        DateTimeOffset simulatedAt)
    {
        var intent = Assert.Single(artifact.Intents);
        return ExecutionTopOfBookSimulationV1.Create(
            artifact,
            intent,
            "fake-provider",
            ExchangeEnvironment.Testnet,
            snapshot,
            rule,
            ExecutionTopOfBookSimulationV1.DefaultCostModelVersion,
            simulatedAt);
    }

    private static DurableExecutionArtifactV2 Artifact(
        decimal quantity,
        string side = "Long",
        bool reduceOnly = false,
        string orderType = "Market",
        decimal limitPrice = 0m) =>
        new(
            DurableExecutionArtifactV2.Version,
            "cycle-1",
            [new DurableExecutionIntentSnapshotV1(
                0,
                "BTCUSDT",
                side,
                quantity,
                reduceOnly,
                90m,
                120m,
                "order-1",
                "strategy.entry",
                reduceOnly ? (side == "Long" ? "CloseLong" : "CloseShort") : (side == "Long" ? "OpenLong" : "OpenShort"),
                orderType,
                limitPrice,
                100m)],
            5,
            true,
            "fake-provider",
            "Testnet",
            "strategy-a",
            "v7",
            Now.AddMinutes(-1),
            "market-v1",
            Now.AddSeconds(-30),
            Now.AddMinutes(5));

    private static RealtimeMarketSnapshot Snapshot(
        decimal bestBid = 99m,
        decimal bestAsk = 101m,
        decimal bidQuantity = 3m,
        decimal askQuantity = 3m,
        DateTimeOffset? updatedAt = null) =>
        new(
            "BTCUSDT",
            100m,
            bestBid,
            bestAsk,
            bidQuantity,
            askQuantity,
            10m,
            9m,
            2m,
            (updatedAt ?? Now.AddSeconds(-1)).UtcDateTime,
            10,
            true);

    private static TradingRule Rule() =>
        new("BTCUSDT", .01m, .1m, .01m, 5m, 20);

    private sealed class FakeFeed(RealtimeMarketSnapshot? snapshot) : IRealtimeMarketFeed
    {
        public string Status => "TEST";
        public bool Healthy => true;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string?> WaitForTriggerAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public RealtimeMarketSnapshot? GetSnapshot(string canonicalSymbol) => snapshot;
        public MarketEvidence Enrich(MarketEvidence market) => market;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeExchange : IExchangeAdapter
    {
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) =>
            Task.FromResult(Rule());
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol, string interval, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol, string interval, DateTime start, DateTime end, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) => throw new NotSupportedException();
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) => throw new NotSupportedException();
        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol, PositionSide side, decimal quantity, decimal price, string clientOrderId, bool reduceOnly, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
