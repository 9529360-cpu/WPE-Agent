namespace 币安量化机器人.Services.Agent;

public enum PositionLifecycleStageV1
{
    Unavailable,
    Protected,
    TrendContinuation,
    NormalPullback,
    ProfitExpansion,
    ExhaustionRisk,
    ConfirmedReversal
}

public sealed record PositionLifecycleAssessmentV1(
    PositionLifecycleStageV1 Stage,
    decimal FavorableR,
    bool FreshState,
    bool StateAligned,
    decimal StructuralAnchor,
    string Narrative);

public static class PositionLifecyclePolicyV1
{
    public static PositionLifecycleAssessmentV1 Assess(
        ManagedPosition position,
        ExecutionIntent opening,
        MarketEvidence market,
        MarketStateSnapshotV1? state)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(opening);
        ArgumentNullException.ThrowIfNull(market);

        var risk=Math.Abs(position.EntryPrice-opening.StopLoss);
        var favorableR=risk<=0?0:(position.Side==PositionSide.Long
            ?market.Price-position.EntryPrice
            :position.EntryPrice-market.Price)/risk;
        var fallbackAnchor=position.Side==PositionSide.Long?market.Support:market.Resistance;

        if(state is null||!state.Available||state.ObservationCount<2||
           !string.Equals(state.Symbol,position.Symbol,StringComparison.OrdinalIgnoreCase)||
           !SameObservation(state.ObservedAtUtc,market.CollectedAt))
            return new(
                PositionLifecycleStageV1.Unavailable,
                favorableR,
                false,
                false,
                fallbackAnchor,
                "Persistent market state is unavailable, immature, or stale for this position observation.");

        var anchor=position.Side==PositionSide.Long
            ?state.StructuralSupport
            :state.StructuralResistance;
        if(anchor<=0)anchor=fallbackAnchor;

        if(IsConfirmedReversal(position,state))
            return new(
                PositionLifecycleStageV1.ConfirmedReversal,
                favorableR,
                true,
                false,
                anchor,
                $"Confirmed adverse market-state transition: {state.TransitionKind}; bias={state.Bias}; scenario={state.Scenario}.");

        var aligned=IsAligned(position,state);
        var adverseReversalAttempt=position.Side==PositionSide.Long
            ?state.Phase==MarketStructurePhase.BearishReversalAttempt||
             state.Event is MarketStructureEvent.LiquiditySweepHighReject or MarketStructureEvent.BearishRejection
            :state.Phase==MarketStructurePhase.BullishReversalAttempt||
             state.Event is MarketStructureEvent.LiquiditySweepLowReclaim or MarketStructureEvent.BullishRejection;

        if(adverseReversalAttempt)
            return new(
                PositionLifecycleStageV1.ExhaustionRisk,
                favorableR,
                true,
                aligned,
                anchor,
                $"Adverse reversal attempt is present without a confirmed opposite state; phase={state.Phase}; event={state.Event}.");

        var normalPullback=position.Side==PositionSide.Long
            ?state.Bias==MarketStructureBias.Bullish&&state.Phase==MarketStructurePhase.BullishPullback
            :state.Bias==MarketStructureBias.Bearish&&state.Phase==MarketStructurePhase.BearishPullback;
        if(normalPullback)
            return new(
                PositionLifecycleStageV1.NormalPullback,
                favorableR,
                true,
                true,
                anchor,
                $"Higher-timeframe bias remains aligned while the 15m structure is in a normal {state.Phase}.");

        var continuation=position.Side==PositionSide.Long
            ?state.Bias==MarketStructureBias.Bullish&&
             (state.Phase==MarketStructurePhase.BullishImpulse||
              IsLongScenario(state.Scenario))
            :state.Bias==MarketStructureBias.Bearish&&
             (state.Phase==MarketStructurePhase.BearishImpulse||
              IsShortScenario(state.Scenario));

        if(continuation&&favorableR>=2m)
            return new(
                PositionLifecycleStageV1.ProfitExpansion,
                favorableR,
                true,
                true,
                anchor,
                $"Aligned structure is expanding beyond 2R; phase={state.Phase}; scenario={state.Scenario}.");

        if(continuation)
            return new(
                PositionLifecycleStageV1.TrendContinuation,
                favorableR,
                true,
                true,
                anchor,
                $"Aligned structure remains in continuation; phase={state.Phase}; scenario={state.Scenario}.");

        return new(
            PositionLifecycleStageV1.Protected,
            favorableR,
            true,
            aligned,
            anchor,
            $"No adverse confirmed transition is present; bias={state.Bias}; phase={state.Phase}; scenario={state.Scenario}.");
    }

    public static bool IsConfirmedReversal(ManagedPosition position,MarketStateSnapshotV1 state)
    {
        if(!state.Available||state.ObservationCount<2)return false;
        var opposingBias=state.TransitionKind==MarketStateTransitionKindV1.BiasReversal&&
            (position.Side==PositionSide.Long
                ?state.Bias==MarketStructureBias.Bearish
                :state.Bias==MarketStructureBias.Bullish);
        var opposingConfirmedScenario=state.TransitionKind==MarketStateTransitionKindV1.ScenarioFlip&&
            state.TriggerPresent&&state.ConfirmationPresent&&
            (position.Side==PositionSide.Long
                ?IsShortScenario(state.Scenario)
                :IsLongScenario(state.Scenario));
        return opposingBias||opposingConfirmedScenario;
    }

    private static bool IsAligned(ManagedPosition position,MarketStateSnapshotV1 state)
    {
        if(position.Side==PositionSide.Long)
            return state.Bias==MarketStructureBias.Bullish||IsLongScenario(state.Scenario);
        return state.Bias==MarketStructureBias.Bearish||IsShortScenario(state.Scenario);
    }

    private static bool SameObservation(DateTime stateObserved,DateTime marketObserved)
    {
        var stateUtc=stateObserved.Kind==DateTimeKind.Utc?stateObserved:stateObserved.ToUniversalTime();
        var marketUtc=marketObserved.Kind==DateTimeKind.Utc?marketObserved:marketObserved.ToUniversalTime();
        return stateUtc==marketUtc;
    }

    private static bool IsLongScenario(MarketStructureScenario scenario)=>scenario is
        MarketStructureScenario.TrendPullbackLong or
        MarketStructureScenario.RangeReversionLong or
        MarketStructureScenario.BreakoutRetestLong;

    private static bool IsShortScenario(MarketStructureScenario scenario)=>scenario is
        MarketStructureScenario.TrendPullbackShort or
        MarketStructureScenario.RangeReversionShort or
        MarketStructureScenario.BreakoutRetestShort;
}
