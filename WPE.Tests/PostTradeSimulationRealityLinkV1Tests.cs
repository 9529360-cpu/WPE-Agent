using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradeSimulationRealityLinkV1Tests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-post-trade-sim-reality-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=new(2026,9,19,3,0,0,TimeSpan.Zero);

    [Fact]
    public async Task ExactReviewLinksFullSimulationRealityChainCanonically()
    {
        var store=await SeedReview(exitPrice:110m);
        var chain=await SeedChain(store,saveSource:true,saveComparison:true,comparedAt:Now.AddSeconds(-1));

        var link=Assert.Single(await new PostTradeSimulationRealityLinkerV1(store).GetRecentAsync(10,default));

        Assert.Equal(PostTradeSimulationRealityLinkStateV1.Linked,link.State);
        Assert.Equal("linked",link.Code);
        Assert.Equal(chain.Source.CanonicalSha256,link.SimulationSourceSha256);
        Assert.Equal(chain.Source.ArtifactSha256,link.ArtifactSha256);
        Assert.Equal(chain.Source.IntentSha256,link.IntentSha256);
        Assert.Equal(chain.Source.MarketProvenanceSha256,link.MarketProvenanceSha256);
        Assert.Equal(chain.Fill.CanonicalSha256,link.SimulatedFillSha256);
        Assert.Equal(chain.Fill.State,link.SimulatedState);
        Assert.Equal(chain.Observed.CanonicalSha256,link.ObservedDriftSha256);
        Assert.Equal(chain.Observed.State,link.ObservedState);
        Assert.Equal(chain.Comparison!.CanonicalSha256,link.ComparisonSha256);
        Assert.Equal(chain.Comparison.StateMatch,link.StateMatch);
        Assert.Equal(chain.Comparison.FillRatioDelta,link.FillRatioDelta);
        Assert.Equal(chain.Comparison.PriceDriftBps,link.PriceDriftBps);
        Assert.Equal(ExecutionRealityCostAuthorityV1.Version,link.CostModelVersion);
        Assert.Equal(AutomaticExecutionSimulationModelV1.Version,link.SimulationModelVersion);
        Assert.True(PostTradeSimulationRealityLinkerV1.IsCanonical(link));
        Assert.False(PostTradeSimulationRealityLinkerV1.IsCanonical(link with{Code="tampered"}));
    }

    [Fact]
    public async Task MissingComparisonIsUnavailableRatherThanPretendingNoDrift()
    {
        var store=await SeedReview();
        await SeedChain(store,saveSource:true,saveComparison:false,comparedAt:Now.AddSeconds(-1));

        var link=Assert.Single(await new PostTradeSimulationRealityLinkerV1(store).GetRecentAsync(10,default));

        Assert.Equal(PostTradeSimulationRealityLinkStateV1.Unavailable,link.State);
        Assert.Equal("simulation-comparison-unavailable",link.Code);
        Assert.Null(link.ComparisonSha256);
        Assert.Null(link.PriceDriftBps);
        Assert.True(PostTradeSimulationRealityLinkerV1.IsCanonical(link));
    }

    [Fact]
    public async Task ComparisonWithoutPersistedSourceIsUnavailable()
    {
        var store=await SeedReview();
        await SeedChain(store,saveSource:false,saveComparison:true,comparedAt:Now.AddSeconds(-1));

        var link=Assert.Single(await new PostTradeSimulationRealityLinkerV1(store).GetRecentAsync(10,default));

        Assert.Equal(PostTradeSimulationRealityLinkStateV1.Unavailable,link.State);
        Assert.Equal("simulation-source-unavailable",link.Code);
    }

    [Fact]
    public async Task ReviewAndObservedExecutionMismatchIsConflict()
    {
        var store=await SeedReview(exitPrice:111m);
        await SeedChain(store,saveSource:true,saveComparison:true,comparedAt:Now.AddSeconds(-1));

        var link=Assert.Single(await new PostTradeSimulationRealityLinkerV1(store).GetRecentAsync(10,default));

        Assert.Equal(PostTradeSimulationRealityLinkStateV1.Conflict,link.State);
        Assert.Equal("observed-review-mismatch",link.Code);
    }

    [Fact]
    public async Task EquivalentRepeatedComparisonUsesLatestWithoutChangingUnderlyingEvidence()
    {
        var store=await SeedReview();
        var chain=await SeedChain(store,saveSource:true,saveComparison:true,comparedAt:Now.AddSeconds(-1));
        var later=ExecutionSimulationComparisonCanonicalizerV1.Create(
            chain.Fill,
            chain.Observed,
            Now);
        await store.SaveExecutionSimulationComparisonAsync(later,default);

        var link=Assert.Single(await new PostTradeSimulationRealityLinkerV1(store).GetRecentAsync(10,default));

        Assert.Equal(PostTradeSimulationRealityLinkStateV1.Linked,link.State);
        Assert.Equal(later.CanonicalSha256,link.ComparisonSha256);
        Assert.Equal(chain.Fill.CanonicalSha256,link.SimulatedFillSha256);
        Assert.Equal(chain.Observed.CanonicalSha256,link.ObservedDriftSha256);
    }

    [Fact]
    public async Task LegacyReviewWithoutStrategyIdentityIsUnavailable()
    {
        var store=await SeedReview(strategyId:null);

        var link=Assert.Single(await new PostTradeSimulationRealityLinkerV1(store).GetRecentAsync(10,default));

        Assert.Equal(PostTradeSimulationRealityLinkStateV1.Unavailable,link.State);
        Assert.Equal("strategy-attribution-unavailable",link.Code);
        Assert.True(PostTradeSimulationRealityLinkerV1.IsCanonical(link));
    }

    private async Task<AgentSqliteStore> SeedReview(
        string? strategyId="strategy-reality",
        decimal exitPrice=110m)
    {
        var store=new AgentSqliteStore(Database,()=>Now);
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO trade_outcomes(
                client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,gross_pnl,fees,
                net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis)
            VALUES(
                'WPE-CLOSE','cycle-close','BTCUSDT','Long','100',$exit,'1','10','0',
                '10','.10',$closed,$strategy,'v7',$basis);
            """;
        command.Parameters.AddWithValue("$exit",exitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$closed",Now.ToString("O"));
        command.Parameters.AddWithValue("$strategy",strategyId is null?DBNull.Value:strategyId);
        command.Parameters.AddWithValue("$basis",strategyId is null?"legacy-version-only":"automatic-correlation");
        await command.ExecuteNonQueryAsync();
        return store;
    }

    private async Task<Chain> SeedChain(
        AgentSqliteStore store,
        bool saveSource,
        bool saveComparison,
        DateTimeOffset comparedAt)
    {
        var artifact=Artifact();
        var intent=artifact.Intents.Single();
        var source=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            intent,
            SimulationObservation(),
            Now.AddSeconds(-20));
        if(saveSource)
            await store.SaveExecutionSimulationSourceAsync(source,default);

        var fill=AutomaticExecutionSimulationModelV1.CreateFill(source);
        await store.SaveExecutionSimulationFillAsync(fill,default);

        var costs=ExecutionRealityCostAuthorityV1.Current;
        var expectation=new ExecutionRealityExpectationV1(
            "cycle-close",
            "WPE-CLOSE",
            "strategy-reality",
            "v7",
            costs.Version,
            "BTCUSDT",
            PositionSide.Long,
            true,
            ExecutionOrderType.Market,
            1m,
            110m,
            costs.CommissionRate,
            costs.SlippageRate,
            Now.AddSeconds(-10));
        var observation=new ExecutionRealityObservationV1(
            "WPE-CLOSE",
            "FILLED",
            1m,
            110m,
            0m,
            ExecutionRealityDriftV1.UnavailableFeeBasis,
            Now.AddSeconds(-2),
            Now.AddSeconds(-1));
        var observed=ExecutionRealityDriftV1.Analyze(expectation,observation);
        await store.SaveExecutionRealityDriftAsync(observed,default);

        ExecutionSimulationComparisonV1? comparison=null;
        if(saveComparison)
        {
            comparison=ExecutionSimulationComparisonCanonicalizerV1.Create(fill,observed,comparedAt);
            await store.SaveExecutionSimulationComparisonAsync(comparison,default);
        }
        return new(source,fill,observed,comparison);
    }

    private static DurableExecutionArtifactV2 Artifact()=>new(
        DurableExecutionArtifactV2.Version,
        "cycle-close",
        [new(
            0,
            "BTCUSDT",
            "Long",
            1m,
            true,
            0m,
            0m,
            "WPE-CLOSE",
            "strategy.exit",
            "CloseLong",
            "Market",
            0m,
            110m)],
        5,
        true,
        "binance",
        "Testnet",
        "strategy-reality",
        "v7",
        Now.AddSeconds(-30),
        "market-close-v1",
        Now.AddSeconds(-25),
        Now.AddMinutes(1));

    private static AutomaticExecutionSimulationObservationV1 SimulationObservation()
    {
        var market=new MarketEvidence(
            "BTCUSDT",
            110m,
            105m,
            115m,
            50,
            0,
            0,
            0,
            new(0,1,1,1,1,1,0),
            Now.AddSeconds(-21).UtcDateTime)
        {
            Quality=new(){QualityScore=90,LiquidityScore=.9,SpreadBps=1,AtrPercent=.01}
        };
        market=market with
        {
            Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance","Testnet")
        };
        return new(
            true,
            "simulation.source-available",
            "binance",
            "Testnet",
            market,
            new TradingRule("BTCUSDT",.001m,.1m,.001m,5m,20),
            Now.AddSeconds(-20));
    }

    private sealed record Chain(
        ExecutionSimulationSourceV1 Source,
        ExecutionSimulationFillV1 Fill,
        ExecutionRealityDriftFactV1 Observed,
        ExecutionSimulationComparisonV1? Comparison);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}
