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
                CancellationToken.None);

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
                CancellationToken.None);

            var intent=Assert.Single(result.Intents);
            Assert.Equal(DecisionAction.ReduceLong,intent.Action);
            Assert.Equal(.5m,intent.Quantity);
            Assert.Equal(PositionExitReasonCodes.PartialTakeProfit2R,intent.ReasonCode);
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

            var first=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None);
            var partial=Assert.Single(first.Intents);
            Assert.StartsWith("WPE-PM-TP2-",partial.ClientOrderId,StringComparison.Ordinal);
            await db.SaveIntentAsync("cycle-partial",partial,"COMPLETED_PARTIAL","2005",CancellationToken.None);

            var second=await new PositionManagementSkill().EvaluateAsync([Position() with{Quantity=.5m}],market,db,CancellationToken.None);
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

            var first=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None);
            var firstPartial=Assert.Single(first.Intents);
            await db.SaveIntentAsync("cycle-partial-a",firstPartial,"COMPLETED_PARTIAL","2006",CancellationToken.None);
            await Task.Delay(20);
            await db.SaveIntentAsync("cycle-open-b",OpeningIntent("open-partial-b"),"PROTECTED","1007",CancellationToken.None);

            var reopened=await new PositionManagementSkill().EvaluateAsync([Position()],market,db,CancellationToken.None);
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
                new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",hypothesis}});

            Assert.Empty(result.Intents);
            Assert.Empty(result.ProtectionAdjustments);
        }
        finally
        {
            Cleanup(path);
        }
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
