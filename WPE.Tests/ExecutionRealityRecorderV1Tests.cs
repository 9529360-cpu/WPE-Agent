using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityRecorderV1Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-execution-recorder-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset ExecutionStart = Now.AddSeconds(-1);

    [Fact]
    public async Task DurableIntentAuthorityAndConfirmedFeeProduceComparableFact()
    {
        var store = Store();
        await AuthorizeArtifact(store, Artifact("cycle-a", "strategy-a", "v7", "order-a"));
        await store.SaveExchangeOrderFeeEvidenceAsync(Fee("order-a", .04008m), default);

        var recorder = new ExecutionRealityRecorderV1(store, () => Now);
        var result = await recorder.RecordAsync(
            "cycle-a",
            Intent("order-a"),
            Order("order-a", "FILLED", 1m, 100.2m),
            Costs(),
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
        Assert.Equal(1000, fact.ObservationLatencyMs);
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
        await AuthorizeArtifact(store, Artifact("cycle-a", "strategy-a", "v7", "order-a"));

        var result = await new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
            "cycle-a",
            Intent("order-a"),
            Order("order-a", "FILLED", 1m, 100.2m),
            Costs(),
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
    public async Task MissingArtifactDoesNotInventStrategyIdentity()
    {
        var store = Store();

        var result = await new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
            "cycle-missing",
            Intent("missing"),
            Order("missing", "FILLED", 1m, 100m),
            Costs(),
            default);

        Assert.False(result.Recorded);
        Assert.Equal("intent-authority-automatic-artifact-missing", result.Code);
        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10, default));
    }

    [Fact]
    public async Task PersistedIntentMismatchDoesNotWriteDrift()
    {
        var store = Store();
        await AuthorizeArtifact(store, Artifact("cycle-a", "strategy-a", "v7", "order-a"));

        var mismatched = Intent("order-a") with { ExpectedPrice = 101m };
        var result = await new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
            "cycle-a",
            mismatched,
            Order("order-a", "FILLED", 1m, 101m),
            Costs(),
            default);

        Assert.False(result.Recorded);
        Assert.Equal("intent-authority-automatic-intent-mismatch", result.Code);
        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10, default));
    }

    [Fact]
    public async Task MissingExecutingEventDoesNotAcceptCallerSuppliedTiming()
    {
        var store = Store();
        var artifact = Artifact("cycle-a", "strategy-a", "v7", "order-a");
        Assert.True((await store.SaveAutomaticExecutionAsync(artifact.CorrelationId, artifact, default)).Succeeded);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync(
            artifact.CorrelationId,
            Receipt(artifact),
            default)).Succeeded);

        var result = await new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
            "cycle-a",
            Intent("order-a"),
            Order("order-a", "FILLED", 1m, 100m),
            Costs(),
            default);

        Assert.False(result.Recorded);
        Assert.Equal("intent-authority-automatic-executing-event-missing", result.Code);
        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10, default));
    }

    [Fact]
    public async Task NonTestnetArtifactCannotAuthorizeRealityWrite()
    {
        var store = Store();
        var artifact = Artifact("cycle-a", "strategy-a", "v7", "order-a") with { Environment = "Mainnet" };
        Assert.True((await store.SaveAutomaticExecutionAsync(artifact.CorrelationId, artifact, default)).Succeeded);

        var result = await new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
            "cycle-a",
            Intent("order-a"),
            Order("order-a", "FILLED", 1m, 100m),
            Costs(),
            default);

        Assert.False(result.Recorded);
        Assert.Equal("intent-authority-automatic-artifact-non-testnet", result.Code);
        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10, default));
    }

    [Fact]
    public async Task OrderIdentityMismatchFailsClosedBeforePersistence()
    {
        var store = Store();
        await AuthorizeArtifact(store, Artifact("cycle-a", "strategy-a", "v1", "order-a"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExecutionRealityRecorderV1(store, () => Now).RecordAsync(
                "cycle-a",
                Intent("order-a"),
                Order("different-order", "FILLED", 1m, 100m),
                Costs(),
                default));
    }

    private AgentSqliteStore Store() => new(Database, () => ExecutionStart);

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
                "automatic.risk-approved", "OpenLong", "Market", 0, 100m)],
            5,
            true,
            "binance-futures",
            "Testnet",
            strategyId,
            strategyVersion,
            ExecutionStart.AddSeconds(-10),
            "market-v1",
            ExecutionStart.AddSeconds(-5),
            ExecutionStart.AddMinutes(1));

    private static DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact)
    {
        var hashes = DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        return new(
            "risk-" + artifact.CorrelationId,
            artifact.CorrelationId,
            hashes.IntentHash,
            true,
            ExecutionStart.AddSeconds(-1),
            ExecutionStart.AddMinutes(1),
            null,
            hashes.ArtifactHash);
    }

    private static async Task AuthorizeArtifact(AgentSqliteStore store, DurableExecutionArtifactV2 artifact)
    {
        Assert.True((await store.SaveAutomaticExecutionAsync(artifact.CorrelationId, artifact, default)).Succeeded);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync(
            artifact.CorrelationId,
            Receipt(artifact),
            default)).Succeeded);
        Assert.True((await store.TryClaimAutomaticExecutionAsync(
            artifact.CorrelationId,
            "worker",
            TimeSpan.FromSeconds(30),
            default)).Claimed);
        Assert.True((await store.TryTransitionAutomaticExecutionAsync(
            artifact.CorrelationId,
            AutomaticExecutionQueueStatus.Claimed,
            AutomaticExecutionQueueStatus.Executing,
            "worker",
            "automatic.executing",
            default)).Succeeded);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
