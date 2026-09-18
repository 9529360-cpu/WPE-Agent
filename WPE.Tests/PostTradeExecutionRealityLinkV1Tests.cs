using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradeExecutionRealityLinkV1Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-post-trade-reality-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactTerminalDriftLinksToPostTradeReviewCanonically()
    {
        var store = await SeedReviewAsync();
        var fact = Drift("research-cost-v1", Now.AddSeconds(-1), feeAvailable:true, feeAmount:.044m);
        await store.SaveExecutionRealityDriftAsync(fact, default);

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Linked, link.State);
        Assert.Equal("linked", link.Code);
        Assert.Equal("research-cost-v1", link.CostModelVersion);
        Assert.Equal(fact.CanonicalSha256, link.DriftCanonicalSha256);
        Assert.True(link.PriceComparable);
        Assert.True(link.FeeComparable);
        Assert.True(link.TotalComparable);
        Assert.Equal(1m, link.FillRatio);
        Assert.Equal(-10m, link.SlippageDriftBps);
        Assert.Equal(0m, link.FeeDriftBps);
        Assert.Equal(-10m, link.TotalExecutionDriftBps);
        Assert.True(PostTradeExecutionRealityLinkerV1.IsCanonical(link));
        Assert.False(PostTradeExecutionRealityLinkerV1.IsCanonical(link with { Code = "tampered" }));
    }

    [Fact]
    public async Task LegacyReviewWithoutStrategyIdentityDoesNotGuess()
    {
        var store = await SeedReviewAsync(strategyId:null);

        var review = Assert.Single(await store.GetRecentPostTradeReviewsAsync(10, default));
        var link = await new PostTradeExecutionRealityLinkerV1(store).LinkAsync(review, default);

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Unavailable, link.State);
        Assert.Equal("strategy-attribution-unavailable", link.Code);
        Assert.Null(link.CostModelVersion);
        Assert.Null(link.DriftCanonicalSha256);
        Assert.True(PostTradeExecutionRealityLinkerV1.IsCanonical(link));
    }

    [Fact]
    public async Task MissingTerminalDriftIsUnavailableRatherThanZeroCost()
    {
        var store = await SeedReviewAsync();

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Unavailable, link.State);
        Assert.Equal("terminal-drift-unavailable", link.Code);
        Assert.Null(link.SlippageDriftBps);
        Assert.Null(link.FeeDriftBps);
        Assert.Null(link.TotalExecutionDriftBps);
    }

    [Fact]
    public async Task MultipleCostModelsForSameCloseAreConflict()
    {
        var store = await SeedReviewAsync();
        await store.SaveExecutionRealityDriftAsync(Drift("research-cost-v1", Now.AddSeconds(-2), true, .044m), default);
        await store.SaveExecutionRealityDriftAsync(Drift("research-cost-v2", Now.AddSeconds(-1), true, .044m), default);

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Conflict, link.State);
        Assert.Equal("cost-model-conflict", link.Code);
        Assert.Null(link.CostModelVersion);
    }

    [Fact]
    public async Task LaterConfirmedFeeEvidenceUpgradesSameExecutionInsteadOfConflicting()
    {
        var store = await SeedReviewAsync();
        var priceOnly = Drift("research-cost-v1", Now.AddSeconds(-2), feeAvailable:false, feeAmount:0m);
        var feeConfirmed = Drift("research-cost-v1", Now.AddSeconds(-1), feeAvailable:true, feeAmount:.044m);
        await store.SaveExecutionRealityDriftAsync(priceOnly, default);
        await store.SaveExecutionRealityDriftAsync(feeConfirmed, default);

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Linked, link.State);
        Assert.Equal(feeConfirmed.CanonicalSha256, link.DriftCanonicalSha256);
        Assert.True(link.FeeComparable);
        Assert.True(link.TotalComparable);
        Assert.Equal(0m, link.FeeDriftBps);
    }

    [Fact]
    public async Task ConflictingConfirmedFeeEvidenceFailsClosed()
    {
        var store = await SeedReviewAsync();
        await store.SaveExecutionRealityDriftAsync(Drift("research-cost-v1", Now.AddSeconds(-2), true, .044m), default);
        await store.SaveExecutionRealityDriftAsync(Drift("research-cost-v1", Now.AddSeconds(-1), true, .055m), default);

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Conflict, link.State);
        Assert.Equal("terminal-fee-conflict", link.Code);
        Assert.Null(link.DriftCanonicalSha256);
    }

    [Fact]
    public async Task ReviewAndTerminalExecutionMismatchFailsClosed()
    {
        var store = await SeedReviewAsync(exitPrice:110m);
        await store.SaveExecutionRealityDriftAsync(
            Drift("research-cost-v1", Now.AddSeconds(-1), true, .0436m, averagePrice:109m),
            default);

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Conflict, link.State);
        Assert.Equal("terminal-drift-review-mismatch", link.Code);
    }

    [Fact]
    public async Task RepeatedEquivalentTerminalObservationUsesStrongestLatestEvidence()
    {
        var store = await SeedReviewAsync();
        var older = Drift("research-cost-v1", Now.AddSeconds(-3), true, .044m);
        var newer = Drift("research-cost-v1", Now.AddSeconds(-1), true, .044m);
        await store.SaveExecutionRealityDriftAsync(older, default);
        await store.SaveExecutionRealityDriftAsync(newer, default);

        var link = Assert.Single(await new PostTradeExecutionRealityLinkerV1(store).GetRecentAsync(10, default));

        Assert.Equal(PostTradeExecutionRealityLinkStateV1.Linked, link.State);
        Assert.Equal(newer.CanonicalSha256, link.DriftCanonicalSha256);
        Assert.Equal(newer.ObservedAtUtc, link.DriftObservedAtUtc);
    }

    private async Task<AgentSqliteStore> SeedReviewAsync(
        string? strategyId = "strategy-a",
        string strategyVersion = "v7",
        decimal exitPrice = 110m)
    {
        var store = new AgentSqliteStore(Database, () => Now);
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_outcomes(
                client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,gross_pnl,fees,
                net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis)
            VALUES(
                'close-order','close-cycle','BTCUSDT','Long','100',$exit,'1','10','.044',
                '9.956','.09956',$closed,$strategy,$version,$basis);
            """;
        command.Parameters.AddWithValue("$exit", exitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$closed", Now.AddSeconds(-2).ToString("O"));
        command.Parameters.AddWithValue("$strategy", strategyId is null ? DBNull.Value : strategyId);
        command.Parameters.AddWithValue("$version", strategyVersion);
        command.Parameters.AddWithValue("$basis", strategyId is null ? "legacy-version-only" : "automatic-correlation");
        await command.ExecuteNonQueryAsync();
        return store;
    }

    private static ExecutionRealityDriftFactV1 Drift(
        string costModelVersion,
        DateTimeOffset observedAt,
        bool feeAvailable,
        decimal feeAmount,
        decimal averagePrice = 110m)
    {
        var expectation = new ExecutionRealityExpectationV1(
            CorrelationId:"close-cycle",
            ClientOrderId:"close-order",
            StrategyId:"strategy-a",
            StrategyVersion:"v7",
            CostModelVersion:costModelVersion,
            Symbol:"BTCUSDT",
            Side:PositionSide.Long,
            ReduceOnly:true,
            OrderType:ExecutionOrderType.Market,
            Quantity:1m,
            ExpectedPrice:110m,
            ExpectedCommissionRate:.0004m,
            ExpectedSlippageRate:.001m,
            IntendedAtUtc:observedAt.AddMilliseconds(-500));
        var observation = new ExecutionRealityObservationV1(
            "close-order",
            "FILLED",
            1m,
            averagePrice,
            feeAvailable ? feeAmount : 0m,
            feeAvailable ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
            observedAt.AddMilliseconds(-100),
            observedAt);
        return ExecutionRealityDriftV1.Analyze(expectation, observation);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
