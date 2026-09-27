using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TestnetHighOpportunityRiskProfileV1Tests
{
    [Fact]
    public void DisabledProfileLeavesConfiguredRiskObjectUnchanged()
    {
        var configured=new RiskLimits
        {
            TestnetHighOpportunityMode=false,
            Leverage=12,
            MaxInitialMarginPerTrade=.02m,
            MaxRiskPerTrade=.005m
        };

        var resolved=TestnetHighOpportunityRiskProfileV1.Resolve(configured,ExchangeEnvironment.Testnet);

        Assert.Same(configured,resolved);
    }

    [Fact]
    public void EnabledProfileUsesHighLeverageRequestButKeepsLossRiskBounded()
    {
        var configured=new RiskLimits
        {
            TestnetHighOpportunityMode=true,
            Leverage=10,
            MaxInitialMarginPerTrade=.02m,
            MaxRiskPerTrade=.02m,
            MaxSymbolExposure=.35m,
            MaxAccountExposure=.60m,
            MaxDailyLoss=.10m,
            DailyDrawdownLimit=.10m,
            MinimumRiskReward=1.2,
            MinimumLiquidityScore=.30,
            MaximumSpreadBps=20,
            MaxAtrPercent=.10,
            Isolated=false
        };

        var resolved=TestnetHighOpportunityRiskProfileV1.Resolve(configured,ExchangeEnvironment.Testnet);

        Assert.NotSame(configured,resolved);
        Assert.True(resolved.TestnetHighOpportunityMode);
        Assert.Equal(150,resolved.Leverage);
        Assert.Equal(.05m,resolved.MaxInitialMarginPerTrade);
        Assert.Equal(.01m,resolved.MaxRiskPerTrade);
        Assert.Equal(.25m,resolved.MaxSymbolExposure);
        Assert.Equal(.50m,resolved.MaxAccountExposure);
        Assert.Equal(5,resolved.MaxConcurrentPositions);
        Assert.Equal(.05m,resolved.MaxDailyLoss);
        Assert.Equal(.08m,resolved.DailyDrawdownLimit);
        Assert.Equal(1.8,resolved.MinimumRiskReward);
        Assert.Equal(.55,resolved.MinimumLiquidityScore);
        Assert.Equal(8,resolved.MaximumSpreadBps);
        Assert.Equal(.045,resolved.MaxAtrPercent);
        Assert.True(resolved.Isolated);
    }

    [Fact]
    public void EnabledProfilePreservesMoreConservativeConfiguredSafetyLimits()
    {
        var configured=new RiskLimits
        {
            TestnetHighOpportunityMode=true,
            MaxRiskPerTrade=.004m,
            MaxSymbolExposure=.10m,
            MaxAccountExposure=.20m,
            MaxDailyLoss=.02m,
            DailyDrawdownLimit=.03m,
            MinimumRiskReward=2.2,
            MinimumLiquidityScore=.70,
            MaximumSpreadBps=5,
            MaxAtrPercent=.03
        };

        var resolved=TestnetHighOpportunityRiskProfileV1.Resolve(configured,ExchangeEnvironment.Testnet);

        Assert.Equal(.004m,resolved.MaxRiskPerTrade);
        Assert.Equal(.10m,resolved.MaxSymbolExposure);
        Assert.Equal(.20m,resolved.MaxAccountExposure);
        Assert.Equal(.02m,resolved.MaxDailyLoss);
        Assert.Equal(.03m,resolved.DailyDrawdownLimit);
        Assert.Equal(2.2,resolved.MinimumRiskReward);
        Assert.Equal(.70,resolved.MinimumLiquidityScore);
        Assert.Equal(5,resolved.MaximumSpreadBps);
        Assert.Equal(.03,resolved.MaxAtrPercent);
    }

    [Fact]
    public void EnabledProfileRejectsMainnet()
    {
        var configured=new RiskLimits{TestnetHighOpportunityMode=true};

        Assert.Throws<InvalidOperationException>(()=>
            TestnetHighOpportunityRiskProfileV1.Resolve(configured,ExchangeEnvironment.Mainnet));
    }

    [Fact]
    public void PlannerStillCapsEffectiveLeverageByProviderAndStructuralStop()
    {
        var configured=new RiskLimits{TestnetHighOpportunityMode=true};
        var resolved=TestnetHighOpportunityRiskProfileV1.Resolve(configured,ExchangeEnvironment.Testnet);
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            EntryPrice=100m,
            StopLossPrice=98.1m,
            TakeProfitPrice=104m
        };

        var effective=RiskAndPositionPlanner.SelectEffectiveLeverage(
            decision,
            new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125),
            resolved);

        Assert.Equal(30,effective);
        Assert.True(effective<resolved.Leverage);
    }

    [Fact]
    public void AutoRuntimeUsesResolvedRiskProfileAcrossAllRiskIncreasingOwners()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));

        Assert.Contains("TestnetHighOpportunityRiskProfileV1.Resolve(settings.Risk,exchange.Environment)",source,StringComparison.Ordinal);
        Assert.Contains("new ReliableOrderExecutor(exchange,Db,runtimeRisk",source,StringComparison.Ordinal);
        Assert.Contains("deterministic.Complete(proposed,proposedMarket,runtimeRisk)",source,StringComparison.Ordinal);
        Assert.Contains("planner.Plan(decision,evidence,tradingRule,runtimeRisk",source,StringComparison.Ordinal);
        Assert.Contains("independentRisk.Review(decision,evidence,intents,runtimeRisk",source,StringComparison.Ordinal);
        Assert.DoesNotContain(".Take(64)",source,StringComparison.Ordinal);
        Assert.Contains(".Take(OpportunityUniverseSelectorV1.DefaultWatchLimit)",source,StringComparison.Ordinal);
    }
}
