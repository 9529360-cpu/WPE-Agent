using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionSimulationResearchProvenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-sim-research-prov-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");

    [Fact]
    public void CanonicalResearchProvenanceIsDeterministicAndRoundTrips()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var first = ExecutionSimulationResearchProvenanceFixture.Create(fill);
        var second = ExecutionSimulationResearchProvenanceFixture.Create(fill);

        Assert.True(ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(first));
        Assert.Equal(first.CanonicalSha256, second.CanonicalSha256);
        Assert.Equal(first.CanonicalBytes, second.CanonicalBytes);
        Assert.Equal(fill.CanonicalSha256, first.SimulatedFillCanonicalSha256);
        Assert.Equal(fill.MarketAsOfUtc, first.MarketCollectedAtUtc);
        Assert.Equal(fill.MarketAsOfUtc, first.MarketAsOfUtc);

        Assert.True(ExecutionSimulationResearchProvenanceCanonicalizerV1.TryDeserialize(
            first.CanonicalBytes,
            first.CanonicalSha256,
            out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(first.CanonicalSha256, parsed!.CanonicalSha256);
        Assert.Equal(first.BacktestValidationSha256, parsed.BacktestValidationSha256);
        Assert.Equal(first.TimelineSha256, parsed.TimelineSha256);
        Assert.Equal(first.ShadowObservationSha256, parsed.ShadowObservationSha256);
        Assert.Equal(first.MarketProvenanceSha256, parsed.MarketProvenanceSha256);
        Assert.Equal(first.CanonicalBytes, parsed.CanonicalBytes);
    }

    [Fact]
    public void ResearchProvenanceChronologyFailsClosed()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceFixture.Create(
                fill,
                timelineAt:ExecutionSimulationResearchProvenanceFixture.ValidationAt.AddMinutes(1)));

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceFixture.Create(
                fill,
                validationAt:fill.MarketAsOfUtc.AddMinutes(1)));

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceFixture.Create(
                fill,
                shadowAt:fill.MarketAsOfUtc.AddMinutes(-1)));

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceFixture.Create(
                fill,
                shadowAt:fill.SimulatedAtUtc.AddMilliseconds(1)));

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceFixture.Create(
                fill,
                boundAt:fill.SimulatedAtUtc.AddMilliseconds(-1)));
    }

    [Fact]
    public void MissingMalformedOrTamperedSourceBytesFailClosed()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var bundle = ExecutionSimulationResearchProvenanceFixture.Bundle(fill);

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
                fill,
                Array.Empty<byte>(),
                bundle.Timeline,
                bundle.Shadow,
                bundle.Market,
                ExecutionSimulationResearchProvenanceFixture.BoundAt));

        var malformed = bundle.Backtest.ToArray();
        malformed[0] = (byte)'x';
        Assert.ThrowsAny<Exception>(() =>
            ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
                fill,
                malformed,
                bundle.Timeline,
                bundle.Shadow,
                bundle.Market,
                ExecutionSimulationResearchProvenanceFixture.BoundAt));

        var provenance = ExecutionSimulationResearchProvenanceFixture.Create(fill);
        Assert.False(ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(
            provenance with { TimelineCanonicalBytes = provenance.MarketProvenanceCanonicalBytes }));
        Assert.False(ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(
            provenance with { CanonicalSha256 = new string('0', 64) }));
    }

    [Fact]
    public void CrossStrategyOrDifferentMarketSourceCannotBindToSimulatedFill()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var wrongIdentity = ExecutionSimulationResearchProvenanceFixture.Bundle(fill, strategyId:"strategy-b");

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
                fill,
                wrongIdentity.Backtest,
                wrongIdentity.Timeline,
                wrongIdentity.Shadow,
                wrongIdentity.Market,
                ExecutionSimulationResearchProvenanceFixture.BoundAt));

        var differentMarket = ExecutionSimulationResearchProvenanceFixture.Bundle(
            fill,
            marketAt:fill.MarketAsOfUtc.AddMilliseconds(-1));
        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
                fill,
                differentMarket.Backtest,
                differentMarket.Timeline,
                differentMarket.Shadow,
                differentMarket.Market,
                ExecutionSimulationResearchProvenanceFixture.BoundAt));
    }

    [Fact]
    public async Task PersistenceRequiresCanonicalSimulatedFillAndSurvivesRestart()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var provenance = ExecutionSimulationResearchProvenanceFixture.Create(fill);
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
        Assert.Equal(provenance.ShadowObservationCanonicalBytes, loaded.ShadowObservationCanonicalBytes);
    }

    [Fact]
    public async Task OneSimulatedFillCannotBeReboundToConflictingResearchHistory()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var store = new AgentSqliteStore(Database);
        await store.SaveExecutionSimulationFillAsync(fill, default);
        await store.SaveExecutionSimulationResearchProvenanceAsync(
            ExecutionSimulationResearchProvenanceFixture.Create(fill),
            default);

        var conflicting = ExecutionSimulationResearchProvenanceFixture.Create(
            fill,
            validationAt:ExecutionSimulationResearchProvenanceFixture.ValidationAt.AddMinutes(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveExecutionSimulationResearchProvenanceAsync(conflicting, default));
        Assert.Contains("conflicting research provenance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProvenanceLedgerIsAppendOnly()
    {
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var store = new AgentSqliteStore(Database);
        await store.SaveExecutionSimulationFillAsync(fill, default);
        await store.SaveExecutionSimulationResearchProvenanceAsync(
            ExecutionSimulationResearchProvenanceFixture.Create(fill),
            default);

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
        var fill = ExecutionSimulationResearchProvenanceFixture.Fill("order-a");
        var provenance = ExecutionSimulationResearchProvenanceFixture.Create(fill);
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

internal static class ExecutionSimulationResearchProvenanceFixture
{
    internal static readonly DateTimeOffset TimelineAt = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset ValidationAt = new(2026, 9, 18, 11, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset MarketAt = new(2026, 9, 18, 11, 59, 59, TimeSpan.Zero);
    internal static readonly DateTimeOffset ShadowAt = MarketAt.AddMilliseconds(500);
    internal static readonly DateTimeOffset SimulatedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset BoundAt = SimulatedAt.AddSeconds(1);

    internal sealed record SourceBundle(byte[] Backtest, byte[] Timeline, byte[] Shadow, byte[] Market);

    internal static ExecutionSimulationResearchProvenanceV1 Create(
        ExecutionSimulationFillV1 fill,
        DateTimeOffset? timelineAt = null,
        DateTimeOffset? validationAt = null,
        DateTimeOffset? marketAt = null,
        DateTimeOffset? shadowAt = null,
        DateTimeOffset? boundAt = null,
        string? strategyId = null)
    {
        var bundle = Bundle(fill, timelineAt, validationAt, marketAt, shadowAt, strategyId);
        return ExecutionSimulationResearchProvenanceCanonicalizerV1.Create(
            fill,
            bundle.Backtest,
            bundle.Timeline,
            bundle.Shadow,
            bundle.Market,
            boundAt ?? BoundAt);
    }

    internal static SourceBundle Bundle(
        ExecutionSimulationFillV1 fill,
        DateTimeOffset? timelineAt = null,
        DateTimeOffset? validationAt = null,
        DateTimeOffset? marketAt = null,
        DateTimeOffset? shadowAt = null,
        string? strategyId = null)
    {
        var sid = strategyId ?? fill.StrategyId;
        var timeline = timelineAt ?? TimelineAt;
        var validation = validationAt ?? ValidationAt;
        var market = marketAt ?? fill.MarketAsOfUtc;
        var shadow = shadowAt ?? ShadowAt;

        var backtest = BacktestBytes(sid, fill.StrategyVersion, fill.Symbol, validation);
        var timelineBytes = TimelineBytes(sid, fill.StrategyVersion, fill.Symbol, timeline);
        var marketBytes = MarketBytes(fill.Symbol, market, fill.AveragePrice > 0 ? fill.AveragePrice : 100m);
        var shadowBytes = ShadowBytes(
            sid,
            fill.StrategyVersion,
            fill.Symbol,
            validation,
            market,
            shadow,
            fill.AveragePrice > 0 ? fill.AveragePrice : 100m,
            Hash(backtest),
            Hash(timelineBytes),
            Hash(marketBytes));

        return new(backtest, timelineBytes, shadowBytes, marketBytes);
    }

    internal static ExecutionSimulationFillV1 Fill(string orderId) =>
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

    private static byte[] BacktestBytes(
        string strategyId,
        string strategyVersion,
        string symbol,
        DateTimeOffset validatedAt)
    {
        var values = new[]
        {
            "wpe.backtest-validation/1.1",
            symbol,
            strategyId,
            strategyVersion,
            validatedAt.ToString("O", CultureInfo.InvariantCulture),
            "800",
            "120",
            "40",
            "365",
            "0.55",
            "1.4",
            "0.01",
            "0.12",
            "1.1",
            "0.08",
            "0.7",
            "0.2",
            "0.8",
            "true",
            "false"
        };
        return Encoding.UTF8.GetBytes(string.Join('|', values));
    }

    private static byte[] TimelineBytes(
        string strategyId,
        string strategyVersion,
        string symbol,
        DateTimeOffset tradableAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("decision_count", 1);
            writer.WritePropertyName("decisions");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("evidence_available_at_utc", tradableAt.AddMinutes(-5));
            writer.WriteNumber("execution_close_price", 100m);
            writer.WriteNumber("execution_open_price", 99.5m);
            writer.WriteNumber("execution_volume", 1000m);
            writer.WriteNumber("sequence", 0);
            writer.WriteString("signal_generated_at_utc", tradableAt.AddMinutes(-4));
            writer.WriteString("source_candle_open_time_utc", tradableAt.AddMinutes(-10));
            writer.WriteString("strategy_id", strategyId);
            writer.WriteString("strategy_version", strategyVersion);
            writer.WriteString("symbol", symbol);
            writer.WriteNumber("target_exposure", 1);
            writer.WriteString("tradable_at_utc", tradableAt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("first_tradable_at_utc", tradableAt);
            writer.WriteString("last_tradable_at_utc", tradableAt);
            writer.WriteString("schema", "wpe.strategy-exposure-timeline/1.0");
            writer.WriteString("strategy_id", strategyId);
            writer.WriteString("strategy_version", strategyVersion);
            writer.WriteString("symbol", symbol);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] MarketBytes(string symbol, DateTimeOffset collectedAt, decimal price)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("candle_count", 2);
            writer.WriteString("collected_at_utc", collectedAt);
            writer.WriteString("environment", "Testnet");
            writer.WriteNumber("price", price);
            writer.WriteString("provider_id", "binance-futures");
            writer.WriteNumber("resistance", price + 2m);
            writer.WriteNumber("rsi", 55d);
            writer.WriteString("schema", "wpe.market-evidence-provenance/1.0");
            writer.WriteNumber("support", price - 2m);
            writer.WriteNumber("trend_15m", .1d);
            writer.WriteNumber("trend_1h", .2d);
            writer.WriteNumber("trend_4h", .3d);
            writer.WritePropertyName("candles");
            writer.WriteStartArray();
            WriteCandle(writer, collectedAt.AddHours(-1), price - 1m);
            WriteCandle(writer, collectedAt.AddMinutes(-30), price - .5m);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] ShadowBytes(
        string strategyId,
        string strategyVersion,
        string symbol,
        DateTimeOffset validationAt,
        DateTimeOffset marketAt,
        DateTimeOffset observedAt,
        decimal marketPrice,
        string validationHash,
        string timelineHash,
        string marketHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256", validationHash);
            writer.WriteNumber("confidence", .6d);
            writer.WriteNumber("direction", 1);
            writer.WriteString("environment", "Testnet");
            writer.WriteString("lifecycle", "Shadow");
            writer.WriteString("market_collected_at_utc", marketAt);
            writer.WriteNumber("market_price", marketPrice);
            writer.WriteString("market_provenance_sha256", marketHash);
            writer.WriteString("market_provider_id", "binance-futures");
            writer.WriteString("observed_at_utc", observedAt);
            writer.WriteString("schema", "wpe.strategy-shadow-observation/1.0");
            writer.WriteString("strategy_id", strategyId);
            writer.WriteString("strategy_version", strategyVersion);
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeline_sha256", timelineHash);
            writer.WriteString("validation_at_utc", validationAt);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCandle(Utf8JsonWriter writer, DateTimeOffset time, decimal price)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(time);
        writer.WriteNumberValue(price);
        writer.WriteNumberValue(price + 1m);
        writer.WriteNumberValue(price - 1m);
        writer.WriteNumberValue(price + .2m);
        writer.WriteNumberValue(1000m);
        writer.WriteNumberValue(100000m);
        writer.WriteNumberValue(100m);
        writer.WriteNumberValue(500m);
        writer.WriteEndArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
