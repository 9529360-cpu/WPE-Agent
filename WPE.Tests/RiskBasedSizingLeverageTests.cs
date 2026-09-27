using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RiskBasedSizingLeverageTests
{
    private static readonly IReadOnlyList<LeverageBracket> WideBrackets=
    [
        new(1,0m,50_000m,125,.004m,0m),
        new(2,50_000m,250_000m,100,.005m,50m)
    ];

    [Fact]
    public void OnePercentAccountRiskWithThreePercentStopUsesMinimumNecessaryLeverage()
    {
        var equity=3_000m;
        var decision=Long("TESTUSDT",100m,97m,105.4m);
        var limits=Limits(maxSymbolExposure:.50m,maxAccountExposure:.80m);
        var rule=Rule("TESTUSDT",.001m,.01m,.001m,5m,125,.0004m);

        var result=new RiskAndPositionPlanner().Plan(decision,Evidence(equity,decision),rule,limits);

        var intent=Assert.Single(result.Intents);
        var worstCase=intent.Quantity*Math.Abs(intent.ExpectedPrice-intent.StopLoss)+intent.EstimatedRoundTripFee+intent.EstimatedExecutionCost;
        var margin=intent.Quantity*intent.ExpectedPrice/intent.EffectiveLeverage;
        Assert.Equal(7,intent.EffectiveLeverage);
        Assert.InRange(intent.Quantity,9.0m,9.1m);
        Assert.True(intent.EstimatedExecutionCost>0);
        Assert.True(worstCase<=equity*limits.MaxRiskPerTrade);
        Assert.True(margin<=equity*limits.MaxInitialMarginPerTrade);
        Assert.True(RiskAndPositionPlanner.StopRespectsLiquidationBuffer(
            intent.Side,intent.ExpectedPrice,intent.StopLoss,intent.EstimatedLiquidationPrice,limits.LiquidationBufferFraction));
    }

    [Fact]
    public void HistoricBtcShapeNeedsAboutFiveXInsteadOfOneHundredFifteenX()
    {
        var equity=3_098.77m;
        var decision=Long("BTCUSDT",84_945m,84_428.2275m,85_875.1905m);
        var limits=Limits();
        var rule=Rule("BTCUSDT",.0001m,.1m,.0001m,5m,125,.0004m);

        var result=new RiskAndPositionPlanner().Plan(decision,Evidence(equity,decision),rule,limits);

        var intent=Assert.Single(result.Intents);
        Assert.Equal(5,intent.EffectiveLeverage);
        Assert.True(intent.EffectiveLeverage<115);
        Assert.True(intent.Quantity*intent.ExpectedPrice<=equity*limits.MaxSymbolExposure+.01m);
        Assert.True(RiskAndPositionPlanner.StopRespectsLiquidationBuffer(
            intent.Side,intent.ExpectedPrice,intent.StopLoss,intent.EstimatedLiquidationPrice,limits.LiquidationBufferFraction));
    }

    [Fact]
    public void HistoricInxShapeUsesLowSingleDigitLeverageAndDoesNotStartInsideBuffer()
    {
        var equity=3_098.77m;
        var decision=Long("INXUSDT",.006837m,.006368m,.007682m);
        var limits=Limits();
        var rule=Rule("INXUSDT",1m,.000001m,1m,5m,125,.0004m);

        var result=new RiskAndPositionPlanner().Plan(decision,Evidence(equity,decision),rule,limits);

        var intent=Assert.Single(result.Intents);
        Assert.InRange(intent.EffectiveLeverage,1,3);
        Assert.True(intent.EffectiveLeverage<9);
        Assert.True(RiskAndPositionPlanner.StopRespectsLiquidationBuffer(
            intent.Side,intent.ExpectedPrice,intent.StopLoss,intent.EstimatedLiquidationPrice,limits.LiquidationBufferFraction));
    }

    [Fact]
    public void ExecutionCostReserveFurtherReducesRiskSizedQuantity()
    {
        var decision=Long("TESTUSDT",100m,97m,105.4m);
        var limits=Limits(maxSymbolExposure:.50m,maxAccountExposure:.80m);
        var rule=Rule("TESTUSDT",.001m,.01m,.001m,5m,125,.0004m);
        var withCosts=Evidence(3_000m,decision,1);
        var noCosts=Evidence(3_000m,decision,0);
        var zeroSlippage=Limits(maxSymbolExposure:.50m,maxAccountExposure:.80m);
        zeroSlippage.MaximumSlippageBps=0;

        var costIntent=Assert.Single(new RiskAndPositionPlanner().Plan(decision,withCosts,rule,limits).Intents);
        var cheapIntent=Assert.Single(new RiskAndPositionPlanner().Plan(decision,noCosts,rule,zeroSlippage).Intents);

        Assert.True(costIntent.Quantity<cheapIntent.Quantity);
        Assert.True(costIntent.EstimatedExecutionCost>0);
        Assert.Equal(0m,cheapIntent.EstimatedExecutionCost);
    }

    [Fact]
    public void FeesReduceRiskSizedQuantity()
    {
        var decision=Long("TESTUSDT",100m,97m,105.4m);
        var limits=Limits(maxSymbolExposure:.50m,maxAccountExposure:.80m);
        var withFees=Rule("TESTUSDT",.001m,.01m,.001m,5m,125,.0004m);
        var noFees=new TradingRule("TESTUSDT",.001m,.01m,.001m,5m,125,WideBrackets,0,0,true);
        var evidence=Evidence(3_000m,decision);

        var feeIntent=Assert.Single(new RiskAndPositionPlanner().Plan(decision,evidence,withFees,limits).Intents);
        var freeIntent=Assert.Single(new RiskAndPositionPlanner().Plan(decision,evidence,noFees,limits).Intents);

        Assert.True(feeIntent.Quantity<freeIntent.Quantity);
        Assert.True(feeIntent.EstimatedRoundTripFee>0);
    }

    [Fact]
    public void LiquidationEstimatorRechecksBracketAtLiquidationNotional()
    {
        var brackets=new[]
        {
            new LeverageBracket(1,0m,1_000m,100,.004m,0m),
            new LeverageBracket(2,1_000m,10_000m,20,.010m,6m)
        };
        var rule=new TradingRule("TESTUSDT",.1m,.01m,.1m,5m,100,brackets,.0002m,.0004m,true);

        var safe=RiskAndPositionPlanner.TryEstimateIsolatedLiquidationPrice(
            PositionSide.Short,100m,9.9m,25,rule,out _,out _);

        Assert.False(safe);
    }

    [Fact]
    public void PortfolioStopRiskBudgetCapsNewTradeRisk()
    {
        var equity=10_000m;
        var decision=Long("TESTUSDT",100m,97m,106.6m);decision.RiskBudgetFraction=.0085m;
        var limits=Limits(maxSymbolExposure:.80m,maxAccountExposure:.90m);
        limits.MaxPortfolioStopRisk=.025m;
        var rule=Rule("TESTUSDT",.001m,.01m,.001m,5m,125,.0004m);
        var existingRisk=240m;

        var result=new RiskAndPositionPlanner().Plan(decision,Evidence(equity,decision),rule,limits,existingPortfolioStopRisk:existingRisk);

        var intent=Assert.Single(result.Intents);
        var newRisk=intent.Quantity*Math.Abs(intent.ExpectedPrice-intent.StopLoss)+intent.EstimatedRoundTripFee+intent.EstimatedExecutionCost;
        Assert.True(newRisk<=10.01m);
        Assert.True(existingRisk+newRisk<=equity*limits.MaxPortfolioStopRisk+.01m);
    }

    [Fact]
    public void PortfolioStopRiskBudgetBlocksWhenAlreadyExhausted()
    {
        var decision=Long("TESTUSDT",100m,97m,106.6m);decision.RiskBudgetFraction=.0085m;
        var limits=Limits(maxSymbolExposure:.80m,maxAccountExposure:.90m);
        limits.MaxPortfolioStopRisk=.025m;
        var rule=Rule("TESTUSDT",.001m,.01m,.001m,5m,125,.0004m);

        var result=new RiskAndPositionPlanner().Plan(decision,Evidence(10_000m,decision),rule,limits,existingPortfolioStopRisk:250m);

        Assert.Empty(result.Intents);
        Assert.Equal("risk.portfolio-stop-budget-exhausted",result.Result);
    }

    [Fact]
    public void HighOpportunityModeFailsClosedWithoutLiveBracketOrFeeEvidence()
    {
        var decision=Long("TESTUSDT",100m,97m,105.4m);
        var evidence=Evidence(3_000m,decision);
        var limits=Limits(maxSymbolExposure:.50m,maxAccountExposure:.80m);
        var missingBrackets=new TradingRule("TESTUSDT",.001m,.01m,.001m,5m,1,null,.0004m,.0004m,true);
        var missingFees=new TradingRule("TESTUSDT",.001m,.01m,.001m,5m,125,WideBrackets,0,0,false);

        var a=new RiskAndPositionPlanner().Plan(decision,evidence,missingBrackets,limits);
        var b=new RiskAndPositionPlanner().Plan(decision,evidence,missingFees,limits);

        Assert.Empty(a.Intents);
        Assert.Equal("risk.exchange-leverage-tiers-unavailable",a.Result);
        Assert.Empty(b.Intents);
        Assert.Equal("risk.exchange-fee-rate-unavailable",b.Result);
    }

    private static DecisionPlan Long(string symbol,decimal entry,decimal stop,decimal take)=>new()
    {
        Action=DecisionAction.OpenLong,
        Instrument=symbol,
        EntryPrice=entry,
        StopLossPrice=stop,
        TakeProfitPrice=take,
        RiskRewardRatio=(double)((take-entry)/(entry-stop)),
        TargetTier=1
    };

    private static TradingRule Rule(string symbol,decimal step,decimal tick,decimal minQty,decimal minNotional,int maxLev,decimal taker)=>
        new(symbol,step,tick,minQty,minNotional,maxLev,WideBrackets,taker,taker,true);

    private static RiskLimits Limits(decimal maxSymbolExposure=.25m,decimal maxAccountExposure=.50m)=>new()
    {
        TestnetHighOpportunityMode=true,
        Leverage=150,
        MaxInitialMarginPerTrade=.05m,
        MarginTiers=[.05m,.10m,.20m],
        MaxMargin=.50m,
        MaxRiskPerTrade=.01m,
        MaxSymbolExposure=maxSymbolExposure,
        MaxAccountExposure=maxAccountExposure,
        MaxConcurrentPositions=5,
        MinimumRiskReward=1.8,
        LiquidationBufferFraction=.05m
    };

    private static EvidencePack Evidence(decimal equity,DecisionPlan decision,double spreadBps=1)=>new()
    {
        Completeness=100,
        Account=new AccountSnapshot(equity,equity,equity,DateTime.UtcNow),
        Markets=new Dictionary<string,MarketEvidence>
        {
            [decision.Instrument]=new(
                decision.Instrument,decision.EntryPrice,decision.StopLossPrice,decision.TakeProfitPrice,50,0,0,0,
                new DerivativesSnapshot(0,0,0,0,0,0,0),DateTime.UtcNow)
            {
                Quality=new MarketQualityEvidence
                {
                    QualityScore=100,
                    LiquidityScore=1,
                    SpreadBps=spreadBps,
                    BestBid=decision.EntryPrice,
                    BestAsk=decision.EntryPrice,
                    AtrPercent=.01
                }
            }
        }
    };
}
