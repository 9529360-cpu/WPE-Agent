using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<bool> SaveStrategyShadowQualificationAsync(
        StrategyShadowQualificationDecisionV1 value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!StrategyShadowQualificationV1.IsCanonical(value))
            throw new InvalidOperationException("Strategy shadow qualification canonical identity is invalid.");

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureStrategyShadowQualificationStorageAsync(connection,ct);

        await using(var insert=connection.CreateCommand())
        {
            insert.CommandText="""
                INSERT OR IGNORE INTO strategy_shadow_qualification_decisions(
                    canonical_sha256,schema,strategy_id,strategy_version,symbol,
                    market_provider_id,environment,backtest_validation_sha256,timeline_sha256,
                    policy_version,policy_sha256,state,qualified,observation_count,
                    first_market_at,last_market_at,evaluated_at,evidence_set_sha256,canonical_bytes)
                VALUES(
                    $hash,$schema,$strategy,$version,$symbol,
                    $provider,$environment,$validationHash,$timelineHash,
                    $policyVersion,$policyHash,$state,$qualified,$count,
                    $first,$last,$evaluated,$evidenceSet,$bytes);
                SELECT changes();
                """;
            insert.Parameters.AddWithValue("$hash",value.CanonicalSha256);
            insert.Parameters.AddWithValue("$schema",value.Schema);
            insert.Parameters.AddWithValue("$strategy",value.StrategyId);
            insert.Parameters.AddWithValue("$version",value.StrategyVersion);
            insert.Parameters.AddWithValue("$symbol",value.Symbol);
            insert.Parameters.AddWithValue("$provider",value.MarketProviderId);
            insert.Parameters.AddWithValue("$environment",value.Environment);
            insert.Parameters.AddWithValue("$validationHash",value.BacktestValidationSha256);
            insert.Parameters.AddWithValue("$timelineHash",value.TimelineSha256);
            insert.Parameters.AddWithValue("$policyVersion",value.PolicyVersion);
            insert.Parameters.AddWithValue("$policyHash",value.PolicySha256);
            insert.Parameters.AddWithValue("$state",value.State.ToString());
            insert.Parameters.AddWithValue("$qualified",value.Qualified?1:0);
            insert.Parameters.AddWithValue("$count",value.ObservationCount);
            insert.Parameters.AddWithValue("$first",value.FirstMarketAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$last",value.LastMarketAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$evaluated",value.EvaluatedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$evidenceSet",value.EvidenceSetSha256);
            insert.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
            if(Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1)
                return true;
        }

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT canonical_sha256,canonical_bytes
            FROM strategy_shadow_qualification_decisions
            WHERE strategy_id=$strategy
              AND strategy_version=$version
              AND evidence_set_sha256=$evidenceSet
              AND policy_sha256=$policyHash
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$strategy",value.StrategyId);
        query.Parameters.AddWithValue("$version",value.StrategyVersion);
        query.Parameters.AddWithValue("$evidenceSet",value.EvidenceSetSha256);
        query.Parameters.AddWithValue("$policyHash",value.PolicySha256);
        await using var reader=await query.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Strategy shadow qualification idempotent replay lost persisted evidence.");

        var bytes=(byte[])reader[1];
        if(!string.Equals(reader.GetString(0),value.CanonicalSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes))
            throw new InvalidOperationException("Strategy shadow qualification replay conflicts with persisted evidence.");
        return false;
    }

    internal async Task<StrategyShadowQualificationDecisionV1?> GetLatestStrategyShadowQualificationAsync(
        string strategyId,
        string strategyVersion,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId)||string.IsNullOrWhiteSpace(strategyVersion))
            return null;

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureStrategyShadowQualificationStorageAsync(connection,ct);

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT canonical_sha256,schema,strategy_id,strategy_version,symbol,
                   market_provider_id,environment,backtest_validation_sha256,timeline_sha256,
                   policy_version,policy_sha256,state,qualified,observation_count,
                   first_market_at,last_market_at,evaluated_at,evidence_set_sha256,canonical_bytes
            FROM strategy_shadow_qualification_decisions
            WHERE strategy_id=$strategy AND strategy_version=$version
            ORDER BY evaluated_at DESC,canonical_sha256 DESC
            LIMIT 20;
            """;
        query.Parameters.AddWithValue("$strategy",strategyId);
        query.Parameters.AddWithValue("$version",strategyVersion);
        await using var reader=await query.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var hash=reader.GetString(0);
            var bytes=(byte[])reader[18];
            if(!StrategyShadowQualificationV1.TryDeserializeCanonical(bytes,hash,out var value)
               ||value is null
               ||!string.Equals(reader.GetString(1),value.Schema,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(2),value.StrategyId,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(3),value.StrategyVersion,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(4),value.Symbol,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(5),value.MarketProviderId,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(6),value.Environment,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(7),value.BacktestValidationSha256,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(8),value.TimelineSha256,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(9),value.PolicyVersion,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(10),value.PolicySha256,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(11),value.State.ToString(),StringComparison.Ordinal)
               ||reader.GetInt32(12)!=(value.Qualified?1:0)
               ||reader.GetInt32(13)!=value.ObservationCount
               ||DateTimeOffset.Parse(reader.GetString(14),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.FirstMarketAtUtc
               ||DateTimeOffset.Parse(reader.GetString(15),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.LastMarketAtUtc
               ||DateTimeOffset.Parse(reader.GetString(16),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=value.EvaluatedAtUtc
               ||!string.Equals(reader.GetString(17),value.EvidenceSetSha256,StringComparison.Ordinal))
                throw new InvalidOperationException("Persisted strategy shadow qualification failed canonical read verification.");
            return value;
        }
        return null;
    }

    private static async Task EnsureStrategyShadowQualificationStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS strategy_shadow_qualification_decisions(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                market_provider_id TEXT NOT NULL,
                environment TEXT NOT NULL CHECK(environment='Testnet'),
                backtest_validation_sha256 TEXT NOT NULL,
                timeline_sha256 TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                policy_sha256 TEXT NOT NULL,
                state TEXT NOT NULL,
                qualified INTEGER NOT NULL CHECK(qualified IN (0,1)),
                observation_count INTEGER NOT NULL CHECK(observation_count>=0),
                first_market_at TEXT NOT NULL,
                last_market_at TEXT NOT NULL,
                evaluated_at TEXT NOT NULL,
                evidence_set_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(strategy_id,strategy_version,evidence_set_sha256,policy_sha256));
            CREATE INDEX IF NOT EXISTS ix_strategy_shadow_qualification_latest
                ON strategy_shadow_qualification_decisions(strategy_id,strategy_version,evaluated_at DESC);
            CREATE TRIGGER IF NOT EXISTS strategy_shadow_qualification_no_update
                BEFORE UPDATE ON strategy_shadow_qualification_decisions
                BEGIN SELECT RAISE(ABORT,'strategy shadow qualifications are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS strategy_shadow_qualification_no_delete
                BEFORE DELETE ON strategy_shadow_qualification_decisions
                BEGIN SELECT RAISE(ABORT,'strategy shadow qualifications are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}
