using System.Text;

namespace 币安量化机器人.Services.Agent;

public sealed class SignalAggregationSkill
{
    public IReadOnlyList<MarketDecisionAssessment> Analyze(EvidencePack evidence,DecisionPolicy policy)
        => evidence.Markets.Values.Select(m=>AnalyzeMarket(m,evidence.Completeness,policy)).OrderByDescending(Quality).ToArray();

    private static MarketDecisionAssessment AnalyzeMarket(MarketEvidence market,int completeness,DecisionPolicy policy)
    {
        var signals=new List<SignalContribution>();
        Add("15分钟趋势","15m",market.Trend15m,.20,.006,"短周期执行动量");
        Add("1小时趋势","1h",market.Trend1h,.22,.012,"中周期方向");
        Add("4小时趋势","4h",market.Trend4h,.28,.025,"高周期背景");
        Add("RSI","15m",market.Rsi-50,.10,20,"相对强弱偏离50");
        Add("主动买卖比","衍生品",(double)(market.Derivatives.TakerBuySellRatio-1),.08,.20,"主动成交方向");
        Add("资金费率反向拥挤","衍生品",(double)-market.Derivatives.FundingRate,.04,.001,"高资金费率按拥挤反向计分");
        Add("多空账户反向拥挤","衍生品",(double)(1-market.Derivatives.LongShortRatio),.04,.30,"账户多空比按拥挤反向计分");
        Add("基差","衍生品",(double)market.Derivatives.Basis,.04,.003,"期现基差方向");

        var positive=signals.Where(x=>x.WeightedScore>0).Sum(x=>x.WeightedScore);
        var negative=-signals.Where(x=>x.WeightedScore<0).Sum(x=>x.WeightedScore);
        var gross=positive+negative;
        var score=signals.Sum(x=>x.WeightedScore);
        var conflict=gross<.0001?1:Math.Clamp(1-Math.Abs(score)/gross,0,1);
        var agreement=gross<.0001?0:Math.Max(positive,negative)/gross;
        var fresh=DateTime.UtcNow-market.CollectedAt<=TimeSpan.FromMinutes(policy.MaximumEvidenceAgeMinutes);
        var confidence=Math.Clamp((Math.Abs(score)*.75+agreement*.25)*(completeness/100d)*(fresh?1:.25),0,1);
        var regime=DetectRegime(market);
        var missing=new List<string>();
        if(!fresh)missing.Add($"行情超过 {policy.MaximumEvidenceAgeMinutes} 分钟，必须刷新");
        if(completeness<policy.MinimumEvidenceCompleteness)missing.Add($"证据完整度需达到 {policy.MinimumEvidenceCompleteness}/100");
        if(Math.Abs(score)<policy.MinimumDirectionalScore)missing.Add($"方向净分需达到 ±{policy.MinimumDirectionalScore:F2}（当前 {score:+0.00;-0.00;0.00}）");
        if(conflict>policy.MaximumConflictRatio)missing.Add($"冲突率需降至 {policy.MaximumConflictRatio:P0} 以下（当前 {conflict:P0}）");
        if(confidence<policy.MinimumConfidence)missing.Add($"聚合置信度需达到 {policy.MinimumConfidence:P0}（当前 {confidence:P0}）");
        var entryReady=missing.Count==0;
        var action=entryReady?(score>0?DecisionAction.OpenLong:DecisionAction.OpenShort):DecisionAction.Hold;
        var summary=$"{market.Symbol} · {regime} · 净分 {score:+0.00;-0.00;0.00} · 置信 {confidence:P0} · 冲突 {conflict:P0} · {(entryReady?"满足入场条件":"等待条件")}";
        return new(){Symbol=market.Symbol,Regime=regime,NetScore=score,Confidence=confidence,ConflictRatio=conflict,Fresh=fresh,EntryReady=entryReady,RecommendedAction=action,Signals=signals,MissingConditions=missing,Summary=summary};

        void Add(string name,string horizon,double raw,double weight,double scale,string explanation)
        {
            var normalized=Math.Clamp(raw/scale,-1,1);var weighted=normalized*weight;
            signals.Add(new(name,horizon,raw,weight,weighted,weighted>.0001?"多":weighted<-.0001?"空":"中性",explanation));
        }
    }

    private static MarketRegime DetectRegime(MarketEvidence m)
    {
        var one=Math.Sign(m.Trend1h);var four=Math.Sign(m.Trend4h);var aligned=one!=0&&one==four&&Math.Abs(m.Trend1h)>=.003&&Math.Abs(m.Trend4h)>=.006;
        if(aligned&&Math.Sign(m.Trend15m)==one)return MarketRegime.Trending;
        if(aligned)return MarketRegime.Transition;
        if(Math.Abs(m.Trend1h)<.004&&Math.Abs(m.Trend4h)<.008)return MarketRegime.Ranging;
        return MarketRegime.Transition;
    }

    private static double Quality(MarketDecisionAssessment x)=>Math.Abs(x.NetScore)*(1-x.ConflictRatio)+(x.Fresh?.05:0);
}

public sealed class DecisionGovernanceSkill
{
    public DecisionReview Review(DecisionPlan proposed,IReadOnlyList<MarketDecisionAssessment> assessments,EvidencePack evidence,DecisionPolicy policy)
    {
        var blocks=new List<string>();
        var assessment=assessments.FirstOrDefault(x=>x.Symbol.Equals(proposed.Instrument,StringComparison.OrdinalIgnoreCase));
        var riskIncreasing=proposed.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort or DecisionAction.Lock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
        if(assessment is null)blocks.Add("所选品种没有可验证的本地聚合结果");
        if(evidence.Completeness<policy.MinimumEvidenceCompleteness&&riskIncreasing)blocks.Add("证据完整度不足");
        if(assessment is{Fresh:false}&&riskIncreasing)blocks.Add("行情数据已过期");
        if(proposed.Confidence<policy.MinimumConfidence&&riskIncreasing)blocks.Add($"Brain 置信度 {proposed.Confidence:P0} 低于 {policy.MinimumConfidence:P0}");
        if(assessment is{EntryReady:false}&&riskIncreasing)blocks.AddRange(assessment.MissingConditions);
        if(assessment is not null&&riskIncreasing)
        {
            var wantsLong=proposed.Action is DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong;
            var wantsShort=proposed.Action is DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort;
            if((wantsLong&&assessment.NetScore<0)||(wantsShort&&assessment.NetScore>0))blocks.Add("Brain 方向与确定性聚合方向相反");
        }
        var final=blocks.Count==0?proposed:CopyAsHold(proposed,blocks);
        var accepted=blocks.Count==0;
        var explanation=Explain(final,assessment,blocks);
        return new(){Decision=final,Accepted=accepted,Verdict=accepted?(final.Action==DecisionAction.Hold?"Reviewer 确认 HOLD":"Reviewer 通过"):"Reviewer 否决并安全降级为 HOLD",BlockingReasons=blocks.Distinct().ToArray(),Explanation=explanation};
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
            b.Append($" · 本地聚合 {assessment.Confidence:P0} · 冲突 {assessment.ConflictRatio:P0}\n");
            b.Append(assessment.Summary);
            var strongest=assessment.Signals.OrderByDescending(x=>Math.Abs(x.WeightedScore)).Take(5).Select(x=>$"{x.Name} {x.Direction} 权重{x.Weight:P0} 贡献{x.WeightedScore:+0.00;-0.00;0.00}");
            b.Append("\n信号：").Append(string.Join("；",strongest));
        }
        b.Append("\n原因：").Append(decision.Reason);
        var missing=blocks.Concat(decision.MissingConditions).Concat(assessment?.MissingConditions??Array.Empty<string>()).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if(missing.Length>0)b.Append("\n允许开单还需要：").Append(string.Join("；",missing));
        return b.ToString();
    }
}

public sealed class AgentSkillRegistry
{
    public static IReadOnlyList<(string Name,string Category,bool Critical)> Skills { get; } =
    [
        ("EvidenceCollector","Observe",true),("SignalAggregation","Reason",true),("MarketRegime","Reason",true),
        ("BrainPlanner","Plan",true),("DecisionCritic","Critic",true),("DecisionReviewer","Review",true),
        ("RiskAndPositionPlanner","Risk",true),("ReliableOrderExecutor","Act",true),("ProtectionRecovery","Recover",true),
        ("DecisionMemory","Memory",false),("ExperienceReplay","Reflect",false),("RuntimeMonitor","Monitor",false)
    ];
}
