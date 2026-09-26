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
    public async Task ObserveAsyncAnalyzesEvidenceAndContinuesDurableSequence()
    {
        Directory.CreateDirectory(_dir);
        var store=new DurableMarketStateStoreV1(new AgentSqliteStore(DbPath));
        var firstMarket=Market(T0);
        var first=await store.ObserveAsync(
            new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
            {
                [firstMarket.Symbol]=firstMarket
            },
            default);

        var firstState=Assert.Single(first).Value;
        Assert.True(firstState.Available);
        Assert.Equal(1,firstState.ObservationCount);

        var restarted=new DurableMarketStateStoreV1(new AgentSqliteStore(DbPath));
        var duplicate=await restarted.ObserveAsync(
            new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
            {
                [firstMarket.Symbol]=firstMarket
            },
            default);
        Assert.Equal(1,Assert.Single(duplicate).Value.ObservationCount);

        var nextMarket=Market(T0.AddMinutes(15));
        var next=await restarted.ObserveAsync(
            new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
            {
                [nextMarket.Symbol]=nextMarket
            },
            default);

        Assert.Equal(2,Assert.Single(next).Value.ObservationCount);
        Assert.Equal(2,(await restarted.LoadAsync("BTCUSDT",default))!.ObservationCount);
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

    private static MarketEvidence Market(DateTime observedAtUtc)
    {
        var m15=Trend(40,90m,.25m,TimeSpan.FromMinutes(15),observedAtUtc);
        var h1=Trend(40,80m,.40m,TimeSpan.FromHours(1),observedAtUtc);
        var h4=Trend(40,60m,.70m,TimeSpan.FromHours(4),observedAtUtc);
        var price=m15[^1].Close;
        return new MarketEvidence(
            "BTCUSDT",
            price,
            m15.TakeLast(20).Min(x=>x.Low),
            m15.TakeLast(20).Max(x=>x.High),
            50,
            0,
            0,
            0,
            new DerivativesSnapshot(0,1,1,1,1,1,0),
            observedAtUtc)
        {
            Candles=m15,
            Candles1h=h1,
            Candles4h=h4
        };
    }

    private static IReadOnlyList<CandleEvidence> Trend(
        int count,
        decimal start,
        decimal step,
        TimeSpan interval,
        DateTime observedAtUtc)
    {
        var values=new List<CandleEvidence>(count);
        var price=start;
        var first=observedAtUtc-interval*count;
        for(var i=0;i<count;i++)
        {
            var open=price;
            var close=open+step;
            values.Add(new(
                first+interval*i,
                open,
                close+.20m,
                open-.20m,
                close,
                100m+i,
                (100m+i)*close,
                100+i,
                55m+i%10));
            price=close;
        }
        return values;
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
