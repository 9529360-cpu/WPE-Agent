using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionSimulationResearchProvenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-sim-research-prov-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");

    private static readonly DateTimeOffset TimelineAt = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidationAt = new(2026, 9, 18, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ShadowAt = new(2026, 9, 18, 11, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MarketAt = new(2026, 9, 18, 11, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset SimulatedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BoundAt = new(2026, 9, 18, 12, 0, 1, TimeSpan.Zero);

    [Fact]
    public void CanonicalResearchProvenanceIsDeterministicAndRoundTrips()
    {
        var fill = Fill("order-a");
        var first = Provenance(fill);
        var second = Provenance(fill);

        Assert.True(ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(first));
        Assert.Equal(first.CanonicalSha256, second.CanonicalSha256);
        Assert.Equal(first.CanonicalBytes, second.CanonicalBytes);
        Assert.Equal(fill.CanonicalSha256, first.SimulatedFillCanonicalSha256);
        Assert.Equal(fill.MarketAsOfUtc, first.MarketAsOfUtc);
        Assert.Equal(fill.SimulatedAtUtc, first.SimulatedAtUtc);

        Assert.True(ExecutionSimulationResearchProvenanceCanonicalizerV1.TryDeserialize(
            first.CanonicalBytes,
            first.CanonicalSha256,
            out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(first, parsed);
    }

    [Fact]
    public void ResearchProvenanceChronologyFailsClosed()
    {
        var fill = Fill("order-a");

        Assert.Throws<InvalidOperationException>(() =>
            Create(fill, TimelineAt.AddHours(2), ValidationAt, ShadowAt, BoundAt));
        Assert.Throws<InvalidOperationException>(() =>
            Create(fill, TimelineAt, ShadowAt.AddMinutes(1), ShadowAt, BoundAt));
        Assert.Throws<InvalidOperationException>(() =>
            Create(fill, TimelineAt, ValidationAt, MarketAt.AddSeconds(1), BoundAt));
        Assert.Throws<InvalidOperationException>(() =>
            Create(fill, TimelineAt, ValidationAt, ShadowAt, SimulatedAt.AddMilliseconds(-1)));
    }

    [Fact]
    public void HashOrFillTamperingCannotProduceCanonicalResearchProvenance()
    {
        var fill = Fill("order-a");
        var provenance = Provenance(fill);

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
                fill,
                "bad",
                TimelineHash,
                ShadowHash,
                MarketHash,
                TimelineAt,
                ValidationAt,
                ShadowAt,
                BoundAt));

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
                fill with { AveragePrice = 999m },
                ValidationHash,
                TimelineHash,
                ShadowHash,
                MarketHash,
                TimelineAt,
                ValidationAt,
                ShadowAt,
                BoundAt));

        Assert.False(ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(
            provenance with { TimelineSha256 = ValidationHash }));
        Assert.False(ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(
            provenance with { CanonicalSha256 = new string('0', 64) }));
    }

    [Fact]
    public async Task PersistenceRequiresTheCanonicalSimulatedFillAndSurvivesRestart()
    {
        var fill = Fill("order-a");
        var provenance = Provenance(fill);
        var store = new AgentSqliteStore(Database);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionSimulationResearchProvenanceAsync(provenance, default));
        Assert.Contains("simulated fill source is missing", missing.Message, StringComparison.OrdinalIgnoreCase);

        await store.SaveExecutionSimulationFillAsync(fill, default);
        var first = await store.SaveExecutionSimulationResearchProvenanceAsync(provenance, default);
        var second = await new AgentSqliteStore(Database).SaveExecutionSimulationResearchProvenanceAsync(provenance, default);

        Assert.True(first.Succeeded);
        Assert.False(first.Idempotent);
        Assert.True(second.Idempotent);

        var loaded = await new AgentSqliteStore(Database).GetExecutionSimulationResearchProvenanceAsync(
            fill.CanonicalSha256,
            default);
        Assert.NotNull(loaded);
        Assert.Equal(provenance.CanonicalSha256, loaded!.CanonicalSha256);
        Assert.Equal(provenance.CanonicalBytes, loaded.CanonicalBytes);
        Assert.Equal(provenance.ShadowObservationSha256, loaded.ShadowObservationSha256);
    }

    [Fact]
    public async Task OneSimulatedFillCannotBeReboundToConflictingResearchHistory()
    {
        var fill = Fill("order-a");
        var store = new AgentSqliteStore(Database);
        await store.SaveExecutionSimulationFillAsync(fill, default);
        await store.SaveExecutionSimulationResearchProvenanceAsync(Provenance(fill), default);

        var conflicting = ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
            fill,
            new string('e', 64),
            TimelineHash,
            ShadowHash,
            MarketHash,
            TimelineAt,
            ValidationAt,
            ShadowAt,
            BoundAt);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionSimulationResearchProvenanceAsync(conflicting, default));
        Assert.Contains("conflicting research provenance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProvenanceLedgerIsAppendOnly()
    {
        var fill = Fill("order-a");
        var store = new AgentSqliteStore(Database);
        await store.SaveExecutionSimulationFillAsync(fill, default);
        await store.SaveExecutionSimulationResearchProvenanceAsync(Provenance(fill), default);

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();

        foreach (var sql in new[]
        {
            "UPDATE execution_simulation_research_provenance SET symbol='ETHUSDT'",
            "DELETE FROM execution_simulation_research_provenance"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task MetadataAndCanonicalBytesMismatchFailsClosedOnRead()
    {
        var fill = Fill("order-a");
        var provenance = Provenance(fill);
        var store = new AgentSqliteStore(Database);
        await store.SaveExecutionSimulationFillAsync(fill, default);

        Assert.Null(await store.GetExecutionSimulationResearchProvenanceAsync(fill.CanonicalSha256, default));

        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO execution_simulation_research_provenance(
                    canonical_sha256,simulated_fill_sha256,correlation_id,client_order_id,
                    strategy_id,strategy_version,cost_model_version,simulation_model_version,
                    venue_rule_version,symbol,backtest_validation_sha256,timeline_sha256,
                    shadow_observation_sha256,market_provenance_sha256,bound_at,canonical_bytes)
                VALUES(
                    $hash,$fillHash,$correlation,$client,'wrong-strategy',$version,$costModel,$simulationModel,
                    $venueRule,$symbol,$validationHash,$timelineHash,$shadowHash,$marketHash,$bound,$bytes);
                """;
            command.Parameters.AddWithValue("$hash", provenance.CanonicalSha256);
            command.Parameters.AddWithValue("$fillHash", provenance.SimulatedFillCanonicalSha256);
            command.Parameters.AddWithValue("$correlation", provenance.CorrelationId);
            command.Parameters.AddWithValue("$client", provenance.ClientOrderId);
            command.Parameters.AddWithValue("$version", provenance.StrategyVersion);
            command.Parameters.AddWithValue("$costModel", provenance.CostModelVersion);
            command.Parameters.AddWithValue("$simulationModel", provenance.SimulationModelVersion);
            command.Parameters.AddWithValue("$venueRule", provenance.VenueRuleVersion);
            command.Parameters.AddWithValue("$symbol", provenance.Symbol);
            command.Parameters.AddWithValue("$validationHash", provenance.BacktestValidationSha256);
            command.Parameters.AddWithValue("$timelineHash", provenance.TimelineSha256);
            command.Parameters.AddWithValue("$shadowHash", provenance.ShadowObservationSha256);
            command.Parameters.AddWithValue("$marketHash", provenance.MarketProvenanceSha256);
            command.Parameters.AddWithValue("$bound", provenance.BoundAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$bytes", provenance.CanonicalBytes);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetExecutionSimulationResearchProvenanceAsync(fill.CanonicalSha256, default));
    }

    private static ExecutionSimulationResearchProvenanceV1 Provenance(ExecutionSimulationFillV1 fill) =>
        Create(fill, TimelineAt, ValidationAt, ShadowAt, BoundAt);

    private static ExecutionSimulationResearchProvenanceV1 Create(
        ExecutionSimulationFillV1 fill,
        DateTimeOffset timelineAt,
        DateTimeOffset validationAt,
        DateTimeOffset shadowAt,
        DateTimeOffset boundAt) =>
        ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
            fill,
            ValidationHash,
            TimelineHash,
            ShadowHash,
            MarketHash,
            timelineAt,
            validationAt,
            shadowAt,
            boundAt);

    private static ExecutionSimulationFillV1 Fill(string orderId) =>
        ExecutionSimulationFillCanonicalizerV1.Create(
            correlationId:"cycle-" + orderId,
            clientOrderId:orderId,
            strategyId:"strategy-a",
            strategyVersion:"v7",
            costModelVersion:"research-cost-v1",
            simulationModelVersion:"execution-sim-v1",
            venueRuleVersion:"binance-testnet-rules-v1",
            symbol:"BTCUSDT",
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
            marketAsOfUtc:MarketAt,
            simulatedAtUtc:SimulatedAt,
            reasonCode:"modeled");

    private static string ValidationHash => new('a', 64);
    private static string TimelineHash => new('b', 64);
    private static string ShadowHash => new('c', 64);
    private static string MarketHash => new('d', 64);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
