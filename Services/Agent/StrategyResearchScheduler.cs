using 币安量化机器人.Core.Models;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

/// <summary>
/// Runs deterministic strategy research independently from the order cycle.
/// It only persists approved lifecycle state; it never submits orders.
/// </summary>
public sealed class StrategyResearchScheduler
{
    private readonly StrategyResearchAgent _research;
    private readonly AgentSqliteStore _database;

    public StrategyResearchScheduler(StrategyResearchAgent research, AgentSqliteStore database)
    {
        _research = research;
        _database = database;
    }

    public Task StartAsync(IReadOnlyList<string> symbols, RiskLimits limits, CancellationToken ct)
        => Task.Run(() => RunAsync(symbols, limits, ct), CancellationToken.None);

    private async Task RunAsync(IReadOnlyList<string> symbols, RiskLimits limits, CancellationToken ct)
    {
        var delay = await ResumeDelayAsync(ct);
        await PublishHealthAsync("STARTING", null, delay, ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await PublishHealthAsync("WAITING", null, delay, ct);
                    var heartbeat = TimeSpan.FromMinutes(1);
                    while (delay > TimeSpan.Zero && !ct.IsCancellationRequested)
                    {
                        var tick = delay < heartbeat ? delay : heartbeat;
                        await Task.Delay(tick, ct);
                        delay -= tick;
                        if (delay > TimeSpan.Zero) await PublishHealthAsync("WAITING", null, delay, ct);
                    }
                }
                await PublishHealthAsync("RUNNING", null, TimeSpan.Zero, ct);
                var snapshot = await _research.RunOnceAsync(symbols, limits, ct);
                await _database.SetStateAsync("strategy-research:scheduler", JsonSerializer.Serialize(new
                {
                    status = "RUNNING",
                    snapshot.LastRunAtUtc,
                    nextRunAtUtc = DateTime.UtcNow.AddHours(1),
                    snapshot.Candidates,
                    snapshot.ActiveCandidates
                }), ct);
                delay = TimeSpan.FromHours(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                await _database.RecordErrorAsync("STRATEGY_RESEARCH_SCHEDULER", ex, CancellationToken.None);
                await PublishHealthAsync("ERROR", ex.Message, delay, CancellationToken.None);
                delay = delay == TimeSpan.Zero ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(Math.Min(delay.TotalMinutes * 2, 60));
            }
        }
    }

    private Task PublishHealthAsync(string status, string? error, TimeSpan nextDelay, CancellationToken ct)
    {
        var healthy = status is "WAITING" or "RUNNING";
        _research.SetSchedulerHealth(healthy);
        return _database.SetStateAsync("strategy-research:health", JsonSerializer.Serialize(new
        {
            status,
            heartbeatAtUtc = DateTime.UtcNow,
            nextRunAtUtc = DateTime.UtcNow.Add(nextDelay),
            error
        }), ct);
    }

    private async Task<TimeSpan> ResumeDelayAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _database.GetStateAsync("strategy-research:scheduler", ct);
            if (string.IsNullOrWhiteSpace(raw)) return TimeSpan.Zero;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("nextRunAtUtc", out var next) || !next.TryGetDateTime(out var nextRun)) return TimeSpan.Zero;
            return nextRun > DateTime.UtcNow ? nextRun - DateTime.UtcNow : TimeSpan.Zero;
        }
        catch { return TimeSpan.Zero; }
    }
}
