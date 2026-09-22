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
        Assert.False(structure.ConfirmationPresent);
        Assert.Equal(MarketStructureScenario.TrendPullbackLong,structure.Scenario);
        Assert.Contains("structure_entry_ready=waiting",structure.Evidence);
        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(market),false);
        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Contains("confirmation is still waiting",decision.Reason,StringComparison.Ordinal);
    }

    [Fact]
    public void ClosedRealtimeMinuteCanConfirmSweepWithoutWaitingForNext15MinuteClose()
    {
        var candles=SweepLowReclaimNext15m();
        var candidate=candles[^1];
        var minuteOpen=candidate.Close+.10m;
        var minuteClose=candidate.High+.80m;
        var minute=new CandleEvidence(
            Now.AddMinutes(15).UtcDateTime,
            minuteOpen,
            minuteClose+.10m,
            minuteOpen-.10m,
            minuteClose,
            80m,
            8_000m,
            120,
            55m);
        var baseline=Market(
            candles,
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(16));
        var market=baseline with{Price=minute.Close,Candles1m=[minute]};

        var structure=MarketStructureIntelligence.Analyze(market);
        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(market),false);

        Assert.Equal(MarketStructureEvent.LiquiditySweepLowReclaim,structure.FifteenMinute.Event);
        Assert.True(structure.TriggerPresent);
        Assert.True(structure.ConfirmationPresent);
        Assert.Equal("1m-realtime-closed",structure.ConfirmationSource);
        Assert.Equal(minute.Close,structure.ConfirmationClose);
        Assert.Contains("structure_confirmation_source=1m-realtime-closed",structure.Evidence);
        Assert.Equal(DecisionAction.OpenLong,decision.Action);
        Assert.True(DirectMarketStructureDecisionSkill.ContextMatches(decision,market));
    }

    [Fact]
    public void SweepFollowThroughCreatesConfirmedEntry()
    {
        var market=Market(
            SweepLowReclaimThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));

        var structure=MarketStructureIntelligence.Analyze(market);

        Assert.Equal(MarketStructureEvent.BullishConfirmation,structure.FifteenMinute.Event);
        Assert.Equal(MarketStructureScenario.TrendPullbackLong,structure.Scenario);
        Assert.True(structure.TriggerPresent);
        Assert.True(structure.ConfirmationPresent);
        Assert.Contains("structure_entry_ready=ready",structure.Evidence);
    }

    [Fact]
    public void RejectionNearDemandNeedsAndAcceptsFollowThroughConfirmation()
    {
        var rejectionOnly=Market(
            RejectionNearDemand15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(15));
        var candidate=MarketStructureIntelligence.Analyze(rejectionOnly);

        Assert.Equal(MarketStructureEvent.BullishRejection,candidate.FifteenMinute.Event);
        Assert.True(candidate.TriggerPresent);
        Assert.False(candidate.ConfirmationPresent);
        Assert.Equal(DecisionAction.Hold,DirectMarketStructureDecisionSkill.Decide(Evidence(rejectionOnly),false).Action);

        var confirmed=Market(
            RejectionNearDemandThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));
        var structure=MarketStructureIntelligence.Analyze(confirmed);
        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(confirmed),false);

        Assert.Equal(MarketStructureEvent.BullishConfirmation,structure.FifteenMinute.Event);
        Assert.True(structure.ConfirmationPresent);
        Assert.Equal(DecisionAction.OpenLong,decision.Action);
    }

    [Fact]
    public void ConfirmedSetupDoesNotChaseLivePriceBeyondAtrBound()
    {
        var market=Market(
            SweepLowReclaimThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));
        var structure=MarketStructureIntelligence.Analyze(market);
        var chased=market with{Price=structure.FifteenMinute.LastClose+Math.Max(structure.FifteenMinute.Atr*2m,structure.FifteenMinute.LastClose*.01m)};

        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(chased),false);

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Contains("moved beyond the bounded entry zone",decision.Reason,StringComparison.Ordinal);
    }

    [Fact]
    public void BearishSweepFollowThroughCreatesConfirmedShortEntry()
    {
        var market=Market(
            SweepHighRejectThenConfirm15m(),
            Trend(48,110m,-.55m,TimeSpan.FromHours(1)),
            Trend(48,130m,-1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));

        var structure=MarketStructureIntelligence.Analyze(market);
        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(market),false);

        Assert.Equal(MarketStructureEvent.BearishConfirmation,structure.FifteenMinute.Event);
        Assert.Equal(MarketStructureScenario.TrendPullbackShort,structure.Scenario);
        Assert.True(structure.TriggerPresent);
        Assert.True(structure.ConfirmationPresent);
        Assert.Equal(DecisionAction.OpenShort,decision.Action);
        Assert.True(decision.StopLossPrice>market.Price);
        Assert.True(DirectMarketStructureDecisionSkill.ContextMatches(decision,market));
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
            SweepLowReclaimThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));
        var context=new AgentContext("WPE Local Brain",false,null,[],0);

        var result=await new DeterministicBrainProvider().DecideAsync(Evidence(market),context,CancellationToken.None);

        Assert.Equal(DecisionAction.OpenLong,result.Decision.Action);
        Assert.Equal(DirectMarketStructureDecisionSkill.DecisionContextKind,result.Decision.DecisionContextKind);
        Assert.Equal(DirectMarketStructureDecisionSkill.Version,result.Decision.StrategyVersion);
        Assert.True(result.Decision.StopLossPrice<market.Price);
        Assert.Equal(0,result.Decision.TakeProfitPrice);
        Assert.True(DirectMarketStructureDecisionSkill.ContextMatches(result.Decision,market));
        Assert.Contains("decision_path=direct-market-structure",result.Decision.EvidenceReferences);
        Assert.Contains("entry_qualification=trigger-plus-confirmation",result.Decision.EvidenceReferences);
        Assert.Contains("\"provider\":\"WPE Local Brain\"",result.Response,StringComparison.Ordinal);
        Assert.Contains("\"agent\":\"Technical Market Decision Agent\"",result.Response,StringComparison.Ordinal);
        Assert.Contains("\"tool\":\"market.structure.analyze\"",result.Response,StringComparison.Ordinal);
        Assert.DoesNotContain("score",result.Decision.ConflictSummary,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TechnicalDecisionAgentUsesInjectedAnalysisTool()
    {
        var market=Market(
            SweepLowReclaimThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));
        var tool=new CountingMarketStructureTool();

        var result=await new TechnicalDecisionAgent(marketStructure:tool)
            .DecideAsync(Evidence(market),new AgentContext("WPE Local Brain",false,null,[],0),CancellationToken.None);

        Assert.Equal(DecisionAction.OpenLong,result.Decision.Action);
        Assert.True(tool.Calls>=2);
    }

    [Fact]
    public void DirectReviewerNeedsNoScoreOrResearchGate()
    {
        var market=Market(
            SweepLowReclaimThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));
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
            SweepLowReclaimThenConfirm15m(),
            Trend(48,90m,.55m,TimeSpan.FromHours(1)),
            Trend(48,70m,1.1m,TimeSpan.FromHours(4)),
            Now.AddMinutes(30));
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

    private static IReadOnlyList<CandleEvidence> SweepLowReclaimThenConfirm15m()
    {
        var values=SweepLowReclaimNext15m().ToList();
        var sweep=values[^1];
        var open=sweep.Close+.10m;
        var close=sweep.High+1.20m;
        values.Add(new(
            Now.AddMinutes(15).UtcDateTime,
            open,
            close+.25m,
            open-.20m,
            close,
            420m,
            42_000m,
            520,
            320m));
        return values;
    }

    private static IReadOnlyList<CandleEvidence> RejectionNearDemand15m()
    {
        var values=Pullback15m().ToList();
        var referenceLow=values.TakeLast(20).SkipLast(1).Min(x=>x.Low);
        var open=referenceLow+.32m;
        var close=referenceLow+.47m;
        values.Add(new(
            Now.UtcDateTime,
            open,
            close+.07m,
            referenceLow-.06m,
            close,
            190m,
            19_000m,
            310,
            150m));
        return values;
    }

    private static IReadOnlyList<CandleEvidence> RejectionNearDemandThenConfirm15m()
    {
        var values=RejectionNearDemand15m().ToList();
        var rejection=values[^1];
        var open=rejection.Close+.05m;
        var close=rejection.High+.75m;
        values.Add(new(
            Now.AddMinutes(15).UtcDateTime,
            open,
            close+.15m,
            open-.10m,
            close,
            300m,
            30_000m,
            420,
            250m));
        return values;
    }

    private static IReadOnlyList<CandleEvidence> SweepHighRejectThenConfirm15m()
    {
        var values=Trend(40,88m,.28m,TimeSpan.FromMinutes(15)).ToList();
        var previous=values[^1];
        var referenceHigh=values.TakeLast(20).Max(x=>x.High);
        var open=previous.Close-.05m;
        var close=referenceHigh-.75m;
        values.Add(new(
            Now.UtcDateTime,
            open,
            referenceHigh+1.40m,
            Math.Min(open,close)-.35m,
            close,
            300m,
            30_000m,
            450,
            90m));
        var sweep=values[^1];
        open=sweep.Close-.10m;
        close=sweep.Low-1.20m;
        values.Add(new(
            Now.AddMinutes(15).UtcDateTime,
            open,
            open+.20m,
            close-.25m,
            close,
            420m,
            42_000m,
            520,
            100m));
        return values;
    }

    private sealed class CountingMarketStructureTool : IMarketStructureAnalysisTool
    {
        public int Calls { get; private set; }
        public string Name => MarketStructureAnalysisTool.ToolName;

        public MarketStructureRead Analyze(MarketEvidence market)
        {
            Calls++;
            return MarketStructureAnalysisTool.Shared.Analyze(market);
        }
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
