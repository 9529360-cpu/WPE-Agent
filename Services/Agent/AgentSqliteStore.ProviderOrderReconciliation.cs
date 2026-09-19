using Microsoft.Data.Sqlite;
using System.Globalization;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    public async Task<bool> SaveProviderOrderReconciliationAsync(
        ProviderOrderReconciliationReportV1 report,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!ProviderOrderReconciliationServiceV1.IsCanonical(report))
            throw new InvalidOperationException("Provider order reconciliation canonical identity is invalid.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureProviderOrderReconciliationStorageAsync(connection, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO provider_order_reconciliation_audits(
                report_id,schema,provider_id,environment,observed_at,evaluated_at,state,
                allows_risk_increase,canonical_sha256,canonical_bytes)
            VALUES(
                $id,$schema,$provider,$environment,$observed,$evaluated,$state,
                $allows,$hash,$bytes);
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", report.ReportId);
        command.Parameters.AddWithValue("$schema", report.Schema);
        command.Parameters.AddWithValue("$provider", report.ProviderId);
        command.Parameters.AddWithValue("$environment", report.Environment);
        command.Parameters.AddWithValue("$observed", report.ObservedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$evaluated", report.EvaluatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$state", report.State.ToString());
        command.Parameters.AddWithValue("$allows", report.AllowsRiskIncrease ? 1 : 0);
        command.Parameters.AddWithValue("$hash", report.CanonicalSha256);
        command.Parameters.Add("$bytes", SqliteType.Blob).Value = report.CanonicalBytes;

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task EnsureProviderOrderReconciliationStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_order_reconciliation_audits(
                report_id TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                environment TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                evaluated_at TEXT NOT NULL,
                state TEXT NOT NULL,
                allows_risk_increase INTEGER NOT NULL CHECK(allows_risk_increase IN (0,1)),
                canonical_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_provider_order_reconciliation_time
                ON provider_order_reconciliation_audits(evaluated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_provider_order_reconciliation_provider
                ON provider_order_reconciliation_audits(provider_id,environment,evaluated_at DESC);
            CREATE TRIGGER IF NOT EXISTS provider_order_reconciliation_no_update
                BEFORE UPDATE ON provider_order_reconciliation_audits
                BEGIN SELECT RAISE(ABORT,'provider order reconciliation audits are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS provider_order_reconciliation_no_delete
                BEFORE DELETE ON provider_order_reconciliation_audits
                BEGIN SELECT RAISE(ABORT,'provider order reconciliation audits are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}
