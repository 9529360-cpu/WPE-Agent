using Microsoft.Data.Sqlite;
using System.Globalization;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<IReadOnlyList<ExecutionRealityDriftFactV1>> GetExecutionRealityDriftByClientOrderIdAsync(
        string clientOrderId,
        int limit,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))
            throw new ArgumentException("Client order id is required.",nameof(clientOrderId));
        limit=Math.Clamp(limit,1,129);

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionRealityDriftStorageAsync(connection,ct);

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT schema,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,symbol,side,
                   reduce_only,order_type,intended_quantity,executed_quantity,expected_price,average_price,
                   exchange_status,state,terminal,comparable,fill_ratio,adverse_slippage_bps,expected_slippage_bps,
                   slippage_drift_bps,observed_fee,fee_basis,fee_comparable,observed_fee_rate_bps,
                   expected_commission_bps,fee_drift_bps,total_comparable,total_execution_drift_bps,
                   observation_latency_ms,exchange_updated_at,observed_at,reason_code,canonical_bytes,canonical_sha256
            FROM execution_reality_drift
            WHERE client_order_id=$client
            ORDER BY observed_at DESC,rowid DESC
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$client",clientOrderId);
        query.Parameters.AddWithValue("$limit",limit);

        var result=new List<ExecutionRealityDriftFactV1>();
        await using var reader=await query.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var fact=ReadRealityFact(reader);
            if(!ExecutionRealityDriftV1.IsCanonical(fact))
                throw new InvalidOperationException("Persisted execution reality drift failed canonical verification.");
            result.Add(fact);
        }
        return result;
    }

    internal async Task<IReadOnlyList<ExecutionSimulationComparisonSummaryV1>> GetExecutionSimulationComparisonsByClientOrderIdAsync(
        string clientOrderId,
        int limit,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))
            throw new ArgumentException("Client order id is required.",nameof(clientOrderId));
        limit=Math.Clamp(limit,1,129);

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection,ct);

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                   simulation_model_version,venue_rule_version,symbol,simulated_sha256,observed_sha256,compared_at,canonical_bytes
            FROM execution_simulation_comparisons
            WHERE client_order_id=$client
            ORDER BY compared_at DESC,rowid DESC
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$client",clientOrderId);
        query.Parameters.AddWithValue("$limit",limit);

        var result=new List<ExecutionSimulationComparisonSummaryV1>();
        await using var reader=await query.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var hash=reader.GetString(0);
            var bytes=(byte[])reader[12];
            VerifySimulationBytes(hash,bytes);
            var parsed=ParseComparisonSummary(hash,bytes);
            if(!string.Equals(parsed.CorrelationId,reader.GetString(1),StringComparison.Ordinal)
               ||!string.Equals(parsed.ClientOrderId,reader.GetString(2),StringComparison.Ordinal)
               ||!string.Equals(parsed.StrategyId,reader.GetString(3),StringComparison.Ordinal)
               ||!string.Equals(parsed.StrategyVersion,reader.GetString(4),StringComparison.Ordinal)
               ||!string.Equals(parsed.CostModelVersion,reader.GetString(5),StringComparison.Ordinal)
               ||!string.Equals(parsed.SimulationModelVersion,reader.GetString(6),StringComparison.Ordinal)
               ||!string.Equals(parsed.VenueRuleVersion,reader.GetString(7),StringComparison.Ordinal)
               ||!string.Equals(parsed.Symbol,reader.GetString(8),StringComparison.Ordinal)
               ||!string.Equals(parsed.SimulatedCanonicalSha256,reader.GetString(9),StringComparison.Ordinal)
               ||!string.Equals(parsed.ObservedCanonicalSha256,reader.GetString(10),StringComparison.Ordinal)
               ||parsed.ComparedAtUtc!=DateTimeOffset.Parse(reader.GetString(11),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind))
                throw new InvalidOperationException("Persisted execution simulation comparison metadata does not match canonical bytes.");
            result.Add(parsed);
        }
        return result;
    }
}
