using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionSimulationEvidenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-sim-evidence-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CanonicalComparisonFactsAndReadyDecisionSurviveRestart()
    {
        var store = new AgentSqliteStore(Database);
        await SavePair(store, "a", Now.AddHours(-4));
        await SavePair(store, "b", Now.AddHours(-3));
        await SavePair(store, "c", Now.AddHours(-2));
        await SavePair(store, "d", Now.AddMinutes(-5));

        var restarted = new AgentSqliteStore(Database);
        var facts = await restarted.GetRecentExecutionSimulationComparisonFactsAsync(
            "strategy-a",
            "v7",
            "research-cost-v1",
            "execution-sim-v1",
            "binance-testnet-rules-v1",
            "BTCUSDT",
            100,
            default);

        Assert.Equal(4, facts.Count);
        Assert.All(facts, fact => Assert.True(ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(fact)));

        var decision = await restarted.EvaluateRecentExecutionSimulationEvidenceAsync(
            "strategy-a",
            "v7",
            "research-cost-v1",
            "execution-sim-v1",
            "binance-testnet-rules-v1",
            "BTCUSDT",
            Policy(),
            100,
            Now,
            default);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Ready, decision.State);
        Assert.True(decision.EvidenceReady);
        Assert.Equal(4, decision.ComparisonCount);
        Assert.True(ExecutionSimulationEvidenceGateV1.IsCanonical(decision));
    }

    [Fact]
    public async Task ExactIdentityQueryDoesNotMixSimulationVenueOrSymbolVersions()
    {
        var store = new AgentSqliteStore(Database);
        await SavePair(store, "a", Now.AddMinutes(-20));
        await SavePair(store, "b", Now.AddMinutes(-15), simulationModelVersion:"execution-sim-v2");
        await SavePair(store, "c", Now.AddMinutes(-10), venueRuleVersion:"binance-testnet-rules-v2");
        await SavePair(store, "d", Now.AddMinutes(-5), symbol:"ETHUSDT");

        var exact = await store.GetRecentExecutionSimulationComparisonFactsAsync(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            100, default);
        var modelV2 = await store.GetRecentExecutionSimulationComparisonFactsAsync(
            "strategy-a","v7","research-cost-v1","execution-sim-v2","binance-testnet-rules-v1","BTCUSDT",
            100, default);
        var venueV2 = await store.GetRecentExecutionSimulationComparisonFactsAsync(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v2","BTCUSDT",
            100, default);
        var eth = await store.GetRecentExecutionSimulationComparisonFactsAsync(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","ETHUSDT",
            100, default);

        Assert.Equal("order-a", Assert.Single(exact).ClientOrderId);
        Assert.Equal("order-b", Assert.Single(modelV2).ClientOrderId);
        Assert.Equal("order-c", Assert.Single(venueV2).ClientOrderId);
        Assert.Equal("order-d", Assert.Single(eth).ClientOrderId);
    }

    [Fact]
    public async Task InsufficientPersistedEvidenceDoesNotBecomeReady()
    {
        var store = new AgentSqliteStore(Database);
        await SavePair(store, "a", Now.AddMinutes(-5));

        var decision = await new AgentSqliteStore(Database).EvaluateRecentExecutionSimulationEvidenceAsync(
            "strategy-a",
            "v7",
            "research-cost-v1",
            "execution-sim-v1",
            "binance-testnet-rules-v1",
            "BTCUSDT",
            Policy(),
            100,
            Now,
            default);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Insufficient, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Equal(1, decision.ComparisonCount);
        Assert.Contains("sample.comparisons", decision.ReasonCodes);
        Assert.Contains("sample.window", decision.ReasonCodes);
    }

    [Fact]
    public async Task MetadataAndCanonicalBytesMismatchFailsClosedOnRead()
    {
        var store = new AgentSqliteStore(Database);
        Assert.Empty(await store.GetRecentExecutionSimulationComparisonFactsAsync(
            "strategy-a","v7","research-cost-v1","tampered-model","binance-testnet-rules-v1","BTCUSDT",
            10, default));

        var simulated = Simulated(
            "corrupt",
            Now.AddMinutes(-5),
            simulationModelVersion:"execution-sim-v1");
        var observed = Observed(
            "corrupt",
            Now.AddMinutes(-5),
            strategyId:"strategy-a",
            strategyVersion:"v7",
            costModelVersion:"research-cost-v1",
            symbol:"BTCUSDT");
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(
            simulated,
            observed,
            Now.AddMinutes(-5));

        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO execution_simulation_comparisons(
                    canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                    simulation_model_version,venue_rule_version,symbol,simulated_sha256,observed_sha256,compared_at,canonical_bytes)
                VALUES(
                    $hash,$correlation,$client,$strategy,$version,$costModel,
                    'tampered-model',$venueRule,$symbol,$simulatedHash,$observedHash,$compared,$bytes);
                """;
            command.Parameters.AddWithValue("$hash", comparison.CanonicalSha256);
            command.Parameters.AddWithValue("$correlation", comparison.CorrelationId);
            command.Parameters.AddWithValue("$client", comparison.ClientOrderId);
            command.Parameters.AddWithValue("$strategy", comparison.StrategyId);
            command.Parameters.AddWithValue("$version", comparison.StrategyVersion);
            command.Parameters.AddWithValue("$costModel", comparison.CostModelVersion);
            command.Parameters.AddWithValue("$venueRule", comparison.VenueRuleVersion);
            command.Parameters.AddWithValue("$symbol", comparison.Symbol);
            command.Parameters.AddWithValue("$simulatedHash", comparison.SimulatedCanonicalSha256);
            command.Parameters.AddWithValue("$observedHash", comparison.ObservedCanonicalSha256);
            command.Parameters.AddWithValue("$compared", comparison.ComparedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$bytes", comparison.CanonicalBytes);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetRecentExecutionSimulationComparisonFactsAsync(
                "strategy-a","v7","research-cost-v1","tampered-model","binance-testnet-rules-v1","BTCUSDT",
                10, default));
    }

    [Fact]
    public async Task InvalidIdentityIsRejectedBeforeDatabaseQuery()
    {
        var store = new AgentSqliteStore(Database);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetRecentExecutionSimulationComparisonFactsAsync(
                "strategy-a","v7","research-cost-v1","","binance-testnet-rules-v1","BTCUSDT",
                10, default));
    }

    private static ExecutionSimulationEvidencePolicyV1 Policy() => new(
        Version:"simulation-evidence-policy-v1",
        MinimumComparisons:4,
        MinimumPriceComparableComparisons:3,
        MinimumFeeComparableComparisons:3,
        MinimumLatencyComparableComparisons:3,
        MinimumTotalComparableComparisons:3,
        MinimumObservationWindow:TimeSpan.FromHours(2),
        MaximumEvidenceAge:TimeSpan.FromHours(1),
        MaximumStateMismatchFraction:.25m,
        MaximumUnsupportedFraction:.25m,
        MaximumP95AbsoluteFillRatioDelta:.10m,
        MaximumP95AdversePriceDriftBps:25m,
        MaximumP95AdverseFeeDriftBps:3m,
        MaximumP95PositiveLatencyDriftMs:500,
        MaximumP95TotalExecutionDriftBps:30m);

    private static async Task SavePair(
        AgentSqliteStore store,
        string id,
        DateTimeOffset comparedAt,
        string simulationModelVersion = "execution-sim-v1",
        string venueRuleVersion = "binance-testnet-rules-v1",
        string symbol = "BTCUSDT")
    {
        var simulated = Simulated(id, comparedAt, simulationModelVersion, venueRuleVersion, symbol);
        var observed = Observed(
            id,
            comparedAt,
            "strategy-a",
            "v7",
            "research-cost-v1",
            symbol);
        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, comparedAt);

        await store.SaveExecutionSimulationFillAsync(simulated, default);
        await store.SaveExecutionRealityDriftAsync(observed, default);
        await store.SaveExecutionSimulationComparisonAsync(comparison, default);
    }

    private static ExecutionSimulationFillV1 Simulated(
        string id,
        DateTimeOffset comparedAt,
        string simulationModelVersion = "execution-sim-v1",
        string venueRuleVersion = "binance-testnet-rules-v1",
        string symbol = "BTCUSDT")
    {
        var simulatedAt = comparedAt.AddSeconds(-10);
        return ExecutionSimulationFillCanonicalizerV1.Create(
            correlationId:"cycle-" + id,
            clientOrderId:"order-" + id,
            strategyId:"strategy-a",
            strategyVersion:"v7",
            costModelVersion:"research-cost-v1",
            simulationModelVersion:simulationModelVersion,
            venueRuleVersion:venueRuleVersion,
            symbol:symbol,
            side:PositionSide.Long,
            reduceOnly:false,
            orderType:ExecutionOrderType.Market,
            intendedQuantity:1m,
            state:ExecutionSimulationFillStateV1.Filled,
            executedQuantity:1m,
            averagePrice:100m,
            feeAmount:.04m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:true,
            simulatedLatencyMs:500,
            marketAsOfUtc:simulatedAt.AddSeconds(-1),
            simulatedAtUtc:simulatedAt,
            reasonCode:"modeled");
    }

    private static ExecutionRealityDriftFactV1 Observed(
        string id,
        DateTimeOffset comparedAt,
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string symbol)
    {
        var intendedAt = comparedAt.AddSeconds(-10);
        var observedAt = intendedAt.AddMilliseconds(500);
        var expectation = new ExecutionRealityExpectationV1(
            "cycle-" + id,
            "order-" + id,
            strategyId,
            strategyVersion,
            costModelVersion,
            symbol,
            PositionSide.Long,
            false,
            ExecutionOrderType.Market,
            1m,
            100m,
            .0004m,
            .001m,
            intendedAt);
        var observation = new ExecutionRealityObservationV1(
            "order-" + id,
            "FILLED",
            1m,
            100m,
            .04m,
            ExecutionRealityDriftV1.ExchangeReportedFeeBasis,
            observedAt.AddMilliseconds(-50),
            observedAt);
        return ExecutionRealityDriftV1.Analyze(expectation, observation);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
