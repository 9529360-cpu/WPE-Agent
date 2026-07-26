using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreModels = global::币安量化机器人.Core.Models;

namespace 币安量化机器人.Services.Agent;

public sealed record LlmUsagePolicy(
    int DailyCallLimit = 100,
    int DailyTokenLimit = 250_000,
    decimal DailyCostLimitUsd = 10m,
    TimeSpan? CacheTtl = null,
    int MaxInputTokens = 24_000,
    int MaxOutputTokens = 32_768,
    int MaxContextCharacters = 96_000,
    TimeSpan? MaxDuration = null,
    decimal HybridRequestCostLimitUsd = 0.03m,
    decimal AiResearchRequestCostLimitUsd = 0.15m)
{
    public TimeSpan EffectiveCacheTtl => CacheTtl ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveMaxDuration => MaxDuration ?? TimeSpan.FromSeconds(90);
}

public sealed record LlmRequestContext(
    CoreModels.AiRuntimeMode Mode,
    string PromptVersion,
    int MaxOutputTokens,
    int ContextLimit,
    TimeSpan Timeout,
    string AgentId = "core",
    string ToolId = "assistant");

public sealed record LlmCallAudit(
    DateTime AtUtc,
    string Provider,
    string Model,
    string Purpose,
    string PromptHash,
    bool CacheHit,
    bool Allowed,
    int EstimatedInputTokens,
    int EstimatedOutputTokens,
    decimal EstimatedCostUsd,
    long DurationMs,
    string Outcome,
    string AgentId = "core",
    string ToolId = "assistant",
    string PromptVersion = "unknown",
    string RequestMode = "LocalOnly",
    string TokenSource = "estimate",
    bool FallbackActivated = false);

public sealed record LlmUsageSnapshot(
    int Calls,
    int Tokens,
    decimal CostUsd,
    int CacheHits,
    int BudgetBlocks,
    string TopProvider,
    string TopPurpose,
    int Fallbacks = 0,
    int PrivacyBlocks = 0,
    DateTime? LastCallAtUtc = null);

public sealed record LlmUsageBreakdown(string TopAgent, int TopAgentCalls, string TopTool, int TopToolCalls);
public sealed record LlmCallUsage(int InputTokens, int OutputTokens, decimal CostUsd, bool CacheHit, bool Allowed, string Outcome, string TokenSource)
{
    public int LoggedTokens => CacheHit || !Allowed ? 0 : Math.Max(0, InputTokens) + Math.Max(0, OutputTokens);
    public decimal LoggedCostUsd => CacheHit || !Allowed ? 0 : CostUsd;
}

public sealed class LlmGovernanceException : InvalidOperationException
{
    public string Code { get; }
    public LlmGovernanceException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>Mandatory fail-closed boundary for every optional remote assistant request.</summary>
public sealed class LlmRequestGovernor
{
    private sealed record CacheEntry(string Body, DateTime ExpiresAtUtc);
    private sealed record PersistedCache(string Key, string Body, DateTime ExpiresAtUtc);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private static readonly AsyncLocal<Guid?> _currentCallUsageScope = new();
    private static readonly ConcurrentDictionary<Guid, LlmCallUsage> _scopedCallUsage = new();
    private static readonly object _usageGate = new();
    private static LlmCallUsage? _lastCallUsage;
    private readonly LlmUsagePolicy _policy;
    private readonly string _auditPath;
    private readonly string _cachePath;
    private DateTime _snapshotAtUtc;
    private LlmUsageSnapshot _snapshot = new(0, 0, 0, 0, 0, "LOCAL", "NONE");
    public static LlmRequestGovernor Shared { get; } = new();

    public LlmRequestGovernor(LlmUsagePolicy? policy = null, string? auditPath = null)
    {
        _policy = policy ?? new LlmUsagePolicy();
        _auditPath = auditPath ?? AppDataPaths.File("llm-calls.jsonl");
        var directory = Path.GetDirectoryName(_auditPath);
        _cachePath = Path.Combine(string.IsNullOrWhiteSpace(directory) ? AppDataPaths.DataDirectory : directory, "llm-cache.jsonl");
        LoadPersistentCache();
    }

    public static void ClearCurrentCallUsage()
    {
        var scope = _currentCallUsageScope.Value ?? Guid.NewGuid();
        _currentCallUsageScope.Value = scope;
        _scopedCallUsage.TryRemove(scope, out _);
        lock (_usageGate) _lastCallUsage = null;
    }
    public static LlmCallUsage? ConsumeCurrentCallUsage()
    {
        var scope = _currentCallUsageScope.Value;
        if (scope is not null && _scopedCallUsage.TryRemove(scope.Value, out var usage))
        {
            lock (_usageGate)
            {
                if (_lastCallUsage == usage) _lastCallUsage = null;
            }

            return usage;
        }

        lock (_usageGate)
        {
            var fallbackUsage = _lastCallUsage;
            _lastCallUsage = null;
            return fallbackUsage;
        }
    }

    public LlmUsageSnapshot GetTodaySnapshot()
    {
        if (DateTime.UtcNow - _snapshotAtUtc < TimeSpan.FromSeconds(3)) return _snapshot;
        var rows = ReadRows().Where(x => x.AtUtc.Date == DateTime.UtcNow.Date).ToArray();
        var billable = rows.Where(x => x.Allowed && !x.CacheHit).ToArray();
        _snapshot = new(
            billable.Length,
            billable.Sum(x => x.EstimatedInputTokens + x.EstimatedOutputTokens),
            billable.Sum(x => x.EstimatedCostUsd),
            rows.Count(x => x.CacheHit),
            rows.Count(x => !x.Allowed && x.Outcome.Contains("BUDGET", StringComparison.Ordinal)),
            billable.GroupBy(x => x.Provider).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key ?? "LOCAL",
            billable.GroupBy(x => x.Purpose).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key ?? "NONE",
            rows.Count(x => x.FallbackActivated),
            rows.Count(x => x.Outcome == "LOCAL_ONLY_BLOCKED"),
            billable.OrderByDescending(x => x.AtUtc).FirstOrDefault()?.AtUtc);
        _snapshotAtUtc = DateTime.UtcNow;
        return _snapshot;
    }

    public LlmUsageBreakdown GetTodayBreakdown()
    {
        var rows = ReadRows().Where(x => x.AtUtc.Date == DateTime.UtcNow.Date && x.Allowed && !x.CacheHit).ToArray();
        var agent = rows.GroupBy(x => string.IsNullOrWhiteSpace(x.AgentId) ? "core" : x.AgentId).OrderByDescending(x => x.Count()).FirstOrDefault();
        var tool = rows.GroupBy(x => string.IsNullOrWhiteSpace(x.ToolId) ? "assistant" : x.ToolId).OrderByDescending(x => x.Count()).FirstOrDefault();
        return new(agent?.Key ?? "core", agent?.Count() ?? 0, tool?.Key ?? "assistant", tool?.Count() ?? 0);
    }

    public async Task<HttpResponseMessage> SendAsync(
        string provider,
        string model,
        string purpose,
        string prompt,
        LlmRequestContext context,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct)
    {
        await _requestLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt ?? string.Empty)));
            var inputTokens = EstimateTokens(prompt);
            var outputLimit = Math.Clamp(context.MaxOutputTokens, 1, _policy.MaxOutputTokens);
            var requestCostLimit = context.Mode == CoreModels.AiRuntimeMode.AIResearch ? _policy.AiResearchRequestCostLimitUsd : _policy.HybridRequestCostLimitUsd;

            if (context.Mode == CoreModels.AiRuntimeMode.LocalOnly)
                await BlockAsync("LOCAL_ONLY_BLOCKED", "Local Only mode prohibits remote assistant calls.", false);
            var configuredContextCharacters = (int)Math.Min(int.MaxValue, Math.Max(1L, (long)context.ContextLimit * 4));
            if ((prompt?.Length ?? 0) > Math.Min(_policy.MaxContextCharacters, configuredContextCharacters))
                await BlockAsync("CONTEXT_BUDGET_BLOCKED", "Remote assistant context budget exceeded.", true);
            if (inputTokens > _policy.MaxInputTokens)
                await BlockAsync("INPUT_TOKEN_BUDGET_BLOCKED", "Remote assistant input token budget exceeded.", true);
            var reservedCost = EstimateCost(inputTokens, outputLimit);
            if (reservedCost > requestCostLimit)
                await BlockAsync("REQUEST_COST_BUDGET_BLOCKED", "Remote assistant per-request cost budget exceeded.", true);

            var key = $"{provider}:{model}:{purpose}:{context.PromptVersion}:{promptHash}";
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
            {
                PublishUsage(new(0, 0, 0, true, true, "CACHE_HIT", "cache"));
                await AuditAsync(Row(true, true, EstimateTokens(cached.Body), 0, "CACHE_HIT", false), ct);
                return JsonResponse(cached.Body);
            }

            var today = await ReadTodayAsync(ct);
            if (today.Calls >= _policy.DailyCallLimit || today.Tokens + inputTokens + outputLimit > _policy.DailyTokenLimit || today.Cost + reservedCost > _policy.DailyCostLimitUsd)
                await BlockAsync("DAILY_BUDGET_BLOCKED", "Remote assistant daily budget reached.", true);

            var maxDuration = context.Timeout <= TimeSpan.Zero ? _policy.EffectiveMaxDuration : context.Timeout;
            if (maxDuration > _policy.EffectiveMaxDuration) maxDuration = _policy.EffectiveMaxDuration;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(maxDuration);
            var started = Stopwatch.StartNew();
            try
            {
                using var response = await send(timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var (actualInput, actualOutput, source) = ReadUsage(body, inputTokens);
                var cost = EstimateCost(actualInput, actualOutput);
                var fallback = !response.IsSuccessStatusCode;
                var outcome = $"HTTP_{(int)response.StatusCode}";
                PublishUsage(new(actualInput, actualOutput, cost, false, true, outcome, source));
                await AuditAsync(Row(true, false, actualOutput, started.ElapsedMilliseconds, outcome, fallback, actualInput, cost, source), CancellationToken.None);
                if (response.IsSuccessStatusCode)
                {
                    var entry = new CacheEntry(body, DateTime.UtcNow.Add(_policy.EffectiveCacheTtl));
                    _cache[key] = entry;
                    await PersistCacheAsync(key, entry, CancellationToken.None);
                }
                return JsonResponse(body, response.StatusCode);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                PublishUsage(new(inputTokens, 0, EstimateCost(inputTokens, 0), false, true, "TIMEOUT", "estimate"));
                await AuditAsync(Row(true, false, 0, started.ElapsedMilliseconds, "TIMEOUT", true), CancellationToken.None);
                throw new LlmGovernanceException("TIMEOUT", "Remote assistant timed out; use deterministic local fallback.", ex);
            }
            catch (Exception ex) when (ex is not LlmGovernanceException)
            {
                PublishUsage(new(inputTokens, 0, EstimateCost(inputTokens, 0), false, true, "REMOTE_ERROR", "estimate"));
                await AuditAsync(Row(true, false, 0, started.ElapsedMilliseconds, "REMOTE_ERROR", true), CancellationToken.None);
                throw new LlmGovernanceException("REMOTE_ERROR", "Remote assistant failed; use deterministic local fallback.", ex);
            }

            LlmCallAudit Row(bool allowed, bool cacheHit, int outputTokens, long duration, string outcome, bool fallback, int? actualInput = null, decimal? cost = null, string tokenSource = "estimate") =>
                new(now, Safe(provider), Safe(model), Safe(purpose), promptHash, cacheHit, allowed, actualInput ?? inputTokens, outputTokens,
                    cost ?? (allowed && !cacheHit ? EstimateCost(actualInput ?? inputTokens, outputTokens) : 0), duration, outcome,
                    Safe(context.AgentId), Safe(context.ToolId), Safe(context.PromptVersion), context.Mode.ToString(), tokenSource, fallback);

            async Task BlockAsync(string code, string message, bool fallback)
            {
                PublishUsage(new(0, 0, 0, false, false, code, "blocked"));
                await AuditAsync(Row(false, false, 0, 0, code, fallback), CancellationToken.None);
                throw new LlmGovernanceException(code, message + " WPE remains available through local deterministic processing.");
            }
        }
        finally { _requestLock.Release(); }
    }

    private IEnumerable<LlmCallAudit> ReadRows()
    {
        if (!File.Exists(_auditPath)) yield break;
        foreach (var line in File.ReadLines(_auditPath))
        {
            LlmCallAudit? row = null;
            try { row = JsonSerializer.Deserialize<LlmCallAudit>(line); } catch (JsonException) { }
            if (row is not null) yield return row;
        }
    }

    private async Task<(int Calls, int Tokens, decimal Cost)> ReadTodayAsync(CancellationToken ct)
    {
        if (!File.Exists(_auditPath)) return (0, 0, 0);
        var day = DateTime.UtcNow.Date; var calls = 0; var tokens = 0; decimal cost = 0;
        foreach (var line in await File.ReadAllLinesAsync(_auditPath, ct))
        {
            try
            {
                var row = JsonSerializer.Deserialize<LlmCallAudit>(line);
                if (row is null || row.AtUtc.Date != day || row.CacheHit || !row.Allowed) continue;
                calls++; tokens += row.EstimatedInputTokens + row.EstimatedOutputTokens; cost += row.EstimatedCostUsd;
            }
            catch (JsonException) { }
        }
        return (calls, tokens, cost);
    }

    private async Task AuditAsync(LlmCallAudit row, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_auditPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await _auditLock.WaitAsync(ct);
        try { await File.AppendAllTextAsync(_auditPath, JsonSerializer.Serialize(row) + Environment.NewLine, ct); _snapshotAtUtc = default; }
        finally { _auditLock.Release(); }
    }

    private void LoadPersistentCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            foreach (var line in File.ReadLines(_cachePath))
            {
                var row = JsonSerializer.Deserialize<PersistedCache>(line);
                if (row is not null && row.ExpiresAtUtc > DateTime.UtcNow) _cache[row.Key] = new(row.Body, row.ExpiresAtUtc);
            }
        }
        catch { }
    }

    private async Task PersistCacheAsync(string key, CacheEntry entry, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(_cachePath, JsonSerializer.Serialize(new PersistedCache(key, entry.Body, entry.ExpiresAtUtc)) + Environment.NewLine, ct);
        }
        catch { }
    }

    private static (int Input, int Output, string Source) ReadUsage(string body, int estimatedInput)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("usage", out var usage)) return (estimatedInput, EstimateTokens(body), "estimate");
            var input = Number(usage, "input_tokens") ?? Number(usage, "prompt_tokens");
            var output = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens");
            if (input is not null && output is not null) return (input.Value, output.Value, "provider");
        }
        catch (JsonException) { }
        return (estimatedInput, EstimateTokens(body), "estimate");
    }

    private static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt32(out var result) ? result : null;
    private static int EstimateTokens(string? value) => Math.Max(1, (value?.Length ?? 0) / 4);
    private static decimal EstimateCost(int input, int output) => input / 1_000_000m * 1m + output / 1_000_000m * 4m;
    private static void PublishUsage(LlmCallUsage usage)
    {
        var scope = _currentCallUsageScope.Value;
        if (scope is not null) _scopedCallUsage[scope.Value] = usage;
        lock (_usageGate) _lastCallUsage = usage;
    }
    private static string Safe(string? value)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 96 ? value : value[..96];
    }
    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
