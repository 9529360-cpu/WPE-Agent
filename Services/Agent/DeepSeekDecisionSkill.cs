using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed class DeepSeekDecisionSkill
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<AgentDecision> DecideAsync(
        MarketSkillSnapshot current,
        MarketSkillSnapshot oneHour,
        MarketSkillSnapshot fourHour,
        AgentMemory? memory,
        double positionQuantity,
        double entryPrice,
        CancellationToken cancellationToken)
    {
        var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "API.txt");
        if (!File.Exists(keyPath)) throw new FileNotFoundException("桌面缺少 DeepSeek API.txt", keyPath);
        var apiKey = (await File.ReadAllTextAsync(keyPath, cancellationToken)).Trim();

        var prompt = new
        {
            role = "BTCUSDT U本位永续合约连续判断智能体",
            output = new
            {
                action = "LONG|SHORT|CLOSE|HOLD",
                confidence = "0..1",
                regime = "震荡|震荡偏多|震荡偏空|修复反弹|偏弱下行|上涨延续|下跌延续",
                reason = "最多三条核心依据",
                support = "数字",
                resistance = "数字",
                invalidation = "失效条件",
                memory_validation = "已验证|部分验证|未验证|已推翻|首次判断"
            },
            principles = new[]
            {
                "价格结构优先于叙事和单一指标",
                "多空冲突时给最终倾向，证据不足则降低置信度并HOLD",
                "核心区间内不声称趋势反转，轻微波动不改变观点",
                "对照上一轮关键位和失效条件进行验证",
                "空仓可LONG或SHORT；有仓位时只可CLOSE或HOLD"
            },
            current,
            oneHour,
            fourHourBackground = fourHour,
            position = new { quantity = positionQuantity, entryPrice },
            previousMemory = memory
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "deepseek-v4-flash",
            messages = new[] { new { role = "user", content = JsonSerializer.Serialize(prompt) } },
            temperature = 0.1,
            max_tokens = 700
        }), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek 请求失败 HTTP {(int)response.StatusCode}: {responseText}");
        using var document = JsonDocument.Parse(responseText);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(content) && message.TryGetProperty("reasoning_content", out var reasoningElement))
            content = reasoningElement.GetString();
        if (string.IsNullOrWhiteSpace(content))
            return new AgentDecision { Action = "HOLD", Confidence = 0, Reason = "DeepSeek 本轮没有返回可解析内容" };
        content = ExtractJson(content);
        AgentDecision? decision;
        try { decision = JsonSerializer.Deserialize<AgentDecision>(content, _json); }
        catch (JsonException) { return new AgentDecision { Action = "HOLD", Confidence = 0, Reason = "DeepSeek 返回格式异常，本轮不交易" }; }
        decision ??= new AgentDecision();
        decision.Action = decision.Action.ToUpperInvariant();
        if (decision.Action is not ("LONG" or "SHORT" or "CLOSE" or "HOLD"))
            return new AgentDecision { Action = "HOLD", Reason = "模型动作无效，本轮不交易" };
        return decision;
    }

    private static string ExtractJson(string content)
    {
        var fenced = content.IndexOf("```", StringComparison.Ordinal);
        if (fenced >= 0)
        {
            content = content[(fenced + 3)..].TrimStart('j', 's', 'o', 'n', '\n', '\r', ' ');
            var endFence = content.IndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0) content = content[..endFence];
        }
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : string.Empty;
    }
}
