using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Services.Agent;

public static class MarketTeacherScheduleV2
{
    public const string DefaultTimeZone="Asia/Shanghai";
    private static readonly (TimeOnly Time,TeacherLessonKindV2 Kind)[] Windows=[(new(8,0),TeacherLessonKindV2.Morning),(new(14,0),TeacherLessonKindV2.Afternoon),(new(20,0),TeacherLessonKindV2.Evening)];
    public static (TeacherLessonKindV2 Kind,DateTimeOffset ScheduledForUtc) Next(DateTimeOffset nowUtc,string timeZoneId=DefaultTimeZone)
    {
        var zone=FindZone(timeZoneId);var local=TimeZoneInfo.ConvertTime(nowUtc,zone);
        foreach(var window in Windows)if(TimeOnly.FromDateTime(local.DateTime)<window.Time)return(window.Kind,ToUtc(local.Date,window.Time,zone));
        return(TeacherLessonKindV2.Morning,ToUtc(local.Date.AddDays(1),Windows[0].Time,zone));
    }
    public static DateTimeOffset ScheduledUtc(DateOnly localDate,TeacherLessonKindV2 kind,string timeZoneId=DefaultTimeZone)
    {
        var window=Windows.Single(x=>x.Kind==kind);return ToUtc(localDate.ToDateTime(TimeOnly.MinValue),window.Time,FindZone(timeZoneId));
    }
    public static (TeacherLessonKindV2 Kind,DateTimeOffset ScheduledForUtc)? Due(DateTimeOffset nowUtc,string timeZoneId=DefaultTimeZone)
    {
        var zone=FindZone(timeZoneId);var local=TimeZoneInfo.ConvertTime(nowUtc,zone);var due=Windows.Where(x=>x.Time<=TimeOnly.FromDateTime(local.DateTime)).LastOrDefault();
        return due==default?null:(due.Kind,ToUtc(local.Date,due.Time,zone));
    }
    private static TimeZoneInfo FindZone(string id){try{return TimeZoneInfo.FindSystemTimeZoneById(id);}catch(TimeZoneNotFoundException) when(id==DefaultTimeZone){return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");}}
    private static DateTimeOffset ToUtc(DateTime date,TimeOnly time,TimeZoneInfo zone)=>new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Date+time.ToTimeSpan(),DateTimeKind.Unspecified),zone),TimeSpan.Zero);
}

public static class MarketTeacherComposerV2
{
    public static TeacherLessonV2 Compose(TeacherLessonKindV2 kind,DateTimeOffset scheduledForUtc,IReadOnlyList<TeacherEvidenceReferenceV2> evidence,string language="zh_CN",string teachingLevel="intermediate",string timeZoneId=MarketTeacherScheduleV2.DefaultTimeZone,DateTimeOffset? generatedAtUtc=null)
    {
        if(kind==TeacherLessonKindV2.Event)throw new ArgumentException("Event lessons require a verified event envelope.",nameof(kind));
        var generated=(generatedAtUtc??DateTimeOffset.UtcNow).ToUniversalTime();ValidateEvidence(evidence,generated);
        var title=kind switch{TeacherLessonKindV2.Morning=>"早课：先看环境，再看机会",TeacherLessonKindV2.Afternoon=>"午课：验证变化，不追逐噪声",_=>"晚课：复盘证据，准备下一步"};
        var ordered=evidence.OrderBy(x=>Array.IndexOf(["market","research","strategy","risk","execution","recovery","audit"],x.Agent)).ToArray();
        var facts=string.Join("\n",ordered.Select(x=>$"- {RoleName(x.Agent)}：已读取规范产物 {Short(x.CanonicalSha256)}，截至 {x.AsOfUtc:yyyy-MM-dd HH:mm} UTC。"));
        var blocks=new[]{Block(TeacherReportBlockKindV2.Conclusion,"本课结论","当前课程只解释已经通过规范校验的系统事实；没有证据的市场结论保持未知。",[]),Block(TeacherReportBlockKindV2.AgentView,"七 Agent 事实链",facts,ordered.Select(x=>x.CanonicalSha256).ToArray()),Block(TeacherReportBlockKindV2.Risk,"纪律边界","老师可以提出研究候选，但没有下单、改策略、放行风控或配置账户的权限。任何交易仍须经过策略、风控、执行、恢复与审计链路。",ordered.Select(x=>x.CanonicalSha256).ToArray()),Block(TeacherReportBlockKindV2.Unknown,"待补证据","本地事实未覆盖的实时价格、新闻因果、股票基本面和宏观变化均标记为不可用，不作推断。",[])};
        var lessonId=$"teacher-{scheduledForUtc:yyyyMMddHHmm}-{kind.ToString().ToLowerInvariant()}";var contentHash=Hash(string.Join('\n',blocks.Select(x=>x.Sha256)));
        return new(lessonId,"wpe.teacher-lesson/2.0",kind,timeZoneId,scheduledForUtc.ToUniversalTime(),generated,language,teachingLevel,MarketTeacherPersonaV2.Default.Version,ordered,blocks,contentHash,false);
    }
    public static void ValidateEvidence(IReadOnlyList<TeacherEvidenceReferenceV2> evidence,DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);var roles=new[]{"market","research","strategy","risk","execution","recovery","audit"};
        if(evidence.Count!=roles.Length||roles.Any(role=>evidence.Count(x=>x.Agent==role)!=1))throw new InvalidOperationException("Teacher requires exactly one canonical reference from each of the seven Agents.");
        if(evidence.Select(x=>x.CycleId).Distinct(StringComparer.Ordinal).Count()!=1)throw new InvalidOperationException("Teacher evidence must belong to one cycle.");
        foreach(var item in evidence)if(item.Availability!=TeacherEvidenceAvailabilityV2.Available||item.AsOfUtc.Offset!=TimeSpan.Zero||item.AsOfUtc>asOfUtc||item.CanonicalSha256.Length!=64||!item.CanonicalSha256.All(Uri.IsHexDigit))throw new InvalidOperationException("Teacher evidence is stale, future, unavailable, or malformed.");
    }
    private static TeacherReportBlockV2 Block(TeacherReportBlockKindV2 kind,string heading,string content,IReadOnlyList<string> hashes){var id=$"{kind.ToString().ToLowerInvariant()}-{Hash(heading+"\n"+content+"\n"+string.Join(',',hashes))[..12]}";return new(id,kind,heading,content,hashes,Hash(id+"\n"+content));}
    private static string RoleName(string role)=>role switch{"market"=>"市场","research"=>"研究","strategy"=>"策略","risk"=>"风控","execution"=>"执行","recovery"=>"恢复","audit"=>"审计",_=>role};
    private static string Short(string hash)=>hash[..12];
    internal static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class MarketTeacherRuntimeV2
{
    public static async Task<TeacherLessonV2?> GenerateDueLocalLessonAsync(AgentSqliteStore store,string cycleId,DateTimeOffset nowUtc,CancellationToken ct,string language="zh_CN",string teachingLevel="intermediate",string timeZoneId=MarketTeacherScheduleV2.DefaultTimeZone)
    {
        ArgumentNullException.ThrowIfNull(store);nowUtc=nowUtc.ToUniversalTime();var due=MarketTeacherScheduleV2.Due(nowUtc,timeZoneId);if(due is null)return null;
        var scheduled=due.Value.ScheduledForUtc.ToUniversalTime();var expectedId=$"teacher-{scheduled:yyyyMMddHHmm}-{due.Value.Kind.ToString().ToLowerInvariant()}";var existing=await store.GetTeacherLessonAsync(expectedId,ct);if(existing is not null)return existing;

        var candidateIds=new List<string>();if(!string.IsNullOrWhiteSpace(cycleId))candidateIds.Add(cycleId);candidateIds.AddRange(await store.GetRecentTeacherCandidateCycleIdsAsync(scheduled,32,ct));
        foreach(var candidateId in candidateIds.Distinct(StringComparer.Ordinal))
        {
            var evidence=await store.GetTeacherEvidenceForCycleAsync(candidateId,scheduled,ct);
            if(!IsEvidenceEligible(evidence,scheduled))continue;
            var lesson=MarketTeacherComposerV2.Compose(due.Value.Kind,scheduled,evidence,language,teachingLevel,timeZoneId,nowUtc);var saved=await store.SaveTeacherLessonAsync(lesson,ct);if(!saved.Succeeded)throw new InvalidOperationException(saved.Code);return lesson;
        }
        return null;
    }

    internal static bool IsEvidenceEligible(IReadOnlyList<TeacherEvidenceReferenceV2> evidence,DateTimeOffset asOfUtc)
    {
        if(evidence is null)return false;asOfUtc=asOfUtc.ToUniversalTime();var roles=new[]{"market","research","strategy","risk","execution","recovery","audit"};
        if(evidence.Count!=roles.Length||roles.Any(role=>evidence.Count(x=>x.Agent==role)!=1)||evidence.Select(x=>x.CycleId).Distinct(StringComparer.Ordinal).Count()!=1)return false;
        return evidence.All(item=>item.Availability==TeacherEvidenceAvailabilityV2.Available&&item.AsOfUtc.Offset==TimeSpan.Zero&&item.AsOfUtc<=asOfUtc&&asOfUtc-item.AsOfUtc<=TimeSpan.FromMinutes(30)&&item.CanonicalSha256.Length==64&&item.CanonicalSha256.All(Uri.IsHexDigit));
    }
}

public static class TeacherNumericProvenanceGuardV2
{
    public static bool PreservesDeterministicValues(string deterministic,string candidate)
    {
        static string[] Numbers(string value)=>System.Text.RegularExpressions.Regex.Matches(value,@"(?<![\p{L}\d])[-+]?\d+(?:\.\d+)?%?").Select(x=>x.Value).ToArray();
        return Numbers(deterministic).SequenceEqual(Numbers(candidate),StringComparer.Ordinal);
    }
}

public static class TeacherRecommendationEngineV2
{
    public static TeacherRecommendationV2 CreateCrypto(string id,string instrument,TeacherRecommendationStateV2 state,string horizon,DateTimeOffset issuedAtUtc,TimeSpan lifetime,string thesis,IReadOnlyList<TeacherEvidenceReferenceV2> evidence,IReadOnlyList<string> confirmation,IReadOnlyList<string> invalidation,IReadOnlyList<string> risks,int version=1,string? supersedesId=null,IReadOnlyList<string>? additionalEvidenceHashes=null)
    {
        if(state is TeacherRecommendationStateV2.Expired or TeacherRecommendationStateV2.Unavailable)throw new ArgumentException("A new crypto recommendation must start in an actionable research state.",nameof(state));
        MarketTeacherComposerV2.ValidateEvidence(evidence,issuedAtUtc);if(lifetime<=TimeSpan.Zero||string.IsNullOrWhiteSpace(instrument)||confirmation.Count==0||invalidation.Count==0||risks.Count==0)throw new ArgumentException("Recommendation conditions and bounded lifetime are required.");
        var hashes=evidence.Select(x=>x.CanonicalSha256).Concat(additionalEvidenceHashes??[]).Distinct().Order().ToArray();if(hashes.Any(x=>x.Length!=64||!x.All(Uri.IsHexDigit)))throw new ArgumentException("Recommendation evidence hash is invalid.");return new(id,"wpe.teacher-recommendation/2.0",version,instrument,"crypto",state,horizon,issuedAtUtc.ToUniversalTime(),issuedAtUtc.ToUniversalTime().Add(lifetime),thesis,hashes,confirmation,invalidation,risks,supersedesId,false);
    }
    public static TeacherRecommendationV2 CurrentEquityUnavailable(string id,string instrument,DateTimeOffset issuedAtUtc)=>new(id,"wpe.teacher-recommendation/2.0",1,instrument,"equity",TeacherRecommendationStateV2.Unavailable,"unavailable",issuedAtUtc.ToUniversalTime(),issuedAtUtc.ToUniversalTime(),"当前股票实时证据尚未接入，不能生成实时推荐。",[],[],[],["equity-provider-unavailable"],null,false);
    public static TeacherRecommendationV2 Expire(TeacherRecommendationV2 current,DateTimeOffset nowUtc,string reason)
    {
        if(current.ExecutionAuthority||nowUtc.ToUniversalTime()<current.ExpiresAtUtc)throw new InvalidOperationException("Recommendation is not eligible for expiry.");
        var now=nowUtc.ToUniversalTime();return current with{Version=current.Version+1,State=TeacherRecommendationStateV2.Expired,IssuedAtUtc=now,ExpiresAtUtc=now,Thesis=reason,SupersedesId=$"{current.RecommendationId}@{current.Version}",ExecutionAuthority=false};
    }
}
