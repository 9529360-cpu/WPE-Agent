using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Agent;

internal sealed record ExecutionSimulationComparisonRecordResultV1(
    bool Recorded,
    bool Idempotent,
    string Code,
    string? CanonicalSha256);

public sealed partial class AgentSqliteStore
{
    internal async Task<ExecutionSimulationComparisonRecordResultV1> RecordExecutionSimulationComparisonAsync(
        string correlationId,
        string clientOrderId,
        string observedCanonicalSha256,
        DateTimeOffset comparedAtUtc,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.",nameof(correlationId));
        if(string.IsNullOrWhiteSpace(clientOrderId))
            throw new ArgumentException("Client order id is required.",nameof(clientOrderId));
        if(observedCanonicalSha256.Length!=64)
            throw new ArgumentException("Observed canonical hash is invalid.",nameof(observedCanonicalSha256));
        if(comparedAtUtc==default||comparedAtUtc.Offset!=TimeSpan.Zero)
            throw new ArgumentException("Comparison time must be UTC.",nameof(comparedAtUtc));

        ExecutionSimulationFillV1? simulated;
        ExecutionRealityDriftFactV1? observed;
        await using(var connection=new SqliteConnection(_cs))
        {
            await connection.OpenAsync(ct);
            await EnsureExecutionSimulationStorageAsync(connection,ct);
            await EnsureExecutionRealityDriftStorageAsync(connection,ct);

            await using var query=connection.CreateCommand();
            query.CommandText="""
                SELECT canonical_sha256
                FROM execution_simulated_fills
                WHERE correlation_id=$correlation AND client_order_id=$client
                ORDER BY simulated_at,rowid;
                """;
            query.Parameters.AddWithValue("$correlation",correlationId);
            query.Parameters.AddWithValue("$client",clientOrderId);
            var hashes=new List<string>();
            await using(var reader=await query.ExecuteReaderAsync(ct))
                while(await reader.ReadAsync(ct))hashes.Add(reader.GetString(0));

            if(hashes.Count==0)
                return new(false,false,"simulation-source-missing",null);
            if(hashes.Distinct(StringComparer.Ordinal).Count()!=1)
                throw new InvalidOperationException("Multiple simulated fill sources conflict for one execution intent.");

            simulated=await ReadPersistedSimulationFillAsync(connection,hashes[0],ct);
            observed=await ReadPersistedObservedDriftAsync(connection,observedCanonicalSha256,ct);
        }

        if(simulated is null)
            return new(false,false,"simulation-source-missing",null);
        if(observed is null)
            return new(false,false,"observed-source-missing",null);
        if(!string.Equals(simulated.CorrelationId,correlationId,StringComparison.Ordinal)
           ||!string.Equals(simulated.ClientOrderId,clientOrderId,StringComparison.Ordinal)
           ||!string.Equals(observed.CorrelationId,correlationId,StringComparison.Ordinal)
           ||!string.Equals(observed.ClientOrderId,clientOrderId,StringComparison.Ordinal))
            throw new InvalidOperationException("Simulation comparison source identity is conflicting.");

        var comparison=ExecutionSimulationComparisonCanonicalizerV1.Create(
            simulated,observed,comparedAtUtc);
        var saved=await SaveExecutionSimulationComparisonAsync(comparison,ct);
        return new(true,saved.Idempotent,saved.Code,comparison.CanonicalSha256);
    }
}
