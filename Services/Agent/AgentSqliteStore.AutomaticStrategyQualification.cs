using Microsoft.Data.Sqlite;
using System.Globalization;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<AutomaticStrategyQualificationEvidenceV1> GetAutomaticStrategyQualificationEvidenceAsync(
        string strategyId,
        string strategyVersion,
        string symbol,
        DateTimeOffset artifactCreatedAtUtc,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId)
           ||string.IsNullOrWhiteSpace(strategyVersion)
           ||string.IsNullOrWhiteSpace(symbol)
           ||artifactCreatedAtUtc==default
           ||artifactCreatedAtUtc.Offset!=TimeSpan.Zero)
            return AutomaticStrategyQualificationEvidenceVerifierV1.Unavailable("strategy-qualification-request-invalid");

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);

        await using(var table=connection.CreateCommand())
        {
            table.CommandText="""
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type='table' AND name='strategy_shadow_qualification_decisions';
                """;
            if(Convert.ToInt32(await table.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)!=1)
                return AutomaticStrategyQualificationEvidenceVerifierV1.Unavailable("strategy-qualification-table-missing");
        }

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT canonical_sha256,strategy_id,strategy_version,symbol,market_provider_id,environment,
                   backtest_validation_sha256,timeline_sha256,policy_version,policy_sha256,
                   state,qualified,observation_count,first_market_at,last_market_at,evaluated_at,
                   evidence_set_sha256,canonical_bytes
            FROM strategy_shadow_qualification_decisions
            WHERE strategy_id=$strategy
              AND strategy_version=$version
              AND qualified=1
              AND evaluated_at <= $created
            ORDER BY evaluated_at DESC,canonical_sha256 DESC
            LIMIT 2;
            """;
        query.Parameters.AddWithValue("$strategy",strategyId);
        query.Parameters.AddWithValue("$version",strategyVersion);
        query.Parameters.AddWithValue("$created",artifactCreatedAtUtc.ToString("O"));

        await using var reader=await query.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))
            return AutomaticStrategyQualificationEvidenceVerifierV1.Unavailable("strategy-qualification-missing");

        var first=ReadAutomaticStrategyQualificationRow(reader);
        DateTimeOffset? secondEvaluated=null;
        if(await reader.ReadAsync(ct))
            secondEvaluated=DateTimeOffset.Parse(
                reader.GetString(15),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        if(secondEvaluated is not null&&secondEvaluated.Value==first.EvaluatedAtUtc)
            return AutomaticStrategyQualificationEvidenceVerifierV1.Unavailable("strategy-qualification-conflicting-latest");

        if(!first.Available
           ||!string.Equals(first.StrategyId,strategyId,StringComparison.Ordinal)
           ||!string.Equals(first.StrategyVersion,strategyVersion,StringComparison.Ordinal)
           ||!string.Equals(first.Symbol,symbol,StringComparison.Ordinal)
           ||first.EvaluatedAtUtc>artifactCreatedAtUtc)
            return AutomaticStrategyQualificationEvidenceVerifierV1.Unavailable(
                first.Available?"strategy-qualification-identity-mismatch":first.Code);

        return first;
    }

    private static AutomaticStrategyQualificationEvidenceV1 ReadAutomaticStrategyQualificationRow(
        SqliteDataReader reader)
    {
        var hash=reader.GetString(0);
        var strategy=reader.GetString(1);
        var version=reader.GetString(2);
        var symbol=reader.GetString(3);
        var provider=reader.GetString(4);
        var environment=reader.GetString(5);
        var validationHash=reader.GetString(6);
        var timelineHash=reader.GetString(7);
        var policyVersion=reader.GetString(8);
        var policyHash=reader.GetString(9);
        var state=reader.GetString(10);
        var qualified=reader.GetInt32(11);
        var count=reader.GetInt32(12);
        var firstAt=DateTimeOffset.Parse(reader.GetString(13),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
        var lastAt=DateTimeOffset.Parse(reader.GetString(14),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
        var evaluated=DateTimeOffset.Parse(reader.GetString(15),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
        var evidenceSet=reader.GetString(16);
        var bytes=(byte[])reader[17];

        if(!AutomaticStrategyQualificationEvidenceVerifierV1.TryParse(bytes,hash,out var value,out var code)
           ||value is null
           ||!string.Equals(strategy,value.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(version,value.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(symbol,value.Symbol,StringComparison.Ordinal)
           ||!string.Equals(provider,value.MarketProviderId,StringComparison.Ordinal)
           ||!string.Equals(environment,value.Environment,StringComparison.Ordinal)
           ||!string.Equals(validationHash,value.BacktestValidationSha256,StringComparison.Ordinal)
           ||!string.Equals(timelineHash,value.TimelineSha256,StringComparison.Ordinal)
           ||!string.Equals(policyVersion,value.PolicyVersion,StringComparison.Ordinal)
           ||!string.Equals(policyHash,value.PolicySha256,StringComparison.Ordinal)
           ||!string.Equals(state,"Ready",StringComparison.Ordinal)
           ||qualified!=1
           ||count!=value.ObservationCount
           ||firstAt!=value.FirstMarketAtUtc
           ||lastAt!=value.LastMarketAtUtc
           ||evaluated!=value.EvaluatedAtUtc
           ||!string.Equals(evidenceSet,value.EvidenceSetSha256,StringComparison.Ordinal))
            return AutomaticStrategyQualificationEvidenceVerifierV1.Unavailable(
                value is null?code:"strategy-qualification-metadata-mismatch");

        return value;
    }
}
