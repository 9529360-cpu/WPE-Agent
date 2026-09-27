using 币安量化机器人.Services.Agent;
using Microsoft.Data.Sqlite;

namespace WPE.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task PendingIntent_CanBeRecoveredAfterStoreIsRecreated()
    {
        var intent = Intent("persisted-client-id");
        var store = new AgentSqliteStore(DatabasePath);
        await store.SaveIntentAsync("cycle-1", intent, "INTENT", null, CancellationToken.None);

        var reopened = new AgentSqliteStore(DatabasePath);
        var recoverable = await reopened.GetRecoverableIntentsAsync(CancellationToken.None);

        var saved = Assert.Single(recoverable);
        Assert.Equal(intent.ClientOrderId, saved.Intent.ClientOrderId);
        Assert.Equal("INTENT", saved.Status);
    }

    [Fact]
    public async Task RecoverableStatusMatrix_IncludesUncertainAndExcludesKnownTerminalStates()
    {
        string[] recoverable = ["UNKNOWN", "EMERGENCY_UNKNOWN", "PROTECTION_FAILED", "EMERGENCY_SUBMITTED"];
        string[] terminal = [
            "PROTECTED", "PROTECTED_PARTIAL", "PARTIALLY_FILLED_PROTECTED", "PREFLIGHT_BLOCKED",
            "COMPLETED", "COMPLETED_PARTIAL", "CANCELED", "REJECTED", "EXPIRED", "EMERGENCY_CLOSED"
        ];
        var store = new AgentSqliteStore(DatabasePath);

        foreach (var status in recoverable.Concat(terminal))
            await store.SaveIntentAsync("cycle-status", Intent("status-" + status), status, null, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var reopened = new AgentSqliteStore(DatabasePath);
        var pending = await reopened.GetRecoverableIntentsAsync(CancellationToken.None);

        Assert.Equal(recoverable.Order(), pending.Select(item => item.Status).Order());
        Assert.DoesNotContain(pending, item => terminal.Contains(item.Status, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PreflightDiagnostic_IsPersistedAlongsideBlockedIntent()
    {
        var store=new AgentSqliteStore(DatabasePath);
        var diagnostic="{\"failures\":[\"spread\"],\"observed\":{\"spreadBps\":12.5}}";
        await store.SaveIntentAsync("cycle-preflight",Intent("client-preflight"),"PREFLIGHT_BLOCKED",null,diagnostic,CancellationToken.None);

        SqliteConnection.ClearAllPools();
        await using var connection=new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT preflight_diagnostic_json FROM order_intents WHERE client_order_id='client-preflight'";
        Assert.Equal(diagnostic,(string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PreflightObservations_AreAppendOnlyAndQueryable()
    {
        var store=new AgentSqliteStore(DatabasePath);
        var intent=Intent("preflight-observation");
        await store.SaveExecutionPreflightObservationAsync("cycle-a",intent,true,"{\"allowed\":true,\"observed\":{\"spreadBps\":2.1}}",CancellationToken.None);
        await store.SaveExecutionPreflightObservationAsync("cycle-b",intent,false,"{\"allowed\":false,\"failures\":[\"spread\"]}",CancellationToken.None);

        var rows=await store.GetRecentExecutionPreflightObservationsAsync("BTCUSDT",10,CancellationToken.None);
        Assert.Equal(2,rows.Count);
        Assert.False(rows[0].Allowed);
        Assert.True(rows[1].Allowed);
        Assert.Equal("cycle-b",rows[0].CycleId);

        await using var connection=new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var update=connection.CreateCommand();
        update.CommandText="UPDATE execution_preflight_observations SET allowed=1 WHERE id=$id";
        update.Parameters.AddWithValue("$id",rows[0].Id);
        await Assert.ThrowsAsync<SqliteException>(()=>update.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task IntentStateSummary_ReportsCountsWithoutIdentifiersOrDetails()
    {
        var store=new AgentSqliteStore(DatabasePath);
        await store.SaveIntentAsync("cycle-private",Intent("client-private-unknown"),"UNKNOWN",null,CancellationToken.None);
        await store.SaveIntentAsync("cycle-private",Intent("client-private-rejected"),"REJECTED",null,CancellationToken.None);

        var summary=await store.GetIntentStateSummaryAsync(CancellationToken.None);
        var json=System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Equal(2,summary.TotalCount);
        Assert.Equal(1,summary.RecoverableCount);
        Assert.Equal(1,summary.UnknownCount);
        Assert.Contains(summary.Statuses,x=>x.Status=="UNKNOWN"&&x.Count==1);
        Assert.Contains(summary.Statuses,x=>x.Status=="REJECTED"&&x.Count==1);
        Assert.DoesNotContain("client-private",json,StringComparison.Ordinal);
        Assert.DoesNotContain("cycle-private",json,StringComparison.Ordinal);
        Assert.DoesNotContain("BTCUSDT",json,StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    internal static ExecutionIntent Intent(string clientOrderId) => new(
        "BTCUSDT", PositionSide.Long, 0.001m, false, 49_000m, 51_000m, clientOrderId, "test", DecisionAction.OpenLong);
}
