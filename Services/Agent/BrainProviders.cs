using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class DeterministicBrainProvider : IAssistantProvider
{
    public string Name => "WPE Local Brain";
    public bool IsLocal => true;
    public Task<BrainHealth> HealthCheckAsync(CancellationToken ct) => Task.FromResult(new BrainHealth(true, "LOCAL_DETERMINISTIC"));

    public Task<BrainDecisionResult> DecideAsync(EvidencePack evidence, AgentContext context, CancellationToken ct)
    {
        var selected = context.MarketAssessments.Where(x => x.EntryReady && x.Fresh)
            .OrderByDescending(x => x.Confidence * (1 - x.ConflictRatio))
            .ThenByDescending(x => Math.Abs(x.NetScore))
            .FirstOrDefault();
        var blocked = context.CircuitBreakerActive || selected is null;
        var decision = new DecisionPlan
        {
            Action = blocked ? DecisionAction.Hold : selected!.RecommendedAction,
            Instrument = selected?.Symbol ?? evidence.Markets.Keys.FirstOrDefault() ?? "BTCUSDT",
            TargetTier = blocked ? 0 : 1,
            Confidence = selected?.Confidence ?? 0,
            Regime = selected?.Regime.ToString() ?? MarketRegime.Unknown.ToString(),
            Reason = context.CircuitBreakerActive ? "local risk circuit breaker is active" : selected?.Summary ?? "no locally validated entry is ready",
            Invalidation = "local signal, data freshness, strategy health, or risk gate becomes invalid",
            EvidenceReferences = selected?.Signals.OrderByDescending(x => Math.Abs(x.WeightedScore)).Take(5).Select(x => x.Name).ToList() ?? [],
            MissingConditions = selected?.MissingConditions.ToList() ?? context.MarketAssessments.SelectMany(x => x.MissingConditions).Distinct().ToList(),
            ConflictSummary = selected is null ? "no executable local assessment" : $"conflict={selected.ConflictRatio:F3}; score={selected.NetScore:F3}",
            StrategyVersion = "wpe-local-deterministic-v1"
        };
        var audit = JsonSerializer.Serialize(new { provider = Name, decision.Action, decision.Instrument, decision.Confidence, decision.Reason });
        return Task.FromResult(new BrainDecisionResult(decision, audit, audit));
    }
}

public sealed class HttpBrainProvider : IAssistantProvider
{
    private readonly HttpClient _http; private readonly BrainSlot _slot; private readonly string _key; private readonly LlmRequestGovernor _governor; public string Name=>_slot.Provider; public bool IsLocal => false;
    public HttpBrainProvider(BrainSlot slot,string key,LlmRequestGovernor? governor=null){_slot=slot;_key=key;_governor=governor??LlmRequestGovernor.Shared;_http=new HttpClient{Timeout=TimeSpan.FromSeconds(Math.Clamp(slot.TimeoutSeconds,5,300))};}
    public async Task<BrainHealth> HealthCheckAsync(CancellationToken ct){if(string.IsNullOrWhiteSpace(_key)||string.IsNullOrWhiteSpace(_slot.Endpoint)||string.IsNullOrWhiteSpace(_slot.Model))return new(false,LocalizationService.Current.T("Provider.Incomplete"));try{using var r=await BuildAndSend("Reply only: HOLD",ct);return new(r.IsSuccessStatusCode,$"HTTP {(int)r.StatusCode}");}catch(Exception ex){return new(false,ex.Message);}}
    public async Task<BrainDecisionResult> DecideAsync(EvidencePack e,AgentContext c,CancellationToken ct)
    {
        var instruction="""
        你是 WPE 合约决策 Planner，只负责提出候选计划，不计算最终下单数量。你的计划之后还会经过本地 Critic、Reviewer 和确定性风控。只能依据 evidence、marketAssessments 和 context 输出一个 JSON 对象，不得输出 Markdown。
        action 只能是 OpenLong/OpenShort/AddLong/AddShort/ReduceLong/ReduceShort/CloseLong/CloseShort/Lock/Unlock/ReverseToLong/ReverseToShort/Hold；instrument 只能从输入 evidence.Markets 的键中选择；targetTier 只能 0..3；止损止盈必须是数字。
        必须逐个独立评估输入中的交易品种，再选择质量更高的一个；跨品种方向相反不是同一品种的信号冲突，禁止因此笼统 HOLD。15分钟负责执行，1小时和4小时负责背景，价格结构优先于新闻叙事。
        marketAssessments 是本地可复算的逐信号权重结果。若 EntryReady=false，通常 HOLD，并在 missingConditions 中准确列出尚缺条件；若提出增加风险的动作，方向必须与该品种 NetScore 一致且 confidence 不低于策略阈值。
        previousOutcomes 是压缩后的经验回放，不要机械复述上一轮；consecutiveHolds 较高时必须重新检查原结论，但不得为了开单而降低标准。
        只有结构破坏/重新站回、失效条件触发、核心驱动替换或验证等级下降时才改变观点；核心区间内不得直接称为反转。多空证据冲突时仍须给出唯一最终倾向。
        不得编造新闻、鲸鱼流向、截图、订单流或来源，不得承诺收益。reason 必须简述核心证据与风险，invalidation 必须写明观点失效条件，evidenceReferences 只能引用输入中实际存在的项目；conflictSummary 描述同一品种内部冲突。
        """;
        instruction += $"\n允许的 instrument：{string.Join(",",e.Markets.Keys)}。所有面向用户的描述字段（reason、invalidation、conflictSummary、missingConditions）必须使用{LocalizationService.Current.CurrentLanguage.AiLanguage}；action、instrument 和 JSON 字段名保持规定的英文枚举。missingConditions 和 evidenceReferences 必须输出字符串数组。";
        var compactEvidence=new
        {
            e.CollectedAt,e.Account,e.Positions,e.Markets,e.MissingSources,e.Completeness,
            News=e.News.OrderByDescending(x=>x.PublishedAt).Take(12).Select(x=>new{x.Source,x.Title,x.PublishedAt,x.Reliability,x.AffectedAssets,x.Confidence,x.CorroboratingSources,x.EventType,x.IsBreaking,x.Sentiment,BodySummary=x.BodySummary.Length>500?x.BodySummary[..500]+"…":x.BodySummary})
        };
        var prompt=JsonSerializer.Serialize(new{instruction,outputLanguage=LocalizationService.Current.CurrentLanguage.AiLanguage,evidence=compactEvidence,marketAssessments=c.MarketAssessments,context=new{c.BrainName,c.CircuitBreakerActive,c.ActiveSymbol,c.PreviousOutcomes,c.ConsecutiveHolds}});string raw="";
        try{using var r=await BuildAndSend(prompt,ct);raw=await r.Content.ReadAsStringAsync(ct);if(!r.IsSuccessStatusCode)throw new BrainCallException($"{Name} HTTP {(int)r.StatusCode}",prompt,raw);var content=ExtractProviderText(raw);var json=ExtractJson(content);var opt=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};opt.Converters.Add(new JsonStringEnumConverter());opt.Converters.Add(new FlexibleStringListConverter());var decision=JsonSerializer.Deserialize<DecisionPlan>(json,opt)??new DecisionPlan{Reason=LocalizationService.Current.T("Provider.EmptyResponse")};decision.MissingConditions??=[];decision.EvidenceReferences??=[];return new(decision,prompt,raw);}
        catch(BrainCallException){throw;}catch(Exception ex){throw new BrainCallException(LocalizationService.Current.T("Provider.ParseFailed",Name,ex.Message),prompt,raw,ex);}
    }
    private async Task<HttpResponseMessage> BuildAndSend(string prompt,CancellationToken ct)
    {
        var provider=_slot.Provider.ToLowerInvariant();var req=new HttpRequestMessage(HttpMethod.Post,_slot.Endpoint);object body;
        if(provider.Contains("claude")){req.Headers.Add("x-api-key",_key);req.Headers.Add("anthropic-version","2023-06-01");body=new{model=_slot.Model,max_tokens=_slot.MaxTokens,messages=new[]{new{role="user",content=prompt}}};}
        else if(provider.Contains("gemini")){req.RequestUri=new Uri(_slot.Endpoint+( _slot.Endpoint.Contains('?')?'&':'?')+"key="+Uri.EscapeDataString(_key));body=new{contents=new[]{new{parts=new[]{new{text=prompt}}}},generationConfig=new{temperature=_slot.Temperature}};}
        else if(provider.Contains("openai")){req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);body=new{model=_slot.Model,input=prompt};}
        else {req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);body=new{model=_slot.Model,messages=new[]{new{role="user",content=prompt}},temperature=_slot.Temperature,max_tokens=_slot.MaxTokens};}
        req.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");var purpose=prompt=="Reply only: HOLD"?"health-check":"assistant-advice";return await _governor.SendAsync(_slot.Provider,_slot.Model,purpose,prompt,_=>_http.SendAsync(req,ct),ct);
    }
    private string ExtractProviderText(string raw){using var d=JsonDocument.Parse(raw);var p=_slot.Provider.ToLowerInvariant();if(p.Contains("claude"))return d.RootElement.GetProperty("content")[0].GetProperty("text").GetString()??"";if(p.Contains("gemini"))return d.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()??"";if(p.Contains("openai")){if(d.RootElement.TryGetProperty("output_text",out var ot))return ot.GetString()??"";foreach(var o in d.RootElement.GetProperty("output").EnumerateArray())if(o.TryGetProperty("content",out var a))foreach(var x in a.EnumerateArray())if(x.TryGetProperty("text",out var t))return t.GetString()??"";}var m=d.RootElement.GetProperty("choices")[0].GetProperty("message");return m.TryGetProperty("content",out var c)?c.GetString()??"":m.GetProperty("reasoning_content").GetString()??"";}
private static string ExtractJson(string s){var a=s.IndexOf('{');var b=s.LastIndexOf('}');if(a<0||b<=a)throw new JsonException(LocalizationService.Current.T("Provider.JsonMissing"));return s[a..(b+1)];}
}

public sealed class FlexibleStringListConverter : JsonConverter<List<string>>
{
    public override bool HandleNull=>true;
    public override List<string> Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.Null)return [];
        if(reader.TokenType==JsonTokenType.String){var value=reader.GetString();return string.IsNullOrWhiteSpace(value)?[]:[value];}
        if(reader.TokenType!=JsonTokenType.StartArray)throw new JsonException("Expected a string array, string, or null.");
        var values=new List<string>();while(reader.Read()&&reader.TokenType!=JsonTokenType.EndArray){if(reader.TokenType!=JsonTokenType.String)throw new JsonException("List entries must be strings.");var value=reader.GetString();if(!string.IsNullOrWhiteSpace(value))values.Add(value);}
        return values;
    }
    public override void Write(Utf8JsonWriter writer,List<string> value,JsonSerializerOptions options){writer.WriteStartArray();foreach(var item in value)writer.WriteStringValue(item);writer.WriteEndArray();}
}

/// <summary>Composition boundary for optional assistant providers.</summary>
public static class AssistantProviderFactory
{
    public static IAssistantProvider CreateLocal() => new DeterministicBrainProvider();

    public static IAssistantProvider Create(BrainSlot? slot, string? secret, bool allowRemote)
    {
        if (!allowRemote || slot is null || string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(slot.Endpoint) || string.IsNullOrWhiteSpace(slot.Model))
            return CreateLocal();
        return new HttpBrainProvider(slot, secret);
    }
}
