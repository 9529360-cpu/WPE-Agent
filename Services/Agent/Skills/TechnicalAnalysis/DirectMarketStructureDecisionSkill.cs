namespace 币安量化机器人.Services.Agent;

public static class DirectMarketStructureDecisionSkill
{
    public const string DecisionContextKind = "market-structure-direct";
    public const string Version = "market-structure-direct-v5";
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
            var setupKnownAt=arm?.ArmedAtUtc??CurrentSetupKnownAt(market);
            var qualification=QualifyEntry(market,structure,scenario,setupKnownAt,arm is not null);
            if(!qualification.TriggerPresent)
            {
                holdReason=arm is null
                    ?$"{market.Symbol}: direct structure scenario is present but the 15m/1m entry trigger is still waiting."
                    :$"{market.Symbol}: persistent {scenario} setup is armed but the 15m/1m entry trigger is still waiting.";
                continue;
            }
            if(!qualification.ConfirmationPresent)
            {
                holdReason=arm is null
                    ?$"{market.Symbol}: direct structure trigger is present but confirmation is still waiting."
                    :$"{market.Symbol}: persistent {scenario} setup has triggered but confirmation is still waiting.";
                continue;
            }

            var longSide=IsLong(scenario);
            var shortSide=IsShort(scenario);
            if(!longSide&&!shortSide)continue;
            if(!EntryStillActionable(market,structure,longSide,shortSide,qualification.ConfirmationClose))
            {
                holdReason=$"{market.Symbol}: confirmed structure exists but the live price has moved beyond the bounded entry zone.";
                continue;
            }

            var stateEvidence=MarketStateEvidence(evidence,market.Symbol);
            var setupEvidence=new List<string>();
            if(arm is not null)
            {
                setupEvidence.Add("setup_memory=armed-v1");
                setupEvidence.Add($"setup_arm_scenario={arm.Scenario}");
                setupEvidence.Add($"setup_arm_age_minutes={arm.Age.TotalMinutes:F1}");
                setupEvidence.Add($"setup_arm_transition={arm.TransitionKind}");
                setupEvidence.Add($"setup_arm_observed_utc={arm.ArmedAtUtc:O}");
            }
            setupEvidence.Add($"entry_confirmation_source={qualification.Source}");
            setupEvidence.Add($"entry_confirmation_close={qualification.ConfirmationClose:F8}");
            if(qualification.ConfirmedAtUtc is DateTime confirmedAt)
                setupEvidence.Add($"entry_confirmation_utc={confirmedAt:O}");
            if(!string.IsNullOrWhiteSpace(qualification.Pattern))
                setupEvidence.Add($"entry_confirmation_pattern={qualification.Pattern}");
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
                ConflictSummary=$"direct-structure; scenario={scenario}; event={structure.FifteenMinute.Event}; confirmation={qualification.ConfirmationPresent}; confirmation_source={qualification.Source}; armed={(arm is not null)}; {MarketStateSummary(evidence,market.Symbol)}",
                StrategyVersion=Version,
                DecisionContextKind=DecisionContextKind,
                DecisionContextId=qualification.Source=="1m-microstructure-closed"?MicroContextId(market,scenario,setupKnownAt,qualification):arm is null?ContextId(market,structure):ArmedContextId(market,scenario,structure)
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
        if(TryParseMicroContext(decision.DecisionContextId,out var microScenario,out var setupKnownAt,out var confirmedAt,out var pattern))
        {
            var longMicro=IsLong(microScenario);
            var shortMicro=IsShort(microScenario);
            var expectedMicroAction=longMicro?DecisionAction.OpenLong:shortMicro?DecisionAction.OpenShort:DecisionAction.Hold;
            var qualification=QualifyEntry(market,structure,microScenario,setupKnownAt,true);
            return market.CollectedAt.ToUniversalTime()-setupKnownAt<=ArmedSetupTtl
                &&BiasSupportsScenario(structure.HigherTimeframeBias,microScenario)
                &&qualification.TriggerPresent
                &&qualification.ConfirmationPresent
                &&qualification.ConfirmedAtUtc==confirmedAt
                &&string.Equals(qualification.Pattern,pattern,StringComparison.Ordinal)
                &&EntryStillActionable(market,structure,longMicro,shortMicro,qualification.ConfirmationClose)
                &&expectedMicroAction==decision.Action
                &&string.Equals(MicroContextId(market,microScenario,setupKnownAt,qualification),decision.DecisionContextId,StringComparison.Ordinal)
                &&string.Equals(decision.StrategyVersion,Version,StringComparison.Ordinal);
        }
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

    private sealed record EntryQualification(
        bool TriggerPresent,
        bool ConfirmationPresent,
        decimal ConfirmationClose,
        string Source,
        DateTime? ConfirmedAtUtc,
        string Pattern);

    private sealed record MicroEntrySignal(
        bool TriggerPresent,
        bool ConfirmationPresent,
        decimal ConfirmationClose,
        DateTime? ConfirmedAtUtc,
        string Pattern);

    private static EntryQualification QualifyEntry(
        MarketEvidence market,
        MarketStructureRead structure,
        MarketStructureScenario scenario,
        DateTime setupKnownAtUtc,
        bool armed)
    {
        var frameTrigger=armed?HasTriggerForScenario(scenario,structure.FifteenMinute):structure.TriggerPresent;
        var frameConfirmation=armed?HasConfirmationForScenario(scenario,structure.FifteenMinute):structure.ConfirmationPresent;
        if(frameTrigger&&frameConfirmation)
        {
            var close=structure.ConfirmationClose>0?structure.ConfirmationClose:structure.FifteenMinute.LastClose;
            return new(true,true,close,structure.ConfirmationSource=="none"?"15m-closed":structure.ConfirmationSource,null,structure.FifteenMinute.Event.ToString());
        }

        var micro=FindMicroEntrySignal(market,scenario,setupKnownAtUtc,structure);
        if(micro is not null)
            return new(frameTrigger||micro.TriggerPresent,micro.ConfirmationPresent,micro.ConfirmationClose,"1m-microstructure-closed",micro.ConfirmedAtUtc,micro.Pattern);

        return new(frameTrigger,false,0,frameTrigger?"15m-trigger":"none",null,string.Empty);
    }

    private static MicroEntrySignal? FindMicroEntrySignal(
        MarketEvidence market,
        MarketStructureScenario scenario,
        DateTime setupKnownAtUtc,
        MarketStructureRead structure)
    {
        var observed=market.CollectedAt.Kind==DateTimeKind.Utc?market.CollectedAt:market.CollectedAt.ToUniversalTime();
        var setupKnown=setupKnownAtUtc.Kind==DateTimeKind.Utc?setupKnownAtUtc:setupKnownAtUtc.ToUniversalTime();
        var candles=ConfirmedMarketCandlesV1.Select(market.Candles1m,"1m",observed)
            .Where(x=>x.OpenTime.ToUniversalTime()+TimeSpan.FromMinutes(1)>=setupKnown)
            .TakeLast(12)
            .ToArray();
        if(candles.Length<4)return null;

        var longSide=IsLong(scenario);
        var shortSide=IsShort(scenario);
        if(!longSide&&!shortSide)return null;

        MicroEntrySignal? latestTrigger=null;
        for(var i=3;i<candles.Length;i++)
        {
            var trigger=candles[i-1];
            var prior=candles.Skip(Math.Max(0,i-4)).Take(Math.Min(3,i)).ToArray();
            if(prior.Length<3)continue;
            var confirmation=candles[i];
            var confirmClosedAt=confirmation.OpenTime.ToUniversalTime()+TimeSpan.FromMinutes(1);
            if(confirmClosedAt>observed||observed-confirmClosedAt>TimeSpan.FromMinutes(3))continue;

            var priorLow=prior.Min(x=>x.Low);
            var priorHigh=prior.Max(x=>x.High);
            var microRange=Math.Max(.00000001m,trigger.High-trigger.Low);
            var microBody=Math.Abs(trigger.Close-trigger.Open);
            var lowerWick=Math.Min(trigger.Open,trigger.Close)-trigger.Low;
            var upperWick=trigger.High-Math.Max(trigger.Open,trigger.Close);
            var recentRanges=prior.Select(x=>Math.Max(.00000001m,x.High-x.Low)).ToArray();
            var microAtr=recentRanges.Average();
            var buffer=Math.Max(microAtr*.08m,trigger.Close*.00012m);
            var bullishSweep=trigger.Low<priorLow-buffer&&trigger.Close>priorLow;
            var bearishSweep=trigger.High>priorHigh+buffer&&trigger.Close<priorHigh;
            var bullishReject=trigger.Close>trigger.Open&&lowerWick>=Math.Max(microBody*1.15m,microRange*.35m);
            var bearishReject=trigger.Close<trigger.Open&&upperWick>=Math.Max(microBody*1.15m,microRange*.35m);
            var triggerPresent=longSide?(bullishSweep||bullishReject):(bearishSweep||bearishReject);
            if(!triggerPresent)continue;

            var confirmRange=Math.Max(.00000001m,confirmation.High-confirmation.Low);
            var confirmBodyRatio=Math.Abs(confirmation.Close-confirmation.Open)/confirmRange;
            var confirmed=longSide
                ?confirmation.Close>confirmation.Open&&confirmBodyRatio>=.50m&&confirmation.Close>trigger.High+buffer
                :confirmation.Close<confirmation.Open&&confirmBodyRatio>=.50m&&confirmation.Close<trigger.Low-buffer;
            var pattern=longSide
                ?bullishSweep?"sweep-low-reclaim-break":"bullish-rejection-break"
                :bearishSweep?"sweep-high-reject-break":"bearish-rejection-break";
            latestTrigger=new(true,confirmed,confirmed?confirmation.Close:0,confirmed?confirmation.OpenTime.ToUniversalTime():null,pattern);
            if(confirmed)return latestTrigger;
        }
        return latestTrigger;
    }

    private static DateTime CurrentSetupKnownAt(MarketEvidence market)
    {
        var candle=ConfirmedMarketCandlesV1.Select(market.Candles,"15m",market.CollectedAt).LastOrDefault();
        return candle is null
            ?market.CollectedAt.ToUniversalTime()
            :candle.OpenTime.ToUniversalTime()+TimeSpan.FromMinutes(15);
    }

    private static string MicroContextId(
        MarketEvidence market,
        MarketStructureScenario scenario,
        DateTime setupKnownAtUtc,
        EntryQualification qualification)
    {
        var setup=setupKnownAtUtc.ToUniversalTime();
        var confirmed=(qualification.ConfirmedAtUtc??market.CollectedAt.ToUniversalTime()).ToUniversalTime();
        return $"MICRO|{market.Symbol}|{setup:yyyyMMddHHmmss}|{scenario}|{confirmed:yyyyMMddHHmmss}|{qualification.Pattern}";
    }

    private static bool TryParseMicroContext(
        string? value,
        out MarketStructureScenario scenario,
        out DateTime setupKnownAtUtc,
        out DateTime confirmedAtUtc,
        out string pattern)
    {
        scenario=MarketStructureScenario.None;
        setupKnownAtUtc=default;
        confirmedAtUtc=default;
        pattern=string.Empty;
        if(string.IsNullOrWhiteSpace(value))return false;
        var parts=value.Split('|');
        if(parts.Length!=6||!string.Equals(parts[0],"MICRO",StringComparison.Ordinal))return false;
        if(!DateTime.TryParseExact(parts[2],"yyyyMMddHHmmss",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal,out setupKnownAtUtc))return false;
        if(!Enum.TryParse(parts[3],false,out scenario)||scenario==MarketStructureScenario.None)return false;
        if(!DateTime.TryParseExact(parts[4],"yyyyMMddHHmmss",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal,out confirmedAtUtc))return false;
        pattern=parts[5];
        return !string.IsNullOrWhiteSpace(pattern);
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

    private static bool EntryStillActionable(MarketEvidence market,MarketStructureRead structure,bool longSide,bool shortSide,decimal confirmationCloseOverride=0)
    {
        if(market.Price<=0||(!longSide&&!shortSide))return false;
        var confirmationClose=confirmationCloseOverride>0?confirmationCloseOverride:structure.ConfirmationClose>0?structure.ConfirmationClose:structure.FifteenMinute.LastClose;
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
