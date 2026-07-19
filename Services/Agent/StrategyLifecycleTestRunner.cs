using System.Text.Json;
using System.IO;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed record StrategyLifecycleTestResult(bool Success, string ReportPath, IReadOnlyList<string> Cases);

public static class StrategyLifecycleTestRunner
{
    public static async Task<StrategyLifecycleTestResult> RunAsync()
    {
        var cases = new List<string>(); var passed = true;
        void Check(string name, bool condition, string detail) { if (!condition) passed = false; cases.Add($"{(condition ? "PASS" : "FAIL")} {name}: {detail}"); }
        var governor = new StrategyGovernor();
        var profile = new StrategyProfile { Id = "TEST", Symbol = "BTCUSDT", Family = StrategyFamily.TrendBreakout, Parameters = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0), Lifecycle = StrategyLifecycle.Draft };
        var good = new StrategyValidation("TEST", 1000, 80, .58, 1.5, .002, .12, 1.2, .08, .7, .2, .78, true, "good");
        Check("严格验证晋级到影子", governor.NextLifecycle(profile, good) == StrategyLifecycle.Shadow, "backtest passed -> shadow");
        profile.Lifecycle = StrategyLifecycle.Shadow; profile.QualityScore = .78; profile.Expectancy = .002; profile.MaxDrawdown = .12; profile.ShadowObservations = StrategyGovernor.MinimumShadowObservations; profile.FailureStreak = 0;
        Check("影子运行达到门槛才上架", governor.NextLifecycle(profile) == StrategyLifecycle.Active, "shadow gate -> active");
        profile.Lifecycle = StrategyLifecycle.Active; profile.FailureStreak = 3;
        Check("连续失效自动降级", governor.NextLifecycle(profile) == StrategyLifecycle.Degraded, "active -> degraded");
        var candles = Enumerable.Range(0, 700).Select(i => { var close = 100m + i * .08m + (decimal)Math.Sin(i / 12d); return new CandleEvidence(DateTime.UtcNow.AddHours(-700 + i), close - 1, close + 1, close - 1.5m, close, 100, 10000, 10, 50); }).ToArray();
        var engine = new HistoricalResearchEngine();
        var result = engine.Validate(new StrategyProfile { Id = "TEST-ENGINE", Symbol = "BTCUSDT", Family = StrategyFamily.TrendBreakout, Parameters = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0) }, candles, Array.Empty<NewsFeature>(), new RiskLimits { MinimumBacktestTrades = 10 });
        Check("本地回测输出可审计指标", result.SampleSize == 700 && double.IsFinite(result.QualityScore) && double.IsFinite(result.WalkForwardScore) && result.MonteCarloLossProbability is >= 0 and <= 1, result.Summary);
        var statePath = Path.Combine(Path.GetTempPath(), $"wpe-strategy-state-{Guid.NewGuid():N}.db");
        var stateValue = "{\"status\":\"WAITING\",\"nextRunAtUtc\":\"" + DateTime.UtcNow.AddHours(1).ToString("O") + "\"}";
        var stateStore = new AgentSqliteStore(statePath);
        await stateStore.SetStateAsync("strategy-research:scheduler", stateValue, CancellationToken.None);
        var restoredStore = new AgentSqliteStore(statePath);
        Check("调度状态可跨重启恢复", await restoredStore.GetStateAsync("strategy-research:scheduler", CancellationToken.None) == stateValue, "sqlite state round-trip");
        var healthy = JsonSerializer.Serialize(new { status = "WAITING", heartbeatAtUtc = DateTime.UtcNow });
        Check("研究心跳新鲜时可继续使用策略", StrategyResearchHealth.IsHealthy(healthy, DateTime.UtcNow, TimeSpan.FromMinutes(2)), "fresh heartbeat");
        var stale = JsonSerializer.Serialize(new { status = "WAITING", heartbeatAtUtc = DateTime.UtcNow.AddMinutes(-5) });
        Check("研究心跳过期时拒绝旧状态", !StrategyResearchHealth.IsHealthy(stale, DateTime.UtcNow, TimeSpan.FromMinutes(2)), "stale heartbeat rejected");
        var gatedResearch = new StrategyResearchAgent(restoredStore);
        var gatedProfile = new StrategyProfile { Id = "GATE", Symbol = "BTCUSDT", Family = StrategyFamily.TrendBreakout, Parameters = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0) };
        var gatedMarket = new MarketEvidence("BTCUSDT", 100, 0, 0, 50, 0, 0, 0, new(0, 0, 1, 1, 1, 1, 0), DateTime.UtcNow);
        Check("研究心跳失效时策略信号强制中性", gatedResearch.GetSignal(gatedProfile, gatedMarket, Array.Empty<NewsEvidence>()).Direction == 0, "stale signal gated to hold");
        var report = new StrategyLifecycleTestResult(passed, AppDataPaths.File("strategy-lifecycle-test-report.json"), cases);
        await File.WriteAllTextAsync(report.ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }
}
