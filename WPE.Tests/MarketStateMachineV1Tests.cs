using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MarketStateMachineV1Tests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-market-state-"+Guid.NewGuid().ToString("N"));
    private string DbPath=>Path.Combine(_dir,"agent.db");
    private static readonly DateTime T0=new(2026,9,26,8,0,0,DateTimeKind.Utc);

    [Fact]
    public void StateProgressesFromDevelopingToConfirmedWithoutScores()
    {
        var developing=MarketStateMachineV1.Advance(
            "BTCUSDT",T0,100m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishPullback,
                MarketStructureScenario.TrendPullbackLong,
                MarketStructureEvent.BullishRejection,
                trigger:false,
                confirmation:false),
            null);

        Assert.Equal(MarketStateLifecycleV1.Developing,developing.Lifecycle);
        Assert.Equal(MarketStateTransitionKindV1.Initial,developing.TransitionKind);
        Assert.Equal(1,developing.ObservationCount);
        Assert.Single(developing.RecentTransitions);

        var stable=MarketStateMachineV1.Advance(
            "BTCUSDT",T0.AddMinutes(15),100.5m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishPullback,
                MarketStructureScenario.TrendPullbackLong,
                MarketStructureEvent.BullishRejection,
                trigger:false,
                confirmation:false),
            developing);

        Assert.Equal(MarketStateTransitionKindV1.Stable,stable.TransitionKind);
        Assert.Equal(2,stable.ObservationCount);
        Assert.Equal(2,stable.BiasStreak);
        Assert.Equal(2,stable.ScenarioStreak);
        Assert.Single(stable.RecentTransitions);

        var confirmed=MarketStateMachineV1.Advance(
            "BTCUSDT",T0.AddMinutes(30),101m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishPullback,
                MarketStructureScenario.TrendPullbackLong,
                MarketStructureEvent.BullishConfirmation,
                trigger:true,
                confirmation:true),
            stable);

        Assert.Equal(MarketStateLifecycleV1.Confirmed,confirmed.Lifecycle);
        Assert.Equal(MarketStateTransitionKindV1.ConfirmationGained,confirmed.TransitionKind);
        Assert.Equal(3,confirmed.ObservationCount);
        Assert.Equal(1,confirmed.ConfirmationStreak);
        Assert.Equal(T0.AddMinutes(30),confirmed.LastConfirmationAtUtc);
        Assert.Equal(2,confirmed.RecentTransitions.Length);
    }

    [Fact]
    public void ConfirmedSetupRecordsInvalidationWhenStructureLosesScenario()
    {
        var confirmed=MarketStateMachineV1.Advance(
            "ETHUSDT",T0,200m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishPullback,
                MarketStructureScenario.TrendPullbackLong,
                MarketStructureEvent.BullishConfirmation,
                trigger:true,
                confirmation:true),
            null);

        var invalidated=MarketStateMachineV1.Advance(
            "ETHUSDT",T0.AddMinutes(15),198m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.Balance,
                MarketStructureScenario.None,
                MarketStructureEvent.None,
                trigger:false,
                confirmation:false),
            confirmed);

        Assert.Equal(MarketStateTransitionKindV1.SetupInvalidated,invalidated.TransitionKind);
        Assert.Equal(MarketStateLifecycleV1.Balance,invalidated.Lifecycle);
        Assert.False(invalidated.ConfirmationPresent);
        Assert.Equal(0,invalidated.ConfirmationStreak);
        Assert.Contains("SetupInvalidated",invalidated.RecentTransitions[^1].Summary,StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateOrRegressedObservationDoesNotAdvanceState()
    {
        var initial=MarketStateMachineV1.Advance(
            "SOLUSDT",T0,50m,
            Structure(
                MarketStructureBias.Range,
                MarketStructurePhase.Balance,
                MarketStructureScenario.None,
                MarketStructureEvent.None,
                false,
                false),
            null);

        var duplicate=MarketStateMachineV1.Advance(
            "SOLUSDT",T0,55m,
            Structure(
                MarketStructureBias.Bearish,
                MarketStructurePhase.BearishImpulse,
                MarketStructureScenario.TrendPullbackShort,
                MarketStructureEvent.BearishBreak,
                true,
                true),
            initial);
        var regressed=MarketStateMachineV1.Advance(
            "SOLUSDT",T0.AddMinutes(-15),45m,
            Structure(
                MarketStructureBias.Bearish,
                MarketStructurePhase.BearishImpulse,
                MarketStructureScenario.TrendPullbackShort,
                MarketStructureEvent.BearishBreak,
                true,
                true),
            initial);

        Assert.Same(initial,duplicate);
        Assert.Same(initial,regressed);
        Assert.Equal(1,initial.ObservationCount);
    }

    [Fact]
    public async Task DurableStateSurvivesStoreRecreation()
    {
        Directory.CreateDirectory(_dir);
        var state=MarketStateMachineV1.Advance(
            "BTCUSDT",T0,100m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishPullback,
                MarketStructureScenario.TrendPullbackLong,
                MarketStructureEvent.BullishRejection,
                false,
                false),
            null);

        var first=new DurableMarketStateStoreV1(new AgentSqliteStore(DbPath));
        await first.SaveAsync(state,default);

        var restarted=new DurableMarketStateStoreV1(new AgentSqliteStore(DbPath));
        var restored=await restarted.LoadAsync("btcusdt",default);

        Assert.NotNull(restored);
        Assert.Equal(state.Schema,restored.Schema);
        Assert.Equal(state.Symbol,restored.Symbol);
        Assert.Equal(state.ObservedAtUtc,restored.ObservedAtUtc);
        Assert.Equal(state.ObservationCount,restored.ObservationCount);
        Assert.Equal(state.Bias,restored.Bias);
        Assert.Equal(state.Phase,restored.Phase);
        Assert.Equal(state.Scenario,restored.Scenario);
        Assert.Equal(state.Lifecycle,restored.Lifecycle);
        Assert.Equal(state.RecentTransitions,restored.RecentTransitions);

        var next=MarketStateMachineV1.Advance(
            "BTCUSDT",T0.AddMinutes(15),100.25m,
            Structure(
                MarketStructureBias.Bullish,
                MarketStructurePhase.BullishPullback,
                MarketStructureScenario.TrendPullbackLong,
                MarketStructureEvent.BullishRejection,
                false,
                false),
            restored);

        Assert.Equal(2,next.ObservationCount);
        Assert.Equal(2,next.BiasStreak);
        Assert.Equal(T0,next.BiasEstablishedAtUtc);
    }

    [Fact]
    public async Task CorruptDurableStateIsIgnoredInsteadOfInventingContinuity()
    {
        Directory.CreateDirectory(_dir);
        var db=new AgentSqliteStore(DbPath);
        await db.SetStateAsync(DurableMarketStateStoreV1.StateKey("BTCUSDT"),"{not-json",default);

        var state=await new DurableMarketStateStoreV1(db).LoadAsync("BTCUSDT",default);

        Assert.Null(state);
    }

    private static MarketStructureRead Structure(
        MarketStructureBias bias,
        MarketStructurePhase phase,
        MarketStructureScenario scenario,
        MarketStructureEvent eventKind,
        bool trigger,
        bool confirmation)
    {
        var state=bias switch
        {
            MarketStructureBias.Bullish=>PriceStructureState.Bullish,
            MarketStructureBias.Bearish=>PriceStructureState.Bearish,
            MarketStructureBias.Range=>PriceStructureState.Range,
            _=>PriceStructureState.Transition
        };
        var m15=Frame("15m",state,eventKind);
        var h1=Frame("1h",state,MarketStructureEvent.None);
        var h4=Frame("4h",state,MarketStructureEvent.None);
        return new(
            true,
            bias,
            phase,
            scenario,
            trigger,
            confirmation,
            95m,
            105m,
            m15,
            h1,
            h4,
            "test structure",
            [])
        {
            ConfirmationClose=confirmation?100m:0m,
            ConfirmationSource=confirmation?"15m-closed":"none"
        };
    }

    private static TimeframeStructureRead Frame(
        string interval,
        PriceStructureState state,
        MarketStructureEvent eventKind)=>
        new(
            interval,
            state,
            eventKind,
            100m,
            95m,
            105m,
            104m,
            103m,
            96m,
            95m,
            2m,
            false,
            false,
            false,
            "test frame");

    public void Dispose()
    {
        try{if(Directory.Exists(_dir))Directory.Delete(_dir,true);}catch{}
    }
}
