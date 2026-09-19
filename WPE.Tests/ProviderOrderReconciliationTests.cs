using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ProviderOrderReconciliationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-order-reconcile-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoRecoverableIntentIsClearCanonicalAndAllowsRiskIncrease()
    {
        var report = ProviderOrderReconciliationServiceV1.Reconcile(
            "binance-futures",
            "Testnet",
            [],
            Now,
            Now);

        Assert.Equal(ProviderOrderReconciliationStateV1.Clear, report.State);
        Assert.True(report.AllowsRiskIncrease);
        Assert.Empty(report.Legs);
        Assert.Empty(report.ReasonCodes);
        Assert.True(ProviderOrderReconciliationServiceV1.IsCanonical(report));
        Assert.False(ProviderOrderReconciliationServiceV1.IsCanonical(report with { AllowsRiskIncrease = false }));
    }

    [Fact]
    public void ProviderNeutralLifecycleClassifiesOpenPartialFilledAndTerminalWithoutIncreasingRisk()
    {
        var inputs = new[]
        {
            Input("not-submitted", false, null),
            Input("open", true, Order("open", "NEW", 0m, 0m)),
            Input("partial-open", true, Order("partial-open", "PARTIALLY_FILLED", .4m, 100m)),
            Input("filled", true, Order("filled", "FILLED", 1m, 100m)),
            Input("terminal-zero", true, Order("terminal-zero", "CANCELED", 0m, 0m)),
            Input("terminal-partial", true, Order("terminal-partial", "EXPIRED", .25m, 100m))
        };

        var report = ProviderOrderReconciliationServiceV1.Reconcile(
            "okx",
            "Testnet",
            inputs,
            Now,
            Now);

        Assert.Equal(ProviderOrderReconciliationStateV1.Pending, report.State);
        Assert.False(report.AllowsRiskIncrease);
        Assert.Equal(
            new[]
            {
                ProviderOrderLifecycleStateV1.Filled,
                ProviderOrderLifecycleStateV1.NotSubmitted,
                ProviderOrderLifecycleStateV1.Open,
                ProviderOrderLifecycleStateV1.PartialOpen,
                ProviderOrderLifecycleStateV1.TerminalPartialFill,
                ProviderOrderLifecycleStateV1.TerminalNoFill
            }.OrderBy(x => x).ToArray(),
            report.Legs.Select(x => x.ProviderState).OrderBy(x => x).ToArray());
        Assert.Contains("order.local-unresolved", report.ReasonCodes);
        Assert.True(ProviderOrderReconciliationServiceV1.IsCanonical(report));
    }

    [Fact]
    public void MissingJournaledQueryFailureAndUnknownStatusFailClosedAsUnknown()
    {
        var inputs = new[]
        {
            Input("missing", true, null),
            Input("query", false, null, queryFailed:true),
            Input("unknown", true, Order("unknown", "VENUE_PRIVATE_STATE", 0m, 0m))
        };

        var report = ProviderOrderReconciliationServiceV1.Reconcile(
            "bybit",
            "Testnet",
            inputs,
            Now,
            Now);

        Assert.Equal(ProviderOrderReconciliationStateV1.Unknown, report.State);
        Assert.False(report.AllowsRiskIncrease);
        Assert.Contains("order.missing-journaled", report.ReasonCodes);
        Assert.Contains("order.query-failed", report.ReasonCodes);
        Assert.Contains("order.unknown-status", report.ReasonCodes);
    }

    [Fact]
    public void IdentityQuantityOrUtcTimestampConflictFailsClosed()
    {
        var wrongIdentity = Input(
            "identity",
            true,
            Order("identity", "FILLED", 1m, 100m) with { ClientOrderId = "other" });
        var overfill = Input(
            "overfill",
            true,
            Order("overfill", "FILLED", 1.2m, 100m));
        var nonUtc = Input(
            "time",
            true,
            Order("time", "FILLED", 1m, 100m) with
            {
                UpdatedAt = DateTime.SpecifyKind(Now.UtcDateTime, DateTimeKind.Unspecified)
            });

        var report = ProviderOrderReconciliationServiceV1.Reconcile(
            "gate",
            "Testnet",
            [wrongIdentity, overfill, nonUtc],
            Now,
            Now);

        Assert.Equal(ProviderOrderReconciliationStateV1.Conflicting, report.State);
        Assert.All(report.Legs, leg => Assert.Equal(ProviderOrderLifecycleStateV1.Conflicting, leg.ProviderState));
        Assert.Contains("order.conflicting", report.ReasonCodes);
    }

    [Fact]
    public void DuplicateInvalidOrStaleEvidenceNeverAllowsRiskIncrease()
    {
        var duplicate = Input("same", false, null);
        var invalid = ProviderOrderReconciliationServiceV1.Reconcile(
            "binance-futures",
            "Testnet",
            [duplicate, duplicate],
            Now,
            Now);
        Assert.Equal(ProviderOrderReconciliationStateV1.Invalid, invalid.State);
        Assert.Contains("order.invalid-local-duplicate", invalid.ReasonCodes);

        var stale = ProviderOrderReconciliationServiceV1.Reconcile(
            "binance-futures",
            "Testnet",
            [],
            Now - ProviderOrderReconciliationServiceV1.MaximumAge - TimeSpan.FromMilliseconds(1),
            Now);
        Assert.Equal(ProviderOrderReconciliationStateV1.Stale, stale.State);
        Assert.False(stale.AllowsRiskIncrease);
        Assert.Contains("order.observation-stale", stale.ReasonCodes);
    }

    [Fact]
    public async Task CaptureUsesOnlyReadOnlyProviderObservationAndDurableJournalState()
    {
        var store = new AgentSqliteStore(Database, () => Now);
        var source = new Source
        {
            Orders =
            {
                ["order-a"] = Order("a", "FILLED", 1m, 100m) with { ClientOrderId = "order-a" }
            }
        };
        var intents = new[]
        {
            new PersistedIntent("cycle-a", Intent("order-a"), "SUBMITTED", null),
            new PersistedIntent("cycle-b", Intent("order-b"), "SUBMITTED", null)
        };

        var report = await ProviderOrderReconciliationServiceV1.CaptureAsync(
            source,
            store,
            intents,
            () => Now,
            default);

        Assert.Equal(2, source.FindCalls);
        Assert.Equal(ProviderOrderReconciliationStateV1.Pending, report.State);
        Assert.Equal(ProviderOrderLifecycleStateV1.Filled, report.Legs.Single(x => x.ClientOrderId == "order-a").ProviderState);
        Assert.Equal(ProviderOrderLifecycleStateV1.NotSubmitted, report.Legs.Single(x => x.ClientOrderId == "order-b").ProviderState);
        Assert.All(report.Legs, leg => Assert.False(leg.SubmissionJournaled));
    }

    [Fact]
    public async Task ReconciliationAuditIsIdempotentAppendOnlyAndRejectsTampering()
    {
        var report = ProviderOrderReconciliationServiceV1.Reconcile(
            "binance-futures",
            "Testnet",
            [Input("open", true, Order("open", "NEW", 0m, 0m))],
            Now,
            Now);
        var store = new AgentSqliteStore(Database);

        Assert.True(await store.SaveProviderOrderReconciliationAsync(report, default));
        Assert.False(await new AgentSqliteStore(Database).SaveProviderOrderReconciliationAsync(report, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveProviderOrderReconciliationAsync(report with { State = ProviderOrderReconciliationStateV1.Clear }, default));

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "UPDATE provider_order_reconciliation_audits SET state='Clear'",
            "DELETE FROM provider_order_reconciliation_audits"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public void AutoTradingStartupStageUsesCanonicalOrderReportInsteadOfRecoverableCountHeuristic()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "AutoTradingAgent.cs"));
        var start = source.IndexOf("Stage(\"Stage.Recovery\"", StringComparison.Ordinal);
        var end = source.IndexOf("Stage(\"Stage.Evidence\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var recoveryStage = source[start..end];

        Assert.Contains("ProviderOrderReconciliationServiceV1.CaptureAsync", recoveryStage, StringComparison.Ordinal);
        Assert.Contains("SaveProviderOrderReconciliationAsync", recoveryStage, StringComparison.Ordinal);
        Assert.Contains("orderReconciliation.AllowsRiskIncrease", recoveryStage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AssessUnverifiedAutomaticMutation(AutomaticMutationPath.StartupRecovery,recoverable.Count)",
            recoveryStage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsMainnetBeforeAnyProviderQuery()
    {
        var source = new Source { EnvironmentValue = ExchangeEnvironment.Mainnet };
        var store = new AgentSqliteStore(Database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderOrderReconciliationServiceV1.CaptureAsync(
                source,
                store,
                [new PersistedIntent("cycle", Intent("order-a"), "SUBMITTED", null)],
                () => Now,
                default));

        Assert.Equal(0, source.FindCalls);
    }

    private static ProviderOrderReconciliationInputV1 Input(
        string suffix,
        bool journaled,
        ExchangeOrder? order,
        bool queryFailed = false) =>
        new(
            "cycle-" + suffix,
            "order-" + suffix,
            "BTCUSDT",
            "SUBMITTED",
            1m,
            journaled,
            order,
            queryFailed);

    private static ExecutionIntent Intent(string clientOrderId) =>
        new(
            "BTCUSDT",
            PositionSide.Long,
            1m,
            false,
            90m,
            120m,
            clientOrderId,
            "test",
            DecisionAction.OpenLong,
            ExecutionOrderType.Market,
            0m,
            100m);

    private static ExchangeOrder Order(string suffix, string status, decimal executed, decimal price) =>
        new(
            "BTCUSDT",
            "exchange-" + suffix,
            "order-" + suffix,
            status,
            executed,
            price,
            "MARKET",
            PositionSide.Long,
            false,
            Now.UtcDateTime);

    private sealed class Source : IProviderOrderObservationSourceV1
    {
        public string ProviderId => "fake-provider";
        public ExchangeEnvironment Environment => EnvironmentValue;
        public ExchangeEnvironment EnvironmentValue { get; init; } = ExchangeEnvironment.Testnet;
        public Dictionary<string, ExchangeOrder> Orders { get; } = new(StringComparer.Ordinal);
        public int FindCalls { get; private set; }

        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            FindCalls++;
            return Task.FromResult(Orders.GetValueOrDefault(clientOrderId));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
