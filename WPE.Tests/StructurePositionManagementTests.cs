using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StructurePositionManagementTests
{
    private static readonly DateTimeOffset Now=new(2026,9,20,9,0,0,TimeSpan.Zero);

    [Fact]
    public async Task LiquidationBufferExitCarriesStableReasonCode()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(),"PROTECTED","1003",CancellationToken.None);
            var position=Position() with{LiquidationPrice=95m};
            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(100m)}},
                db,
                CancellationToken.None,
                ManagedLedger());

            var intent=Assert.Single(result.Intents);
            Assert.Equal(DecisionAction.CloseLong,intent.Action);
            Assert.Equal(PositionExitReasonCodes.LiquidationBuffer,intent.ReasonCode);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task NormalLeveragedPositionDoesNotExitWhenProtectiveStopIsSafelyAheadOfLiquidation()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(), "PROTECTED", "1003-safe", CancellationToken.None);
            var position=Position() with{LiquidationPrice=80m};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(100m)}},
                db,
                CancellationToken.None,
                ManagedLedger());

            Assert.Empty(result.Intents);
            Assert.DoesNotContain(result.Notes,x=>x.StartsWith("liquidation-safety-compromised:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ConfirmedScenarioFlipClosesManagedLongWithStateReason()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-state-flip"),"PROTECTED","state-open-1",CancellationToken.None);
            var market=Market(100m);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.ScenarioFlip,
                    MarketStructureBias.Range,
                    MarketStructureScenario.TrendPullbackShort,
                    trigger:true,
                    confirmation:true)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules(),
                marketStates:states);

            var intent=Assert.Single(result.Intents);
            Assert.True(intent.ReduceOnly);
            Assert.Equal(DecisionAction.CloseLong,intent.Action);
            Assert.Equal(PositionExitReasonCodes.MarketStateReversal,intent.ReasonCode);
            Assert.StartsWith("WPE-PM-STATE-",intent.ClientOrderId,StringComparison.Ordinal);
            Assert.Contains(result.Notes,x=>x.StartsWith("market-state-reversal:",StringComparison.Ordinal));
            Assert.Empty(result.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task UnconfirmedScenarioFlipCannotCloseManagedPosition()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-state-unconfirmed"),"PROTECTED","state-open-2",CancellationToken.None);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.ScenarioFlip,
                    MarketStructureBias.Range,
                    MarketStructureScenario.TrendPullbackShort,
                    trigger:true,
                    confirmation:false)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(100m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules(),
                marketStates:states);

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.DoesNotContain(result.Notes,x=>x.StartsWith("market-state-reversal:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task StaleMarketStateCannotCloseManagedPosition()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-state-stale"),"PROTECTED","state-open-3",CancellationToken.None);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.ScenarioFlip,
                    MarketStructureBias.Range,
                    MarketStructureScenario.TrendPullbackShort,
                    trigger:true,
                    confirmation:true,
                    observedAt:Now.AddMinutes(-15).UtcDateTime)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(100m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules(),
                marketStates:states);

            Assert.Empty(result.Intents);
            Assert.DoesNotContain(result.Notes,x=>x.StartsWith("market-state-reversal:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task StableOpposingStateDoesNotReplayAStateExit()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-state-stable"),"PROTECTED","state-open-4",CancellationToken.None);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.Stable,
                    MarketStructureBias.Range,
                    MarketStructureScenario.TrendPullbackShort,
                    trigger:true,
                    confirmation:true,
                    observations:4)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(100m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules(),
                marketStates:states);

            Assert.Empty(result.Intents);
            Assert.DoesNotContain(result.Notes,x=>x.StartsWith("market-state-reversal:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TwoRPartialTakeCarriesStableReasonCode()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(),"PROTECTED","1004",CancellationToken.None);
            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            var intent=Assert.Single(result.Intents);
            Assert.Equal(DecisionAction.ReduceLong,intent.Action);
            Assert.Equal(.5m,intent.Quantity);
            Assert.Equal(PositionExitReasonCodes.PartialTakeProfit2R,intent.ReasonCode);
            Assert.Empty(result.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TwoRPartialTakeRoundsDownToExchangeStep()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(),"PROTECTED","1004-step",CancellationToken.None);
            var position=Position() with{Quantity=1.1m};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}},
                db,
                CancellationToken.None,
                ManagedLedger(1.1m),
                tradingRules:Rules(.3m,.3m));

            var intent=Assert.Single(result.Intents);
            Assert.Equal(.3m,intent.Quantity);
            Assert.Equal(DecisionAction.ReduceLong,intent.Action);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TwoRPartialTakeTooSmallForExchangeRuleFallsBackToBreakeven()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(),"PROTECTED","1004-small",CancellationToken.None);
            var position=Position() with{Quantity=.3m};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}},
                db,
                CancellationToken.None,
                ManagedLedger(.3m),
                tradingRules:Rules(.2m,.2m));

            Assert.Empty(result.Intents);
            Assert.Single(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.StartsWith("partial-2r-quantity-unavailable:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TwoRPartialTakeDoesNotReplayAfterDurableAttemptForSameOpening()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-partial-a"),"PROTECTED","1005",CancellationToken.None);
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}};

            var first=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());
            var partial=Assert.Single(first.Intents);
            Assert.StartsWith("WPE-PM-TP2-",partial.ClientOrderId,StringComparison.Ordinal);
            await db.SaveIntentAsync("cycle-partial",partial,"COMPLETED_PARTIAL","2005",CancellationToken.None);

            var second=await new PositionManagementSkill().EvaluateAsync([Position() with{Quantity=.5m}],market,db,CancellationToken.None,ManagedLedger(.5m),tradingRules:Rules());
            Assert.DoesNotContain(second.Intents,value=>value.ReasonCode==PositionExitReasonCodes.PartialTakeProfit2R);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task FailedLaterOpeningDoesNotReplaceManagedOpeningIdentity()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open-a",OpeningIntent("open-partial-a"),"PROTECTED","1006",CancellationToken.None);
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}};

            var first=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());
            var firstPartial=Assert.Single(first.Intents);
            await db.SaveIntentAsync("cycle-partial-a",firstPartial,"COMPLETED_PARTIAL","2006",CancellationToken.None);

            await Task.Delay(20);
            await db.SaveIntentAsync("cycle-open-rejected",OpeningIntent("open-partial-rejected"),"REJECTED",null,CancellationToken.None);

            var second=await new PositionManagementSkill().EvaluateAsync([Position() with{Quantity=.5m}],market,db,CancellationToken.None,ManagedLedger(.5m),tradingRules:Rules());

            Assert.DoesNotContain(second.Intents,value=>value.ReasonCode==PositionExitReasonCodes.PartialTakeProfit2R);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ReopenedSameSideGetsFreshTwoRActionIdentity()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open-a",OpeningIntent("open-partial-a"),"PROTECTED","1006",CancellationToken.None);
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}};

            var first=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());
            var firstPartial=Assert.Single(first.Intents);
            await db.SaveIntentAsync("cycle-partial-a",firstPartial,"COMPLETED_PARTIAL","2006",CancellationToken.None);
            await Task.Delay(20);
            await db.SaveIntentAsync("cycle-open-b",OpeningIntent("open-partial-b"),"PROTECTED","1007",CancellationToken.None);

            var reopened=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());
            var secondPartial=Assert.Single(reopened.Intents);
            Assert.Equal(PositionExitReasonCodes.PartialTakeProfit2R,secondPartial.ReasonCode);
            Assert.NotEqual(firstPartial.ClientOrderId,secondPartial.ClientOrderId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task BreakevenProtectionIsOpeningScopedAndStopsAfterDurableState()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-breakeven-a"),"PROTECTED","1008",CancellationToken.None);
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}};

            var first=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());
            Assert.Empty(first.Intents);
            var adjustment=Assert.Single(first.ProtectionAdjustments);
            Assert.NotNull(adjustment.AdjustmentId);
            Assert.StartsWith("WPE-PM-BE-",adjustment.AdjustmentId!,StringComparison.Ordinal);
            Assert.Equal(100.1m,adjustment.StopLoss);
            Assert.Equal(120m,adjustment.TakeProfit);

            await db.SetStateAsync(
                PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
                "COMPLETED",
                CancellationToken.None);

            var second=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());
            Assert.Empty(second.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task NormalPullbackDefersRunnerProfitLock()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-normal-pullback") with{TakeProfit=140m};
            await SeedCompletedPartialAndBreakeven(db,opening,"pullback");
            var market=Market(125m,105m,140m);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.Stable,
                    MarketStructureBias.Bullish,
                    MarketStructureScenario.TrendPullbackLong,
                    trigger:true,
                    confirmation:true,
                    observations:4,
                    phaseOverride:MarketStructurePhase.BullishPullback,
                    support:112m,
                    resistance:140m)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=125m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}},
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules(),
                marketStates:states);

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.Contains(":NormalPullback:",StringComparison.Ordinal));
            Assert.Contains(result.Notes,x=>x.StartsWith("structure-profit-lock-deferred-normal-pullback:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ProfitExpansionLocksRunnerBehindPersistentStructuralAnchor()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-profit-expansion") with{TakeProfit=140m};
            await SeedCompletedPartialAndBreakeven(db,opening,"expansion");
            var market=Market(125m,105m,140m);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.EventChanged,
                    MarketStructureBias.Bullish,
                    MarketStructureScenario.BreakoutRetestLong,
                    trigger:true,
                    confirmation:true,
                    observations:5,
                    phaseOverride:MarketStructurePhase.BullishImpulse,
                    support:118m,
                    resistance:140m,
                    eventOverride:MarketStructureEvent.BullishBreak)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=125m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}},
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules(),
                marketStates:states);

            Assert.Empty(result.Intents);
            var adjustment=Assert.Single(result.ProtectionAdjustments);
            Assert.Equal(117m,adjustment.StopLoss);
            Assert.Equal(140m,adjustment.TakeProfit);
            Assert.Contains(result.Notes,x=>x.Contains(":ProfitExpansion:",StringComparison.Ordinal));
            Assert.Contains(result.Notes,x=>x.Contains("level=118",StringComparison.Ordinal)&&x.Contains("lifecycle=ProfitExpansion",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ExhaustionRiskTightensProtectionWithoutForcingExit()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-exhaustion-risk") with{TakeProfit=140m};
            await SeedCompletedPartialAndBreakeven(db,opening,"exhaustion");
            var market=Market(125m,105m,140m);
            var states=new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=MarketState(
                    MarketStateTransitionKindV1.EventChanged,
                    MarketStructureBias.Bullish,
                    MarketStructureScenario.None,
                    trigger:false,
                    confirmation:false,
                    observations:5,
                    phaseOverride:MarketStructurePhase.BearishReversalAttempt,
                    support:116m,
                    resistance:140m,
                    eventOverride:MarketStructureEvent.BearishRejection)
            };

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=125m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}},
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules(),
                marketStates:states);

            Assert.Empty(result.Intents);
            var adjustment=Assert.Single(result.ProtectionAdjustments);
            Assert.Equal(115m,adjustment.StopLoss);
            Assert.Contains(result.Notes,x=>x.Contains(":ExhaustionRisk:",StringComparison.Ordinal));
            Assert.Contains(result.Notes,x=>x.Contains("lifecycle=ExhaustionRisk",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task DynamicStructureRatchetCanTightenAcrossNewMarketStatesButNeverLoosen()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-dynamic-ratchet") with{TakeProfit=140m};
            await SeedCompletedPartialAndBreakeven(db,opening,"dynamic-ratchet");

            var firstAt=Now.UtcDateTime;
            var firstMarket=Market(125m,105m,140m,firstAt);
            var firstState=MarketState(
                MarketStateTransitionKindV1.EventChanged,
                MarketStructureBias.Bullish,
                MarketStructureScenario.BreakoutRetestLong,
                trigger:true,
                confirmation:true,
                observedAt:firstAt,
                observations:5,
                phaseOverride:MarketStructurePhase.BullishImpulse,
                support:118m,
                resistance:140m,
                eventOverride:MarketStructureEvent.BullishBreak);
            var first=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=125m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",firstMarket}},
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules(),
                marketStates:new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",firstState}});

            var firstAdjustment=Assert.Single(first.ProtectionAdjustments);
            Assert.StartsWith("WPE-PM-RT-",firstAdjustment.AdjustmentId,StringComparison.Ordinal);
            Assert.Equal(117m,firstAdjustment.StopLoss);
            await PersistCompletedProtection(db,opening,firstAdjustment);

            var secondAt=firstAt.AddMinutes(15);
            var secondMarket=Market(130m,108m,140m,secondAt);
            var secondState=MarketState(
                MarketStateTransitionKindV1.EventChanged,
                MarketStructureBias.Bullish,
                MarketStructureScenario.BreakoutRetestLong,
                trigger:true,
                confirmation:true,
                observedAt:secondAt,
                observations:6,
                phaseOverride:MarketStructurePhase.BullishImpulse,
                support:123m,
                resistance:140m,
                eventOverride:MarketStructureEvent.BullishDisplacement);
            var second=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=130m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",secondMarket}},
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules(),
                marketStates:new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",secondState}});

            var secondAdjustment=Assert.Single(second.ProtectionAdjustments);
            Assert.StartsWith("WPE-PM-RT-",secondAdjustment.AdjustmentId,StringComparison.Ordinal);
            Assert.NotEqual(firstAdjustment.AdjustmentId,secondAdjustment.AdjustmentId);
            Assert.Equal(122m,secondAdjustment.StopLoss);
            Assert.True(secondAdjustment.StopLoss>firstAdjustment.StopLoss);
            await PersistCompletedProtection(db,opening,secondAdjustment);

            var thirdAt=secondAt.AddMinutes(15);
            var thirdMarket=Market(132m,110m,140m,thirdAt);
            var thirdState=MarketState(
                MarketStateTransitionKindV1.EventChanged,
                MarketStructureBias.Bullish,
                MarketStructureScenario.BreakoutRetestLong,
                trigger:true,
                confirmation:true,
                observedAt:thirdAt,
                observations:7,
                phaseOverride:MarketStructurePhase.BullishImpulse,
                support:120m,
                resistance:140m,
                eventOverride:MarketStructureEvent.BullishBreak);
            var third=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=132m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",thirdMarket}},
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules(),
                marketStates:new Dictionary<string,MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",thirdState}});

            Assert.Empty(third.Intents);
            Assert.Empty(third.ProtectionAdjustments);
            Assert.Contains(third.Notes,x=>x.StartsWith("structure-profit-lock-waiting:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("FAILED")]
    public async Task LatestDynamicProtectionMutationFailsSafeAfterRestart(string state)
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-ratchet-recovery");
            await db.SaveIntentAsync("cycle-open-ratchet",opening,"PROTECTED","ratchet-opening",CancellationToken.None);
            const string adjustmentId="WPE-PM-RT-recovery-state";
            await db.SetStatePairAsync(
                PositionManagementDurableState.ProtectionAdjustmentKey(adjustmentId),
                state,
                PositionManagementDurableState.LatestProtectionAdjustmentKey(opening.ClientOrderId),
                adjustmentId,
                CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            var close=Assert.Single(result.Intents);
            Assert.True(close.ReduceOnly);
            Assert.Equal(PositionExitReasonCodes.ProtectionReplaceFailed,close.ReasonCode);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.EndsWith(":"+state,StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task MissingStateBehindLatestProtectionPointerFailsSafe()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-ratchet-corrupt-pointer");
            await db.SaveIntentAsync("cycle-open-ratchet-corrupt",opening,"PROTECTED","ratchet-opening-corrupt",CancellationToken.None);
            await db.SetStateAsync(
                PositionManagementDurableState.LatestProtectionAdjustmentKey(opening.ClientOrderId),
                "WPE-PM-RT-missing-state",
                CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            var close=Assert.Single(result.Intents);
            Assert.Equal(PositionExitReasonCodes.ProtectionReplaceFailed,close.ReasonCode);
            Assert.Contains(result.Notes,x=>x.EndsWith(":UNKNOWN",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task CompletedPartialAndBreakevenCanLockRunnerBehindFreshFifteenMinuteSupport()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-runner-lock") with{TakeProfit=140m};
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1008-lock",CancellationToken.None);

            var first=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BTCUSDT"]=Market(121m,95m,140m)
                },
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());
            var partial=Assert.Single(first.Intents);
            Assert.Equal(PositionExitReasonCodes.PartialTakeProfit2R,partial.ReasonCode);
            await db.SaveIntentAsync("cycle-partial",partial,"COMPLETED","2008-lock",CancellationToken.None);

            var breakevenId=PositionManagementActionId("BE",opening.ClientOrderId);
            await db.SetStateAsync(
                PositionManagementDurableState.ProtectionAdjustmentKey(breakevenId),
                "COMPLETED",
                CancellationToken.None);

            var runner=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=125m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BTCUSDT"]=Market(125m,112m,140m)
                },
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules());

            Assert.Empty(runner.Intents);
            var adjustment=Assert.Single(runner.ProtectionAdjustments);
            Assert.StartsWith("WPE-PM-LOCK2R-",adjustment.AdjustmentId,StringComparison.Ordinal);
            Assert.Equal(111m,adjustment.StopLoss);
            Assert.Equal(140m,adjustment.TakeProfit);
            Assert.Contains(runner.Notes,x=>x.StartsWith("structure-profit-lock:",StringComparison.Ordinal));

            await db.SetStateAsync(
                PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
                "COMPLETED",
                CancellationToken.None);
            var replay=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=126m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BTCUSDT"]=Market(126m,105m,140m)
                },
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules());
            Assert.Empty(replay.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task UnresolvedRunnerProfitLockFailsSafeInsteadOfReplayingProtection()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-runner-pending") with{TakeProfit=140m};
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1008-pending",CancellationToken.None);
            var partialId=PositionManagementActionId("TP2",opening.ClientOrderId);
            var partial=new ExecutionIntent(
                "BTCUSDT",PositionSide.Long,.5m,true,0,0,partialId,"partial",
                DecisionAction.ReduceLong,ExpectedPrice:120m,
                ReasonCode:PositionExitReasonCodes.PartialTakeProfit2R);
            await db.SaveIntentAsync("cycle-partial",partial,"COMPLETED","2008-pending",CancellationToken.None);
            await db.SetStateAsync(
                PositionManagementDurableState.ProtectionAdjustmentKey(PositionManagementActionId("BE",opening.ClientOrderId)),
                "COMPLETED",
                CancellationToken.None);
            await db.SetStateAsync(
                PositionManagementDurableState.ProtectionAdjustmentKey(PositionManagementActionId("LOCK2R",opening.ClientOrderId)),
                "PENDING",
                CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position() with{Quantity=.5m,MarkPrice=125m}],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BTCUSDT"]=Market(125m,112m,140m)
                },
                db,
                CancellationToken.None,
                ManagedLedger(.5m),
                tradingRules:Rules());

            var close=Assert.Single(result.Intents);
            Assert.Equal(PositionExitReasonCodes.ProtectionReplaceFailed,close.ReasonCode);
            Assert.Empty(result.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("FAILED")]
    public async Task UnresolvedProtectionMutationStateClosesManagedPosition(string state)
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-protection-recovery");
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1008-recovery",CancellationToken.None);
            var protectionId=PositionManagementDurableState.ProtectionAdjustmentKey(
                PositionManagementActionId("BE",opening.ClientOrderId));
            await db.SetStateAsync(protectionId,state,CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            var intent=Assert.Single(result.Intents);
            Assert.True(intent.ReduceOnly);
            Assert.Equal(DecisionAction.CloseLong,intent.Action);
            Assert.Equal(Position().Quantity,intent.Quantity);
            Assert.Equal(PositionExitReasonCodes.ProtectionReplaceFailed,intent.ReasonCode);
            Assert.StartsWith("WPE-PM-PROTFAIL-",intent.ClientOrderId,StringComparison.Ordinal);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.EndsWith(":"+state,StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task UnresolvedProtectionMutationCloseDoesNotRequireStrategyMarketEvidence()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-protection-no-market");
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1008-no-market",CancellationToken.None);
            var protectionId=PositionManagementDurableState.ProtectionAdjustmentKey(
                PositionManagementActionId("BE",opening.ClientOrderId));
            await db.SetStateAsync(protectionId,"PENDING",CancellationToken.None);
            var position=Position() with{MarkPrice=107m};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase),
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            var intent=Assert.Single(result.Intents);
            Assert.Equal(DecisionAction.CloseLong,intent.Action);
            Assert.Equal(PositionExitReasonCodes.ProtectionReplaceFailed,intent.ReasonCode);
            Assert.Equal(107m,intent.ExpectedPrice);
            Assert.Empty(result.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task CanceledProtectionMutationDoesNotAutoCloseOnNextCycle()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-protection-canceled");
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1008-canceled",CancellationToken.None);
            var protectionId=PositionManagementDurableState.ProtectionAdjustmentKey(
                PositionManagementActionId("BE",opening.ClientOrderId));
            await db.SetStateAsync(protectionId,"CANCELED",CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.DoesNotContain(result.Notes,x=>x.StartsWith("protection-recovery-close:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ProtectionRecoveryCloseDoesNotReplayAfterDurableAttempt()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-protection-recovery-replay");
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1008-replay",CancellationToken.None);
            var protectionId=PositionManagementDurableState.ProtectionAdjustmentKey(
                PositionManagementActionId("BE",opening.ClientOrderId));
            await db.SetStateAsync(protectionId,"PENDING",CancellationToken.None);

            var first=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());
            var close=Assert.Single(first.Intents);
            await db.SaveIntentAsync("cycle-close",close,"COMPLETED","3001",CancellationToken.None);

            var second=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            Assert.Empty(second.Intents);
            Assert.Empty(second.ProtectionAdjustments);
            Assert.Contains(second.Notes,x=>x.StartsWith("protection-recovery-close:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task BreakevenProtectionUsesSaferOpeningAndFillAnchor()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-breakeven-slippage"),"PROTECTED","1009-slip",CancellationToken.None);
            var position=Position() with{EntryPrice=99m};
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(108m)}};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules());

            Assert.Empty(result.Intents);
            var adjustment=Assert.Single(result.ProtectionAdjustments);
            Assert.Equal(100.1m,adjustment.StopLoss);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task BreakevenProtectionSkipsWhenSaferTickWouldCrossMarket()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-breakeven-coarse"),"PROTECTED","1009",CancellationToken.None);
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(110m)}};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],market,db,CancellationToken.None,ManagedLedger(),tradingRules:Rules(tickSize:20m));

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.StartsWith("breakeven-price-unavailable:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task BreakevenProtectionSkipsWhenRoundedStopReachesTakeProfit()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent("open-breakeven-tp-boundary"),"PROTECTED","1010",CancellationToken.None);
            var position=Position() with{Quantity=.1m};
            var market=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(130m)}};

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],market,db,CancellationToken.None,ManagedLedger(.1m),
                tradingRules:Rules(step:.1m,minQuantity:.1m,tickSize:20m));

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.StartsWith("partial-2r-quantity-unavailable:",StringComparison.Ordinal));
            Assert.Contains(result.Notes,x=>x.StartsWith("breakeven-price-unavailable:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task HistoricalOpeningCannotManageExternalSameSidePosition()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-old-open",OpeningIntent("old-closed-opening"),"PROTECTED","old-order",CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}},
                db,
                CancellationToken.None,
                []);

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task MissingOwnershipCandidateCannotRecaptureReappearedSameQuantityPosition()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-uncertain-ownership");
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1011-candidate",CancellationToken.None);
            await db.SetStateAsync(
                PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId),
                Now.ToString("O"),
                CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.StartsWith("position-ownership-uncertain:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task RevokedOpeningCannotManageLaterSameQuantityPosition()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var opening=OpeningIntent("open-revoked-ownership");
            await db.SaveIntentAsync("cycle-open",opening,"PROTECTED","1011",CancellationToken.None);
            await db.SetStateAsync(
                PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId),
                "position.missing-on-exchange",
                CancellationToken.None);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(120m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                tradingRules:Rules());

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
            Assert.Contains(result.Notes,x=>x.StartsWith("position-ownership-revoked:",StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static IReadOnlyList<ExecutionPositionLegV1> ManagedLedger(decimal quantity=1m)=>
        [new("BTCUSDT",PositionSide.Long,quantity)];

    private static IReadOnlyDictionary<string,TradingRule> Rules(decimal step=.1m,decimal minQuantity=.1m,decimal tickSize=.1m)=>
        new Dictionary<string,TradingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"]=new("BTCUSDT",step,tickSize,minQuantity,5m,20)
        };

    private static string PositionManagementActionId(string action,string openingClientOrderId)
    {
        var raw=System.Text.Encoding.UTF8.GetBytes($"{openingClientOrderId}\u001f{action}");
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant();
        return $"WPE-PM-{action}-{hash[..20]}";
    }

    private static ExecutionIntent OpeningIntent(string clientOrderId="open-structure-1")=>new(
        "BTCUSDT",PositionSide.Long,1m,false,90m,120m,clientOrderId,"test opening",
        DecisionAction.OpenLong,ExpectedPrice:100m);

    private static ManagedPosition Position()=>new(
        "BTCUSDT",PositionSide.Long,1m,100m,100m,0m,2m,true,50m);

    private static MarketEvidence Market(decimal price,decimal support=95m,decimal resistance=110m,DateTime? observedAt=null)=>new(
        "BTCUSDT",price,support,resistance,50,0,0,0,
        new DerivativesSnapshot(.0001m,1_000_000m,1m,1m,1m,1m,0m),
        observedAt??Now.UtcDateTime);

    private static MarketStateSnapshotV1 MarketState(
        MarketStateTransitionKindV1 transition,
        MarketStructureBias bias,
        MarketStructureScenario scenario,
        bool trigger,
        bool confirmation,
        DateTime? observedAt=null,
        long observations=2,
        MarketStructurePhase? phaseOverride=null,
        decimal support=95m,
        decimal resistance=110m,
        MarketStructureEvent? eventOverride=null)
    {
        var at=observedAt??Now.UtcDateTime;
        var phase=phaseOverride??(scenario is MarketStructureScenario.TrendPullbackShort or MarketStructureScenario.BreakoutRetestShort
            ?MarketStructurePhase.BearishPullback
            :scenario is MarketStructureScenario.TrendPullbackLong or MarketStructureScenario.BreakoutRetestLong
                ?MarketStructurePhase.BullishPullback
                :MarketStructurePhase.Balance);
        var lifecycle=confirmation&&trigger?MarketStateLifecycleV1.Confirmed:trigger?MarketStateLifecycleV1.Triggered:MarketStateLifecycleV1.Developing;
        var eventKind=eventOverride??(confirmation
            ?scenario is MarketStructureScenario.TrendPullbackShort or MarketStructureScenario.RangeReversionShort or MarketStructureScenario.BreakoutRetestShort
                ?MarketStructureEvent.BearishConfirmation
                :MarketStructureEvent.BullishConfirmation
            :MarketStructureEvent.None);
        return new(
            MarketStateSnapshotV1.CurrentSchema,
            "BTCUSDT",
            at,
            true,
            100m,
            support,
            resistance,
            bias,
            phase,
            scenario,
            eventKind,
            lifecycle,
            trigger,
            confirmation,
            observations,
            2,
            2,
            1,
            1,
            confirmation?1:0,
            at.AddMinutes(-15),
            at,
            at,
            confirmation?at:null,
            transition,
            []);
    }

    private static async Task PersistCompletedProtection(
        AgentSqliteStore db,
        ExecutionIntent opening,
        ProtectionAdjustment adjustment)
    {
        await db.SetStatePairAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
            "COMPLETED",
            PositionManagementDurableState.LatestProtectionAdjustmentKey(opening.ClientOrderId),
            adjustment.AdjustmentId!,
            CancellationToken.None);
        await db.SetStateAsync(
            PositionManagementDurableState.EffectiveProtectionKey(opening.ClientOrderId),
            PositionManagementDurableState.SerializeEffectiveProtection(
                new EffectiveProtectionStateV1(
                    1,
                    opening.ClientOrderId,
                    adjustment.StopLoss,
                    adjustment.TakeProfit,
                    adjustment.AdjustmentId!,
                    DateTimeOffset.UtcNow)),
            CancellationToken.None);
    }

    private static async Task SeedCompletedPartialAndBreakeven(
        AgentSqliteStore db,
        ExecutionIntent opening,
        string suffix)
    {
        await db.SaveIntentAsync("cycle-open-"+suffix,opening,"PROTECTED","open-"+suffix,CancellationToken.None);
        var partialId=PositionManagementActionId("TP2",opening.ClientOrderId);
        var partial=new ExecutionIntent(
            opening.Symbol,opening.Side,.5m,true,0,0,partialId,"partial",
            DecisionAction.ReduceLong,ExpectedPrice:120m,
            ReasonCode:PositionExitReasonCodes.PartialTakeProfit2R);
        await db.SaveIntentAsync("cycle-partial-"+suffix,partial,"COMPLETED","partial-"+suffix,CancellationToken.None);
        await db.SetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(PositionManagementActionId("BE",opening.ClientOrderId)),
            "COMPLETED",
            CancellationToken.None);
    }

    private static string TempDb()=>Path.Combine(Path.GetTempPath(),$"wpe-structure-position-{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var directory=Path.GetDirectoryName(path)!;
        var name=Path.GetFileName(path);
        foreach(var file in Directory.GetFiles(directory,name+"*"))
            try{File.Delete(file);}catch{}
    }
}
