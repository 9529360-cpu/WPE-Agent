using System.Text;
using System.Text.Json;
using WpeAgent.ModelOff;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class SignalAggregationSkill
{
    private readonly Func<DateTimeOffset> _evaluationClock;

    public SignalAggregationSkill():this(()=>DateTimeOffset.UtcNow){}
    public SignalAggregationSkill(Func<DateTimeOffset> evaluationClock)=>
        _evaluationClock=evaluationClock??throw new ArgumentNullException(nameof(evaluationClock));

    public IReadOnlyList<MarketDecisionAssessment> Analyze(EvidencePack evidence,DecisionPolicy policy,IReadOnlyDictionary<string,StrategySignal>? localSignals=null)
        =>Analyze(evidence,policy,_evaluationClock(),localSignals);

    public IReadOnlyList<MarketDecisionAssessment> Analyze(EvidencePack evidence,DecisionPolicy policy,DateTimeOffset evaluationTimeUtc,IReadOnlyDictionary<string,StrategySignal>? localSignals=null)
    {
        ArgumentNullException.ThrowIfNull(evidence);ArgumentNullException.ThrowIfNull(policy);
        if(evaluationTimeUtc==default||evaluationTimeUtc.Offset!=TimeSpan.Zero)throw new ArgumentException("Signal evaluation time must be an explicit UTC instant.",nameof(evaluationTimeUtc));
        if(evidence.Markets is null)throw new ArgumentException("Market evidence collection is required.",nameof(evidence));
        return evidence.Markets.Values.Select(m=>m is null?InvalidMarket(null):AnalyzeMarket(m,evidence.Completeness,policy,evaluationTimeUtc,localSignals?.GetValueOrDefault(m.Symbol)))
            .OrderByDescending(Quality).ThenBy(x=>x.Symbol,StringComparer.Ordinal).ToArray();
    }

    private static MarketDecisionAssessment AnalyzeMarket(MarketEvidence market,int completeness,DecisionPolicy policy,DateTimeOffset evaluationTimeUtc,StrategySignal? localSignal)
    {
        if(!ValidMarket(market))return InvalidMarket(market?.Symbol);
        var signals=new List<SignalContribution>();
        Add("trend_15m","15m",market.Trend15m,.17,.006);
        Add("trend_1h","1h",market.Trend1h,.19,.012);
        Add("trend_4h","4h",market.Trend4h,.23,.025);
        Add("rsi","15m",market.Rsi-50,.08,20);
        Add("order_flow","derivatives",(double)(market.Derivatives.TakerBuySellRatio-1),.08,.20);
        Add("funding","derivatives",(double)-market.Derivatives.FundingRate,.04,.001);
        Add("crowd","derivatives",(double)(1-market.Derivatives.LongShortRatio),.04,.30);
        Add("basis","derivatives",(double)market.Derivatives.Basis,.03,.003);
        Add("order_book","microstructure",market.Quality.OrderBookImbalance,.06,.35);
            Add("relative_volume","volume",Math.Sign(market.Trend15m)*Math.Max(0,market.Quality.RelativeVolume-1),.04,1);
            if(localSignal is not null) Add("local_strategy","strategy",localSignal.Direction*localSignal.Confidence,.20,1);

        var positive=signals.Where(x=>x.WeightedScore>0).Sum(x=>x.WeightedScore);
        var negative=-signals.Where(x=>x.WeightedScore<0).Sum(x=>x.WeightedScore);
        var gross=positive+negative;
        var score=signals.Sum(x=>x.WeightedScore);
        var conflict=gross<.0001?1:Math.Clamp(1-Math.Abs(score)/gross,0,1);
        var agreement=gross<.0001?0:Math.Max(positive,negative)/gross;
        var maximumAge=TimeSpan.FromMinutes(policy.MaximumEvidenceAgeMinutes);
        var fresh=maximumAge>=TimeSpan.Zero&&market.CollectedAt.Kind==DateTimeKind.Utc&&market.CollectedAt<=evaluationTimeUtc.UtcDateTime&&evaluationTimeUtc.UtcDateTime-market.CollectedAt<=maximumAge;
        var confidence=Math.Clamp((Math.Abs(score)*.72+agreement*.28)*(completeness/100d)*(market.Quality.QualityScore/100d)*(fresh?1:.25),0,1);
        var regime=MarketRegimeClassifier.Detect(market);
        var missing=new List<string>();
        if(!fresh)missing.Add(L("Decision.Stale",policy.MaximumEvidenceAgeMinutes));
        if(completeness<policy.MinimumEvidenceCompleteness)missing.Add(L("Decision.Completeness",policy.MinimumEvidenceCompleteness));
        if(Math.Abs(score)<policy.MinimumDirectionalScore)missing.Add(L("Decision.Score",policy.MinimumDirectionalScore,score));
        if(conflict>policy.MaximumConflictRatio)missing.Add(L("Decision.Conflict",policy.MaximumConflictRatio,conflict));
        if(confidence<policy.MinimumConfidence)missing.Add(L("Decision.Confidence",policy.MinimumConfidence,confidence));
        if(market.Quality.QualityScore<policy.MinimumMarketQuality)missing.Add(L("Decision.MarketQuality",policy.MinimumMarketQuality,market.Quality.QualityScore));
        if(regime==MarketRegime.Extreme)missing.Add(L("Decision.ExtremeRegime"));
        var entryReady=missing.Count==0;
        var action=entryReady?(score>0?DecisionAction.OpenLong:DecisionAction.OpenShort):DecisionAction.Hold;
        var summary=L("Decision.Summary",market.Symbol,regime,score,confidence,conflict,L(entryReady?"Decision.Ready":"Decision.Waiting"));
        return new(){Symbol=market.Symbol,Regime=regime,NetScore=score,Confidence=confidence,ConflictRatio=conflict,Fresh=fresh,EntryReady=entryReady,RecommendedAction=action,Signals=signals,MissingConditions=missing,Summary=summary};

        void Add(string name,string horizon,double raw,double weight,double scale)
        {
            var normalized=Math.Clamp(raw/scale,-1,1);var weighted=normalized*weight;
            signals.Add(new(name,horizon,raw,weight,weighted,weighted>.0001?"LONG":weighted<-.0001?"SHORT":"NEUTRAL",name));
        }
    }

    private static bool ValidMarket(MarketEvidence? market)=>market is not null&&market.Derivatives is not null&&MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)&&
        !string.IsNullOrWhiteSpace(market.Symbol)&&market.Price>0&&market.CollectedAt!=default&&market.CollectedAt.Kind==DateTimeKind.Utc&&
        market.Rsi is>=0 and<=100&&market.Support>0&&market.Support<=market.Price&&market.Resistance>=market.Price&&market.Quality.QualityScore is>=0 and<=100&&market.Quality.LiquidityScore is>=0 and<=1&&
        Finite(market.Rsi,market.Trend15m,market.Trend1h,market.Trend4h,market.Quality.OrderBookImbalance,market.Quality.RelativeVolume,market.Quality.AtrPercent,market.Quality.LiquidationIntensity);
    private static bool Finite(params double[] values)=>values.All(double.IsFinite);
    private static MarketDecisionAssessment InvalidMarket(string? symbol)=>new()
    {
        Symbol=string.IsNullOrWhiteSpace(symbol)?"UNKNOWN":symbol,Regime=MarketRegime.Unknown,Fresh=false,EntryReady=false,
        RecommendedAction=DecisionAction.Hold,MissingConditions=["signal.invalid-market-evidence"],Summary="signal.invalid-market-evidence"
    };

    private static double Quality(MarketDecisionAssessment x)=>Math.Abs(x.NetScore)*(1-x.ConflictRatio)+(x.Fresh?.05:0);
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}

public sealed class DecisionGovernanceSkill
{
    public DecisionReview Review(DecisionPlan proposed,IReadOnlyList<MarketDecisionAssessment> assessments,EvidencePack evidence,DecisionPolicy policy)
    {
        var blocks=new List<string>();
        var assessment=assessments.FirstOrDefault(x=>x.Symbol.Equals(proposed.Instrument,StringComparison.OrdinalIgnoreCase));
        var riskIncreasing=proposed.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort or DecisionAction.Lock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
        if(assessment is null)blocks.Add(L("Review.NoAssessment"));
        if(evidence.Completeness<policy.MinimumEvidenceCompleteness&&riskIncreasing)blocks.Add(L("Review.Incomplete"));
        if(assessment is{Fresh:false}&&riskIncreasing)blocks.Add(L("Review.Stale"));
        if(proposed.Confidence<policy.MinimumConfidence&&riskIncreasing)blocks.Add(L("Review.BrainConfidence",proposed.Confidence,policy.MinimumConfidence));
        if(assessment is{EntryReady:false}&&riskIncreasing)blocks.AddRange(assessment.MissingConditions);
        if(assessment is not null&&riskIncreasing)
        {
            var wantsLong=proposed.Action is DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong;
            var wantsShort=proposed.Action is DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort;
            if((wantsLong&&assessment.NetScore<0)||(wantsShort&&assessment.NetScore>0))blocks.Add(L("Review.DirectionConflict"));
        }
        var final=blocks.Count==0?proposed:CopyAsHold(proposed,blocks);
        var accepted=blocks.Count==0;
        var explanation=Explain(final,assessment,blocks);
        return new(){Decision=final,Accepted=accepted,Verdict=L(accepted?(final.Action==DecisionAction.Hold?"Review.Hold":"Review.Accepted"):"Review.Rejected"),BlockingReasons=blocks.Distinct().ToArray(),Explanation=explanation};
    }

    private static DecisionPlan CopyAsHold(DecisionPlan p,IReadOnlyList<string> blocks)=>new()
    {
        Action=DecisionAction.Hold,Instrument=p.Instrument,TargetTier=0,Confidence=p.Confidence,Invalidation=p.Invalidation,Regime=p.Regime,Reason=p.Reason,
        EvidenceReferences=p.EvidenceReferences,MissingConditions=blocks.Distinct().ToList(),ConflictSummary=p.ConflictSummary
    };

    private static string Explain(DecisionPlan decision,MarketDecisionAssessment? assessment,IReadOnlyList<string> blocks)
    {
        var b=new StringBuilder();b.Append($"{decision.Action} · Brain {decision.Confidence:P0}");
        if(assessment is not null)
        {
            b.Append(L("Review.Aggregation",assessment.Confidence,assessment.ConflictRatio)).Append('\n');
            b.Append(assessment.Summary);
            var strongest=assessment.Signals.OrderByDescending(x=>Math.Abs(x.WeightedScore)).Take(5).Select(x=>L("Review.Signal",L("SignalName."+x.Name),L("Direction."+x.Direction),x.Weight,x.WeightedScore));
            b.Append('\n').Append(L("Review.Signals")).Append(string.Join("; ",strongest));
        }
        b.Append('\n').Append(L("Review.Reason")).Append(decision.Reason);
        var missing=blocks.Concat(decision.MissingConditions).Concat(assessment?.MissingConditions??Array.Empty<string>()).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if(missing.Length>0)b.Append('\n').Append(L("Review.Missing")).Append(string.Join("; ",missing));
        return b.ToString();
    }
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}

public sealed record SkillDescriptor(string Name,string Category,string Input,string Output,string Permission,int TimeoutSeconds,int Retries,bool Critical,string HealthCheck);
public sealed class AgentSkillRegistry
{
    public static IReadOnlyList<SkillDescriptor> Skills { get; } =
    [
        new("EvidenceCollector","Observe","symbols","EvidencePack","read:market,read:account,network:rss",25,2,true,"market freshness and source quorum"),
        new("RealTimeMarket","Observe","testnet websocket streams","RealtimeMarketSnapshot","network:testnet,read:market",20,0,true,"fresh book, trades and account stream"),
        new("NewsResearch","Observe","multi-source feeds and articles","NewsEvidence[]","network:web,write:news-index",30,1,false,"source quorum, full text and corroboration"),
        new("HistoricalData","Research","symbol and time range","hourly candles","network:testnet,write:history",60,2,true,"coverage and continuity"),
        new("DataQuality","Validate","MarketEvidence","MarketQualityEvidence","read:market",8,1,true,"quality score and clock skew"),
        new("SignalAggregation","Reason","EvidencePack","MarketDecisionAssessment[]","local:compute",5,0,true,"finite weighted contributions"),
        new("MarketRegime","Reason","multi-timeframe evidence","MarketRegime","local:compute",3,0,true,"known regime result"),
        new("StrategyResearch","Research","candles and costs","ResearchValidationResult","local:compute,write:research",12,0,true,"in/out-sample metrics"),
        new("PortfolioRisk","Risk","positions, intents and returns","PortfolioRiskAssessment","read:account,local:compute",6,0,true,"VaR, CVaR, concentration and correlation"),
        new("BrainPlanner","Plan","audited evidence","DecisionPlan","local:compute",3,0,true,"deterministic local plan and schema-valid response"),
        new("OptionalRemoteBrain","Assist","audited evidence","advisory DecisionPlan","network:brain",30,2,false,"optional remote provider; never required for execution"),
        new("DeterministicPlan","Plan","candidate and market","protected DecisionPlan","local:compute",3,0,true,"entry/stop/take/RR valid"),
        new("DecisionCritic","Critic","candidate and counter-evidence","DecisionReview","local:compute",3,0,true,"blocking reasons available"),
        new("DecisionReviewer","Review","candidate and assessments","approval verdict","local:compute",3,0,true,"verdict deterministic"),
        new("IndependentRiskManager","Risk","plan, account, history","IndependentRiskReview","read:account,local:compute",4,0,true,"all hard checks evaluated"),
        new("RiskAndPositionPlanner","Risk","approved plan and rules","ExecutionIntent[]","read:account,local:compute",4,0,true,"quantity and protection valid"),
        new("PositionManagement","Manage","positions and market","close/adjust intents","read:position,write:intent",8,1,true,"position state reconciled"),
        new("ReliableOrderExecutor","Act","approved intent","confirmed order lifecycle","testnet:trade",20,1,true,"idempotency and exchange confirmation"),
        new("ProtectionRecovery","Recover","positions, orders, intents","RecoveryResult","testnet:trade,write:state",20,1,true,"every position protected"),
        new("ProtectionAudit","Recover","positions, orders and persisted intents","RecoveryResult","testnet:trade,write:state",20,1,true,"position protection reconciled"),
        new("DecisionMemory","Memory","decision audit","compressed memory","write:local-db",5,1,false,"database writable"),
        new("ExperienceReplay","Reflect","trade outcomes","strategy performance","read:local-db",8,0,false,"bounded sample window"),
        new("RuntimeMonitor","Monitor","skill and error events","health status","read:telemetry",3,0,false,"recent heartbeat"),
        new("EmergencyClose","Safety","all open positions","flat account confirmation","testnet:trade",90,1,true,"no residual position")
    ];
}

public enum ModelOffResearchCapabilityV1 { News, Macro, Technical, Fundamental, Backtest }

public sealed record ModelOffResearchInputV1(
    ModelOffResearchCapabilityV1 Capability,
    string OutputId,
    string CycleId,
    DateTimeOffset EvaluationTimeUtc,
    string InputSchema,
    string MethodId,
    string MethodVersion,
    IReadOnlyList<ModelOffSourceV1> Sources,
    JsonElement Facts,
    IReadOnlyList<JsonElement> Calculations);

public static class DeterministicResearchCapabilityProducerV1
{
    public const string InputSchema = "wpe.research-input/1.0";
    public const string MethodVersion = "1.0";
    public const string TemplateVersion = ModelOffFixedTemplatesV1.SummaryVersion;

    public static ModelOffAgentOutputV1 Produce(ModelOffResearchInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireToken(input.OutputId, nameof(input.OutputId));
        RequireToken(input.CycleId, nameof(input.CycleId));
        RequireToken(input.MethodId, nameof(input.MethodId));
        RequireToken(input.MethodVersion, nameof(input.MethodVersion));
        if (!string.Equals(input.InputSchema, InputSchema, StringComparison.Ordinal))
            throw new ArgumentException("A versioned research input schema is required.", nameof(input));
        if (!string.Equals(input.MethodId, ExpectedMethodId(input.Capability), StringComparison.Ordinal) ||
            !string.Equals(input.MethodVersion, MethodVersion, StringComparison.Ordinal))
            throw new ArgumentException("The capability method identity and version must match the frozen contract.", nameof(input));
        if (input.EvaluationTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Research evaluation time must be injected in UTC.", nameof(input));
        if (input.Sources is null || input.Sources.Any(source => source is null))
            throw new ArgumentException("Research sources cannot be null.", nameof(input));
        if (input.Calculations is null)
            throw new ArgumentException("Research calculations cannot be null.", nameof(input));
        if (input.Facts.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Research facts must be an explicit JSON object.", nameof(input));

        var reasons = RefusalReasons(input).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var succeeded = reasons.Length == 0;
        var missing = reasons.Where(reason => reason.StartsWith("research.missing", StringComparison.Ordinal)).ToArray();
        var canonicalSources = input.Sources.Where(IsCanonicalSourceMetadata).ToArray();
        return new ModelOffAgentOutputV1(
            ModelOffAgentV1.Research,
            input.OutputId,
            input.CycleId,
            input.EvaluationTimeUtc,
            input.EvaluationTimeUtc,
            input.InputSchema,
            new ModelOffRuleSetV1(input.MethodId, input.MethodVersion),
            canonicalSources,
            succeeded ? ModelOffOutputStatusV1.Succeeded : ModelOffOutputStatusV1.Abstained,
            new ModelOffUncertaintyV1(succeeded ? ModelOffUncertaintyLevelV1.None : ModelOffUncertaintyLevelV1.Unknown, reasons, missing),
            input.Facts.Clone(),
            input.Calculations.Select(value => value.Clone()).ToArray(),
            new ModelOffDecisionV1(succeeded ? "record_research" : "abstain", succeeded, reasons),
            Array.Empty<JsonElement>(),
            TemplateVersion);
    }

    private static IEnumerable<string> RefusalReasons(ModelOffResearchInputV1 input)
    {
        if (input.Sources.Count == 0)
            yield return "research.missing.sources";
        if (!input.Facts.EnumerateObject().Any())
            yield return "research.missing.facts";

        var requiredKind = input.Capability switch
        {
            ModelOffResearchCapabilityV1.News => ModelOffSourceKindV1.News,
            ModelOffResearchCapabilityV1.Macro => ModelOffSourceKindV1.Macro,
            ModelOffResearchCapabilityV1.Fundamental => ModelOffSourceKindV1.Fundamental,
            ModelOffResearchCapabilityV1.Technical => ModelOffSourceKindV1.Market,
            ModelOffResearchCapabilityV1.Backtest => ModelOffSourceKindV1.Strategy,
            _ => (ModelOffSourceKindV1?)null
        };
        if (requiredKind.HasValue && !input.Sources.Any(source => source.Kind == requiredKind.Value))
            yield return $"research.missing.{requiredKind.Value.ToString().ToLowerInvariant()}_source";

        if (input.Capability == ModelOffResearchCapabilityV1.Macro)
            foreach (var reason in ValidateMacroFacts(input))
                yield return reason;
        if (input.Capability == ModelOffResearchCapabilityV1.Fundamental)
            foreach (var reason in ValidateFundamentalFacts(input))
                yield return reason;
        if(input.Capability==ModelOffResearchCapabilityV1.Technical)
            foreach(var reason in ValidateTechnicalFacts(input))yield return reason;
        if(input.Capability==ModelOffResearchCapabilityV1.Backtest)
            foreach(var reason in ValidateBacktestFacts(input))yield return reason;

        foreach (var source in input.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceId) || !source.AsOfUtc.HasValue || !source.ReceivedAtUtc.HasValue || string.IsNullOrWhiteSpace(source.ArtifactHash))
                yield return "research.invalid.source_metadata";
            if (source.AsOfUtc > input.EvaluationTimeUtc || source.ReceivedAtUtc > input.EvaluationTimeUtc)
                yield return "research.invalid.future_source";
            if (source.Status != ModelOffSourceStatusV1.Available)
                yield return $"research.source.{source.Status.ToString().ToLowerInvariant()}";
        }
    }

    private static IEnumerable<string> ValidateMacroFacts(ModelOffResearchInputV1 input)
    {
        var facts = input.Facts;
        if (!StringFact(facts, "schema", out var schema) || !string.Equals(schema, "wpe.macro-facts/1.0", StringComparison.Ordinal))
            yield return "research.invalid.macro_schema";
        foreach (var name in new[] { "indicatorId", "geography", "frequency", "unit" })
            if (!StringFact(facts, name, out _))
                yield return $"research.missing.macro_{name.ToLowerInvariant()}";

        if (!facts.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _))
            yield return "research.invalid.macro_value";

        var observed = UtcFact(facts, "observationAtUtc", out var observedAt);
        var released = UtcFact(facts, "releasedAtUtc", out var releasedAt);
        if (!observed) yield return "research.invalid.macro_observation_time";
        if (!released) yield return "research.invalid.macro_release_time";
        if (!observed || !released) yield break;
        if (observedAt > releasedAt) yield return "research.invalid.macro_temporal_order";
        if (releasedAt > input.EvaluationTimeUtc) yield return "research.invalid.macro_future_release";
        if (!input.Sources.Any(source => source.Kind == ModelOffSourceKindV1.Macro && source.AsOfUtc >= releasedAt))
            yield return "research.invalid.macro_source_asof";
    }

    private static IEnumerable<string> ValidateFundamentalFacts(ModelOffResearchInputV1 input)
    {
        var facts=input.Facts;if(!StringFact(facts,"schema",out var schema)||schema!="wpe.crypto-instrument-fundamental/1.0")yield return "research.invalid.fundamental_schema";
        var required=new[]{"providerId","environment","symbol","nativeSymbol","baseAsset","quoteAsset","marginAsset","contractType","tradingStatus","sourceArtifactSha256","canonicalSha256"};foreach(var name in required)if(!StringFact(facts,name,out _))yield return $"research.missing.fundamental_{name.ToLowerInvariant()}";
        if(StringFact(facts,"providerId",out var providerId)&&providerId!="binance-futures")yield return "research.invalid.fundamental_provider";
        if(StringFact(facts,"environment",out var environment)&&environment!="Testnet")yield return "research.invalid.fundamental_environment";
        if(StringFact(facts,"contractType",out var contract)&&contract!="PERPETUAL")yield return "research.invalid.fundamental_contract";
        if(StringFact(facts,"tradingStatus",out var status)&&status!="TRADING")yield return "research.invalid.fundamental_status";
        if(StringFact(facts,"symbol",out var symbol)&&StringFact(facts,"nativeSymbol",out var nativeSymbol)&&StringFact(facts,"baseAsset",out var baseAsset)&&StringFact(facts,"quoteAsset",out var quoteAsset)&&(!CanonicalSymbol(symbol)||nativeSymbol!=symbol||symbol!=baseAsset+quoteAsset))yield return "research.invalid.fundamental_symbol";
        if(StringFact(facts,"quoteAsset",out quoteAsset)&&StringFact(facts,"marginAsset",out var marginAsset)&&(quoteAsset!=marginAsset||!CanonicalAsset(quoteAsset)||!CanonicalAsset(marginAsset)))yield return "research.invalid.fundamental_margin";
        if(StringFact(facts,"sourceArtifactSha256",out var sourceHash)&&!ValidSha(sourceHash)||StringFact(facts,"canonicalSha256",out var canonicalHash)&&!ValidSha(canonicalHash))yield return "research.invalid.fundamental_hash";
        var onboard=UtcFact(facts,"onboardAtUtc",out var onboardAt);var observed=UtcFact(facts,"observedAtUtc",out var observedAt);if(!onboard)yield return "research.invalid.fundamental_onboard_time";if(!observed)yield return "research.invalid.fundamental_observed_time";if(onboard&&observed&&onboardAt>observedAt)yield return "research.invalid.fundamental_temporal_order";if(observed&&observedAt>input.EvaluationTimeUtc)yield return "research.invalid.fundamental_future";if(observed&&input.EvaluationTimeUtc-observedAt>TimeSpan.FromHours(24))yield return "research.invalid.fundamental_stale";
        if(observed&&StringFact(facts,"canonicalSha256",out canonicalHash)&&!input.Sources.Any(x=>x.Kind==ModelOffSourceKindV1.Fundamental&&x.AsOfUtc>=observedAt&&x.ArtifactHash=="sha256:"+canonicalHash))yield return "research.invalid.fundamental_source_asof";
        if(onboard&&observed&&StringFact(facts,"providerId",out var provider)&&StringFact(facts,"environment",out environment)&&StringFact(facts,"symbol",out symbol)&&StringFact(facts,"nativeSymbol",out var native)&&StringFact(facts,"baseAsset",out baseAsset)&&StringFact(facts,"quoteAsset",out quoteAsset)&&StringFact(facts,"marginAsset",out marginAsset)&&StringFact(facts,"contractType",out contract)&&StringFact(facts,"tradingStatus",out status)&&StringFact(facts,"sourceArtifactSha256",out sourceHash)&&StringFact(facts,"canonicalSha256",out canonicalHash))
        {
            var expected=CryptoInstrumentFundamentalCanonicalizerV1.Create(provider,environment,symbol,native,baseAsset,quoteAsset,marginAsset,contract,status,onboardAt,observedAt,sourceHash);if(!string.Equals(expected.CanonicalSha256,canonicalHash,StringComparison.Ordinal))yield return "research.invalid.fundamental_canonical_hash";
        }
    }

    private static IEnumerable<string> ValidateTechnicalFacts(ModelOffResearchInputV1 input)
    {
        var facts=input.Facts;if(!StringFact(facts,"schema",out var schema)||schema!="wpe.technical-assessment/1.0")yield return "research.invalid.technical_schema";
        if(!StringFact(facts,"symbol",out var symbol)||!CanonicalSymbol(symbol))yield return "research.invalid.technical_symbol";
        if(!StringFact(facts,"marketSymbol",out var marketSymbol)||!CanonicalSymbol(marketSymbol)||!string.Equals(symbol,marketSymbol,StringComparison.Ordinal))yield return "research.invalid.technical_symbol_binding";
        if(!StringFact(facts,"marketEvidenceSha256",out var hash)||!ValidSha(hash))yield return "research.invalid.technical_hash";
        var observed=UtcFact(facts,"observedAtUtc",out var observedAt);if(!observed)yield return "research.invalid.technical_observed_time";else{if(observedAt>input.EvaluationTimeUtc)yield return "research.invalid.technical_future";if(input.EvaluationTimeUtc-observedAt>TimeSpan.FromMinutes(5))yield return "research.invalid.technical_stale";}
        foreach(var name in new[]{"trend15m","trend1h","trend4h"})if(!FiniteFact(facts,name,out var value)||Math.Abs(value)>100)yield return $"research.invalid.technical_{name.ToLowerInvariant()}";
        if(!FiniteFact(facts,"rsi",out var rsi)||rsi is <0 or >100)yield return "research.invalid.technical_rsi";
        if(observed&&ValidSha(hash)&&!input.Sources.Any(x=>x.Kind==ModelOffSourceKindV1.Market&&x.AsOfUtc>=observedAt&&x.ArtifactHash=="sha256:"+hash&&x.SourceId=="market:"+symbol))yield return "research.invalid.technical_source";
    }

    private static IEnumerable<string> ValidateBacktestFacts(ModelOffResearchInputV1 input)
    {
        var facts=input.Facts;
        if(!StringFact(facts,"schema",out var schema)||schema!=BacktestValidationCanonicalizerV1.Schema)yield return "research.invalid.backtest_schema";
        if(!StringFact(facts,"symbol",out var symbol)||!CanonicalSymbol(symbol))yield return "research.invalid.backtest_symbol";
        if(!StringFact(facts,"strategyVersion",out var strategyVersion)||strategyVersion.Length>128||!strategyVersion.All(c=>char.IsLetterOrDigit(c)||c is '-' or '_' or '.'))yield return "research.invalid.backtest_strategy_version";
        var validated=UtcFact(facts,"validatedAtUtc",out var validatedAt);
        if(!validated)yield return "research.invalid.backtest_validation_time";
        else if(validatedAt>input.EvaluationTimeUtc)yield return "research.invalid.backtest_future";
        else if(input.EvaluationTimeUtc-validatedAt>TimeSpan.FromHours(24))yield return "research.invalid.backtest_stale";
        if(!IntFact(facts,"sampleSize",out var sampleSize)||sampleSize<1)yield return "research.invalid.backtest_sample_size";
        if(!IntFact(facts,"trades",out var trades)||trades<0||trades>sampleSize)yield return "research.invalid.backtest_trades";
        if(!IntFact(facts,"outOfSampleTrades",out var outOfSampleTrades)||outOfSampleTrades<0||outOfSampleTrades>trades)yield return "research.invalid.backtest_oos_trades";
        if(!IntFact(facts,"coverageDays",out var coverageDays)||coverageDays<0)yield return "research.invalid.backtest_coverage";
        foreach(var name in new[]{"winRate","maxDrawdown","walkForwardScore","monteCarloLossProbability","qualityScore"})
            if(!FiniteFact(facts,name,out var value)||value is<0 or>1)yield return $"research.invalid.backtest_{name.ToLowerInvariant()}";
        if(!FiniteFact(facts,"profitFactor",out var profitFactor)||profitFactor<0)yield return "research.invalid.backtest_profit_factor";
        foreach(var name in new[]{"expectancy","sharpe","outOfSampleReturn"})if(!FiniteFact(facts,name,out _))yield return $"research.invalid.backtest_{name.ToLowerInvariant()}";
        if(!BoolFact(facts,"approved",out var approved))yield return "research.invalid.backtest_approved";
        if(!BoolFact(facts,"promoted",out var promoted))yield return "research.invalid.backtest_promoted";
        if(promoted&&!approved)yield return "research.invalid.backtest_lifecycle";
        if(!StringFact(facts,"canonicalSha256",out var canonicalHash)||!ValidSha(canonicalHash))yield return "research.invalid.backtest_hash";
        if(validated&&CanonicalSymbol(symbol)&&!string.IsNullOrWhiteSpace(strategyVersion)&&ValidSha(canonicalHash)&&
           sampleSize>=1&&trades>=0&&trades<=sampleSize&&outOfSampleTrades>=0&&outOfSampleTrades<=trades&&coverageDays>=0&&
           FiniteFact(facts,"winRate",out var winRate)&&FiniteFact(facts,"profitFactor",out profitFactor)&&FiniteFact(facts,"expectancy",out var expectancy)&&
           FiniteFact(facts,"maxDrawdown",out var maxDrawdown)&&FiniteFact(facts,"sharpe",out var sharpe)&&FiniteFact(facts,"outOfSampleReturn",out var oosReturn)&&
           FiniteFact(facts,"walkForwardScore",out var walkForward)&&FiniteFact(facts,"monteCarloLossProbability",out var monteCarlo)&&FiniteFact(facts,"qualityScore",out var qualityScore)&&
           BoolFact(facts,"approved",out approved)&&BoolFact(facts,"promoted",out promoted))
        {
            var expected=BacktestValidationCanonicalizerV1.Create(symbol,strategyVersion,validatedAt,sampleSize,trades,outOfSampleTrades,coverageDays,winRate,profitFactor,expectancy,maxDrawdown,sharpe,oosReturn,walkForward,monteCarlo,qualityScore,approved,promoted);
            if(!string.Equals(expected.CanonicalSha256,canonicalHash,StringComparison.Ordinal))yield return "research.invalid.backtest_canonical_hash";
            if(!input.Sources.Any(x=>x.Kind==ModelOffSourceKindV1.Strategy&&x.SourceId==$"backtest:{symbol}:{strategyVersion}"&&x.AsOfUtc>=validatedAt&&x.ArtifactHash=="sha256:"+canonicalHash))yield return "research.invalid.backtest_source";
        }
    }

    private static bool ValidSha(string value)=>value.Length==64&&value.All(Uri.IsHexDigit);
    private static bool CanonicalSymbol(string value)=>value.Length is>=5 and<=30&&value.All(c=>c is>='A' and<='Z' or>='0' and<='9');
    private static bool CanonicalAsset(string value)=>value.Length is>=2 and<=16&&value.All(c=>c is>='A' and<='Z' or>='0' and<='9');
    private static bool FiniteFact(JsonElement facts,string name,out double value){value=0;return facts.TryGetProperty(name,out var element)&&element.ValueKind==JsonValueKind.Number&&element.TryGetDouble(out value)&&double.IsFinite(value);}
    private static bool IntFact(JsonElement facts,string name,out int value){value=0;return facts.TryGetProperty(name,out var element)&&element.ValueKind==JsonValueKind.Number&&element.TryGetInt32(out value);}
    private static bool BoolFact(JsonElement facts,string name,out bool value)
    {
        value=false;
        if(!facts.TryGetProperty(name,out var element)||element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return false;
        value=element.GetBoolean();
        return true;
    }

    private static bool StringFact(JsonElement facts, string name, out string value)
    {
        value = string.Empty;
        return facts.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = element.GetString() ?? string.Empty);
    }

    private static bool UtcFact(JsonElement facts, string name, out DateTimeOffset value)
    {
        value = default;
        return facts.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String &&
               element.TryGetDateTimeOffset(out value) && value.Offset == TimeSpan.Zero;
    }

    private static void RequireToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty deterministic identifier is required.", name);
    }

    private static bool IsCanonicalSourceMetadata(ModelOffSourceV1 source) =>
        !string.IsNullOrWhiteSpace(source.SourceId) &&
        Enum.IsDefined(source.Kind) &&
        source.AsOfUtc.HasValue &&
        source.ReceivedAtUtc.HasValue &&
        Enum.IsDefined(source.Status) &&
        !string.IsNullOrWhiteSpace(source.ArtifactHash);

    private static string ExpectedMethodId(ModelOffResearchCapabilityV1 capability) =>
        $"wpe.{capability.ToString().ToLowerInvariant()}-method";
}
