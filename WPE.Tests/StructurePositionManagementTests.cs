using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StructurePositionManagementTests
{
    private static readonly DateTimeOffset Now=new(2026,9,20,9,0,0,TimeSpan.Zero);

    [Fact]
    public async Task InvalidatedLocalStructureClosesManagedLongReduceOnly()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(),"PROTECTED","1001",CancellationToken.None);
            var position=Position();
            var market=Market(98m);
            var hypothesis=Hypothesis(MarketStructureRead.DecisionBasis,TradeHypothesisStage.Invalidated);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [position],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",hypothesis}});

            var intent=Assert.Single(result.Intents);
            Assert.True(intent.ReduceOnly);
            Assert.Equal(position.Quantity,intent.Quantity);
            Assert.Equal(DecisionAction.CloseLong,intent.Action);
            Assert.Equal(position.Symbol,intent.Symbol);
            Assert.Equal(market.Price,intent.ExpectedPrice);
            Assert.Equal(PositionExitReasonCodes.StructureInvalidated,intent.ReasonCode);
            Assert.Contains("structure-invalidated:",Assert.Single(result.Notes),StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

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
    public async Task InvalidatedStructureNeverClosesExternalPositionWithoutWpeOpeningIntent()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            var hypothesis=Hypothesis(MarketStructureRead.DecisionBasis,TradeHypothesisStage.Invalidated);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(98m)}},
                db,
                CancellationToken.None,
                [],
                new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",hypothesis}});

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
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

    [Fact]
    public async Task LegacySummaryInvalidationCannotTriggerStructureExit()
    {
        var path=TempDb();
        try
        {
            var db=new AgentSqliteStore(path);
            await db.SaveIntentAsync("cycle-open",OpeningIntent(),"PROTECTED","1002",CancellationToken.None);
            var hypothesis=Hypothesis("summary-v1",TradeHypothesisStage.Invalidated);

            var result=await new PositionManagementSkill().EvaluateAsync(
                [Position()],
                new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",Market(100m)}},
                db,
                CancellationToken.None,
                ManagedLedger(),
                new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",hypothesis}});

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
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

    private static MarketEvidence Market(decimal price)=>new(
        "BTCUSDT",price,95m,110m,50,0,0,0,
        new DerivativesSnapshot(.0001m,1_000_000m,1m,1m,1m,1m,0m),
        Now.UtcDateTime);

    private static TradeHypothesis Hypothesis(string decisionBasis,TradeHypothesisStage stage)=>new(
        "HYP-BTCUSDT-STRUCTURE",
        TradeHypothesis.CurrentVersion,
        "BTCUSDT",
        TradeHypothesisKind.TrendPullbackLong,
        stage,
        1,
        MarketRegime.Trending,
        Now,
        Now,
        4,
        100m,
        95m,
        110m,
        94m,
        96m,
        0,
        0,
        0,
        50,
        0,
        1m,
        0,
        "local chart thesis",
        "wait for confirmation",
        "invalid below structure",
        [])
    {
        DecisionBasis=decisionBasis,
        LastOrderBookAvailable=true,
        LastStructureEvidenceAtUtc=Now,
        LastStructurePhase=MarketStructurePhase.BearishImpulse
    };

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
