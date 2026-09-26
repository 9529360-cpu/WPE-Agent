namespace 币安量化机器人.Services.Agent;

public static class DirectMarketStructureDecisionSkill
{
    public const string DecisionContextKind = "market-structure-direct";
    public const string Version = "market-structure-direct-v4";
    internal static readonly TimeSpan ArmedSetupTtl = TimeSpan.FromMinutes(45);

    public static DecisionPlan Decide(EvidencePack evidence, bool circuitBreakerActive)=>
        Decide(evidence,circuitBreakerActive,MarketStructureAnalysisTool.Shared);

    internal static DecisionPlan Decide(EvidencePack evidence,bool circuitBreakerActive,IMarketStructureAnalysisTool analysisTool)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(analysisTool);
        var markets=(evidence.Markets??new Dictionary<string,MarketEvidence>())
            .Values.Where(x=>x is not null)
            .OrderBy(x=>x.Symbol,StringComparer.Ordinal)
            .ToArray();
        var openSymbols=(evidence.Positions??Array.Empty<ManagedPosition>())
            .Where(x=>x.Quantity>0)
            .Select(x=>x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var holdReason="No confirmed direct candle-structure setup is actionable.";
        foreach(var market in markets)
        {
            if(openSymbols.Contains(market.Symbol))continue;
            var structure=analysisTool.Analyze(market);
            if(!structure.Available)continue;
            var arm=structure.Scenario==MarketStructureScenario.None?TryResolveArmedSetup(evidence,market,structure):null;
            var scenario=structure.Scenario!=MarketStructureScenario.None?structure.Scenario:arm?.Scenario??MarketStructureScenario.None;
            if(scenario==MarketStructureScenario.None)continue;
            var triggerPresent=arm is null?structure.TriggerPresent:HasTriggerForScenario(scenario,structure.FifteenMinute);
            var confirmationPresent=arm is null?structure.ConfirmationPresent:HasConfirmationForScenario(scenario,structure.FifteenMinute);
            if(!triggerPresent)
            {
                holdReason=arm is null
                    ?$"{market.Symbol}: direct structure scenario is present but the entry trigger is still waiting."
                    :$"{market.Symbol}: persistent {scenario} setup is armed but the entry trigger is still waiting.";
                continue;
            }
            if(!confirmationPresent)
            {
                holdReason=arm is null
                    ?$"{market.Symbol}: direct structure trigger is present but confirmation is still waiting."
                    :$"{market.Symbol}: persistent {scenario} setup has triggered but confirmation is still waiting.";
                continue;
            }

            var longSide=IsLong(scenario);
            var shortSide=IsShort(scenario);
            if(!longSide&&!shortSide)continue;
            if(!EntryStillActionable(market,structure,longSide,shortSide))
            {
                holdReason=$"{market.Symbol}: confirmed structure exists but the live price has moved beyond the bounded entry zone.";
                continue;
            }

            var stateEvidence=MarketStateEvidence(evidence,market.Symbol);
            var setupEvidence=arm is null
                ?Array.Empty<string>()
                :new[]
                {
                    "setup_memory=armed-v1",
                    $"setup_arm_scenario={arm.Scenario}",
                    $"setup_arm_age_minutes={arm.Age.TotalMinutes:F1}",
                    $"setup_arm_transition={arm.TransitionKind}",
                    $"setup_arm_observed_utc={arm.ArmedAtUtc:O}"
                };
            var entry=market.Price;
            var buffer=Math.Max(structure.FifteenMinute.Atr*.15m,entry*.0005m);
            var stop=longSide
                ?structure.StructuralSupport-buffer
                :structure.StructuralResistance+buffer;
            if(stop<=0||(longSide&&stop>=entry)||(shortSide&&stop<=entry))
            {
                var fallback=Math.Max(structure.FifteenMinute.Atr,entry*.005m);
                stop=longSide?entry-fallback:entry+fallback;
            }

            var targetBuffer=Math.Max(structure.FifteenMinute.Atr*.10m,entry*.0003m);
            var take=longSide
                ?structure.StructuralResistance-targetBuffer
                :structure.StructuralSupport+targetBuffer;
            if(take<=0||(longSide&&take<=entry)||(shortSide&&take>=entry))
                take=entry;

            return new DecisionPlan
            {
                Action=longSide?DecisionAction.OpenLong:DecisionAction.OpenShort,
                Instrument=market.Symbol,
                TargetTier=1,
                EntryPrice=entry,
                StopLossPrice=stop,
                TakeProfitPrice=take,
                Regime=structure.HigherTimeframeBias.ToString(),
                Reason=arm is null
                    ?$"Direct local candle-structure decision: {structure.Narrative}"
                    :$"Persistent armed {scenario} setup completed by fresh candle confirmation: {structure.Narrative}",
                Invalidation=longSide
                    ?$"Exit if local structure breaks below {stop:F2} or higher-timeframe bias turns bearish."
                    :$"Exit if local structure breaks above {stop:F2} or higher-timeframe bias turns bullish.",
                EvidenceReferences=structure.Evidence.Concat(stateEvidence).Concat(setupEvidence).Append("decision_path=direct-market-structure").Append("entry_qualification=trigger-plus-confirmation").Append("live_entry_guard=bounded-chase").Append("target_geometry=structural-opposite-boundary").ToList(),
                MissingConditions=[],
                ConflictSummary=$"direct-structure; scenario={scenario}; event={structure.FifteenMinute.Event}; confirmation={confirmationPresent}; armed={(arm is not null)}; {MarketStateSummary(evidence,market.Symbol)}",
                StrategyVersion=Version,
                DecisionContextKind=DecisionContextKind,
                DecisionContextId=arm is null?ContextId(market,structure):ArmedContextId(market,scenario,structure)
            };
        }

        return Hold(markets.FirstOrDefault(),holdReason,analysisTool);
    }

    public static bool IsDirect(DecisionPlan? decision)=>
        decision is not null&&string.Equals(decision.DecisionContextKind,DecisionContextKind,StringComparison.Ordinal);

    public static bool ContextMatches(DecisionPlan decision, MarketEvidence market)
    {
        if(!IsDirect(decision)||!string.Equals(decision.Instrument,market.Symbol,StringComparison.OrdinalIgnoreCase))return false;
        var structure=MarketStructureIntelligence.Analyze(market);
        if(!structure.Available)return false;
        if(TryParseArmedContext(decision.DecisionContextId,out var armedScenario))
        {
            var longArmed=IsLong(armedScenario);
            var shortArmed=IsShort(armedScenario);
            var expectedArmedAction=longArmed?DecisionAction.OpenLong:shortArmed?DecisionAction.OpenShort:DecisionAction.Hold;
            return BiasSupportsScenario(structure.HigherTimeframeBias,armedScenario)
                &&HasTriggerForScenario(armedScenario,structure.FifteenMinute)
                &&HasConfirmationForScenario(armedScenario,structure.FifteenMinute)
                &&EntryStillActionable(market,structure,longArmed,shortArmed)
                &&expectedArmedAction==decision.Action
                &&string.Equals(ArmedContextId(market,armedScenario,structure),decision.DecisionContextId,StringComparison.Ordinal)
                &&string.Equals(decision.StrategyVersion,Version,StringComparison.Ordinal);
        }
        if(structure.Scenario==MarketStructureScenario.None||!structure.TriggerPresent||!structure.ConfirmationPresent)return false;
        var longSide=IsLong(structure.Scenario);
        var shortSide=IsShort(structure.Scenario);
        var expectedAction=longSide?DecisionAction.OpenLong:shortSide?DecisionAction.OpenShort:DecisionAction.Hold;
        return EntryStillActionable(market,structure,longSide,shortSide)
            &&expectedAction==decision.Action
            &&string.Equals(ContextId(market,structure),decision.DecisionContextId,StringComparison.Ordinal)
            &&string.Equals(decision.StrategyVersion,Version,StringComparison.Ordinal);
    }

    public static bool PositionInvalidated(ManagedPosition position, MarketEvidence market)
    {
        var structure=MarketStructureIntelligence.Analyze(market);
        if(!structure.Available)return false;
        if(position.Side==PositionSide.Long)
            return structure.HigherTimeframeBias==MarketStructureBias.Bearish
                ||(market.Price<structure.StructuralSupport&&structure.FifteenMinute.Event is MarketStructureEvent.BearishBreak or MarketStructureEvent.BearishDisplacement);
        return structure.HigherTimeframeBias==MarketStructureBias.Bullish
            ||(market.Price>structure.StructuralResistance&&structure.FifteenMinute.Event is MarketStructureEvent.BullishBreak or MarketStructureEvent.BullishDisplacement);
    }

    private sealed record ArmedSetup(
        MarketStructureScenario Scenario,
        DateTime ArmedAtUtc,
        TimeSpan Age,
        MarketStateTransitionKindV1 TransitionKind);

    private static ArmedSetup? TryResolveArmedSetup(EvidencePack evidence,MarketEvidence market,MarketStructureRead structure)
    {
        if(!evidence.MarketStates.TryGetValue(market.Symbol,out var state)||!state.Available)return null;
        if(state.ObservedAtUtc!=market.CollectedAt.ToUniversalTime())return null;
        var observed=market.CollectedAt.ToUniversalTime();
        var transitions=state.RecentTransitions.OrderByDescending(x=>x.ObservedAtUtc).ToArray();
        foreach(var candidate in transitions)
        {
            if(candidate.FromScenario==MarketStructureScenario.None||candidate.ToScenario!=MarketStructureScenario.None)continue;
            var age=observed-candidate.ObservedAtUtc.ToUniversalTime();
            if(age<TimeSpan.Zero||age>ArmedSetupTtl)continue;
            if(!BiasSupportsScenario(structure.HigherTimeframeBias,candidate.FromScenario))continue;
            var invalidated=transitions.Any(x=>x.ObservedAtUtc>candidate.ObservedAtUtc&&x.Kind is
                MarketStateTransitionKindV1.BiasReversal or
                MarketStateTransitionKindV1.SetupInvalidated or
                MarketStateTransitionKindV1.ScenarioFlip);
            if(invalidated)continue;
            return new(candidate.FromScenario,candidate.ObservedAtUtc.ToUniversalTime(),age,candidate.Kind);
        }
        return null;
    }

    private static bool BiasSupportsScenario(MarketStructureBias bias,MarketStructureScenario scenario)=>scenario switch
    {
        MarketStructureScenario.TrendPullbackLong or MarketStructureScenario.BreakoutRetestLong=>bias==MarketStructureBias.Bullish,
        MarketStructureScenario.TrendPullbackShort or MarketStructureScenario.BreakoutRetestShort=>bias==MarketStructureBias.Bearish,
        MarketStructureScenario.RangeReversionLong or MarketStructureScenario.RangeReversionShort=>bias==MarketStructureBias.Range,
        _=>false
    };

    private static bool HasTriggerForScenario(MarketStructureScenario scenario,TimeframeStructureRead m15)=>scenario switch
    {
        MarketStructureScenario.TrendPullbackLong=>
            m15.Event is MarketStructureEvent.LiquiditySweepLowReclaim or MarketStructureEvent.BullishRejection or MarketStructureEvent.BullishConfirmation or MarketStructureEvent.BullishRetest or MarketStructureEvent.BullishDisplacement,
        MarketStructureScenario.TrendPullbackShort=>
            m15.Event is MarketStructureEvent.LiquiditySweepHighReject or MarketStructureEvent.BearishRejection or MarketStructureEvent.BearishConfirmation or MarketStructureEvent.BearishRetest or MarketStructureEvent.BearishDisplacement,
        MarketStructureScenario.RangeReversionLong=>
            m15.Event is MarketStructureEvent.LiquiditySweepLowReclaim or MarketStructureEvent.BullishRejection or MarketStructureEvent.BullishConfirmation,
        MarketStructureScenario.RangeReversionShort=>
            m15.Event is MarketStructureEvent.LiquiditySweepHighReject or MarketStructureEvent.BearishRejection or MarketStructureEvent.BearishConfirmation,
        MarketStructureScenario.BreakoutRetestLong=>
            m15.Event==MarketStructureEvent.BullishRetest||(m15.Event==MarketStructureEvent.BullishBreak&&m15.VolumeExpansion),
        MarketStructureScenario.BreakoutRetestShort=>
            m15.Event==MarketStructureEvent.BearishRetest||(m15.Event==MarketStructureEvent.BearishBreak&&m15.VolumeExpansion),
        _=>false
    };

    private static bool HasConfirmationForScenario(MarketStructureScenario scenario,TimeframeStructureRead m15)
    {
        if(IsLong(scenario))
            return m15.Event is MarketStructureEvent.BullishConfirmation or MarketStructureEvent.BullishBreak or MarketStructureEvent.BullishRetest or MarketStructureEvent.BullishDisplacement
                ||(m15.State==PriceStructureState.Bullish&&m15.LastClose>m15.PreviousSwingHigh);
        if(IsShort(scenario))
            return m15.Event is MarketStructureEvent.BearishConfirmation or MarketStructureEvent.BearishBreak or MarketStructureEvent.BearishRetest or MarketStructureEvent.BearishDisplacement
                ||(m15.State==PriceStructureState.Bearish&&m15.LastClose<m15.PreviousSwingLow);
        return false;
    }

    private static string ArmedContextId(MarketEvidence market,MarketStructureScenario scenario,MarketStructureRead structure)
    {
        var candle=ConfirmedMarketCandlesV1.Select(market.Candles,"15m",market.CollectedAt).LastOrDefault();
        var at=candle?.OpenTime.ToUniversalTime()??market.CollectedAt.ToUniversalTime();
        return $"ARMED|{market.Symbol}|{at:yyyyMMddHHmm}|{scenario}|{structure.FifteenMinute.Event}";
    }

    private static bool TryParseArmedContext(string? value,out MarketStructureScenario scenario)
    {
        scenario=MarketStructureScenario.None;
        if(string.IsNullOrWhiteSpace(value))return false;
        var parts=value.Split('|');
        return parts.Length==5
            &&string.Equals(parts[0],"ARMED",StringComparison.Ordinal)
            &&Enum.TryParse(parts[3],false,out scenario)
            &&scenario!=MarketStructureScenario.None;
    }

    private static string ContextId(MarketEvidence market,MarketStructureRead structure)
    {
        var candle=ConfirmedMarketCandlesV1.Select(market.Candles,"15m",market.CollectedAt).LastOrDefault();
        var at=candle?.OpenTime.ToUniversalTime()??market.CollectedAt.ToUniversalTime();
        return $"DIRECT-{market.Symbol}-{at:yyyyMMddHHmm}-{structure.Scenario}-{structure.FifteenMinute.Event}";
    }

    private static bool EntryStillActionable(MarketEvidence market,MarketStructureRead structure,bool longSide,bool shortSide)
    {
        if(market.Price<=0||(!longSide&&!shortSide))return false;
        var confirmationClose=structure.ConfirmationClose>0?structure.ConfirmationClose:structure.FifteenMinute.LastClose;
        if(confirmationClose<=0)return false;
        var chaseTolerance=Math.Max(structure.FifteenMinute.Atr*.75m,confirmationClose*.0015m);
        if(longSide)
            return market.Price>structure.StructuralSupport&&market.Price<=confirmationClose+chaseTolerance;
        return market.Price<structure.StructuralResistance&&market.Price>=confirmationClose-chaseTolerance;
    }

    private static IReadOnlyList<string> MarketStateEvidence(EvidencePack evidence,string symbol)
    {
        if(!evidence.MarketStates.TryGetValue(symbol,out var state))return ["market_state=unavailable"];
        return
        [
            $"market_state_schema={state.Schema}",
            $"market_state_lifecycle={state.Lifecycle}",
            $"market_state_observations={state.ObservationCount}",
            $"market_state_bias_streak={state.BiasStreak}",
            $"market_state_phase_streak={state.PhaseStreak}",
            $"market_state_scenario_streak={state.ScenarioStreak}",
            $"market_state_confirmation_streak={state.ConfirmationStreak}",
            $"market_state_transition={state.TransitionKind}",
            $"market_state_last_transition_utc={state.LastTransitionAtUtc:O}"
        ];
    }

    private static string MarketStateSummary(EvidencePack evidence,string symbol)=>
        evidence.MarketStates.TryGetValue(symbol,out var state)
            ?$"state={state.Lifecycle}; observations={state.ObservationCount}; transition={state.TransitionKind}"
            :"state=unavailable";

    private static bool IsLong(MarketStructureScenario scenario)=>scenario is
        MarketStructureScenario.TrendPullbackLong or MarketStructureScenario.RangeReversionLong or MarketStructureScenario.BreakoutRetestLong;

    private static bool IsShort(MarketStructureScenario scenario)=>scenario is
        MarketStructureScenario.TrendPullbackShort or MarketStructureScenario.RangeReversionShort or MarketStructureScenario.BreakoutRetestShort;

    private static DecisionPlan Hold(MarketEvidence? market,string reason,IMarketStructureAnalysisTool analysisTool)=>new()
    {
        Action=DecisionAction.Hold,
        Instrument=market?.Symbol??string.Empty,
        TargetTier=0,
        Regime=market is null?MarketRegime.Unknown.ToString():analysisTool.Analyze(market).HigherTimeframeBias.ToString(),
        Reason=reason,
        Invalidation="No risk-increasing decision exists.",
        StrategyVersion=Version,
        DecisionContextKind="market-observation"
    };
}
