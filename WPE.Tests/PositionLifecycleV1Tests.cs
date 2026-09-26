using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PositionLifecycleV1Tests
{
    private static readonly DateTime Now=new(2026,9,26,9,0,0,DateTimeKind.Utc);

    [Fact]
    public void AlignedImpulseIsTrendContinuationBeforeTwoR()
    {
        var assessment=PositionLifecyclePolicyV1.Assess(
            Position(),
            Opening(),
            Market(115m),
            State(MarketStructureBias.Bullish,MarketStructurePhase.BullishImpulse,MarketStructureScenario.BreakoutRetestLong));

        Assert.Equal(PositionLifecycleStageV1.TrendContinuation,assessment.Stage);
        Assert.Equal(1.5m,assessment.FavorableR);
        Assert.True(assessment.FreshState);
        Assert.True(assessment.StateAligned);
    }

    [Fact]
    public void AlignedImpulseBecomesProfitExpansionBeyondTwoR()
    {
        var assessment=PositionLifecyclePolicyV1.Assess(
            Position(),
            Opening(),
            Market(125m),
            State(MarketStructureBias.Bullish,MarketStructurePhase.BullishImpulse,MarketStructureScenario.BreakoutRetestLong,support:116m));

        Assert.Equal(PositionLifecycleStageV1.ProfitExpansion,assessment.Stage);
        Assert.Equal(2.5m,assessment.FavorableR);
        Assert.Equal(116m,assessment.StructuralAnchor);
    }

    [Fact]
    public void HigherTimeframeAlignedPullbackRemainsNormalPullbackEvenBeyondTwoR()
    {
        var assessment=PositionLifecyclePolicyV1.Assess(
            Position(),
            Opening(),
            Market(125m),
            State(MarketStructureBias.Bullish,MarketStructurePhase.BullishPullback,MarketStructureScenario.TrendPullbackLong,support:112m));

        Assert.Equal(PositionLifecycleStageV1.NormalPullback,assessment.Stage);
        Assert.True(assessment.StateAligned);
        Assert.Equal(112m,assessment.StructuralAnchor);
    }

    [Fact]
    public void AdverseReversalAttemptIsExhaustionRiskNotAutomaticReversal()
    {
        var assessment=PositionLifecyclePolicyV1.Assess(
            Position(),
            Opening(),
            Market(121m),
            State(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BearishReversalAttempt,
                MarketStructureScenario.None,
                eventKind:MarketStructureEvent.BearishRejection,
                transition:MarketStateTransitionKindV1.EventChanged));

        Assert.Equal(PositionLifecycleStageV1.ExhaustionRisk,assessment.Stage);
        Assert.NotEqual(PositionLifecycleStageV1.ConfirmedReversal,assessment.Stage);
    }

    [Fact]
    public void ConfirmedOppositeScenarioFlipIsConfirmedReversal()
    {
        var assessment=PositionLifecyclePolicyV1.Assess(
            Position(),
            Opening(),
            Market(98m),
            State(
                MarketStructureBias.Range,
                MarketStructurePhase.BearishImpulse,
                MarketStructureScenario.TrendPullbackShort,
                trigger:true,
                confirmation:true,
                eventKind:MarketStructureEvent.BearishConfirmation,
                transition:MarketStateTransitionKindV1.ScenarioFlip));

        Assert.Equal(PositionLifecycleStageV1.ConfirmedReversal,assessment.Stage);
        Assert.False(assessment.StateAligned);
    }

    [Fact]
    public void StaleStateIsUnavailableAndCannotDriveLifecycleActions()
    {
        var assessment=PositionLifecyclePolicyV1.Assess(
            Position(),
            Opening(),
            Market(120m),
            State(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishImpulse,
                MarketStructureScenario.BreakoutRetestLong,
                observedAt:Now.AddMinutes(-15)));

        Assert.Equal(PositionLifecycleStageV1.Unavailable,assessment.Stage);
        Assert.False(assessment.FreshState);
    }

    private static ExecutionIntent Opening()=>new(
        "BTCUSDT",PositionSide.Long,1m,false,90m,140m,"open-lifecycle","test",
        DecisionAction.OpenLong,ExpectedPrice:100m);

    private static ManagedPosition Position()=>new(
        "BTCUSDT",PositionSide.Long,1m,100m,100m,0m,2m,true,50m);

    private static MarketEvidence Market(decimal price)=>new(
        "BTCUSDT",price,95m,140m,50,0,0,0,
        new DerivativesSnapshot(.0001m,1_000_000m,1m,1m,1m,1m,0m),
        Now);

    private static MarketStateSnapshotV1 State(
        MarketStructureBias bias,
        MarketStructurePhase phase,
        MarketStructureScenario scenario,
        bool trigger=false,
        bool confirmation=false,
        decimal support=95m,
        decimal resistance=140m,
        MarketStructureEvent eventKind=MarketStructureEvent.None,
        MarketStateTransitionKindV1 transition=MarketStateTransitionKindV1.Stable,
        DateTime? observedAt=null)
    {
        var at=observedAt??Now;
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
            confirmation&&trigger?MarketStateLifecycleV1.Confirmed:MarketStateLifecycleV1.Impulse,
            trigger,
            confirmation,
            3,
            3,
            2,
            2,
            1,
            confirmation?1:0,
            at.AddMinutes(-30),
            at.AddMinutes(-15),
            at,
            confirmation?at:null,
            transition,
            []);
    }
}
