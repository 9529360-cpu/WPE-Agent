using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 币安量化机器人.Services.Agent;

public sealed class HttpBrainProvider : IBrainProvider
{
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(75)}; private readonly BrainSlot _slot; private readonly string _key; public string Name=>_slot.Provider;
    public HttpBrainProvider(BrainSlot slot,string key){_slot=slot;_key=key;}
    public async Task<BrainHealth> HealthCheckAsync(CancellationToken ct){if(string.IsNullOrWhiteSpace(_key)||string.IsNullOrWhiteSpace(_slot.Endpoint)||string.IsNullOrWhiteSpace(_slot.Model))return new(false,"配置不完整");try{using var r=await BuildAndSend("只回复 HOLD",ct);return new(r.IsSuccessStatusCode,$"HTTP {(int)r.StatusCode}");}catch(Exception ex){return new(false,ex.Message);}}
    public async Task<BrainDecisionResult> DecideAsync(EvidencePack e,AgentContext c,CancellationToken ct)
    {
        var instruction="""
        你是 WPE 合约决策大脑，只负责判断，不计算最终下单数量。只能依据 evidence 和 context 输出一个 JSON 对象，不得输出 Markdown。
        action 只能是 OpenLong/OpenShort/AddLong/AddShort/ReduceLong/ReduceShort/CloseLong/CloseShort/Lock/Unlock/ReverseToLong/ReverseToShort/Hold；instrument 只能 BTCUSDT 或 ETHUSDT；targetTier 只能 0..3；止损止盈必须是数字。
        价格结构优先于新闻叙事；15分钟负责执行，1小时和4小时负责背景。必须利用 previousOutcomes 检查上一轮判断，证据不足、来源冲突或完整度偏低时降低 confidence 并优先 HOLD。
        只有结构破坏/重新站回、失效条件触发、核心驱动替换或验证等级下降时才改变观点；核心区间内不得直接称为反转。多空证据冲突时仍须给出唯一最终倾向。
        不得编造新闻、鲸鱼流向、截图、订单流或来源，不得承诺收益。reason 必须简述核心证据与风险，invalidation 必须写明观点失效条件，evidenceReferences 只能引用 evidence 中实际存在的项目。
        """;
        var prompt=JsonSerializer.Serialize(new{instruction,evidence=e,context=c});string raw="";
        try{using var r=await BuildAndSend(prompt,ct);raw=await r.Content.ReadAsStringAsync(ct);if(!r.IsSuccessStatusCode)throw new BrainCallException($"{Name} HTTP {(int)r.StatusCode}",prompt,raw);var content=ExtractProviderText(raw);var json=ExtractJson(content);var opt=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};opt.Converters.Add(new JsonStringEnumConverter());var decision=JsonSerializer.Deserialize<DecisionPlan>(json,opt)??new DecisionPlan{Reason="空响应"};return new(decision,prompt,raw);}
        catch(BrainCallException){throw;}catch(Exception ex){throw new BrainCallException($"{Name} 响应解析失败：{ex.Message}",prompt,raw,ex);}
    }
    private async Task<HttpResponseMessage> BuildAndSend(string prompt,CancellationToken ct)
    {
        var provider=_slot.Provider.ToLowerInvariant();var req=new HttpRequestMessage(HttpMethod.Post,_slot.Endpoint);object body;
        if(provider.Contains("claude")){req.Headers.Add("x-api-key",_key);req.Headers.Add("anthropic-version","2023-06-01");body=new{model=_slot.Model,max_tokens=1200,messages=new[]{new{role="user",content=prompt}}};}
        else if(provider.Contains("gemini")){req.RequestUri=new Uri(_slot.Endpoint+( _slot.Endpoint.Contains('?')?'&':'?')+"key="+Uri.EscapeDataString(_key));body=new{contents=new[]{new{parts=new[]{new{text=prompt}}}},generationConfig=new{temperature=.1}};}
        else if(provider.Contains("openai")){req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);body=new{model=_slot.Model,input=prompt};}
        else {req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);body=new{model=_slot.Model,messages=new[]{new{role="user",content=prompt}},temperature=.1,max_tokens=1200};}
        req.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");return await Http.SendAsync(req,ct);
    }
    private string ExtractProviderText(string raw){using var d=JsonDocument.Parse(raw);var p=_slot.Provider.ToLowerInvariant();if(p.Contains("claude"))return d.RootElement.GetProperty("content")[0].GetProperty("text").GetString()??"";if(p.Contains("gemini"))return d.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()??"";if(p.Contains("openai")){if(d.RootElement.TryGetProperty("output_text",out var ot))return ot.GetString()??"";foreach(var o in d.RootElement.GetProperty("output").EnumerateArray())if(o.TryGetProperty("content",out var a))foreach(var x in a.EnumerateArray())if(x.TryGetProperty("text",out var t))return t.GetString()??"";}var m=d.RootElement.GetProperty("choices")[0].GetProperty("message");return m.TryGetProperty("content",out var c)?c.GetString()??"":m.GetProperty("reasoning_content").GetString()??"";}
    private static string ExtractJson(string s){var a=s.IndexOf('{');var b=s.LastIndexOf('}');if(a<0||b<=a)throw new JsonException("模型没有返回JSON");return s[a..(b+1)];}
}
