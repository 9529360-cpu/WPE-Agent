using 币安量化机器人.Services.Agent;
using Microsoft.Data.Sqlite;

namespace WPE.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task CompletedCycle_CanBeReadAfterStoreIsRecreated()
    {
        var store = new AgentSqliteStore(DatabasePath);
        var evidence = new EvidencePack { Completeness = 100 };
        await store.StartCycleAsync("cycle-1", evidence, "test-brain", CancellationToken.None);
        await store.CompleteCycleAsync("cycle-1", new DecisionPlan { Action = DecisionAction.Hold, Reason = "test" }, "risk-ok", null, null, CancellationToken.None);

        var reopened = new AgentSqliteStore(DatabasePath);
        var outcomes = await reopened.RecentOutcomesAsync(CancellationToken.None);

        Assert.Single(outcomes);
        Assert.Contains("Hold BTCUSDT", outcomes[0], StringComparison.Ordinal);
        Assert.Contains("mode=unknown", outcomes[0], StringComparison.Ordinal);
        Assert.Contains("risk=allowed/unknown", outcomes[0], StringComparison.Ordinal);
        Assert.Contains("exec=not_attempted", outcomes[0], StringComparison.Ordinal);
        Assert.Contains("state=no", outcomes[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredOutcomeMemory_CanBeReadAfterStoreIsRecreated()
    {
        var store = new AgentSqliteStore(DatabasePath);
        await store.StartCycleAsync("cycle-1", new EvidencePack { Completeness = 100 }, "WPE Local Brain", CancellationToken.None);
        await store.CompleteCycleAsync("cycle-1", new DecisionPlan { Action = DecisionAction.OpenLong, Instrument = "SOLUSDT", Reason = "bounded reason" }, "risk-ok", null, null, CancellationToken.None);

        var reopened = new AgentSqliteStore(DatabasePath);
        var rows = await reopened.RecentOutcomeMemoriesAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("local-only", row.Mode);
        Assert.Equal("SOLUSDT", row.Symbol);
        Assert.Equal("OpenLong", row.DecisionAction);
        Assert.Equal("allowed", row.RiskResult);
        Assert.Equal("unknown", row.RiskReasonCode);
        Assert.False(row.ExecutionAttempted);
        Assert.Equal("not_attempted", row.ExecutionResult);
        Assert.False(row.StateChanged);
    }

    [Fact]
    public async Task StructuredOutcomeMemory_UsesLatestExecutionStatusForFailClosedProjection()
    {
        var store = new AgentSqliteStore(DatabasePath);
        await store.StartCycleAsync("cycle-2", new EvidencePack { Completeness = 100 }, "remote-brain", CancellationToken.None);
        await store.CompleteCycleAsync("cycle-2", new DecisionPlan { Action = DecisionAction.OpenLong, Instrument = "BTCUSDT", Reason = "execution path" }, "capability stale blocked", null, null, CancellationToken.None);
        var intent = Intent("persisted-exec") with { Symbol = "BTCUSDT", Action = DecisionAction.OpenLong, ReduceOnly = false };
        await store.RecordExecutionAsync("cycle-2", intent, new ExchangeOrder("BTCUSDT", "1", intent.ClientOrderId, "PARTIALLY_FILLED", 0.001m, 50100m, "MARKET", PositionSide.Long, false, DateTime.UtcNow), "test-v1", CancellationToken.None);

        var row = Assert.Single(await store.RecentOutcomeMemoriesAsync(CancellationToken.None));
        Assert.Equal("blocked", row.RiskResult);
        Assert.Equal("capability_stale", row.RiskReasonCode);
        Assert.True(row.ExecutionAttempted);
        Assert.Equal("partial", row.ExecutionResult);
        Assert.True(row.StateChanged);
        Assert.Equal("refresh_capabilities", row.RecoveryHint);
    }

    [Fact]
    public async Task RecentOutcomes_UsesStructuredProjectionForPlannerHistory()
    {
        const string secret = "sk-planner-history-secret-123456";
        var store = new AgentSqliteStore(DatabasePath);
        await store.StartCycleAsync("cycle-3", new EvidencePack { Completeness = 100 }, "WPE Local Brain", CancellationToken.None);
        await store.CompleteCycleAsync(
            "cycle-3",
            new DecisionPlan
            {
                Action = DecisionAction.OpenLong,
                Instrument = "SOLUSDT",
                Reason = "Authorization: Bearer " + secret + " token=" + secret + new string('x', 220)
            },
            "capability stale blocked",
            null,
            null,
            CancellationToken.None);
        var intent = Intent("structured-history") with { Symbol = "SOLUSDT", Action = DecisionAction.OpenLong, ReduceOnly = false };
        await store.RecordExecutionAsync("cycle-3", intent, new ExchangeOrder("SOLUSDT", "2", intent.ClientOrderId, "PARTIALLY_FILLED", 0.001m, 151m, "MARKET", PositionSide.Long, false, DateTime.UtcNow), "test-v1", CancellationToken.None);

        var outcome = Assert.Single(await store.RecentOutcomesAsync(CancellationToken.None));
        Assert.DoesNotContain(secret, outcome, StringComparison.Ordinal);
        Assert.Contains("OpenLong SOLUSDT", outcome, StringComparison.Ordinal);
        Assert.Contains("mode=local-only", outcome, StringComparison.Ordinal);
        Assert.Contains("risk=blocked/capability_stale", outcome, StringComparison.Ordinal);
        Assert.Contains("exec=partial", outcome, StringComparison.Ordinal);
        Assert.Contains("state=yes", outcome, StringComparison.Ordinal);
        Assert.Contains("next=refresh_capabilities", outcome, StringComparison.Ordinal);
        Assert.True(outcome.Length <= 200);
    }

    [Fact]
    public async Task RecentOutcomes_GroupsRepeatedStructuredMemoriesIntoOneBoundedLine()
    {
        const string secret = "sk-repeats-secret-123456";
        var store = new AgentSqliteStore(DatabasePath);

        for (var index = 0; index < 2; index++)
        {
            var cycleId = $"cycle-repeat-{index}";
            await store.StartCycleAsync(cycleId, new EvidencePack { Completeness = 100 }, "WPE Local Brain", CancellationToken.None);
            await store.CompleteCycleAsync(
                cycleId,
                new DecisionPlan
                {
                    Action = DecisionAction.Hold,
                    Instrument = "BTCUSDT",
                    Reason = $"Authorization: Bearer {secret} repeat={index} " + new string('x', 180)
                },
                "provider unavailable",
                null,
                null,
                CancellationToken.None);
        }

        var outcome = Assert.Single(await store.RecentOutcomesAsync(CancellationToken.None));
        Assert.DoesNotContain(secret, outcome, StringComparison.Ordinal);
        Assert.Contains("Hold BTCUSDT", outcome, StringComparison.Ordinal);
        Assert.Contains("risk=degraded/provider_unavailable", outcome, StringComparison.Ordinal);
        Assert.Contains("repeats=2", outcome, StringComparison.Ordinal);
        Assert.True(outcome.Length <= 200);
    }

    [Fact]
    public async Task RecentOutcomes_LegacyFallbackStillUsesRedactedBoundedFormat()
    {
        const string secret = "sk-legacy-secret-123456";
        var store = new AgentSqliteStore(DatabasePath);
        var decisionJson = System.Text.Json.JsonSerializer.Serialize(new DecisionPlan
        {
            Action = DecisionAction.OpenLong,
            Instrument = "ETHUSDT",
            Confidence = 0.73,
            Reason = "Authorization: Bearer " + secret + " token=" + secret + new string('x', 220)
        });

        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cycles(id,started_at,completed_at,status,decision_json,risk_result)
                VALUES($id,$started,$completed,'COMPLETED',$decision,$risk)
                """;
            command.Parameters.AddWithValue("$id", "legacy-cycle");
            command.Parameters.AddWithValue("$started", "not-a-timestamp");
            command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$decision", decisionJson);
            command.Parameters.AddWithValue("$risk", "provider unavailable");
            await command.ExecuteNonQueryAsync();
        }

        var outcome = Assert.Single(await store.RecentOutcomesAsync(CancellationToken.None));
        Assert.DoesNotContain(secret, outcome, StringComparison.Ordinal);
        Assert.Contains("OpenLong ETHUSDT", outcome, StringComparison.Ordinal);
        Assert.Contains("conf=0.73", outcome, StringComparison.Ordinal);
        Assert.Contains("result=provider unavailable", outcome, StringComparison.Ordinal);
        Assert.Contains("note=", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("latestReason=", outcome, StringComparison.Ordinal);
        Assert.True(outcome.Length <= 200);
    }

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
