using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using CoreModels = global::币安量化机器人.Core.Models;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public interface IAssistantProtocolAdapter
{
    HttpRequestMessage CreateRequest(BrainSlot slot, string secret, string prompt);
    string ExtractText(JsonDocument document);
}

public static class AssistantProtocolAdapterFactory
{
    public static IAssistantProtocolAdapter Create(string provider)
    {
        var id = provider.ToLowerInvariant();
        if (id.Contains("claude") || id.Contains("anthropic")) return new AnthropicMessagesAdapter();
        if (id.Contains("gemini") || id.Contains("google")) return new GeminiGenerativeAdapter();
        return new OpenAiCompatibleAdapter();
    }
}

public sealed class OpenAiCompatibleAdapter : IAssistantProtocolAdapter
{
    public HttpRequestMessage CreateRequest(BrainSlot slot, string secret, string prompt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, slot.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        object body = slot.Provider.Contains("openai", StringComparison.OrdinalIgnoreCase)
            ? new { model = slot.Model, input = prompt }
            : new { model = slot.Model, messages = new[] { new { role = "user", content = prompt } }, temperature = slot.Temperature, max_tokens = slot.MaxTokens };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }
    public string ExtractText(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("output_text", out var outputText)) return outputText.GetString() ?? string.Empty;
        if (root.TryGetProperty("output", out var output))
            foreach (var item in output.EnumerateArray())
                if (item.TryGetProperty("content", out var content))
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var text)) return text.GetString() ?? string.Empty;
        var message = root.GetProperty("choices")[0].GetProperty("message");
        return message.TryGetProperty("content", out var value) ? value.GetString() ?? string.Empty : message.GetProperty("reasoning_content").GetString() ?? string.Empty;
    }
}

public sealed class AnthropicMessagesAdapter : IAssistantProtocolAdapter
{
    public HttpRequestMessage CreateRequest(BrainSlot slot, string secret, string prompt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, slot.Endpoint);
        request.Headers.Add("x-api-key", secret);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = slot.Model, max_tokens = slot.MaxTokens, messages = new[] { new { role = "user", content = prompt } } }), Encoding.UTF8, "application/json");
        return request;
    }
    public string ExtractText(JsonDocument document) => document.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
}

public sealed class GeminiGenerativeAdapter : IAssistantProtocolAdapter
{
    public HttpRequestMessage CreateRequest(BrainSlot slot, string secret, string prompt)
    {
        var separator = slot.Endpoint.Contains('?') ? '&' : '?';
        var request = new HttpRequestMessage(HttpMethod.Post, slot.Endpoint + separator + "key=" + Uri.EscapeDataString(secret));
        request.Content = new StringContent(JsonSerializer.Serialize(new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { temperature = slot.Temperature } }), Encoding.UTF8, "application/json");
        return request;
    }
    public string ExtractText(JsonDocument document) => document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;
}

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

public sealed record AssistantAdapterDescriptor(string Id, string DisplayName, bool Local, string Protocol);

public static class AssistantAdapterCatalog
{
    public static IReadOnlyList<AssistantAdapterDescriptor> All { get; } =
    [
        new("local-deterministic", "WPE Local Deterministic", true, "native"),
        new("openai", "OpenAI", false, "OpenAI-compatible"),
        new("claude", "Anthropic Claude", false, "Anthropic Messages"),
        new("gemini", "Google Gemini", false, "Gemini Generative API"),
        new("deepseek", "DeepSeek", false, "OpenAI-compatible"),
        new("ollama", "Ollama", true, "OpenAI-compatible"),
        new("custom", "Custom API", false, "OpenAI-compatible")
    ];
}

internal static class BrainPromptComposer
{
    private const int MaxDetailedMarketContexts = 2;

    private enum PromptDetailLevel
    {
        SlimBrief,
        DetailedContext
    }

    private const int MaxMarketBriefContexts = 4;

    private sealed record PromptCompressionProfile(
        int NewsCount,
        int NewsSummaryChars,
        int PreviousOutcomeCount,
        int PreviousOutcomeChars,
        int SignalCount,
        int RecentCloseCount,
        int MissingConditionCount);

    private static readonly PromptCompressionProfile[] Profiles =
    [
        new(12, 220, 8, 180, 6, 4, 8),
        new(6, 140, 5, 120, 4, 3, 6),
        new(4, 90, 3, 80, 2, 2, 4)
    ];

    internal static string BuildDecisionPrompt(EvidencePack evidence, AgentContext context, string outputLanguage, int contextLimit, string instruction)
    {
        var budget = DetermineTargetCharacters(contextLimit);
        string? best = null;
        foreach (var profile in Profiles)
        {
            var slimPrompt = BuildPrompt(evidence, context, outputLanguage, instruction, profile, PromptDetailLevel.SlimBrief);
            best = slimPrompt;
            if (slimPrompt.Length <= budget || !ShouldUpgradeToDetailedContext(evidence, context)) return slimPrompt;

            var detailedPrompt = BuildPrompt(evidence, context, outputLanguage, instruction, profile, PromptDetailLevel.DetailedContext);
            best = detailedPrompt;
            if (detailedPrompt.Length <= budget) return detailedPrompt;
        }

        return best ?? JsonSerializer.Serialize(new { instruction, outputLanguage });
    }

    private static string BuildPrompt(
        EvidencePack evidence,
        AgentContext context,
        string outputLanguage,
        string instruction,
        PromptCompressionProfile profile,
        PromptDetailLevel detailLevel)
    {
        var orderedAssessments = context.MarketAssessments
            .OrderByDescending(x => x.EntryReady)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => Math.Abs(x.NetScore))
            .ToArray();
        var detailedSymbols = detailLevel == PromptDetailLevel.DetailedContext
            ? SelectDetailedSymbols(evidence, context, orderedAssessments)
            : Array.Empty<string>();
        return JsonSerializer.Serialize(new
        {
            instruction,
            outputLanguage,
            promptStructure = detailLevel == PromptDetailLevel.SlimBrief ? "two-level-slim-brief" : "two-level-detailed-context",
            evidence = BuildEvidence(evidence, context, profile, detailLevel, detailedSymbols),
            marketAssessments = orderedAssessments
                .Select(x => BuildAssessment(x, profile, detailLevel))
                .ToArray(),
            context = new
            {
                context.BrainName,
                context.CircuitBreakerActive,
                context.ActiveSymbol,
                outcomeMemorySchema = "wpe.planner-outcome-memory/1.0",
                outcomeMemory = context.OutcomeMemories
                    .Take(profile.PreviousOutcomeCount)
                    .Select(x => new
                    {
                        t = x.CycleStartedUtc,
                        m = TrimText(x.Mode, 16),
                        x = TrimText(x.Exchange, 24),
                        s = TrimText(x.Symbol, 24),
                        a = TrimText(x.DecisionAction, 16),
                        r = TrimText(x.RiskResult, 16),
                        rc = TrimText(x.RiskReasonCode, 40),
                        ea = x.ExecutionAttempted,
                        er = TrimText(x.ExecutionResult, 24),
                        sc = x.StateChanged,
                        next = TrimText(x.RecoveryHint, 40),
                        note = TrimText(x.DecisionSummary, Math.Min(profile.PreviousOutcomeChars, 80))
                    })
                    .ToArray(),
                relevantMemorySchema = "wpe.planner-tiered-memory/1.0",
                relevantMemory = (context.RelevantMemories??[]).Take(6).Select(x=>new
                {
                    t=TrimText(x.Tier,12),
                    at=x.OccurredAtUtc,
                    r=TrimText(x.Result,24),
                    src=TrimText(x.Source,32),
                    note=TrimText(x.Summary,100)
                }).ToArray(),
                context.ConsecutiveHolds
            },
            detailedMarketContext = detailLevel == PromptDetailLevel.DetailedContext
                ? BuildDetailedMarkets(evidence, detailedSymbols, profile.RecentCloseCount)
                : null
        });
    }

    private static object BuildEvidence(EvidencePack evidence, AgentContext context, PromptCompressionProfile profile, PromptDetailLevel detailLevel, IReadOnlyList<string> detailedSymbols) => new
    {
        evidence.CollectedAt,
        Mode = context.CircuitBreakerActive ? "degraded" : "active",
        Account = new
        {
            evidence.Account.WalletBalance,
            evidence.Account.AvailableBalance,
            evidence.Account.Equity,
            evidence.Account.Timestamp
        },
        Positions = evidence.Positions
            .Take(6)
            .Select(x => new
            {
                x.Symbol,
                x.Side,
                x.Quantity,
                x.EntryPrice,
                x.UnrealizedPnl
            })
            .ToArray(),
        Markets = BuildMarketBriefs(evidence, context, detailLevel, detailedSymbols),
        MissingSources = evidence.MissingSources
            .Take(6)
            .Select(x => TrimText(x, 96))
            .ToArray(),
        evidence.Completeness,
        News = BuildNews(evidence.News, profile, detailLevel)
    };

    private static object[] BuildNews(IReadOnlyList<NewsEvidence> news, PromptCompressionProfile profile, PromptDetailLevel detailLevel) =>
        news.OrderByDescending(x => x.PublishedAt ?? x.CollectedAt)
            .Take(profile.NewsCount)
            .Select(x =>
            {
                var item = new Dictionary<string, object?>
                {
                    ["Source"] = x.Source,
                    ["Title"] = x.Title,
                    ["PublishedAt"] = x.PublishedAt,
                    ["Confidence"] = x.Confidence,
                    ["EventType"] = x.EventType,
                    ["IsBreaking"] = x.IsBreaking
                };
                if (detailLevel == PromptDetailLevel.DetailedContext && !string.IsNullOrWhiteSpace(x.BodySummary))
                    item["BodySummary"] = TrimText(x.BodySummary, profile.NewsSummaryChars);
                return (object)item;
            })
            .ToArray();

    private static object[] BuildMarketBriefs(EvidencePack evidence, AgentContext context, PromptDetailLevel detailLevel, IReadOnlyList<string> detailedSymbols)
    {
        var selected = new List<string>();
        var excluded = detailLevel == PromptDetailLevel.DetailedContext
            ? detailedSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedAssessments = context.MarketAssessments
            .OrderByDescending(x => x.EntryReady)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => Math.Abs(x.NetScore))
            .ToArray();

        void Add(string? symbol, bool allowDetailedDuplicate = false)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            if (!evidence.Markets.ContainsKey(symbol)) return;
            if (!allowDetailedDuplicate && excluded.Contains(symbol)) return;
            if (!selected.Contains(symbol, StringComparer.OrdinalIgnoreCase)) selected.Add(symbol);
        }

        Add(context.ActiveSymbol);
        foreach (var symbol in evidence.Positions.Select(x => x.Symbol))
            Add(symbol);
        foreach (var assessment in orderedAssessments.Where(x => x.EntryReady))
        {
            Add(assessment.Symbol);
            if (selected.Count >= MaxMarketBriefContexts) break;
        }
        foreach (var assessment in orderedAssessments)
        {
            Add(assessment.Symbol);
            if (selected.Count >= MaxMarketBriefContexts) break;
        }
        foreach (var symbol in evidence.Markets.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            Add(symbol);
            if (selected.Count >= MaxMarketBriefContexts) break;
        }
        if (selected.Count == 0 && detailLevel == PromptDetailLevel.DetailedContext)
            foreach (var symbol in detailedSymbols.Take(MaxMarketBriefContexts))
            {
                Add(symbol, allowDetailedDuplicate: true);
                if (selected.Count >= MaxMarketBriefContexts) break;
            }

        return selected
            .Where(symbol => evidence.Markets.ContainsKey(symbol))
            .Select(symbol => BuildMarketBrief(evidence.Markets[symbol]))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object> BuildDetailedMarkets(EvidencePack evidence, IReadOnlyList<string> detailedSymbols, int recentCloseCount) =>
        evidence.Markets
            .Where(x => detailedSymbols.Contains(x.Key))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => BuildDetailedMarket(x.Value, recentCloseCount), StringComparer.OrdinalIgnoreCase);

    private static bool ShouldUpgradeToDetailedContext(EvidencePack evidence, AgentContext context)
    {
        var ordered = context.MarketAssessments
            .OrderByDescending(x => x.EntryReady)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => Math.Abs(x.NetScore))
            .ToArray();
        if (ordered.Any(x => x.EntryReady)) return true;
        if (!string.IsNullOrWhiteSpace(context.ActiveSymbol)) return true;
        if (evidence.Positions.Count > 0) return true;
        if (ordered.Length > 1 && Math.Abs(ordered[0].NetScore - ordered[1].NetScore) <= 0.15) return true;
        return false;
    }

    private static IReadOnlyList<string> SelectDetailedSymbols(
        EvidencePack evidence,
        AgentContext context,
        IReadOnlyList<MarketDecisionAssessment> orderedAssessments)
    {
        var selected = new List<string>();

        void Add(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            if (!evidence.Markets.ContainsKey(symbol)) return;
            if (!selected.Contains(symbol, StringComparer.OrdinalIgnoreCase)) selected.Add(symbol);
        }

        Add(context.ActiveSymbol);
        foreach (var symbol in evidence.Positions.Select(x => x.Symbol))
            Add(symbol);
        foreach (var assessment in orderedAssessments.Where(x => x.EntryReady))
        {
            Add(assessment.Symbol);
            if (selected.Count >= MaxDetailedMarketContexts) break;
        }
        if (selected.Count == 0 && orderedAssessments.Count > 0)
        {
            Add(orderedAssessments[0].Symbol);
            if (orderedAssessments.Count > 1 && Math.Abs(orderedAssessments[0].NetScore - orderedAssessments[1].NetScore) <= 0.15)
                Add(orderedAssessments[1].Symbol);
        }

        return selected.Take(MaxDetailedMarketContexts).ToArray();
    }

    private static object BuildMarketBrief(MarketEvidence market) =>
        new
        {
            market.Symbol,
            market.Price,
            market.Support,
            market.Resistance,
            market.Rsi,
            market.Trend15m,
            market.CollectedAt,
            QualityScore = market.Quality.QualityScore
        };

    private static object BuildDetailedMarket(MarketEvidence market, int recentCloseCount)
    {
        CandleEvidence? last = market.Candles.Count > 0 ? market.Candles[^1] : null;
        CandleEvidence? previous = market.Candles.Count > 1 ? market.Candles[^2] : null;
        var recent = market.Candles.TakeLast(Math.Max(1, recentCloseCount)).Select(x => x.Close).ToArray();
        var window = market.Candles.TakeLast(Math.Min(24, market.Candles.Count)).ToArray();
        var dayHigh = window.Length == 0 ? market.Price : window.Max(x => x.High);
        var dayLow = window.Length == 0 ? market.Price : window.Min(x => x.Low);
        var lastChange = last is null || previous is null || previous.Close == 0 ? 0 : (double)(last.Close / previous.Close - 1);

        return new
        {
            market.Symbol,
            market.Price,
            market.Support,
            market.Resistance,
            market.Rsi,
            market.Trend15m,
            market.Trend1h,
            market.Trend4h,
            market.CollectedAt,
            Derivatives = new
            {
                market.Derivatives.FundingRate,
                market.Derivatives.OpenInterest,
                market.Derivatives.LongShortRatio,
                market.Derivatives.Basis
            },
            Quality = new
            {
                market.Quality.SpreadBps,
                market.Quality.AtrPercent,
                market.Quality.LiquidityScore,
                market.Quality.QualityScore,
                Anomalies = market.Quality.Anomalies.Take(2).Select(x => TrimText(x, 80)).ToArray()
            },
            CandleSummary = new
            {
                LastClose = last?.Close ?? market.Price,
                LastCloseChangePct = lastChange,
                DayHigh = dayHigh,
                DayLow = dayLow,
                RecentCloses = recent
            }
        };
    }

    private static object BuildAssessment(MarketDecisionAssessment assessment, PromptCompressionProfile profile, PromptDetailLevel detailLevel) => new
    {
        assessment.Symbol,
        assessment.Regime,
        assessment.NetScore,
        assessment.Confidence,
        assessment.ConflictRatio,
        assessment.Fresh,
        assessment.EntryReady,
        assessment.RecommendedAction,
        Signals = assessment.Signals
            .OrderByDescending(x => Math.Abs(x.WeightedScore))
            .Take(profile.SignalCount)
            .Select(x => BuildSignal(x, detailLevel))
            .ToArray(),
        MissingConditions = assessment.MissingConditions
            .Take(profile.MissingConditionCount)
            .Select(x => TrimText(x, 120))
            .ToArray(),
        Summary = TrimText(assessment.Summary, 180)
    };

    private static object BuildSignal(SignalContribution signal, PromptDetailLevel detailLevel)
    {
        var item = new Dictionary<string, object?>
        {
            ["Name"] = signal.Name,
            ["Horizon"] = signal.Horizon,
            ["WeightedScore"] = signal.WeightedScore,
            ["Direction"] = signal.Direction
        };
        if (detailLevel == PromptDetailLevel.DetailedContext)
        {
            item["RawValue"] = signal.RawValue;
            item["Weight"] = signal.Weight;
            item["Explanation"] = TrimText(signal.Explanation, 120);
        }
        return item;
    }

    private static int DetermineTargetCharacters(int contextLimit)
    {
        var bounded = Math.Clamp(contextLimit, 1024, 32000);
        var target = bounded * 3;
        return Math.Clamp(target, 3_000, 12_000);
    }

    private static string TrimText(string? value, int maxChars)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length <= maxChars) return value;
        return value[..Math.Max(1, maxChars - 3)] + "...";
    }
}

public sealed class HttpBrainProvider : IAssistantProvider
{
    private readonly HttpClient _http; private readonly BrainSlot _slot; private readonly string _key; private readonly LlmRequestGovernor _governor; private readonly IAssistantProtocolAdapter _protocol; private readonly CoreModels.AiRuntimeMode _mode; public string Name=>_slot.Provider; public bool IsLocal => false;
    public HttpBrainProvider(BrainSlot slot,string key,CoreModels.AiRuntimeMode mode=CoreModels.AiRuntimeMode.LocalOnly,LlmRequestGovernor? governor=null, IAssistantProtocolAdapter? protocol=null){_slot=slot;_key=key;_mode=mode;_governor=governor??LlmRequestGovernor.Shared;_protocol=protocol??AssistantProtocolAdapterFactory.Create(slot.Provider);_http=new HttpClient{Timeout=Timeout.InfiniteTimeSpan};}
    public async Task<BrainHealth> HealthCheckAsync(CancellationToken ct){if(string.IsNullOrWhiteSpace(_key)||string.IsNullOrWhiteSpace(_slot.Endpoint)||string.IsNullOrWhiteSpace(_slot.Model))return new(false,LocalizationService.Current.T("Provider.Incomplete"));try{using var r=await BuildAndSend("Reply only: HOLD",ct);return new(r.IsSuccessStatusCode,$"HTTP {(int)r.StatusCode}");}catch(Exception ex){return new(false,global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(ex.Message,180,_key,_slot.EncryptedKey));}}
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
        var prompt = BrainPromptComposer.BuildDecisionPrompt(e, c, LocalizationService.Current.CurrentLanguage.AiLanguage, _slot.ContextLimit, instruction);
        string raw="";
        try{using var r=await BuildAndSend(prompt,ct);raw=await r.Content.ReadAsStringAsync(ct);if(!r.IsSuccessStatusCode)throw new BrainCallException($"{Name} HTTP {(int)r.StatusCode}",AuditRef("prompt",prompt),AuditRef("response",raw));var content=ExtractProviderText(raw);var json=ExtractJson(content);var opt=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};opt.Converters.Add(new JsonStringEnumConverter());opt.Converters.Add(new FlexibleStringListConverter());var decision=JsonSerializer.Deserialize<DecisionPlan>(json,opt)??new DecisionPlan{Reason=LocalizationService.Current.T("Provider.EmptyResponse")};decision.MissingConditions??=[];decision.EvidenceReferences??=[];return new(decision,AuditRef("prompt",prompt),AuditRef("response",raw));}
        catch(BrainCallException){throw;}catch(Exception ex){throw new BrainCallException(LocalizationService.Current.T("Provider.ParseFailed",Name,global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(ex.Message,180,_key,_slot.EncryptedKey)),AuditRef("prompt",prompt),AuditRef("response",raw),ex);}
    }
    private async Task<HttpResponseMessage> BuildAndSend(string prompt,CancellationToken ct)
    {
        var purpose=prompt=="Reply only: HOLD"?"health-check":"assistant-advice";
        var context=new LlmRequestContext(_mode,_slot.PromptVersion,_slot.MaxTokens,_slot.ContextLimit,TimeSpan.FromSeconds(Math.Clamp(_slot.TimeoutSeconds,5,300)),"brain-planner","assistant-provider");
        return await _governor.SendAsync(_slot.Provider,_slot.Model,purpose,prompt,context,token=>
        {
            var request=_protocol.CreateRequest(_slot,_key,prompt);
            return _http.SendAsync(request,token);
        },ct);
    }
    private string ExtractProviderText(string raw){using var d=JsonDocument.Parse(raw);return _protocol.ExtractText(d);}
private static string ExtractJson(string s){var a=s.IndexOf('{');var b=s.LastIndexOf('}');if(a<0||b<=a)throw new JsonException(LocalizationService.Current.T("Provider.JsonMissing"));return s[a..(b+1)];}
private string AuditRef(string kind,string value){var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value??string.Empty)));return $"{kind}Hash={hash}; length={value?.Length??0}; promptVersion={_slot.PromptVersion}";}
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

    public static IAssistantProvider Create(BrainSlot? slot, string? secret, CoreModels.AiRuntimeMode mode)
    {
        if (mode == CoreModels.AiRuntimeMode.LocalOnly || slot is null || string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(slot.Endpoint) || string.IsNullOrWhiteSpace(slot.Model))
            return CreateLocal();
        return new HttpBrainProvider(slot, secret, mode);
    }

    public static IAssistantProvider Create(BrainSlot? slot, string? secret, bool allowRemote) =>
        Create(slot, secret, allowRemote ? CoreModels.AiRuntimeMode.Hybrid : CoreModels.AiRuntimeMode.LocalOnly);
}
