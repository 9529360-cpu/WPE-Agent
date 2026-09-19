using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradeHypothesisEngineTests
{
    private static readonly DateTimeOffset Now=new(2026,9,19,22,0,0,TimeSpan.Zero);

    [Fact]
    public void HigherTimeframeUptrendWithDeepPullbackNearSupportStartsWatchingLongHypothesis()
    {
        var market=Market(
            price:81075.2m,
            support:80906m,
            resistance:81805.3m,
            rsi:27.3,
            trend15m:-.376,
            trend1h:-.225,
            trend4h:5.13,
            book:-.82);

        var hypothesis=TradeHypothesisEngine.EvaluateMarket(market,null,[],Now);

        Assert.Equal(TradeHypothesisKind.TrendPullbackLong,hypothesis.Kind);
        Assert.Equal(TradeHypothesisStage.Watching,hypothesis.Stage);
        Assert.Equal(1,hypothesis.Direction);
        Assert.False(hypothesis.Actionable);
        Assert.Contains("pulling back near support",hypothesis.Thesis,StringComparison.OrdinalIgnoreCase);
        Assert.True(hypothesis.InvalidationPrice<market.Support);
        Assert.Equal(0,hypothesis.RiskBudgetMultiplier);
    }

    [Fact]
    public void WatchingPullbackBecomesScoutWhenSupportHoldsAndMarketBehaviorImproves()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);

        var scout=TradeHypothesisEngine.EvaluateMarket(improved,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
        Assert.True(scout.Actionable);
        Assert.Equal(.18,scout.RiskBudgetMultiplier,10);
        Assert.True(scout.Revision>watching.Revision);
    }

    [Fact]
    public void RealtimeBuyFlowCanUnblockScoutWhenDepthBookIsStillHostile()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.75,flowAvailable:true,flow:.40);

        var scout=TradeHypothesisEngine.EvaluateMarket(improved,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
        Assert.True(scout.Actionable);
        Assert.Contains("order_flow_5m=0.400",scout.Evidence);
    }

    [Fact]
    public void MissingDepthDoesNotCountAsNeutralMicrostructure()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var improvedButDepthMissing=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,0,bookAvailable:false);

        var stillWatching=TradeHypothesisEngine.EvaluateMarket(improvedButDepthMissing,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.Watching,stillWatching.Stage);
        Assert.False(stillWatching.Actionable);
        Assert.Contains("order_book=unavailable",stillWatching.Evidence);
    }

    [Fact]
    public void PersistentlySupportiveOrderBookCanConfirmScoutWithoutNeedingImpossibleFurtherImprovement()
    {
        var first=Market(81117.2m,80906m,81715.8m,26.5,-.34,-.23,5.13,.99);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var next=Market(81120m,80906m,81715.8m,27,-.33,-.22,5.10,.82);

        var scout=TradeHypothesisEngine.EvaluateMarket(next,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
        Assert.True(scout.Actionable);
        Assert.Equal(.18,scout.RiskBudgetMultiplier,10);
    }

    [Fact]
    public void ScoutFallsBackToWatchingWhenMicrostructureTurnsHostileBeforeEntry()
    {
        var first=Market(81117.2m,80906m,81715.8m,26.5,-.34,-.23,5.13,.99);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81120m,80906m,81715.8m,27,-.33,-.22,5.10,.82),
            watching,[],Now.AddMinutes(1));

        var weakened=TradeHypothesisEngine.EvaluateMarket(
            Market(81097.6m,80906m,81715.8m,27,-.34,-.22,5.08,-.22),
            scout,[],Now.AddMinutes(2));

        Assert.Equal(TradeHypothesisStage.Watching,weakened.Stage);
        Assert.False(weakened.Actionable);
        Assert.Equal(0,weakened.RiskBudgetMultiplier);
    }

    [Fact]
    public void ConfirmedFallsBackToScoutWhenMomentumConfirmationFadesBeforeEntry()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08),
            watching,[],Now.AddMinutes(1));
        var confirmed=TradeHypothesisEngine.EvaluateMarket(
            Market(81180m,80906m,81805.3m,39,.08,.03,4.95,.22),
            scout,[],Now.AddMinutes(2));

        var weakened=TradeHypothesisEngine.EvaluateMarket(
            Market(81145m,80906m,81805.3m,35,-.05,-.02,4.90,.25),
            confirmed,[],Now.AddMinutes(3));

        Assert.Equal(TradeHypothesisStage.ScoutReady,weakened.Stage);
        Assert.True(weakened.Actionable);
        Assert.Equal(.18,weakened.RiskBudgetMultiplier,10);
    }

    [Fact]
    public async Task ScoutCanBecomeConfirmedButExistingPositionPreventsRepeatedEntryDecision()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08),
            watching,[],Now.AddMinutes(1));
        var confirmedMarket=Market(81180m,80906m,81805.3m,39,.08,.03,4.95,.22);
        var confirmed=TradeHypothesisEngine.EvaluateMarket(confirmedMarket,scout,[],Now.AddMinutes(2));

        Assert.Equal(TradeHypothesisStage.Confirmed,confirmed.Stage);
        Assert.Equal(.45,confirmed.RiskBudgetMultiplier,10);

        var evidence=Evidence(confirmedMarket,
        [
            new("BTCUSDT",PositionSide.Long,.001m,81100m,81180m,0,5,true,75000m)
        ]);
        var assessment=Assessment("BTCUSDT",entryReady:false,netScore:-.35,confidence:.25);
        var context=new AgentContext(
            "WPE Local Brain",false,"BTCUSDT",[],[assessment],0,null,null,
            new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=confirmed
            });

        var decision=(await new DeterministicBrainProvider().DecideAsync(evidence,context,CancellationToken.None)).Decision;

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Equal(TradeHypothesisEngine.DecisionContextKind,decision.DecisionContextKind);
        Assert.Equal(confirmed.Id,decision.DecisionContextId);
    }

    [Fact]
    public async Task HypothesisStatePersistsAcrossEngineInstancesAndContinuesTheSameThesis()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wpe-hypothesis-{Guid.NewGuid():N}.db");
        try
        {
            var firstStore=new AgentSqliteStore(path);
            var firstMarket=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
            var firstPlan=await new TradeHypothesisEngine(firstStore).EvaluateAsync(Evidence(firstMarket),CancellationToken.None);
            var watching=firstPlan["BTCUSDT"];
            Assert.Equal(TradeHypothesisStage.Watching,watching.Stage);

            var secondStore=new AgentSqliteStore(path);
            var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
            var secondPlan=await new TradeHypothesisEngine(secondStore).EvaluateAsync(Evidence(improved),CancellationToken.None);
            var scout=secondPlan["BTCUSDT"];

            Assert.Equal(watching.Id,scout.Id);
            Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
            Assert.True(scout.Revision>watching.Revision);
            Assert.NotNull(await secondStore.GetStateAsync("trade-hypothesis:last-plan",CancellationToken.None));
        }
        finally
        {
            foreach(var file in Directory.GetFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+"*"))
                try{File.Delete(file);}catch{}
        }
    }

    [Fact]
    public void SupportBreakInvalidatesLongHypothesisEvenWhenLegacyScoreWouldStillLookBullish()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var broken=Market(watching.InvalidationPrice-20m,80906m,81805.3m,24,-.8,-.5,4.2,-.7);

        var invalidated=TradeHypothesisEngine.EvaluateMarket(broken,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.Invalidated,invalidated.Stage);
        Assert.False(invalidated.Actionable);
        Assert.False(invalidated.IsLive);
        Assert.Contains("invalidated",invalidated.Thesis,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidatedHypothesisCannotImmediatelyRearmIntoTheSameTradeIdea()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var broken=Market(watching.InvalidationPrice-20m,80906m,81805.3m,24,-.8,-.5,4.2,-.7);
        var invalidated=TradeHypothesisEngine.EvaluateMarket(broken,watching,[],Now.AddMinutes(1));
        var recovered=Market(81080m,80906m,81805.3m,30,-.25,-.10,4.5,-.25);

        var stillInvalid=TradeHypothesisEngine.EvaluateMarket(
            recovered,
            invalidated,
            [],
            invalidated.UpdatedAtUtc.Add(TradeHypothesisEngine.InvalidatedHypothesisCooldown).AddSeconds(-1));

        Assert.Equal(invalidated.Id,stillInvalid.Id);
        Assert.Equal(TradeHypothesisStage.Invalidated,stillInvalid.Stage);
        Assert.False(stillInvalid.Actionable);
    }

    [Fact]
    public void InvalidatedHypothesisCanFormANewIdeaAfterCooldownWhenStructureStillSupportsIt()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var broken=Market(watching.InvalidationPrice-20m,80906m,81805.3m,24,-.8,-.5,4.2,-.7);
        var invalidated=TradeHypothesisEngine.EvaluateMarket(broken,watching,[],Now.AddMinutes(1));
        var recovered=Market(81080m,80906m,81805.3m,30,-.25,-.10,4.5,-.25);

        var rearmed=TradeHypothesisEngine.EvaluateMarket(
            recovered,
            invalidated,
            [],
            invalidated.UpdatedAtUtc.Add(TradeHypothesisEngine.InvalidatedHypothesisCooldown).AddSeconds(1));

        Assert.NotEqual(invalidated.Id,rearmed.Id);
        Assert.Equal(TradeHypothesisKind.TrendPullbackLong,rearmed.Kind);
        Assert.Equal(TradeHypothesisStage.Watching,rearmed.Stage);
        Assert.False(rearmed.Actionable);
    }

    [Fact]
    public async Task BrainActsOnScoutHypothesisEvenWhenLegacyAggregationIsNotEntryReady()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var current=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
        var scout=TradeHypothesisEngine.EvaluateMarket(current,watching,[],Now.AddMinutes(1));
        var assessment=Assessment("BTCUSDT",entryReady:false,netScore:-.42,confidence:.30);
        var context=new AgentContext(
            "WPE Local Brain",false,null,[],[assessment],0,null,null,
            new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=scout
            });

        var result=await new DeterministicBrainProvider().DecideAsync(Evidence(current),context,CancellationToken.None);

        Assert.Equal(DecisionAction.OpenLong,result.Decision.Action);
        Assert.Equal(1,result.Decision.TargetTier);
        Assert.Equal(0,result.Decision.Confidence);
        Assert.Equal(.18,result.Decision.RiskBudgetMultiplier,10);
        Assert.Equal(TradeHypothesisEngine.DecisionContextKind,result.Decision.DecisionContextKind);
        Assert.Equal(scout.Id,result.Decision.DecisionContextId);
        Assert.Equal(TradeHypothesisStage.ScoutReady.ToString(),result.Decision.HypothesisStage);
    }

    [Fact]
    public void ReviewerUsesActionableHypothesisInsteadOfLegacyScoreThresholds()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var market=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
        var scout=TradeHypothesisEngine.EvaluateMarket(market,watching,[],Now.AddMinutes(1));
        var assessment=Assessment("BTCUSDT",entryReady:false,netScore:-.42,confidence:.30);
        var decision=new DecisionPlan
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
        var hypotheses=new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"]=scout
        };

        var review=new DecisionGovernanceSkill().Review(
            decision,[assessment],Evidence(market),new DecisionPolicy(),hypotheses);

        Assert.True(review.Accepted);
        Assert.Equal(DecisionAction.OpenLong,review.Decision.Action);
        Assert.Empty(review.BlockingReasons);
    }

    [Fact]
    public void ReviewerStillRejectsLegacyLowConfidenceEntry()
    {
        var market=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
        var assessment=Assessment("BTCUSDT",entryReady:false,netScore:.40,confidence:.30);
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            Confidence=.30,
            DecisionContextKind="legacy-signal"
        };

        var review=new DecisionGovernanceSkill().Review(
            decision,[assessment],Evidence(market),new DecisionPolicy());

        Assert.False(review.Accepted);
        Assert.Equal(DecisionAction.Hold,review.Decision.Action);
    }

    [Fact]
    public void IndependentRiskTreatsHypothesisResearchAsContextNotEntryPermission()
    {
        var market=Market(100m,98m,106m,40,-.10,-.05,1.2,.1) with { CollectedAt=DateTime.UtcNow };
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            EntryPrice=100m,
            StopLossPrice=98m,
            TakeProfitPrice=104m,
            RiskRewardRatio=2,
            DecisionContextKind=TradeHypothesisEngine.DecisionContextKind,
            DecisionContextId="HYP-BTCUSDT-TEST",
            HypothesisStage=TradeHypothesisStage.ScoutReady.ToString(),
            RiskBudgetMultiplier=.18
        };
        var intent=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,.1m,false,98m,104m,"test-intent","hypothesis scout",
            DecisionAction.OpenLong,ExpectedPrice:100m);
        var portfolio=new PortfolioRiskAssessment
        {
            Approved=true,
            GrossExposure=10m,
            NetExposure=10m,
            Summary="approved"
        };
        var limits=new RiskLimits
        {
            MinimumLiquidityScore=.5,
            MaximumSpreadBps=8,
            MaxAtrPercent=.05,
            MinimumRiskReward=1.8,
            MaxConsecutiveLosses=3,
            MaxDailyLoss=.05m,
            ApiFailureThreshold=3,
            MaxSymbolExposure=.25m,
            MaxAccountExposure=.5m,
            MaxRiskPerTrade=.01m
        };

        var review=new IndependentRiskManagerSkill().Review(
            decision,Evidence(market),Assessment("BTCUSDT",false,-.4,.2),[intent],limits,
            new RiskHistorySnapshot(0,0,0,false),research:null,portfolio);

        Assert.True(review.Approved);
        Assert.Contains("market_hypothesis_context",review.Checks);
        Assert.DoesNotContain(review.BlockingReasons,x=>x.Contains("research",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelOffSafetyGateStillBlocksRiskIncreaseUntilHypothesisAuditIsCanonical()
    {
        var intent=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,.1m,false,98m,104m,"test-intent","hypothesis scout",
            DecisionAction.OpenLong,ExpectedPrice:100m);

        var gated=AutoTradingAgent.ApplyModelOffProductionRiskIncreaseGate([intent],null);

        Assert.Empty(gated);
    }

    private static EvidencePack Evidence(MarketEvidence market,IReadOnlyList<ManagedPosition>? positions=null) =>
        new()
        {
            CollectedAt=market.CollectedAt,
            Completeness=100,
            Account=new(10_000m,10_000m,10_000m,market.CollectedAt),
            Positions=positions??[],
            Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
            {
                [market.Symbol]=market
            }
        };

    private static MarketDecisionAssessment Assessment(string symbol,bool entryReady,double netScore,double confidence) =>
        new()
        {
            Symbol=symbol,
            Regime=MarketRegime.Transition,
            NetScore=netScore,
            Confidence=confidence,
            ConflictRatio=.75,
            Fresh=true,
            EntryReady=entryReady,
            RecommendedAction=entryReady?(netScore>=0?DecisionAction.OpenLong:DecisionAction.OpenShort):DecisionAction.Hold,
            MissingConditions=entryReady?[]:["legacy aggregation threshold not met"],
            Summary="legacy aggregation is observation only"
        };

    private static MarketEvidence Market(
        decimal price,
        decimal support,
        decimal resistance,
        double rsi,
        double trend15m,
        double trend1h,
        double trend4h,
        double book,
        bool flowAvailable=false,
        double flow=0,
        bool bookAvailable=true) =>
        new(
            "BTCUSDT",
            price,
            support,
            resistance,
            rsi,
            trend15m,
            trend1h,
            trend4h,
            new DerivativesSnapshot(.00003m,400_000_000m,0,0,0,0,-.0005m),
            Now.UtcDateTime)
        {
            Quality=new()
            {
                BestBid=price-.5m,
                BestAsk=price+.5m,
                SpreadBps=1.2,
                OrderBookImbalance=book,
                OrderFlowAvailable=flowAvailable,
                OrderFlowImbalance=flow,
                AtrPercent=.0015,
                RealizedVolatility=.01,
                RelativeVolume=1.2,
                LiquidityScore=.95,
                QualityScore=97,
                Anomalies=bookAvailable?[]:["order_book_missing"]
            }
        };
}
