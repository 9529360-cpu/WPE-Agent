using System.Text;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class SignalAggregationSkill
{
    public IReadOnlyList<MarketDecisionAssessment> Analyze(EvidencePack evidence,DecisionPolicy policy)
        => evidence.Markets.Values.Select(m=>AnalyzeMarket(m,evidence.Completeness,policy)).OrderByDescending(Quality).ToArray();

    private static MarketDecisionAssessment AnalyzeMarket(MarketEvidence market,int completeness,DecisionPolicy policy)
    {
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

        var positive=signals.Where(x=>x.WeightedScore>0).Sum(x=>x.WeightedScore);
        var negative=-signals.Where(x=>x.WeightedScore<0).Sum(x=>x.WeightedScore);
        var gross=positive+negative;
        var score=signals.Sum(x=>x.WeightedScore);
        var conflict=gross<.0001?1:Math.Clamp(1-Math.Abs(score)/gross,0,1);
        var agreement=gross<.0001?0:Math.Max(positive,negative)/gross;
        var fresh=DateTime.UtcNow-market.CollectedAt<=TimeSpan.FromMinutes(policy.MaximumEvidenceAgeMinutes);
        var confidence=Math.Clamp((Math.Abs(score)*.72+agreement*.28)*(completeness/100d)*(market.Quality.QualityScore/100d)*(fresh?1:.25),0,1);
        var regime=DetectRegime(market);
        var missing=new List<string>();
        if(!fresh)missing.Add(L("Decision.Stale",policy.MaximumEvidenceAgeMinutes));
        if(completeness<policy.MinimumEvidenceCompleteness)missing.Add(L("Decision.Completeness",policy.MinimumEvidenceCompleteness));
        if(Math.Abs(score)<policy.MinimumDirectionalScore)missing.Add(L("Decision.Score",policy.MinimumDirectionalScore,score));
        if(conflict>policy.MaximumConflictRatio)missing.Add(L("Decision.Conflict",policy.MaximumConflictRatio,conflict));
        if(confidence<policy.MinimumConfidence)missing.Add(L("Decision.Confidence",policy.MinimumConfidence,confidence));
        if(market.Quality.QualityScore<policy.MinimumMarketQuality)missing.Add(L("Decision.MarketQuality",policy.MinimumMarketQuality,market.Quality.QualityScore));
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

    private static MarketRegime DetectRegime(MarketEvidence m)
    {
        if(m.Quality.AtrPercent>=.05||m.Quality.LiquidationIntensity>=.80)return MarketRegime.Extreme;
        var one=Math.Sign(m.Trend1h);var four=Math.Sign(m.Trend4h);var aligned=one!=0&&one==four&&Math.Abs(m.Trend1h)>=.003&&Math.Abs(m.Trend4h)>=.006;
        if(aligned&&Math.Sign(m.Trend15m)==one)return MarketRegime.Trending;
        if(aligned)return MarketRegime.Transition;
        if(Math.Abs(m.Trend1h)<.004&&Math.Abs(m.Trend4h)<.008)return MarketRegime.Ranging;
        return MarketRegime.Transition;
    }

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
        new("DataQuality","Validate","MarketEvidence","MarketQualityEvidence","read:market",8,1,true,"quality score and clock skew"),
        new("SignalAggregation","Reason","EvidencePack","MarketDecisionAssessment[]","local:compute",5,0,true,"finite weighted contributions"),
        new("MarketRegime","Reason","multi-timeframe evidence","MarketRegime","local:compute",3,0,true,"known regime result"),
        new("StrategyResearch","Research","candles and costs","ResearchValidationResult","local:compute,write:research",12,0,true,"in/out-sample metrics"),
        new("BrainPlanner","Plan","audited evidence","DecisionPlan","network:brain",30,2,true,"schema-valid response"),
        new("DeterministicPlan","Plan","candidate and market","protected DecisionPlan","local:compute",3,0,true,"entry/stop/take/RR valid"),
        new("DecisionCritic","Critic","candidate and counter-evidence","DecisionReview","local:compute",3,0,true,"blocking reasons available"),
        new("DecisionReviewer","Review","candidate and assessments","approval verdict","local:compute",3,0,true,"verdict deterministic"),
        new("IndependentRiskManager","Risk","plan, account, history","IndependentRiskReview","read:account,local:compute",4,0,true,"all hard checks evaluated"),
        new("RiskAndPositionPlanner","Risk","approved plan and rules","ExecutionIntent[]","read:account,local:compute",4,0,true,"quantity and protection valid"),
        new("PositionManagement","Manage","positions and market","close/adjust intents","read:position,write:intent",8,1,true,"position state reconciled"),
        new("ReliableOrderExecutor","Act","approved intent","confirmed order lifecycle","testnet:trade",20,1,true,"idempotency and exchange confirmation"),
        new("ProtectionRecovery","Recover","positions, orders, intents","RecoveryResult","testnet:trade,write:state",20,1,true,"every position protected"),
        new("DecisionMemory","Memory","decision audit","compressed memory","write:local-db",5,1,false,"database writable"),
        new("ExperienceReplay","Reflect","trade outcomes","strategy performance","read:local-db",8,0,false,"bounded sample window"),
        new("RuntimeMonitor","Monitor","skill and error events","health status","read:telemetry",3,0,false,"recent heartbeat"),
        new("EmergencyClose","Safety","all open positions","flat account confirmation","testnet:trade",90,1,true,"no residual position")
    ];
}
