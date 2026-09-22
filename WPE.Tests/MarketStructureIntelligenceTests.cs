using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MarketStructureIntelligenceTests
{
    private static readonly DateTimeOffset Now=new(2026,9,20,8,0,0,TimeSpan.Zero);

    [Fact]
    public void RawCandlesCreateBullishPullbackStructure()
    {
        var market=Market(
            Pullback15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)));

        var structure=MarketStructureIntelligence.Analyze(market);

        Assert.True(structure.Available);
        Assert.Equal(MarketStructureBias.Bullish,structure.HigherTimeframeBias);
        Assert.Equal(MarketStructurePhase.BullishPullback,structure.Phase);
        Assert.Equal(MarketStructureScenario.TrendPullbackLong,structure.Scenario);
        Assert.False(structure.TriggerPresent);
    }

    [Fact]
    public void BullishImpulseAwayFromDemandIsNotAFakeTrigger()
    {
        var market=Market(
            Trend(40,90m,.35m,TimeSpan.FromMinutes(15)),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)));

        var structure=MarketStructureIntelligence.Analyze(market);

        Assert.True(structure.Available);
        Assert.Equal(MarketStructureBias.Bullish,structure.HigherTimeframeBias);
        Assert.Equal(MarketStructurePhase.BullishImpulse,structure.Phase);
        Assert.Equal(MarketStructureScenario.None,structure.Scenario);
        Assert.False(structure.TriggerPresent);
    }

    [Fact]
    public void SweepAndReclaimCreatesDirectTrigger()
    {
        var market=Market(
            SweepLowReclaimNext15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(15));

        var structure=MarketStructureIntelligence.Analyze(market);

        Assert.Equal(MarketStructureEvent.LiquiditySweepLowReclaim,structure.FifteenMinute.Event);
        Assert.Equal(MarketStructurePhase.BullishReversalAttempt,structure.Phase);
        Assert.True(structure.TriggerPresent);
        Assert.Equal(MarketStructureScenario.TrendPullbackLong,structure.Scenario);
    }

    [Fact]
    public void UnconfirmedCandleCannotFabricateABreak()
    {
        var confirmed=Pullback15m();
        var openFuture=confirmed.Concat([
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
        ]).ToArray();
        var market=Market(
            openFuture,
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)));

        var structure=MarketStructureIntelligence.Analyze(market);

        Assert.Equal(confirmed[^1].Close,structure.FifteenMinute.LastClose);
        Assert.NotEqual(MarketStructureEvent.BullishBreak,structure.FifteenMinute.Event);
    }

    [Fact]
    public async Task LocalBrainTradesDirectlyFromFreshCandleStructure()
    {
        var market=Market(
            SweepLowReclaimNext15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(15));
        var context=new AgentContext("WPE Local Brain",false,null,[],0);

        var result=await new DeterministicBrainProvider().DecideAsync(Evidence(market),context,CancellationToken.None);

        Assert.Equal(DecisionAction.OpenLong,result.Decision.Action);
        Assert.Equal(DirectMarketStructureDecisionSkill.DecisionContextKind,result.Decision.DecisionContextKind);
        Assert.Equal(DirectMarketStructureDecisionSkill.Version,result.Decision.StrategyVersion);
        Assert.True(result.Decision.StopLossPrice<market.Price);
        Assert.Equal(0,result.Decision.TakeProfitPrice);
        Assert.True(DirectMarketStructureDecisionSkill.ContextMatches(result.Decision,market));
        Assert.Contains("decision_path=direct-market-structure",result.Decision.EvidenceReferences);
        Assert.DoesNotContain("score",result.Decision.ConflictSummary,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectReviewerNeedsNoScoreOrResearchGate()
    {
        var market=Market(
            SweepLowReclaimNext15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(15));
        var evidence=Evidence(market);
        var decision=DirectMarketStructureDecisionSkill.Decide(evidence,false);

        var review=new DecisionGovernanceSkill().Review(decision,evidence,new DecisionPolicy());

        Assert.True(review.Accepted);
        Assert.Empty(review.BlockingReasons);
        Assert.Equal(DecisionAction.OpenLong,review.Decision.Action);
        Assert.Contains("Direct candle structure",review.Explanation,StringComparison.Ordinal);
    }

    [Fact]
    public async Task HardCircuitBreakerStillOverridesDirectTrade()
    {
        var market=Market(
            SweepLowReclaimNext15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(15));
        var context=new AgentContext("WPE Local Brain",true,null,[],0);

        var result=await new DeterministicBrainProvider().DecideAsync(Evidence(market),context,CancellationToken.None);

        Assert.Equal(DecisionAction.Hold,result.Decision.Action);
        Assert.Contains("circuit breaker",result.Decision.Reason,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoDirectTriggerProducesHold()
    {
        var market=Market(
            Pullback15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)));

        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(market),false);

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Equal("market-observation",decision.DecisionContextKind);
        Assert.Equal(0,decision.RiskBudgetMultiplier);
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
        IReadOnlyList<CandleEvidence> h4,
        DateTimeOffset? observedAt=null)
    {
        var observed=observedAt??Now;
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
            observed.UtcDateTime)
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

    private static IReadOnlyList<CandleEvidence> SweepLowReclaimNext15m()
    {
        var values=Pullback15m().ToList();
        var previous=values[^1];
        var referenceLow=values.TakeLast(20).Min(x=>x.Low);
        var open=previous.Close+.05m;
        var close=referenceLow+.75m;
        values.Add(new(
            Now.UtcDateTime,
            open,
            Math.Max(open,close)+.35m,
            referenceLow-1.4m,
            close,
            300m,
            30_000m,
            450,
            210m));
        return values;
    }

    private static IReadOnlyList<CandleEvidence> Trend(int count,decimal start,decimal step,TimeSpan interval)
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
            result.Add(new(first+interval*i,open,high,low,close,100m+i,(100m+i)*close,100+i,55m+i%12));
            price=close;
        }
        return result;
    }
}
