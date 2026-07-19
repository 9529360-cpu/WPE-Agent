using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record LlmUsagePolicy(int DailyCallLimit = 100, int DailyTokenLimit = 250_000, decimal DailyCostLimitUsd = 10m, TimeSpan? CacheTtl = null)
{
    public TimeSpan EffectiveCacheTtl => CacheTtl ?? TimeSpan.FromMinutes(15);
}

public sealed record LlmCallAudit(DateTime AtUtc, string Provider, string Model, string Purpose, string PromptHash, bool CacheHit, bool Allowed, int EstimatedInputTokens, int EstimatedOutputTokens, decimal EstimatedCostUsd, long DurationMs, string Outcome);
public sealed record LlmUsageSnapshot(int Calls, int Tokens, decimal CostUsd, int CacheHits, int BudgetBlocks, string TopProvider);

/// <summary>Mandatory boundary for every optional remote assistant request.</summary>
public sealed class LlmRequestGovernor
{
    private sealed record CacheEntry(string Body, DateTime ExpiresAtUtc);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly LlmUsagePolicy _policy;
    private readonly string _auditPath;
    private DateTime _snapshotAtUtc;
    private LlmUsageSnapshot _snapshot = new(0, 0, 0, 0, 0, "LOCAL");
    public static LlmRequestGovernor Shared { get; } = new();

    public LlmRequestGovernor(LlmUsagePolicy? policy = null, string? auditPath = null)
    {
        _policy = policy ?? new LlmUsagePolicy();
        _auditPath = auditPath ?? AppDataPaths.File("llm-calls.jsonl");
    }

    public LlmUsageSnapshot GetTodaySnapshot()
    {
        if (DateTime.UtcNow - _snapshotAtUtc < TimeSpan.FromSeconds(3)) return _snapshot;
        var rows = new List<LlmCallAudit>();
        if (File.Exists(_auditPath)) foreach (var line in File.ReadLines(_auditPath)) try { var row = JsonSerializer.Deserialize<LlmCallAudit>(line); if (row is not null && row.AtUtc.Date == DateTime.UtcNow.Date) rows.Add(row); } catch (JsonException) { }
        var billable = rows.Where(x => x.Allowed && !x.CacheHit).ToArray();
        _snapshot = new(billable.Length, billable.Sum(x => x.EstimatedInputTokens + x.EstimatedOutputTokens), billable.Sum(x => x.EstimatedCostUsd), rows.Count(x => x.CacheHit), rows.Count(x => !x.Allowed), billable.GroupBy(x => x.Provider).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key ?? "LOCAL");
        _snapshotAtUtc = DateTime.UtcNow;
        return _snapshot;
    }

    public async Task<HttpResponseMessage> SendAsync(string provider, string model, string purpose, string prompt, Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        await _requestLock.WaitAsync(ct);
        try
        {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\n{model}\n{purpose}\n{prompt}")));
        var key = $"{provider}:{model}:{purpose}:{hash}";
        var inputTokens = EstimateTokens(prompt);
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            await AuditAsync(new(DateTime.UtcNow, provider, model, purpose, hash, true, true, inputTokens, EstimateTokens(cached.Body), 0, 0, "CACHE_HIT"), ct);
            return JsonResponse(cached.Body);
        }

        var today = await ReadTodayAsync(ct);
        var estimatedCost = EstimateCost(inputTokens, 0);
        if (today.Calls >= _policy.DailyCallLimit || today.Tokens + inputTokens > _policy.DailyTokenLimit || today.Cost + estimatedCost > _policy.DailyCostLimitUsd)
        {
            await AuditAsync(new(DateTime.UtcNow, provider, model, purpose, hash, false, false, inputTokens, 0, 0, 0, "DAILY_BUDGET_BLOCKED"), ct);
            throw new InvalidOperationException("Remote assistant daily budget reached; WPE remains in local deterministic mode.");
        }

        var started = Stopwatch.StartNew();
        using var response = await send(ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var outputTokens = EstimateTokens(body);
        var cost = EstimateCost(inputTokens, outputTokens);
        var outcome = $"HTTP_{(int)response.StatusCode}";
        await AuditAsync(new(DateTime.UtcNow, provider, model, purpose, hash, false, true, inputTokens, outputTokens, cost, started.ElapsedMilliseconds, outcome), ct);
        if (response.IsSuccessStatusCode) _cache[key] = new(body, DateTime.UtcNow.Add(_policy.EffectiveCacheTtl));
        return JsonResponse(body, response.StatusCode);
        }
        finally { _requestLock.Release(); }
    }

    private async Task<(int Calls, int Tokens, decimal Cost)> ReadTodayAsync(CancellationToken ct)
    {
        if (!File.Exists(_auditPath)) return (0, 0, 0);
        var day = DateTime.UtcNow.Date; var calls = 0; var tokens = 0; decimal cost = 0;
        foreach (var line in await File.ReadAllLinesAsync(_auditPath, ct))
        {
            try { var row = JsonSerializer.Deserialize<LlmCallAudit>(line); if (row is null || row.AtUtc.Date != day || row.CacheHit || !row.Allowed) continue; calls++; tokens += row.EstimatedInputTokens + row.EstimatedOutputTokens; cost += row.EstimatedCostUsd; } catch (JsonException) { }
        }
        return (calls, tokens, cost);
    }

    private async Task AuditAsync(LlmCallAudit row, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);
        await _auditLock.WaitAsync(ct);
        try { await File.AppendAllTextAsync(_auditPath, JsonSerializer.Serialize(row) + Environment.NewLine, ct); }
        finally { _auditLock.Release(); }
    }

    private static int EstimateTokens(string value) => Math.Max(1, (value?.Length ?? 0) / 4);
    private static decimal EstimateCost(int input, int output) => input / 1_000_000m * 1m + output / 1_000_000m * 4m;
    private static HttpResponseMessage JsonResponse(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
