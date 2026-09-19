using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<bool> SaveExecutionPreTradeSimulationProvenanceAsync(
        ExecutionPreTradeSimulationProvenanceV1 value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!ExecutionPreTradeSimulationProvenanceCanonicalizerV1.IsCanonical(value))
            throw new InvalidOperationException("Pre-trade simulation provenance is not canonical.");

        await VerifyPreTradeSimulationSourcesAsync(value,ct);

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionPreTradeSimulationStorageAsync(connection,ct);

        await using(var insert=connection.CreateCommand())
        {
            insert.CommandText="""
                INSERT OR IGNORE INTO execution_pretrade_simulation_provenance(
                    canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,symbol,
                    provider_id,environment,artifact_sha256,intent_sha256,backtest_validation_sha256,
                    backtest_validation_bytes,timeline_sha256,market_provenance_sha256,market_provenance_bytes,
                    cost_model_version,cost_model_sha256,venue_rule_version,venue_rule_sha256,venue_rule_bytes,
                    simulated_fill_sha256,created_at,canonical_bytes)
                VALUES(
                    $hash,$correlation,$client,$strategy,$version,$symbol,
                    $provider,$environment,$artifact,$intent,$validationHash,
                    $validationBytes,$timeline,$marketHash,$marketBytes,
                    $costVersion,$costHash,$venueVersion,$venueHash,$venueBytes,
                    $fillHash,$created,$bytes);
                SELECT changes();
                """;
            insert.Parameters.AddWithValue("$hash",value.CanonicalSha256);
            insert.Parameters.AddWithValue("$correlation",value.CorrelationId);
            insert.Parameters.AddWithValue("$client",value.ClientOrderId);
            insert.Parameters.AddWithValue("$strategy",value.StrategyId);
            insert.Parameters.AddWithValue("$version",value.StrategyVersion);
            insert.Parameters.AddWithValue("$symbol",value.Symbol);
            insert.Parameters.AddWithValue("$provider",value.ProviderId);
            insert.Parameters.AddWithValue("$environment",value.Environment);
            insert.Parameters.AddWithValue("$artifact",value.ArtifactSha256);
            insert.Parameters.AddWithValue("$intent",value.IntentSha256);
            insert.Parameters.AddWithValue("$validationHash",value.BacktestValidationSha256);
            insert.Parameters.Add("$validationBytes",SqliteType.Blob).Value=value.BacktestValidationCanonicalBytes;
            insert.Parameters.AddWithValue("$timeline",value.TimelineSha256);
            insert.Parameters.AddWithValue("$marketHash",value.MarketProvenanceSha256);
            insert.Parameters.Add("$marketBytes",SqliteType.Blob).Value=value.MarketProvenanceCanonicalBytes;
            insert.Parameters.AddWithValue("$costVersion",value.CostModelVersion);
            insert.Parameters.AddWithValue("$costHash",value.CostModelSha256);
            insert.Parameters.AddWithValue("$venueVersion",value.VenueRuleVersion);
            insert.Parameters.AddWithValue("$venueHash",value.VenueRuleSha256);
            insert.Parameters.Add("$venueBytes",SqliteType.Blob).Value=value.VenueRuleCanonicalBytes;
            insert.Parameters.AddWithValue("$fillHash",value.SimulatedFillSha256);
            insert.Parameters.AddWithValue("$created",value.CreatedAtUtc.ToString("O"));
            insert.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
            if(Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1)
                return true;
        }

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT canonical_sha256,strategy_id,strategy_version,symbol,provider_id,environment,
                   artifact_sha256,intent_sha256,backtest_validation_sha256,backtest_validation_bytes,
                   timeline_sha256,market_provenance_sha256,market_provenance_bytes,
                   cost_model_version,cost_model_sha256,venue_rule_version,venue_rule_sha256,venue_rule_bytes,
                   simulated_fill_sha256,created_at,canonical_bytes
            FROM execution_pretrade_simulation_provenance
            WHERE correlation_id=$correlation AND client_order_id=$client
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$correlation",value.CorrelationId);
        query.Parameters.AddWithValue("$client",value.ClientOrderId);
        await using var reader=await query.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Pre-trade simulation provenance replay lost persisted evidence.");

        var validationBytes=(byte[])reader[9];
        var marketBytes=(byte[])reader[12];
        var venueBytes=(byte[])reader[17];
        var canonicalBytes=(byte[])reader[20];
        if(!string.Equals(reader.GetString(0),value.CanonicalSha256,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(1),value.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(2),value.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(3),value.Symbol,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(4),value.ProviderId,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(5),value.Environment,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(6),value.ArtifactSha256,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(7),value.IntentSha256,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(8),value.BacktestValidationSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(validationBytes,value.BacktestValidationCanonicalBytes)
           ||!string.Equals(reader.GetString(10),value.TimelineSha256,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(11),value.MarketProvenanceSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(marketBytes,value.MarketProvenanceCanonicalBytes)
           ||!string.Equals(reader.GetString(13),value.CostModelVersion,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(14),value.CostModelSha256,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(15),value.VenueRuleVersion,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(16),value.VenueRuleSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(venueBytes,value.VenueRuleCanonicalBytes)
           ||!string.Equals(reader.GetString(18),value.SimulatedFillSha256,StringComparison.Ordinal)
           ||DateTimeOffset.Parse(reader.GetString(19),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.CreatedAtUtc
           ||!CryptographicOperations.FixedTimeEquals(canonicalBytes,value.CanonicalBytes))
            throw new InvalidOperationException("Pre-trade simulation provenance conflicts with persisted evidence.");

        return false;
    }

    private async Task VerifyPreTradeSimulationSourcesAsync(
        ExecutionPreTradeSimulationProvenanceV1 value,
        CancellationToken ct)
    {
        var automatic=await GetAutomaticExecutionAsync(value.CorrelationId,ct);
        if(automatic is null
           ||!automatic.ArtifactValid
           ||automatic.Artifact is null
           ||!string.Equals(automatic.Artifact.ProviderId,value.ProviderId,StringComparison.Ordinal)
           ||!string.Equals(automatic.Artifact.Environment,value.Environment,StringComparison.Ordinal)
           ||!string.Equals(automatic.Artifact.StrategyId,value.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(automatic.Artifact.StrategyVersion,value.StrategyVersion,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade automatic artifact source is missing or invalid.");

        var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(automatic.Artifact);
        if(!FixedPreTradeHash(value.ArtifactSha256,hashes.ArtifactHash)
           ||!FixedPreTradeHash(value.IntentSha256,hashes.IntentHash)
           ||!FixedPreTradeHash(automatic.ArtifactHash,value.ArtifactSha256)
           ||!FixedPreTradeHash(automatic.IntentHash,value.IntentSha256)
           ||automatic.Artifact.Intents.Count(x=>string.Equals(x.ClientOrderId,value.ClientOrderId,StringComparison.Ordinal))!=1)
            throw new InvalidOperationException("Pre-trade automatic artifact provenance does not match.");

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
            throw new InvalidOperationException("Pre-trade historical validation source is missing.");

        var validation=persistedValidation.Validation;
        var completedUtc=backtest.CompletedAtUtc.Kind==DateTimeKind.Utc
            ?backtest.CompletedAtUtc
            :backtest.CompletedAtUtc.ToUniversalTime();
        var completedAt=new DateTimeOffset(DateTime.SpecifyKind(completedUtc,DateTimeKind.Utc));
        if((persistedValidation.CreatedAtUtc-completedAt).Duration()>StrategyResearchAuthority.MaximumEvidencePairSkew
           ||!validation.Passed
           ||!string.Equals(validation.TimelineSha256,value.TimelineSha256,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade historical validation source conflicts with durable evidence.");

        var expectedValidation=BacktestValidationCanonicalizerV1.Create(
            backtest.Symbol,
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
            promoted:true);
        if(!BacktestValidationCanonicalizerV1.IsCanonical(expectedValidation,value.CreatedAtUtc)
           ||!FixedPreTradeHash(expectedValidation.CanonicalSha256,value.BacktestValidationSha256)
           ||!CryptographicOperations.FixedTimeEquals(
               expectedValidation.CanonicalBytes,value.BacktestValidationCanonicalBytes))
            throw new InvalidOperationException("Pre-trade historical validation fact does not replay.");

        var timeline=await GetStrategyExposureTimelineAsync(
            value.TimelineSha256,value.StrategyId,value.StrategyVersion,value.Symbol,ct);
        if(timeline is null)
            throw new InvalidOperationException("Pre-trade timeline source is missing.");

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection,ct);
        var fill=await ReadPersistedSimulationFillAsync(connection,value.SimulatedFillSha256,ct);
        if(fill is null
           ||!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill)
           ||!string.Equals(fill.CorrelationId,value.CorrelationId,StringComparison.Ordinal)
           ||!string.Equals(fill.ClientOrderId,value.ClientOrderId,StringComparison.Ordinal)
           ||!string.Equals(fill.StrategyId,value.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(fill.StrategyVersion,value.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(fill.Symbol,value.Symbol,StringComparison.Ordinal)
           ||!string.Equals(fill.CostModelVersion,value.CostModelVersion,StringComparison.Ordinal)
           ||!string.Equals(fill.VenueRuleVersion,value.VenueRuleVersion,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade simulated fill source is missing or conflicting.");
    }

    private static async Task EnsureExecutionPreTradeSimulationStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_pretrade_simulation_provenance(
                canonical_sha256 TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                environment TEXT NOT NULL CHECK(environment='Testnet'),
                artifact_sha256 TEXT NOT NULL,
                intent_sha256 TEXT NOT NULL,
                backtest_validation_sha256 TEXT NOT NULL,
                backtest_validation_bytes BLOB NOT NULL,
                timeline_sha256 TEXT NOT NULL,
                market_provenance_sha256 TEXT NOT NULL,
                market_provenance_bytes BLOB NOT NULL,
                cost_model_version TEXT NOT NULL,
                cost_model_sha256 TEXT NOT NULL,
                venue_rule_version TEXT NOT NULL,
                venue_rule_sha256 TEXT NOT NULL,
                venue_rule_bytes BLOB NOT NULL,
                simulated_fill_sha256 TEXT NOT NULL,
                created_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(correlation_id,client_order_id));
            CREATE INDEX IF NOT EXISTS ix_execution_pretrade_simulation_strategy
                ON execution_pretrade_simulation_provenance(strategy_id,strategy_version,created_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_pretrade_simulation_no_update
                BEFORE UPDATE ON execution_pretrade_simulation_provenance
                BEGIN SELECT RAISE(ABORT,'pre-trade simulation provenance is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_pretrade_simulation_no_delete
                BEFORE DELETE ON execution_pretrade_simulation_provenance
                BEGIN SELECT RAISE(ABORT,'pre-trade simulation provenance is append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool FixedPreTradeHash(string? left,string? right)
    {
        if(left is null||right is null||left.Length!=64||right.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),Convert.FromHexString(right));}
        catch(FormatException){return false;}
    }
}

internal sealed class ExecutionPreTradeSimulationServiceV1
{
    private readonly AgentSqliteStore _store;
    private readonly Func<DateTimeOffset> _utcNow;

    internal ExecutionPreTradeSimulationServiceV1(
        AgentSqliteStore store,
        Func<DateTimeOffset>? utcNow=null)
    {
        _store=store??throw new ArgumentNullException(nameof(store));
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    internal async Task<ExecutionPreTradeSimulationBatchV1> PrepareAndPersistAsync(
        DurableExecutionArtifactV2 artifact,
        IReadOnlyList<ExecutionIntent> intents,
        MarketEvidence market,
        TradingRule rule,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(intents);
        var clock=_utcNow();
        if(clock.Offset!=TimeSpan.Zero)
            throw new InvalidOperationException("Pre-trade simulation clock must be UTC.");
        var now=clock;

        var profile=(await _store.GetStrategiesAsync(ct)).SingleOrDefault(x=>
            x.Lifecycle==StrategyLifecycle.Active
            &&string.Equals(x.Id,artifact.StrategyId,StringComparison.Ordinal)
            &&string.Equals(x.Version,artifact.StrategyVersion,StringComparison.Ordinal)
            &&string.Equals(x.Symbol,market.Symbol,StringComparison.Ordinal))
            ??throw new InvalidOperationException("Pre-trade simulation requires the exact active strategy.");

        var persistedValidation=await _store.GetLatestStrategyValidationAsync(
            artifact.StrategyId,artifact.StrategyVersion,ct);
        var backtest=await _store.GetLatestBacktestRunAsync(
            artifact.StrategyId,artifact.StrategyVersion,ct);
        if(persistedValidation is null
           ||backtest is null
           ||!string.Equals(backtest.Symbol,profile.Symbol,StringComparison.Ordinal)
           ||!string.Equals(backtest.Status,"PASSED",StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade simulation requires exact historical validation evidence.");

        var validation=persistedValidation.Validation;
        var completedUtc=backtest.CompletedAtUtc.Kind==DateTimeKind.Utc
            ?backtest.CompletedAtUtc
            :backtest.CompletedAtUtc.ToUniversalTime();
        var completedAt=new DateTimeOffset(DateTime.SpecifyKind(completedUtc,DateTimeKind.Utc));
        if(completedAt>now
           ||now-completedAt>StrategyResearchAuthority.MaximumValidationAge
           ||(persistedValidation.CreatedAtUtc-completedAt).Duration()>StrategyResearchAuthority.MaximumEvidencePairSkew
           ||!validation.Passed
           ||!new StrategyGovernor().CanPromote(profile,validation)
           ||string.IsNullOrWhiteSpace(validation.TimelineSha256))
            throw new InvalidOperationException("Pre-trade historical validation is stale or unqualified.");

        var validationFact=BacktestValidationCanonicalizerV1.Create(
            profile.Symbol,
            profile.Id,
            profile.Version,
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
            promoted:true);
        if(!BacktestValidationCanonicalizerV1.IsCanonical(validationFact,now))
            throw new InvalidOperationException("Pre-trade validation fact is not canonical.");

        var timeline=await _store.GetStrategyExposureTimelineAsync(
            validation.TimelineSha256,profile.Id,profile.Version,profile.Symbol,ct)
            ??throw new InvalidOperationException("Pre-trade strategy timeline is unavailable.");

        var batch=ExecutionPreTradeSimulationV1.Create(
            artifact,intents,market,rule,validationFact,timeline,now);

        foreach(var item in batch.Items)
        {
            await _store.SaveExecutionSimulationFillAsync(item.Fill,ct);
            await _store.SaveExecutionPreTradeSimulationProvenanceAsync(item.Provenance,ct);
        }
        return batch;
    }
}
