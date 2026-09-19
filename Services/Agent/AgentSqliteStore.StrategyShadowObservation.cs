using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<bool> SaveStrategyShadowObservationAsync(
        StrategyShadowObservationV1 value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!StrategyShadowObservationCanonicalizerV1.IsCanonical(value))
            throw new InvalidOperationException("Strategy shadow observation canonical identity is invalid.");
        await VerifyStrategyShadowSourcesAsync(value,ct);

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureStrategyShadowObservationStorageAsync(connection,ct);

        await using(var insert=connection.CreateCommand())
        {
            insert.CommandText="""
                INSERT OR IGNORE INTO strategy_shadow_observation_artifacts(
                    canonical_sha256,schema,strategy_id,strategy_version,symbol,lifecycle,
                    validation_at,observed_at,market_collected_at,market_provenance_sha256,
                    market_provenance_bytes,backtest_validation_sha256,backtest_validation_bytes,
                    timeline_sha256,canonical_bytes)
                VALUES(
                    $hash,$schema,$strategy,$version,$symbol,$lifecycle,
                    $validationAt,$observed,$marketAt,$marketHash,$marketBytes,$validationHash,$validationBytes,
                    $timelineHash,$bytes);
                SELECT changes();
                """;
            insert.Parameters.AddWithValue("$hash",value.CanonicalSha256);
            insert.Parameters.AddWithValue("$schema",value.Schema);
            insert.Parameters.AddWithValue("$strategy",value.StrategyId);
            insert.Parameters.AddWithValue("$version",value.StrategyVersion);
            insert.Parameters.AddWithValue("$symbol",value.Symbol);
            insert.Parameters.AddWithValue("$lifecycle",value.Lifecycle);
            insert.Parameters.AddWithValue("$validationAt",value.ValidationAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$observed",value.ObservedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$marketAt",value.MarketCollectedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$marketHash",value.MarketProvenanceSha256);
            insert.Parameters.Add("$marketBytes",SqliteType.Blob).Value=value.MarketProvenanceCanonicalBytes;
            insert.Parameters.AddWithValue("$validationHash",value.BacktestValidationSha256);
            insert.Parameters.Add("$validationBytes",SqliteType.Blob).Value=value.BacktestValidationCanonicalBytes;
            insert.Parameters.AddWithValue("$timelineHash",value.TimelineSha256);
            insert.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
            if(Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1)
                return true;
        }

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT canonical_sha256,schema,symbol,lifecycle,validation_at,observed_at,market_collected_at,
                   market_provenance_sha256,market_provenance_bytes,
                   backtest_validation_sha256,backtest_validation_bytes,timeline_sha256,canonical_bytes
            FROM strategy_shadow_observation_artifacts
            WHERE strategy_id=$strategy
              AND strategy_version=$version
              AND market_provenance_sha256=$marketHash
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$strategy",value.StrategyId);
        query.Parameters.AddWithValue("$version",value.StrategyVersion);
        query.Parameters.AddWithValue("$marketHash",value.MarketProvenanceSha256);
        await using var reader=await query.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Strategy shadow observation idempotent replay lost persisted evidence.");

        var marketBytes=(byte[])reader[8];
        var validationBytes=(byte[])reader[10];
        var bytes=(byte[])reader[12];
        if(!string.Equals(reader.GetString(0),value.CanonicalSha256,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(1),value.Schema,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(2),value.Symbol,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(3),value.Lifecycle,StringComparison.Ordinal)
           ||DateTimeOffset.Parse(reader.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.ValidationAtUtc
           ||DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.ObservedAtUtc
           ||DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.MarketCollectedAtUtc
           ||!string.Equals(reader.GetString(7),value.MarketProvenanceSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(marketBytes,value.MarketProvenanceCanonicalBytes)
           ||!string.Equals(reader.GetString(9),value.BacktestValidationSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(validationBytes,value.BacktestValidationCanonicalBytes)
           ||!string.Equals(reader.GetString(11),value.TimelineSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes))
            throw new InvalidOperationException("Strategy shadow observation replay conflicts with persisted evidence.");

        return false;
    }

    private async Task VerifyStrategyShadowSourcesAsync(
        StrategyShadowObservationV1 value,
        CancellationToken ct)
    {
        var persistedValidation=await GetLatestStrategyValidationAsync(
            value.StrategyId,value.StrategyVersion,ct);
        var backtest=await GetLatestBacktestRunAsync(
            value.StrategyId,value.StrategyVersion,ct);
        if(persistedValidation is null
           ||backtest is null
           ||!string.Equals(backtest.StrategyId,value.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(backtest.StrategyVersion,value.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(backtest.Symbol,value.Symbol,StringComparison.Ordinal)
           ||!string.Equals(backtest.Status,"PASSED",StringComparison.Ordinal))
            throw new InvalidOperationException("Strategy shadow observation source validation is missing.");

        var validation=persistedValidation.Validation;
        var completedUtc=backtest.CompletedAtUtc.Kind==DateTimeKind.Utc
            ?backtest.CompletedAtUtc
            :backtest.CompletedAtUtc.ToUniversalTime();
        var completedAt=new DateTimeOffset(DateTime.SpecifyKind(completedUtc,DateTimeKind.Utc));
        if(value.ValidationAtUtc!=completedAt
           ||(persistedValidation.CreatedAtUtc-completedAt).Duration()>StrategyResearchAuthority.MaximumEvidencePairSkew
           ||!validation.Passed
           ||!string.Equals(validation.TimelineSha256,value.TimelineSha256,StringComparison.Ordinal))
            throw new InvalidOperationException("Strategy shadow observation source validation conflicts with durable evidence.");

        var expectedValidation=BacktestValidationCanonicalizerV1.Create(
            value.Symbol,
            value.StrategyId,
            value.StrategyVersion,
            completedAt,
            validation.SampleSize,
            validation.Trades,
            validation.OutOfSampleTrades,
            backtest.CoverageDays,
            validation.WinRate,
            validation.ProfitFactor,
            validation.Expectancy,
            validation.MaxDrawdown,
            validation.Sharpe,
            validation.OutOfSampleReturn,
            validation.WalkForwardScore,
            validation.MonteCarloLossProbability,
            validation.QualityScore,
            approved:true,
            promoted:string.Equals(value.Lifecycle,"Active",StringComparison.Ordinal));
        if(!BacktestValidationCanonicalizerV1.IsCanonical(expectedValidation,value.ObservedAtUtc)
           ||!string.Equals(expectedValidation.CanonicalSha256,value.BacktestValidationSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(
               expectedValidation.CanonicalBytes,value.BacktestValidationCanonicalBytes))
            throw new InvalidOperationException("Strategy shadow observation backtest provenance does not replay.");

        var timeline=await GetStrategyExposureTimelineAsync(
            value.TimelineSha256,
            value.StrategyId,
            value.StrategyVersion,
            value.Symbol,
            ct);
        if(timeline is null)
            throw new InvalidOperationException("Strategy shadow observation timeline provenance is missing.");
    }

    private static async Task EnsureStrategyShadowObservationStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS strategy_shadow_observation_artifacts(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                lifecycle TEXT NOT NULL,
                validation_at TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                market_collected_at TEXT NOT NULL,
                market_provenance_sha256 TEXT NOT NULL,
                market_provenance_bytes BLOB NOT NULL,
                backtest_validation_sha256 TEXT NOT NULL,
                backtest_validation_bytes BLOB NOT NULL,
                timeline_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(strategy_id,strategy_version,market_provenance_sha256));
            CREATE INDEX IF NOT EXISTS ix_strategy_shadow_observation_time
                ON strategy_shadow_observation_artifacts(strategy_id,strategy_version,observed_at DESC);
            CREATE TRIGGER IF NOT EXISTS strategy_shadow_observation_no_update
                BEFORE UPDATE ON strategy_shadow_observation_artifacts
                BEGIN SELECT RAISE(ABORT,'strategy shadow observations are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS strategy_shadow_observation_no_delete
                BEFORE DELETE ON strategy_shadow_observation_artifacts
                BEGIN SELECT RAISE(ABORT,'strategy shadow observations are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}
