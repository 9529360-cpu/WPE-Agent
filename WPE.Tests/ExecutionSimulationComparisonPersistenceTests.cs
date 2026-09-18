using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionSimulationComparisonPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-sim-compare-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset MarketAt = new(2026, 9, 18, 11, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset SimulatedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 18, 12, 0, 1, TimeSpan.Zero);

    [Fact]
    public async Task FillAndComparisonRoundTripAreIdempotentAndRestartSafe()
    {
        var store = new AgentSqliteStore(Database);
        var simulated = Simulated("strategy-a", "v7", "research-cost-v1", "sim-v1", "order-a", 100m);
        var observed = Observed("strategy-a", "v7", "research-cost-v1", "order-a", 101m);
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        var fillFirst = await store.SaveExecutionSimulationFillAsync(simulated, default);
        var fillSecond = await store.SaveExecutionSimulationFillAsync(simulated, default);
        var comparisonFirst = await store.SaveExecutionSimulationComparisonAsync(comparison, default);
        var comparisonSecond = await store.SaveExecutionSimulationComparisonAsync(comparison, default);

        Assert.True(fillFirst.Succeeded);
        Assert.False(fillFirst.Idempotent);
        Assert.True(fillSecond.Idempotent);
        Assert.True(comparisonFirst.Succeeded);
        Assert.False(comparisonFirst.Idempotent);
        Assert.True(comparisonSecond.Idempotent);

        var rows = await new AgentSqliteStore(Database).GetRecentExecutionSimulationComparisonsAsync(
            10, default, "strategy-a", "v7", "research-cost-v1");
        var row = Assert.Single(rows);
        Assert.Equal("sim-v1", row.SimulationModelVersion);
        Assert.Equal("binance-testnet-rules-v1", row.VenueRuleVersion);
        Assert.Equal(100m, row.PriceDriftBps);
        Assert.True(row.PriceComparable);
        Assert.True(row.FeeComparable);
        Assert.True(row.TotalComparable);
        Assert.Equal(comparison.CanonicalSha256, row.CanonicalSha256);
        Assert.Equal(simulated.CanonicalSha256, row.SimulatedCanonicalSha256);
        Assert.Equal(observed.CanonicalSha256, row.ObservedCanonicalSha256);
    }

    [Fact]
    public async Task AppendOnlyTriggersRejectMutation()
    {
        var store = new AgentSqliteStore(Database);
        var simulated = Simulated("strategy-a", "v7", "research-cost-v1", "sim-v1", "order-a", 100m);
        var observed = Observed("strategy-a", "v7", "research-cost-v1", "order-a", 100m);
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));
        await store.SaveExecutionSimulationFillAsync(simulated, default);
        await store.SaveExecutionSimulationComparisonAsync(comparison, default);

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();

        await using var updateFill = connection.CreateCommand();
        updateFill.CommandText = "UPDATE execution_simulated_fills SET symbol='ETHUSDT'";
        await Assert.ThrowsAsync<SqliteException>(() => updateFill.ExecuteNonQueryAsync());

        await using var deleteFill = connection.CreateCommand();
        deleteFill.CommandText = "DELETE FROM execution_simulated_fills";
        await Assert.ThrowsAsync<SqliteException>(() => deleteFill.ExecuteNonQueryAsync());

        await using var updateComparison = connection.CreateCommand();
        updateComparison.CommandText = "UPDATE execution_simulation_comparisons SET symbol='ETHUSDT'";
        await Assert.ThrowsAsync<SqliteException>(() => updateComparison.ExecuteNonQueryAsync());

        await using var deleteComparison = connection.CreateCommand();
        deleteComparison.CommandText = "DELETE FROM execution_simulation_comparisons";
        await Assert.ThrowsAsync<SqliteException>(() => deleteComparison.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task QueryFiltersDoNotMixStrategyOrCostModelVersions()
    {
        var store = new AgentSqliteStore(Database);
        await SavePair(store, "strategy-a", "v7", "research-cost-v1", "sim-v1", "a", 100m, 100m);
        await SavePair(store, "strategy-a", "v7", "research-cost-v2", "sim-v1", "b", 100m, 101m);
        await SavePair(store, "strategy-b", "v2", "research-cost-v1", "sim-v9", "c", 100m, 99m);

        var rows = await store.GetRecentExecutionSimulationComparisonsAsync(
            100, default, "strategy-a", "v7", "research-cost-v1");

        var row = Assert.Single(rows);
        Assert.Equal("order-a", row.ClientOrderId);
        Assert.Equal("research-cost-v1", row.CostModelVersion);
        Assert.Equal("strategy-a", row.StrategyId);
    }

    [Fact]
    public async Task NonCanonicalArtifactsAreRejectedBeforePersistence()
    {
        var store = new AgentSqliteStore(Database);
        var simulated = Simulated("strategy-a", "v7", "research-cost-v1", "sim-v1", "order-a", 100m);
        var observed = Observed("strategy-a", "v7", "research-cost-v1", "order-a", 100m);
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionSimulationFillAsync(simulated with { AveragePrice = 999m }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionSimulationComparisonAsync(comparison with { PriceDriftBps = 999m }, default));

        Assert.Empty(await store.GetRecentExecutionSimulationComparisonsAsync(10, default));
    }

    [Fact]
    public async Task CanonicalBytesAndIndexMetadataMustAgreeOnRead()
    {
        var store = new AgentSqliteStore(Database);
        var simulated = Simulated("strategy-a", "v7", "research-cost-v1", "sim-v1", "order-a", 100m);
        var observed = Observed("strategy-a", "v7", "research-cost-v1", "order-a", 100m);
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        // Materialize the append-only schema first, then inject a malformed row as if the database were externally corrupted.
        Assert.Empty(await store.GetRecentExecutionSimulationComparisonsAsync(10, default));

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO execution_simulation_comparisons(
                canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                simulation_model_version,venue_rule_version,symbol,simulated_sha256,observed_sha256,compared_at,canonical_bytes)
            VALUES($hash,$correlation,$client,'wrong-strategy',$version,$costModel,$simulationModel,$venueRule,$symbol,
                $simulatedHash,$observedHash,$compared,$bytes);
            """;
        command.Parameters.AddWithValue("$hash", comparison.CanonicalSha256);
        command.Parameters.AddWithValue("$correlation", comparison.CorrelationId);
        command.Parameters.AddWithValue("$client", comparison.ClientOrderId);
        command.Parameters.AddWithValue("$version", comparison.StrategyVersion);
        command.Parameters.AddWithValue("$costModel", comparison.CostModelVersion);
        command.Parameters.AddWithValue("$simulationModel", comparison.SimulationModelVersion);
        command.Parameters.AddWithValue("$venueRule", comparison.VenueRuleVersion);
        command.Parameters.AddWithValue("$symbol", comparison.Symbol);
        command.Parameters.AddWithValue("$simulatedHash", comparison.SimulatedCanonicalSha256);
        command.Parameters.AddWithValue("$observedHash", comparison.ObservedCanonicalSha256);
        command.Parameters.AddWithValue("$compared", comparison.ComparedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$bytes", comparison.CanonicalBytes);
        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetRecentExecutionSimulationComparisonsAsync(10, default));
    }

    private static async Task SavePair(
        AgentSqliteStore store,
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string suffix,
        decimal simulatedPrice,
        decimal observedPrice)
    {
        var simulated = Simulated(strategyId, strategyVersion, costModelVersion, simulationModelVersion, "order-" + suffix, simulatedPrice);
        var observed = Observed(strategyId, strategyVersion, costModelVersion, "order-" + suffix, observedPrice);
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));
        await store.SaveExecutionSimulationFillAsync(simulated, default);
        await store.SaveExecutionSimulationComparisonAsync(comparison, default);
    }

    private static ExecutionSimulationFillV1 Simulated(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string orderId,
        decimal price) =>
        ExecutionSimulationFillCanonicalizerV1.Create(
            correlationId:"cycle-" + orderId,
            clientOrderId:orderId,
            strategyId:strategyId,
            strategyVersion:strategyVersion,
            costModelVersion:costModelVersion,
            simulationModelVersion:simulationModelVersion,
            venueRuleVersion:"binance-testnet-rules-v1",
            symbol:"BTCUSDT",
            side:PositionSide.Long,
            reduceOnly:false,
            orderType:ExecutionOrderType.Market,
            intendedQuantity:1m,
            state:ExecutionSimulationFillStateV1.Filled,
            executedQuantity:1m,
            averagePrice:price,
            feeAmount:price * .0004m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:true,
            simulatedLatencyMs:500,
            marketAsOfUtc:MarketAt,
            simulatedAtUtc:SimulatedAt,
            reasonCode:"modeled");

    private static ExecutionRealityDriftFactV1 Observed(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string orderId,
        decimal price)
    {
        var expectation = new ExecutionRealityExpectationV1(
            "cycle-" + orderId, orderId, strategyId, strategyVersion, costModelVersion, "BTCUSDT",
            PositionSide.Long, false, ExecutionOrderType.Market, 1m, 100m, .0004m, .001m, SimulatedAt);
        var observation = new ExecutionRealityObservationV1(
            orderId, "FILLED", 1m, price, price * .0004m,
            ExecutionRealityDriftV1.ExchangeReportedFeeBasis,
            ObservedAt.AddMilliseconds(-100),
            ObservedAt);
        return ExecutionRealityDriftV1.Analyze(expectation, observation);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
