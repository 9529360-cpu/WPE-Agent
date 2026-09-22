using Microsoft.Data.Sqlite;
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
    public void OneFamilyLossNudgesRiskWithoutChangingScoutMarketOpinion()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08),
            watching,[],Now.AddMinutes(1));

        var adjusted=TradeHypothesisEngine.ApplyExecutionFeedback(
            scout,
            new HypothesisExecutionFeedback(1,-.003529,0,4d/9d));

        Assert.Equal(TradeHypothesisStage.ScoutReady,adjusted.Stage);
        Assert.True(adjusted.Actionable);
        Assert.InRange(adjusted.RiskBudgetMultiplier,.17,.18);
        Assert.Contains("family_trades=1",adjusted.Evidence);
        Assert.Contains("family_key=BTCUSDT:TrendPullbackLong",adjusted.Evidence);
    }

    [Fact]
    public void FamilyFeedbackScalesRiskMonotonicallyAndRemainsBounded()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08),
            watching,[],Now.AddMinutes(1));

        var negative=TradeHypothesisEngine.ApplyExecutionFeedback(
            scout,new HypothesisExecutionFeedback(20,-.01,.10,6d/28d));
        var positive=TradeHypothesisEngine.ApplyExecutionFeedback(
            scout,new HypothesisExecutionFeedback(20,.01,.90,22d/28d));

        Assert.Equal(TradeHypothesisStage.ScoutReady,negative.Stage);
        Assert.Equal(TradeHypothesisStage.ScoutReady,positive.Stage);
        Assert.True(negative.Actionable&&positive.Actionable);
        Assert.True(negative.RiskBudgetMultiplier<scout.RiskBudgetMultiplier);
        Assert.True(positive.RiskBudgetMultiplier>scout.RiskBudgetMultiplier);
        Assert.InRange(negative.RiskBudgetMultiplier,.18*.75,.18);
        Assert.InRange(positive.RiskBudgetMultiplier,.18,.18*1.15);
    }

    [Fact]
    public void FamilyExcursionFeedbackConservativelyDelaysTriggerAndReducesRisk()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08),
            watching,[],Now.AddMinutes(1));

        var adjusted=TradeHypothesisEngine.ApplyExecutionFeedback(
            scout,
            new HypothesisExecutionFeedback(
                8,-.002,.375,.4375,
                ExcursionTrades:8,
                AverageMae:-.02,
                AverageMfe:.005,
                StopLossRate:.75,
                TakeProfitRate:.125));

        Assert.Equal(TradeHypothesisStage.ScoutReady,adjusted.Stage);
        Assert.True(adjusted.RiskBudgetMultiplier<scout.RiskBudgetMultiplier);
        Assert.True(adjusted.TriggerPrice<scout.TriggerPrice);
        Assert.InRange((double)((scout.TriggerPrice-adjusted.TriggerPrice)/scout.TriggerPrice),0,.00301);
        Assert.Contains("family_excursion_trades=8",adjusted.Evidence);
        Assert.Contains(adjusted.Evidence,value=>value.StartsWith("family_trigger_shift=",StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsyncLearnsAcrossDynamicIdsWithinTheSameHypothesisFamily()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wpe-hypothesis-family-{Guid.NewGuid():N}.db");
        try
        {
            var store=new AgentSqliteStore(path);
            var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
            var watching=(await new TradeHypothesisEngine(store).EvaluateAsync(Evidence(first),CancellationToken.None))["BTCUSDT"];
            Assert.Equal(TradeHypothesisStage.Watching,watching.Stage);

            await InsertHypothesisOutcomeAsync(path,"HYP-BTCUSDT-TrendPullbackLong-20260919010000",-0.0035m,1);
            await InsertHypothesisOutcomeAsync(path,"HYP-BTCUSDT-RangeReversionLong-20260919010100",0.02m,2);

            var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
            var scout=(await new TradeHypothesisEngine(new AgentSqliteStore(path)).EvaluateAsync(Evidence(improved),CancellationToken.None))["BTCUSDT"];

            Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
            Assert.InRange(scout.RiskBudgetMultiplier,.17,.18);
            Assert.Contains("family_trades=1",scout.Evidence);
            Assert.Contains("family_avg_return=-0.350%",scout.Evidence);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach(var file in Directory.GetFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+"*"))
                try{File.Delete(file);}catch{}
        }
    }

    [Fact]
    public async Task EvaluateAsyncUsesPersistedExcursionAndExitFeedbackForFamily()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wpe-hypothesis-family-path-{Guid.NewGuid():N}.db");
        try
        {
            var store=new AgentSqliteStore(path);
            var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
            var watching=(await new TradeHypothesisEngine(store).EvaluateAsync(Evidence(first),CancellationToken.None))["BTCUSDT"];
            Assert.Equal(TradeHypothesisStage.Watching,watching.Stage);

            for(var index=1;index<=4;index++)
                await InsertHypothesisOutcomeAsync(
                    path,
                    $"HYP-BTCUSDT-TrendPullbackLong-20260919010{index}00",
                    -.002m,
                    index,
                    mae:-.02m,
                    mfe:.005m,
                    excursionBasis:"runtime-mark-observations",
                    exitReason:"protection.fill-reconciled.stop-loss");

            var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
            var scout=(await new TradeHypothesisEngine(new AgentSqliteStore(path)).EvaluateAsync(Evidence(improved),CancellationToken.None))["BTCUSDT"];

            Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
            Assert.Contains("family_excursion_trades=4",scout.Evidence);
            Assert.Contains("family_stop_loss_rate=100.0%",scout.Evidence);
            Assert.True(scout.TriggerPrice<watching.TriggerPrice || scout.TriggerPrice<improved.Price);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach(var file in Directory.GetFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+"*"))
                try{File.Delete(file);}catch{}
        }
    }

    [Fact]
    public async Task EvaluateAsyncSurfacesStructureAndPartialTakeProfitExitAttribution()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wpe-hypothesis-exit-attribution-{Guid.NewGuid():N}.db");
        try
        {
            var store=new AgentSqliteStore(path);
            var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
            var watching=(await new TradeHypothesisEngine(store).EvaluateAsync(Evidence(first),CancellationToken.None))["BTCUSDT"];
            Assert.Equal(TradeHypothesisStage.Watching,watching.Stage);

            for(var index=1;index<=4;index++)
                await InsertHypothesisOutcomeAsync(
                    path,
                    $"HYP-BTCUSDT-TrendPullbackLong-20260919020{index}00",
                    index<=2?-.001m:.004m,
                    index,
                    mae:-.01m,
                    mfe:.015m,
                    excursionBasis:"runtime-mark-observations",
                    exitReason:index<=2
                        ?PositionExitReasonCodes.StructureInvalidated
                        :PositionExitReasonCodes.PartialTakeProfit2R);

            var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08);
            var scout=(await new TradeHypothesisEngine(new AgentSqliteStore(path)).EvaluateAsync(Evidence(improved),CancellationToken.None))["BTCUSDT"];

            Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
            Assert.Contains("family_excursion_trades=4",scout.Evidence);
            Assert.Contains("family_take_profit_rate=50.0%",scout.Evidence);
            Assert.Contains("family_structure_invalidation_rate=50.0%",scout.Evidence);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach(var file in Directory.GetFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+"*"))
                try{File.Delete(file);}catch{}
        }
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
    public void ExtremeOpposingRealtimeFlowBlocksScoutEvenWhenDepthBookLooksSupportive()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,.40,flowAvailable:true,flow:-.30);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var improved=Market(81185.2m,80906m,81805.3m,51.3,.322,-.060,4.49,.861,flowAvailable:true,flow:-.997);

        var stillWatching=TradeHypothesisEngine.EvaluateMarket(improved,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.Watching,stillWatching.Stage);
        Assert.False(stillWatching.Actionable);
        Assert.Equal(0,stillWatching.RiskBudgetMultiplier);
        Assert.Contains("extreme opposing aggressive flow",stillWatching.Trigger,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModerateOpposingRealtimeFlowStillAllowsBoundedScoutExploration()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,.40,flowAvailable:true,flow:-.30);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var improved=Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,.75,flowAvailable:true,flow:-.45);

        var scout=TradeHypothesisEngine.EvaluateMarket(improved,watching,[],Now.AddMinutes(1));

        Assert.Equal(TradeHypothesisStage.ScoutReady,scout.Stage);
        Assert.True(scout.Actionable);
        Assert.Equal(.18,scout.RiskBudgetMultiplier,10);
    }

    [Fact]
    public void ScoutFallsBackToWatchingWhenAggressiveFlowTurnsExtremelyOpposed()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,.45),
            watching,[],Now.AddMinutes(1));

        var conflicted=TradeHypothesisEngine.EvaluateMarket(
            Market(81185m,80906m,81805.3m,51,.32,-.06,4.49,.86,flowAvailable:true,flow:-.99),
            scout,[],Now.AddMinutes(2));

        Assert.Equal(TradeHypothesisStage.Watching,conflicted.Stage);
        Assert.False(conflicted.Actionable);
        Assert.Equal(0,conflicted.RiskBudgetMultiplier);
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
    public void LegacyPersistedHypothesisWithoutBookAvailabilityDefaultsConservatively()
    {
        var current=TradeHypothesisEngine.EvaluateMarket(
            Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,.40),
            null,[],Now);
        Assert.True(current.LastOrderBookAvailable);

        var node=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(current))!.AsObject();
        Assert.True(node.Remove(nameof(TradeHypothesis.LastOrderBookAvailable)));
        var restored=System.Text.Json.JsonSerializer.Deserialize<TradeHypothesis>(node.ToJsonString());

        Assert.NotNull(restored);
        Assert.False(restored!.LastOrderBookAvailable);
    }

    [Fact]
    public void RecoveredDepthDoesNotPretendUnknownBaselineImproved()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,0,bookAvailable:false);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var depthRecovered=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,.25);

        var stillWatching=TradeHypothesisEngine.EvaluateMarket(depthRecovered,watching,[],Now.AddMinutes(1));

        Assert.False(watching.LastOrderBookAvailable);
        Assert.Equal(TradeHypothesisStage.Watching,stillWatching.Stage);
        Assert.True(stillWatching.LastOrderBookAvailable);
        Assert.False(stillWatching.Actionable);
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
    public async Task ExistingHypothesisCannotOverrideDirectCandleDecisionAuthority()
    {
        var first=Market(81075.2m,80906m,81805.3m,27.3,-.376,-.225,5.13,-.82);
        var watching=TradeHypothesisEngine.EvaluateMarket(first,null,[],Now);
        var scout=TradeHypothesisEngine.EvaluateMarket(
            Market(81105m,80906m,81805.3m,34,-.18,-.12,5.05,-.08),
            watching,[],Now.AddMinutes(1));
        var confirmedMarket=Market(81180m,80906m,81805.3m,39,.08,.03,4.95,.22);
        var confirmed=TradeHypothesisEngine.EvaluateMarket(confirmedMarket,scout,[],Now.AddMinutes(2));
        var evidence=Evidence(confirmedMarket,
        [
            new("BTCUSDT",PositionSide.Long,.001m,81100m,81180m,0,5,true,75000m)
        ]);
        var context=new AgentContext(
            "WPE Local Brain",false,"BTCUSDT",[],[],0,null,null,
            new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=confirmed
            });

        var decision=(await new DeterministicBrainProvider().DecideAsync(evidence,context,CancellationToken.None)).Decision;

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Equal("market-observation",decision.DecisionContextKind);
        Assert.Empty(decision.DecisionContextId);
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
    public async Task ScoutHypothesisCannotForceTradeWithoutDirectCandleTrigger()
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

        Assert.Equal(DecisionAction.Hold,result.Decision.Action);
        Assert.Equal(0,result.Decision.TargetTier);
        Assert.Equal(0,result.Decision.Confidence);
        Assert.Equal(0,result.Decision.RiskBudgetMultiplier);
        Assert.Equal("market-observation",result.Decision.DecisionContextKind);
        Assert.Empty(result.Decision.DecisionContextId);
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
    public void IndependentRiskKeepsHardSafetyButDoesNotUseScoresOrResearchAsDirectEntryPermission()
    {
        var market=Market(100m,98m,106m,40,-.10,-.05,1.2,.1) with
        {
            CollectedAt=DateTime.UtcNow,
            Quality=new()
            {
                BestBid=99.99m,
                BestAsk=100.01m,
                SpreadBps=2,
                AtrPercent=.01,
                LiquidityScore=.95,
                QualityScore=1
            }
        };
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            EntryPrice=100m,
            StopLossPrice=98m,
            TakeProfitPrice=104m,
            RiskRewardRatio=2,
            DecisionContextKind=DirectMarketStructureDecisionSkill.DecisionContextKind,
            DecisionContextId="DIRECT-BTCUSDT-TEST",
            StrategyVersion=DirectMarketStructureDecisionSkill.Version,
            RiskBudgetMultiplier=1
        };
        var intent=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,.1m,false,98m,104m,"test-intent","direct structure",
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
            decision,Evidence(market),assessment:null,[intent],limits,
            new RiskHistorySnapshot(0,0,0,false),research:null,portfolio);

        Assert.True(review.Approved);
        Assert.Contains("direct_market_structure_context",review.Checks);
        Assert.DoesNotContain("market_quality",review.Checks);
        Assert.DoesNotContain(review.BlockingReasons,x=>x.Contains("research",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelOffShadowCannotBlockDirectExecutionAuthority()
    {
        var intent=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,.1m,false,98m,104m,"test-intent","direct structure",
            DecisionAction.OpenLong,ExpectedPrice:100m);

        var gated=AutoTradingAgent.ApplyModelOffProductionRiskIncreaseGate([intent],null);

        Assert.Equal(new[] { intent },gated);
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

    private static async Task InsertHypothesisOutcomeAsync(
        string databasePath,
        string strategyId,
        decimal returnPct,
        int index,
        decimal mae=0,
        decimal mfe=0,
        string excursionBasis="unavailable",
        string exitReason="unclassified")
    {
        await using var connection=new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText=@"INSERT INTO trade_outcomes(
client_order_id,cycle_id,symbol,side,net_pnl,return_pct,mae_return_pct,mfe_return_pct,excursion_basis,excursion_samples,exit_reason,closed_at,strategy_id,strategy_version,attribution_basis)
VALUES($client,$cycle,'BTCUSDT','Long',$pnl,$return,$mae,$mfe,$basis,$samples,$exit,$closed,$strategy,$version,'automatic-artifact')";
        command.Parameters.AddWithValue("$client",$"family-feedback-{index}");
        command.Parameters.AddWithValue("$cycle",$"cycle-{index}");
        command.Parameters.AddWithValue("$pnl",(returnPct*100m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$return",returnPct.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$mae",mae.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$mfe",mfe.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$basis",excursionBasis);
        command.Parameters.AddWithValue("$samples",excursionBasis=="runtime-mark-observations"?2:0);
        command.Parameters.AddWithValue("$exit",exitReason);
        command.Parameters.AddWithValue("$closed",Now.AddMinutes(index).ToString("O"));
        command.Parameters.AddWithValue("$strategy",strategyId);
        command.Parameters.AddWithValue("$version",TradeHypothesis.CurrentVersion);
        await command.ExecuteNonQueryAsync();
    }

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
