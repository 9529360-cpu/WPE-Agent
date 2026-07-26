using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;
using WpeAgent.Notifications;

namespace 币安量化机器人.Services.Agent;

public sealed record MarketTeacherBriefV1(string Content,string Sha256,DateTimeOffset AsOfUtc);

public static class MarketTeacherBriefComposerV1
{
    public static MarketTeacherBriefV1 Compose(ModelOffAgentOutputV1 research)
    {
        ArgumentNullException.ThrowIfNull(research);
        if(research.Agent!=ModelOffAgentV1.Research||!ModelOffEligibilityV1.IsEligibleForDownstream(research))throw new ArgumentException("An eligible canonical Research output is required.",nameof(research));
        var facts=research.Facts;if(facts.ValueKind!=System.Text.Json.JsonValueKind.Object)throw new ArgumentException("Research facts are invalid.",nameof(research));
        var validations=facts.TryGetProperty("validations",out var v)&&v.ValueKind==System.Text.Json.JsonValueKind.Array?v.GetArrayLength():0;
        var lines=new List<string>{"Market briefing","As of "+research.EvaluationTimeUtc.ToString("yyyy-MM-dd HH:mm 'UTC'",CultureInfo.InvariantCulture),$"Research coverage: {validations} validated market set(s)."};
        if(facts.TryGetProperty("macro_observations",out var macro)&&macro.ValueKind==System.Text.Json.JsonValueKind.Array)
        foreach(var item in macro.EnumerateArray().OrderBy(x=>x.GetProperty("IndicatorId").GetString(),StringComparer.Ordinal))
        {
            var id=item.GetProperty("IndicatorId").GetString();var value=item.GetProperty("Value").GetDecimal();var unit=item.GetProperty("Unit").GetString();var period=item.GetProperty("ObservationAtUtc").GetDateTimeOffset();var revision=item.GetProperty("Revision").GetInt32();
            var meaning=id switch{"CUUR0000SA0"=>"CPI tracks average consumer price change; it is not a market forecast.","LNS14000000"=>"The unemployment rate measures the share of the labor force without work and seeking work; it is not a trading signal.",_=>"Observed macroeconomic series; no forecast is inferred."};
            lines.Add($"{id}: {value.ToString(CultureInfo.InvariantCulture)} {unit}, period {period:yyyy-MM}, revision {revision}. {meaning}");
        }
        lines.Add("This briefing organizes verified observations only. It is not investment advice or an order instruction.");
        var content=string.Join('\n',lines);if(content.Length>1800)content=content[..1800];var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();return new(content,hash,research.EvaluationTimeUtc);
    }
}

public static class MarketTeacherBriefPublisherV1
{
    public static async Task<bool> PublishDailyAsync(AgentSqliteStore store,IConfirmedNotificationObserver observer,ModelOffAgentOutputV1 research,string provider,string environment,CancellationToken ct)
    {
        var brief=MarketTeacherBriefComposerV1.Compose(research);var day=brief.AsOfUtc.UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);var key="teacher.market-brief:"+day;
        if(await store.HasStateAsync(key,ct))return false;
        var eventKey=ConfirmedNotificationTruth.EventKey(day,brief.Sha256,NotificationEventKind.MarketBrief);
        await observer.ObserveAsync(ConfirmedNotificationTruth.System(eventKey,NotificationEventKind.MarketBrief,provider,environment,brief.AsOfUtc.UtcDateTime,"teacher.market-brief.ready",content:brief.Content),ct);
        await store.SetStateAsync(key,brief.Sha256,ct);return true;
    }
}
