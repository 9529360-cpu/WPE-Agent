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
        var delay = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                var snapshot = await _research.RunOnceAsync(symbols, limits, ct);
                await _database.SetStateAsync("strategy-research:scheduler", JsonSerializer.Serialize(new
                {
                    status = "RUNNING",
                    snapshot.LastRunAtUtc,
                    snapshot.Candidates,
                    snapshot.ActiveCandidates
                }), ct);
                delay = TimeSpan.FromHours(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                await _database.RecordErrorAsync("STRATEGY_RESEARCH_SCHEDULER", ex, CancellationToken.None);
                delay = delay == TimeSpan.Zero ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(Math.Min(delay.TotalMinutes * 2, 60));
            }
        }
    }
}
