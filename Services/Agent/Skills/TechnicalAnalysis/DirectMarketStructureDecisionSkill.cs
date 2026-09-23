namespace 币安量化机器人.Services.Agent;

public static class DirectMarketStructureDecisionSkill
{
    public const string DecisionContextKind = "market-structure-direct";
    public const string Version = "market-structure-direct-v3";

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
            if(structure.Scenario==MarketStructureScenario.None)continue;
            if(!structure.TriggerPresent)
            {
                holdReason=$"{market.Symbol}: direct structure scenario is present but the entry trigger is still waiting.";
                continue;
            }
            if(!structure.ConfirmationPresent)
            {
                holdReason=$"{market.Symbol}: direct structure trigger is present but confirmation is still waiting.";
                continue;
            }

            var longSide=IsLong(structure.Scenario);
            var shortSide=IsShort(structure.Scenario);
            if(!longSide&&!shortSide)continue;
            if(!EntryStillActionable(market,structure,longSide,shortSide))
            {
                holdReason=$"{market.Symbol}: confirmed structure exists but the live price has moved beyond the bounded entry zone.";
                continue;
            }

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
                Confidence=0,
                EntryPrice=entry,
                StopLossPrice=stop,
                TakeProfitPrice=take,
                Regime=structure.HigherTimeframeBias.ToString(),
                Reason=$"Direct local candle-structure decision: {structure.Narrative}",
                Invalidation=longSide
                    ?$"Exit if local structure breaks below {stop:F2} or higher-timeframe bias turns bearish."
                    :$"Exit if local structure breaks above {stop:F2} or higher-timeframe bias turns bullish.",
                EvidenceReferences=structure.Evidence.Append("decision_path=direct-market-structure").Append("entry_qualification=trigger-plus-confirmation").Append("live_entry_guard=bounded-chase").Append("target_geometry=structural-opposite-boundary").ToList(),
                MissingConditions=[],
                ConflictSummary=$"direct-structure; scenario={structure.Scenario}; event={structure.FifteenMinute.Event}; confirmation={structure.ConfirmationPresent}",
                StrategyVersion=Version,
                DecisionContextKind=DecisionContextKind,
                DecisionContextId=ContextId(market,structure),
                RiskBudgetMultiplier=1
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
        if(!structure.Available||structure.Scenario==MarketStructureScenario.None||!structure.TriggerPresent||!structure.ConfirmationPresent)return false;
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

    private static bool IsLong(MarketStructureScenario scenario)=>scenario is
        MarketStructureScenario.TrendPullbackLong or MarketStructureScenario.RangeReversionLong or MarketStructureScenario.BreakoutRetestLong;

    private static bool IsShort(MarketStructureScenario scenario)=>scenario is
        MarketStructureScenario.TrendPullbackShort or MarketStructureScenario.RangeReversionShort or MarketStructureScenario.BreakoutRetestShort;

    private static DecisionPlan Hold(MarketEvidence? market,string reason,IMarketStructureAnalysisTool analysisTool)=>new()
    {
        Action=DecisionAction.Hold,
        Instrument=market?.Symbol??string.Empty,
        TargetTier=0,
        Confidence=0,
        Regime=market is null?MarketRegime.Unknown.ToString():analysisTool.Analyze(market).HigherTimeframeBias.ToString(),
        Reason=reason,
        Invalidation="No risk-increasing decision exists.",
        StrategyVersion=Version,
        DecisionContextKind="market-observation",
        RiskBudgetMultiplier=0
    };
}
