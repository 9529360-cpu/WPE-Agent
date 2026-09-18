using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityDriftPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-execution-reality-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset IntendedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppendOnlyRoundTripIsIdempotentAndRestartSafe()
    {
        var fact = Fact("strategy-a", "v1", "order-a", "FILLED", 1m, 100.2m, .04008m, true, IntendedAt.AddSeconds(1));
        var store = new AgentSqliteStore(Database);

        var first = await store.SaveExecutionRealityDriftAsync(fact, default);
        var second = await store.SaveExecutionRealityDriftAsync(fact, default);

        Assert.True(first.Succeeded);
        Assert.False(first.Idempotent);
        Assert.True(second.Idempotent);

        var rows = await new AgentSqliteStore(Database).GetRecentExecutionRealityDriftAsync(10, default);
        var stored = Assert.Single(rows);
        Assert.Equal(fact.CanonicalSha256, stored.CanonicalSha256);
        Assert.Equal(fact.CanonicalBytes, stored.CanonicalBytes);
        Assert.True(ExecutionRealityDriftV1.IsCanonical(stored));

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE execution_reality_drift SET reason_code='tampered'";
        await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM execution_reality_drift";
        await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SummarySeparatesPriceFeeAndTotalComparability()
    {
        var store = new AgentSqliteStore(Database);
        await store.SaveExecutionRealityDriftAsync(
            Fact("strategy-a", "v1", "filled", "FILLED", 1m, 100.2m, .04008m, true, IntendedAt.AddSeconds(1)),
            default);
        await store.SaveExecutionRealityDriftAsync(
            Fact("strategy-a", "v1", "partial", "PARTIALLY_FILLED", .5m, 99.9m, .01998m, true, IntendedAt.AddSeconds(2)),
            default);
        await store.SaveExecutionRealityDriftAsync(
            Fact("strategy-a", "v1", "fee-missing", "PARTIALLY_FILLED", .25m, 100.1m, 0m, false, IntendedAt.AddSeconds(3)),
            default);
        await store.SaveExecutionRealityDriftAsync(
            Fact("strategy-a", "v1", "rejected", "REJECTED", 0m, 0m, 0m, false, IntendedAt.AddMilliseconds(500)),
            default);
        await store.SaveExecutionRealityDriftAsync(
            Fact("strategy-b", "v7", "other", "FILLED", 1m, 110m, .044m, true, IntendedAt.AddSeconds(4), expectedPrice:110m),
            default);

        var summary = await store.GetExecutionRealityDriftSummaryAsync("strategy-a", "v1", 100, default);

        Assert.NotNull(summary);
        Assert.Equal(4, summary!.ObservationCount);
        Assert.Equal(3, summary.ComparableCount);
        Assert.Equal(2, summary.FeeComparableCount);
        Assert.Equal(2, summary.TotalComparableCount);
        Assert.Equal(2, summary.TerminalCount);
        Assert.Equal((1m + .5m + .25m) / 3m, summary.AverageFillRatio);
        Assert.Equal((-10m) / 3m, summary.AverageSlippageDriftBps);
        Assert.Equal(0m, summary.AverageFeeDriftBps);
        Assert.Equal(-5m, summary.AverageTotalExecutionDriftBps);
        Assert.Equal(3000, summary.MaximumObservationLatencyMs);
        Assert.Equal(IntendedAt.AddSeconds(3), summary.LatestObservedAtUtc);

        var calibration = await store.GetExecutionRealityCalibrationAsync("strategy-a", "v1", 100, default);
        Assert.NotNull(calibration);
        Assert.True(ExecutionRealityCalibrationV1.IsCanonical(calibration!));
        Assert.Equal(4, calibration!.ObservationCount);
        Assert.Equal(3, calibration.ComparableCount);
        Assert.Equal(2, calibration.TotalComparableCount);

        var rows = await store.GetRecentExecutionRealityDriftAsync(100, default, "strategy-a", "v1");
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, x => x.StrategyId == "strategy-b");
        var missingFee = Assert.Single(rows, x => x.ClientOrderId == "fee-missing");
        Assert.True(missingFee.Comparable);
        Assert.False(missingFee.FeeComparable);
        Assert.False(missingFee.TotalComparable);
    }

    [Fact]
    public async Task NonCanonicalFactIsRejectedBeforePersistence()
    {
        var store = new AgentSqliteStore(Database);
        var fact = Fact("strategy-a", "v1", "order-a", "FILLED", 1m, 100m, .04m, true, IntendedAt.AddSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionRealityDriftAsync(fact with { AveragePrice = 101m }, default));

        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10, default));
    }

    private static ExecutionRealityDriftFactV1 Fact(
        string strategyId,
        string version,
        string orderId,
        string status,
        decimal executed,
        decimal price,
        decimal fee,
        bool feeAvailable,
        DateTimeOffset observedAt,
        decimal expectedPrice = 100m)
    {
        var expected = new ExecutionRealityExpectationV1(
            CorrelationId:"cycle-" + orderId,
            ClientOrderId:orderId,
            StrategyId:strategyId,
            StrategyVersion:version,
            Symbol:"BTCUSDT",
            Side:PositionSide.Long,
            ReduceOnly:false,
            OrderType:ExecutionOrderType.Market,
            Quantity:1m,
            ExpectedPrice:expectedPrice,
            ExpectedCommissionRate:.0004m,
            ExpectedSlippageRate:.001m,
            IntendedAtUtc:IntendedAt);
        var observation = new ExecutionRealityObservationV1(
            orderId,
            status,
            executed,
            price,
            fee,
            feeAvailable ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
            observedAt.AddMilliseconds(-100),
            observedAt);
        return ExecutionRealityDriftV1.Analyze(expected, observation);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
