using System.Globalization;

namespace 币安量化机器人.Services.Agent;

public sealed record TeacherMarketEventV2(string EventId,string Schema,string Symbol,string EventKind,string Severity,DateTimeOffset ObservedAtUtc,string PreviousEvidenceHash,string CurrentEvidenceHash,IReadOnlyList<string> ConfirmedFacts,IReadOnlyList<TeacherCausalLinkV2> Hypotheses,IReadOnlyList<string> AlternativeHypotheses,IReadOnlyList<string> Uncertainties,string Fingerprint);

public static class TeacherCryptoEventDetectorV2
{
    public static TeacherMarketEventV2? Detect(TeacherCryptoMarketFactV2 previous,TeacherCryptoMarketFactV2 current)
    {
        if(!TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(previous)||!TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(current)||previous.Symbol!=current.Symbol||current.ObservedAtUtc<=previous.ObservedAtUtc)return null;var priceChange=Percent(previous.MarkPrice,current.MarkPrice);var oiChange=previous.OpenInterest>0?Percent(previous.OpenInterest,current.OpenInterest):0;var fundingChange=current.FundingRate-previous.FundingRate;
        var triggers=new List<string>();if(Math.Abs(priceChange)>=3)triggers.Add("price-move");if(Math.Abs(oiChange)>=10)triggers.Add("open-interest-shift");if(Math.Abs(current.FundingRate)>=.001m||Math.Abs(fundingChange)>=.0005m)triggers.Add("funding-extreme");if(triggers.Count==0)return null;
        var facts=new[]{$"{current.Symbol} 标记价格从 {F(previous.MarkPrice)} 变为 {F(current.MarkPrice)}，变化 {F(priceChange)}%。",$"未平仓量从 {F(previous.OpenInterest)} 变为 {F(current.OpenInterest)}，变化 {F(oiChange)}%。",$"资金费率从 {F(previous.FundingRate)} 变为 {F(current.FundingRate)}。"};
        var support=new[]{previous.CanonicalSha256,current.CanonicalSha256};var hypotheses=new[]{new TeacherCausalLinkV2("衍生品仓位与资金成本变化","当前价格和波动变化","plausible",support,[],["成交方向","清算明细","现货资金流","同期官方事件"])};var alternatives=new[]{"现货主导的供需变化","宏观或监管事件","大额头寸调整或流动性变化","多个因素同时作用"};var uncertainties=new[]{"三个公开端点只能确认量价、持仓量和资金费率，不能单独证明因果。","没有合格新闻或公告证据时，不归因于具体事件。"};var kind=string.Join('+',triggers.Order());var fingerprint=MarketTeacherComposerV2.Hash(string.Join('|',current.Symbol,kind,current.ObservedAtUtc.ToString("yyyyMMddHH",CultureInfo.InvariantCulture),Math.Sign(priceChange),Math.Sign(oiChange)));return new("event-"+fingerprint[..16],"wpe.teacher-market-event/2.0",current.Symbol,kind,triggers.Count>1?"high":"medium",current.ObservedAtUtc,previous.CanonicalSha256,current.CanonicalSha256,facts,hypotheses,alternatives,uncertainties,fingerprint);
    }
    private static decimal Percent(decimal before,decimal after)=>before==0?0:decimal.Round((after-before)/before*100m,4,MidpointRounding.ToEven);
    private static string F(decimal value)=>value.ToString("0.########",CultureInfo.InvariantCulture);
}

public static class TeacherEventLessonComposerV2
{
    public static TeacherLessonV2 Compose(TeacherMarketEventV2 marketEvent,IReadOnlyList<TeacherEvidenceReferenceV2> agents,DateTimeOffset generatedAtUtc,string language="zh_CN",string teachingLevel="intermediate",string timeZoneId=MarketTeacherScheduleV2.DefaultTimeZone)
    {
        ArgumentNullException.ThrowIfNull(marketEvent);MarketTeacherComposerV2.ValidateEvidence(agents,generatedAtUtc);var external=new[]{marketEvent.PreviousEvidenceHash,marketEvent.CurrentEvidenceHash};var blocks=new[]{Block(TeacherReportBlockKindV2.Conclusion,"发生了什么",string.Join('\n',marketEvent.ConfirmedFacts),external),Block(TeacherReportBlockKindV2.CitedFact,"为什么重要",$"{marketEvent.Symbol} 同时触发 {marketEvent.EventKind} 观察条件。它表示市场结构发生了需要研究的变化，不等于交易指令。",external),Block(TeacherReportBlockKindV2.Hypothesis,"可能的传导机制",string.Join('\n',marketEvent.Hypotheses.Select(x=>$"- {x.From} -> {x.To}（{x.Strength}）")),external),Block(TeacherReportBlockKindV2.Counterargument,"其他解释",string.Join('\n',marketEvent.AlternativeHypotheses.Select(x=>"- "+x)),external),Block(TeacherReportBlockKindV2.AgentView,"七 Agent 当前事实",string.Join('\n',agents.OrderBy(x=>x.Agent).Select(x=>$"- {x.Agent}: {x.CanonicalSha256[..12]}")),agents.Select(x=>x.CanonicalSha256).ToArray()),Block(TeacherReportBlockKindV2.Recommendation,"观察条件","将该事件列为研究候选；等待研究、策略与风控链确认。若后续证据否定当前结构或数据过期，则该候选失效。",external),Block(TeacherReportBlockKindV2.Unknown,"不确定性与下一证据",string.Join('\n',marketEvent.Uncertainties.Select(x=>"- "+x))+"\n- 下一步检查成交方向、清算、现货流与合格公告。",external),Block(TeacherReportBlockKindV2.Risk,"风险提示","不追涨杀跌，不根据单次异动下单。老师没有执行权。",external)};var id="teacher-event-"+marketEvent.Fingerprint[..16];return new(id,"wpe.teacher-lesson/2.0",TeacherLessonKindV2.Event,timeZoneId,marketEvent.ObservedAtUtc,generatedAtUtc.ToUniversalTime(),language,teachingLevel,MarketTeacherPersonaV2.Default.Version,agents,blocks,MarketTeacherComposerV2.Hash(string.Join('\n',blocks.Select(x=>x.Sha256))),false);
    }
    private static TeacherReportBlockV2 Block(TeacherReportBlockKindV2 kind,string heading,string content,IReadOnlyList<string> hashes){var id=$"{kind.ToString().ToLowerInvariant()}-{MarketTeacherComposerV2.Hash(heading+'\n'+content)[..12]}";return new(id,kind,heading,content,hashes,MarketTeacherComposerV2.Hash(id+'\n'+content+'\n'+string.Join(',',hashes)));}
}
