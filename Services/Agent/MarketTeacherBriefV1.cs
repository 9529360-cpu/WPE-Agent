using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;
using WpeAgent.Notifications;

namespace 币安量化机器人.Services.Agent;

public sealed record MarketTeacherBriefV1(string Content,string Sha256,DateTimeOffset AsOfUtc,string PersonaVersion);
public sealed record MarketTeacherPersonaV1(string Version,string RoleName,int PersonaAge,IReadOnlyList<string> Traits,IReadOnlyList<string> Boundaries);

public static class MarketTeacherBriefComposerV1
{
    public static MarketTeacherPersonaV1 Persona{get;}=new("wpe.market-teacher-persona/1.0","Senior market mentor",48,["evidence-first","plain-spoken","calm","experienced","non-performative"],["no-forecast-invention","no-investment-advice","no-order-authority","no-quiz-or-scoring"]);

    public static MarketTeacherBriefV1 Compose(ModelOffAgentOutputV1 research,string languageCode="en_US")
    {
        ArgumentNullException.ThrowIfNull(research);
        if(research.Agent!=ModelOffAgentV1.Research||!ModelOffEligibilityV1.IsEligibleForDownstream(research))throw new ArgumentException("An eligible canonical Research output is required.",nameof(research));
        var facts=research.Facts;if(facts.ValueKind!=System.Text.Json.JsonValueKind.Object)throw new ArgumentException("Research facts are invalid.",nameof(research));
        var validations=facts.TryGetProperty("validations",out var v)&&v.ValueKind==System.Text.Json.JsonValueKind.Array?v.GetArrayLength():0;var chinese=languageCode.StartsWith("zh",StringComparison.OrdinalIgnoreCase);
        var lines=chinese?new List<string>{"市场笔记","时间："+research.EvaluationTimeUtc.ToString("yyyy-MM-dd HH:mm 'UTC'",CultureInfo.InvariantCulture),"先看事实，不急着下结论。",$"本轮覆盖 {validations} 组已验证的市场研究。"}:new List<string>{"Market notes","As of "+research.EvaluationTimeUtc.ToString("yyyy-MM-dd HH:mm 'UTC'",CultureInfo.InvariantCulture),"Start with the facts; conclusions can wait.",$"This cycle covers {validations} validated market research set(s)."};
        if(facts.TryGetProperty("macro_observations",out var macro)&&macro.ValueKind==System.Text.Json.JsonValueKind.Array)
        foreach(var item in macro.EnumerateArray().OrderBy(x=>x.GetProperty("IndicatorId").GetString(),StringComparer.Ordinal))
        {
            var id=item.GetProperty("IndicatorId").GetString();var value=item.GetProperty("Value").GetDecimal();var unit=item.GetProperty("Unit").GetString();var period=item.GetProperty("ObservationAtUtc").GetDateTimeOffset();var revision=item.GetProperty("Revision").GetInt32();
            var meaning=chinese?id switch{"CUUR0000SA0"=>"CPI 衡量消费者价格的总体变化。它说明通胀已经发生了什么，不替市场预测下一步。","LNS14000000"=>"失业率衡量劳动力中没有工作且正在求职的人所占比例。它是经济背景，不是交易信号。",_=>"这是已观测的宏观序列，系统不从中编造预测。"}:id switch{"CUUR0000SA0"=>"CPI tracks average consumer price change. It describes what inflation has done; it does not predict the market's next move.","LNS14000000"=>"The unemployment rate measures the share of the labor force without work and seeking work. It is economic context, not a trading signal.",_=>"This is an observed macroeconomic series; no forecast is inferred."};
            lines.Add(chinese?$"{id}：{value.ToString(CultureInfo.InvariantCulture)} {unit}，观察期 {period:yyyy-MM}，第 {revision} 版。{meaning}":$"{id}: {value.ToString(CultureInfo.InvariantCulture)} {unit}, period {period:yyyy-MM}, revision {revision}. {meaning}");
        }
        lines.Add(chinese?"把这些数据当作背景坐标，不要当成买卖按钮。真正的订单仍必须经过策略、风控和执行链。":"Treat these observations as coordinates, not action triggers. This is not investment advice. Any real order must still pass the strategy, risk, and execution chain.");
        var content=string.Join('\n',lines);if(content.Length>1800)content=content[..1800];var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Persona.Version+'\n'+content))).ToLowerInvariant();return new(content,hash,research.EvaluationTimeUtc,Persona.Version);
    }
}

public static class MarketTeacherBriefPublisherV1
{
    public static async Task<bool> PublishDailyAsync(AgentSqliteStore store,IConfirmedNotificationObserver observer,ModelOffAgentOutputV1 research,string provider,string environment,CancellationToken ct,string languageCode="en_US")
    {
        var brief=MarketTeacherBriefComposerV1.Compose(research,languageCode);var day=brief.AsOfUtc.UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);var key="teacher.market-brief:"+day;
        if(await store.HasStateAsync(key,ct))return false;
        var eventKey=ConfirmedNotificationTruth.EventKey(day,brief.Sha256,NotificationEventKind.MarketBrief);
        await observer.ObserveAsync(ConfirmedNotificationTruth.System(eventKey,NotificationEventKind.MarketBrief,provider,environment,brief.AsOfUtc.UtcDateTime,"teacher.market-brief.v1.ready",content:brief.Content),ct);
        await store.SetStateAsync(key,brief.Sha256,ct);return true;
    }
}
