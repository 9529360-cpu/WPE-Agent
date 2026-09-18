using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityRecorderV1Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-execution-recorder-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 5, TimeSpan.Zero);

    [Fact]
    public async Task ExactAutomaticAttributionAndConfirmedFeeProduceComparableFact()
    {
        var store = Store();
        await SaveArtifact(store, "execution-a", Artifact("cycle-a", "strategy-a", "v7", "order-a"));
        await store.SaveExchangeOrderFeeEvidenceAsync(Fee("order-a", .04008m), default);

        var recorder = new ExecutionRealityRecorderV1(store, () => Now);
        var result = await recorder.RecordAsync(
            "cycle-a",
            Intent("order-a"),
            Order("order-a", "FILLED", 1m, 100.2m),
            Now.AddSeconds(-1),
            Costs(),
            "fallback-v1",
            default);

        Assert.True(result.Recorded);
        Assert.True(result.FeeComparable);
        Assert.False(result.Idempotent);
        Assert.Equal("stored", result.Code);

        var fact = Assert.Single(await store.GetRecentExecutionRealityDriftAsync(
            10, default, "strategy-a", "v7", "research-cost-v1"));
        Assert.Equal("strategy-a", fact.StrategyId);
        Assert.Equal("v7", fact.StrategyVersion);
        Assert.Equal("research-cost-v1", fact.CostModelVersion);
        Assert.True(fact.FeeComparable);
        Assert.True(fact.TotalComparable);
        Assert.Equal(10m, fact.SlippageDriftBps);
        Assert.Equal(0m, fact.FeeDriftBps);
        Assert.Equal(10m, fact.TotalExecutionDriftBps);
    }

    [Fact]
    public async Task MissingFeeEvidenceKeepsPriceFactButWithholdsTotalComparison()
    {
        var store = Store();
        await SaveArtifact(store, "execution-a", Artifact("cycle-a", "strategy-a", "v7", "order-a"));

        var result = await new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
            "cycle-a",
            Intent("order-a"),
            Order("order-a", "FILLED", 1m, 100.2m),
            Now.AddSeconds(-1),
            Costs(),
            "fallback-v1",
            default);

        Assert.True(result.Recorded);
        Assert.False(result.FeeComparable);
        var fact = Assert.Single(await store.GetRecentExecutionRealityDriftAsync(10, default));
        Assert.True(fact.Comparable);
        Assert.False(fact.FeeComparable);
        Assert.False(fact.TotalComparable);
        Assert.Equal(10m, fact.SlippageDriftBps);
        Assert.Equal(0m, fact.TotalExecutionDriftBps);
        Assert.Equal("filled-fee-unavailable", fact.ReasonCode);
    }

    [Fact]
    public async Task MissingOrConflictingStrategyAttributionDoesNotInventIdentity()
    {
        var store = Store();
        var recorder = new ExecutionRealityRecorderV1(store, () => Now);

        var missing = await recorder.RecordAsync(
            "cycle-missing",
            Intent("missing"),
            Order("missing", "FILLED", 1m, 100m),
            Now.AddSeconds(-1),
            Costs(),
            "fallback-v1",
            default);
        Assert.False(missing.Recorded);
        Assert.Equal("strategy-attribution-legacy-version-only", missing.Code);

        await SaveArtifact(store, "execution-a", Artifact("cycle-conflict", "strategy-a", "v1", "conflict-a"));
        await SaveArtifact(store, "execution-b", Artifact("cycle-conflict", "strategy-b", "v2", "conflict-b"));
        var conflicting = await recorder.RecordAsync(
            "cycle-conflict",
            Intent("conflict-a"),
            Order("conflict-a", "FILLED", 1m, 100m),
            Now.AddSeconds(-1),
            Costs(),
            "fallback-v1",
            default);
        Assert.False(conflicting.Recorded);
        Assert.Equal("strategy-attribution-conflicting-correlation", conflicting.Code);

        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10, default));
    }

    [Fact]
    public async Task OrderIdentityMismatchFailsClosed()
    {
        var store = Store();
        await SaveArtifact(store, "execution-a", Artifact("cycle-a", "strategy-a", "v1", "order-a"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
                "cycle-a",
                Intent("order-a"),
                Order("different-order", "FILLED", 1m, 100m),
                Now.AddSeconds(-1),
                Costs(),
                "fallback-v1",
                default));
    }

    private AgentSqliteStore Store() => new(Database, () => Now);

    private static ExecutionRealityCostAssumptionV1 Costs() =>
        new("research-cost-v1", .0004m, .001m);

    private static ExecutionIntent Intent(string clientOrderId) =>
        new("BTCUSDT", PositionSide.Long, 1m, false, 90m, 120m, clientOrderId, "test",
            DecisionAction.OpenLong, ExecutionOrderType.Market, 0, 100m);

    private static ExchangeOrder Order(string clientOrderId, string status, decimal quantity, decimal price) =>
        new("BTCUSDT", "exchange-" + clientOrderId, clientOrderId, status, quantity, price,
            "MARKET", PositionSide.Long, false, Now.AddMilliseconds(-100).UtcDateTime);

    private static ExchangeOrderFeeEvidenceV1 Fee(string clientOrderId, decimal fee) =>
        ExchangeOrderFeeEvidenceCanonicalizerV1.Create(
            "binance-futures", "Testnet", "BTCUSDT", "exchange-" + clientOrderId, clientOrderId,
            1, 1m, fee, "USDT", Now, ExchangeOrderFeeEvidenceStateV1.Confirmed);

    private static DurableExecutionArtifactV2 Artifact(
        string correlationId,
        string strategyId,
        string strategyVersion,
        string clientOrderId) =>
        new(
            DurableExecutionArtifactV2.Version,
            correlationId,
            [new DurableExecutionIntentSnapshotV1(
                0, "BTCUSDT", "Long", 1m, false, 90m, 120m, clientOrderId,
                "strategy.entry", "OpenLong", "Market", 0, 100m)],
            5,
            true,
            "binance",
            "Testnet",
            strategyId,
            strategyVersion,
            Now.AddSeconds(-10),
            "market-v1",
            Now.AddSeconds(-5),
            Now.AddMinutes(1));

    private static async Task SaveArtifact(AgentSqliteStore store, string executionId, DurableExecutionArtifactV2 artifact)
    {
        var result = await store.SaveAutomaticExecutionAsync(executionId, artifact, default);
        Assert.True(result.Succeeded);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
