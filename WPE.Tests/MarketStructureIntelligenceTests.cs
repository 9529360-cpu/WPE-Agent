using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MarketStructureIntelligenceTests
{
    private static readonly DateTimeOffset Now=new(2026,9,20,8,0,0,TimeSpan.Zero);

    [Fact]
    public void RawMultiTimeframeCandlesCreateTrendPullbackWithoutLegacyDirectionalScores()
    {
        var market=Market(
            Pullback15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)));

        var structure=MarketStructureIntelligence.Analyze(market);
        var hypothesis=TradeHypothesisEngine.EvaluateMarket(market,null,[],Now);

        Assert.True(structure.Available);
        Assert.Equal(MarketStructureBias.Bullish,structure.HigherTimeframeBias);
        Assert.Equal(MarketStructureScenario.TrendPullbackLong,structure.Scenario);
        Assert.Equal(TradeHypothesisKind.TrendPullbackLong,hypothesis.Kind);
        Assert.Equal(TradeHypothesisStage.Watching,hypothesis.Stage);
        Assert.Equal(MarketStructureRead.DecisionBasis,hypothesis.DecisionBasis);
        Assert.Contains("structure_basis=candles-structure-v2",hypothesis.Evidence);
        Assert.Contains("4h=Bullish",hypothesis.Thesis,StringComparison.Ordinal);
    }

    [Fact]
    public void SweepAndReclaimAdvancesWatchingStructureToScout()
    {
        var h1=Trend(48,90m,.55m,TimeSpan.FromHours(1));
        var h4=Trend(48,70m,1.1m,TimeSpan.FromHours(4));
        var first=Market(Pullback15m(),h1,h4);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var current=Market(SweepLowReclaim15m(),h1,h4);

        var structure=MarketStructureIntelligence.Analyze(current);
        var scout=TradeHypothesisEngine.EvaluateMarket(current,watching,[],Now.AddMinutes(15));

        Assert.Equal(MarketStructureEvent.LiquiditySweepLowReclaim,structure.FifteenMinute.Event);
        Assert.True(structure.TriggerPresent);
        Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
        Assert.True(scout.Actionable);
        Assert.Equal(.16,scout.RiskBudgetMultiplier,10);
    }

    [Fact]
    public void UnconfirmedCandleCannotFabricateAStructureBreak()
    {
        var confirmed=Pullback15m();
        var openFuture=confirmed
            .Concat([
                new CandleEvidence(
                    Now.UtcDateTime,
                    confirmed[^1].Close,
                    confirmed[^1].Close+20m,
                    confirmed[^1].Close-.2m,
                    confirmed[^1].Close+19m,
                    1000m,
                    100_000m,
                    1000,
                    900m)
            ])
            .ToArray();
        var market=Market(
            openFuture,
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)));

        var structure=MarketStructureIntelligence.Analyze(market);

        Assert.Equal(confirmed[^1].Close,structure.FifteenMinute.LastClose);
        Assert.NotEqual(MarketStructureEvent.BullishBreak,structure.FifteenMinute.Event);
    }

    [Fact]
    public void HypothesisReviewNoLongerRequiresLegacyAggregationAssessment()
    {
        var h1=Trend(48,90m,.55m,TimeSpan.FromHours(1));
        var h4=Trend(48,70m,1.1m,TimeSpan.FromHours(4));
        var first=Market(Pullback15m(),h1,h4);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var current=Market(SweepLowReclaim15m(),h1,h4);
        var scout=TradeHypothesisEngine.EvaluateMarket(current,watching,[],Now.AddMinutes(15));
        var plan=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            TargetTier=1,
            Confidence=0,
            DecisionContextKind=TradeHypothesisEngine.DecisionContextKind,
            DecisionContextId=scout.Id,
            HypothesisStage=scout.Stage.ToString(),
            RiskBudgetMultiplier=scout.RiskBudgetMultiplier,
            StrategyVersion=scout.Version,
            Reason=scout.Thesis,
            Invalidation=scout.Invalidation
        };

        var review=new DecisionGovernanceSkill().Review(
            plan,
            [],
            Evidence(current),
            new DecisionPolicy(),
            new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=scout
            });

        Assert.True(review.Accepted);
        Assert.Empty(review.BlockingReasons);
        Assert.DoesNotContain("Aggregation",review.Explanation,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBrainUsesStructureHypothesisWithoutPublishingLegacyScoreAsDecisionBasis()
    {
        var h1=Trend(48,90m,.55m,TimeSpan.FromHours(1));
        var h4=Trend(48,70m,1.1m,TimeSpan.FromHours(4));
        var first=Market(Pullback15m(),h1,h4);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var current=Market(SweepLowReclaim15m(),h1,h4);
        var scout=TradeHypothesisEngine.EvaluateMarket(current,watching,[],Now.AddMinutes(15));
        var legacy=new MarketDecisionAssessment
        {
            Symbol="BTCUSDT",
            Regime=MarketRegime.Transition,
            NetScore=-.9,
            Confidence=.05,
            ConflictRatio=.95,
            Fresh=true,
            EntryReady=false,
            RecommendedAction=DecisionAction.Hold,
            MissingConditions=["legacy score disagrees"],
            Summary="legacy observation only"
        };
        var context=new AgentContext(
            "WPE Local Brain",false,null,[],[legacy],0,null,null,
            new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=scout
            });

        var result=await new DeterministicBrainProvider().DecideAsync(Evidence(current),context,CancellationToken.None);

        Assert.Equal(DecisionAction.OpenLong,result.Decision.Action);
        Assert.Equal(current.Price,result.Decision.EntryPrice);
        Assert.Equal(scout.InvalidationPrice,result.Decision.StopLossPrice);
        Assert.Equal(MarketStructureRead.DecisionBasis,scout.DecisionBasis);
        Assert.DoesNotContain("legacy_score",result.Decision.ConflictSummary,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("basis=candles-structure-v2",result.Decision.ConflictSummary,StringComparison.Ordinal);
    }

    private static EvidencePack Evidence(MarketEvidence market)=>new()
    {
        CollectedAt=market.CollectedAt,
        Completeness=100,
        Account=new(10_000m,10_000m,10_000m,market.CollectedAt),
        Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            [market.Symbol]=market
        }
    };

    private static MarketEvidence Market(
        IReadOnlyList<CandleEvidence> m15,
        IReadOnlyList<CandleEvidence> h1,
        IReadOnlyList<CandleEvidence> h4)
    {
        var price=m15[^1].Close;
        return new(
            "BTCUSDT",
            price,
            m15.TakeLast(20).Min(x=>x.Low),
            m15.TakeLast(20).Max(x=>x.High),
            50,
            0,
            0,
            0,
            new DerivativesSnapshot(.00003m,400_000_000m,0,0,0,1.05m,-.0005m),
            Now.UtcDateTime)
        {
            Candles=m15,
            Candles1h=h1,
            Candles4h=h4,
            Quality=new()
            {
                BestBid=price-.1m,
                BestAsk=price+.1m,
                SpreadBps=.2,
                OrderBookImbalance=.25,
                AtrPercent=.004,
                RealizedVolatility=.01,
                RelativeVolume=1.2,
                LiquidityScore=.95,
                QualityScore=97,
                Anomalies=[]
            }
        };
    }

    private static IReadOnlyList<CandleEvidence> Pullback15m() =>
        Trend(40,112m,-.28m,TimeSpan.FromMinutes(15));

    private static IReadOnlyList<CandleEvidence> SweepLowReclaim15m()
    {
        var values=Trend(39,112m,-.25m,TimeSpan.FromMinutes(15)).ToList();
        var previous=values[^1];
        var referenceLow=values.TakeLast(20).Min(x=>x.Low);
        var open=previous.Close+.05m;
        var close=previous.Close+.55m;
        values.Add(new(
            previous.OpenTime.AddMinutes(15),
            open,
            close+.35m,
            referenceLow-1.4m,
            close,
            300m,
            30_000m,
            450,
            210m));
        return values;
    }

    private static IReadOnlyList<CandleEvidence> Trend(
        int count,
        decimal start,
        decimal step,
        TimeSpan interval)
    {
        var result=new List<CandleEvidence>(count);
        var price=start;
        var first=Now.UtcDateTime-interval*count;
        for(var i=0;i<count;i++)
        {
            var open=price;
            var close=Math.Max(1m,open+step);
            var high=Math.Max(open,close)+.30m;
            var low=Math.Min(open,close)-.30m;
            result.Add(new(
                first+interval*i,
                open,high,low,close,
                100m+i,
                (100m+i)*close,
                100+i,
                55m+i%12));
            price=close;
        }
        return result;
    }
}
