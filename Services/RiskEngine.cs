using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public class RiskEngine
{
    private readonly DataCacheService _cacheService;

    public RiskEngine(DataCacheService cacheService)
    {
        _cacheService = cacheService;
    }

    public async Task<RiskReport> GenerateReportAsync(IEnumerable<PositionSnapshot> positions, IEnumerable<RiskRule> rules, string benchmarkSymbol, decimal accountEquity)
    {
        var positionList = positions.ToList();
        if (!positionList.Any())
            return new RiskReport
            {
                Metrics = Array.Empty<RiskMetrics>(),
                BreachedRules = Array.Empty<RiskRule>(),
                StressTests = Array.Empty<StressTestResult>()
            };

        var returns = await _cacheService.LoadReturnsAsync(benchmarkSymbol, 500).ConfigureAwait(false);
        var riskMetrics = positionList.Select(p => CalculateMetrics(p, returns, accountEquity)).ToArray();

        var breached = rules.Where(r => r.IsActive && riskMetrics.Any(m => EvaluateRule(m, r))).ToArray();
        var stress = BuildStressTests(positionList, returns).ToArray();

        return new RiskReport
        {
            Metrics = riskMetrics,
            BreachedRules = breached,
            StressTests = stress
        };
    }

    private static RiskMetrics CalculateMetrics(PositionSnapshot position, IReadOnlyList<double> returns, decimal accountEquity)
    {
        var pnlSeries = returns.Select(r => (double)position.PositionAmt * (double)position.MarkPrice * r).ToArray();
        var var99 = HistoricalVaR(pnlSeries, 0.99);
        var cvar = ConditionalVaR(pnlSeries, 0.99);
        var volatility = returns.Any() ? Math.Sqrt(returns.Average(r => r * r) * 252) : 0;
        var drawdown = MaxDrawdown(pnlSeries);
        var exposure = Math.Abs(position.PositionAmt * position.MarkPrice);
        var leverage = accountEquity == 0 ? 0 : (decimal)exposure / accountEquity;
        var liquidation = position.EntryPrice == 0 ? 0 : (double)position.EntryPrice * (1 - Math.Sign(position.PositionAmt) * 1.0 / (double)position.Leverage);
        var kelly = returns.Any() ? KellyFraction(returns) : 0;

        return new RiskMetrics
        {
            Symbol = position.Symbol,
            ValueAtRisk = (decimal)var99,
            ConditionalVaR = (decimal)cvar,
            Volatility = (decimal)volatility,
            MaxDrawdown = (decimal)drawdown,
            Exposure = (decimal)exposure,
            Leverage = leverage,
            KellyFraction = (decimal)kelly,
            LiquidationPrice = (decimal)liquidation,
            CalculatedAt = DateTime.UtcNow
        };
    }

    private static bool EvaluateRule(RiskMetrics metrics, RiskRule rule)
    {
        return rule.Comparator switch
        {
            ">" => metrics.ValueAtRisk > rule.Threshold,
            ">=" => metrics.ValueAtRisk >= rule.Threshold,
            "<" => metrics.ValueAtRisk < rule.Threshold,
            "<=" => metrics.ValueAtRisk <= rule.Threshold,
            "LEV>" => metrics.Leverage > rule.Threshold,
            "DD>" => metrics.MaxDrawdown > rule.Threshold,
            _ => false
        };
    }

    private static IEnumerable<StressTestResult> BuildStressTests(IEnumerable<PositionSnapshot> positions, IReadOnlyList<double> returns)
    {
        var scenarios = new Dictionary<string, double>
        {
            ["Flash Crash -15%"] = -0.15,
            ["Volatility Spike +8σ"] = returns.Any() ? returns.Average(r => Math.Abs(r)) * 8 : -0.08,
            ["Mean Reversion +5%"] = 0.05
        };

        foreach (var scenario in scenarios)
        {
            var pnl = positions.Sum(p => (double)p.PositionAmt * (double)p.MarkPrice * scenario.Value);
            var margin = positions.Sum(p => (double)p.MaintenanceMargin);
            yield return new StressTestResult
            {
                Scenario = scenario.Key,
                PortfolioPnl = (decimal)pnl,
                MarginUsage = (decimal)margin,
                BreachProbability = returns.Any() ? (decimal)returns.Count(r => r < scenario.Value) / returns.Count : 0
            };
        }
    }

    private static double HistoricalVaR(IReadOnlyList<double> pnl, double confidence)
    {
        if (pnl.Count == 0)
            return 0;

        var sorted = pnl.OrderBy(x => x).ToArray();
        var index = (int)Math.Floor((1 - confidence) * sorted.Length);
        index = Math.Max(0, Math.Min(sorted.Length - 1, index));
        return -sorted[index];
    }

    private static double ConditionalVaR(IReadOnlyList<double> pnl, double confidence)
    {
        if (pnl.Count == 0)
            return 0;

        var threshold = HistoricalVaR(pnl, confidence);
        var tail = pnl.Where(x => -x >= threshold).ToArray();
        return tail.Length == 0 ? threshold : tail.Average(x => -x);
    }

    private static double MaxDrawdown(IReadOnlyList<double> pnl)
    {
        double peak = 0;
        double trough = 0;
        double maxDrawdown = 0;

        foreach (var value in pnl)
        {
            peak = Math.Max(peak + value, 0);
            trough = Math.Min(trough + value, peak);
            maxDrawdown = Math.Max(maxDrawdown, peak - trough);
        }

        return maxDrawdown;
    }

    private static double KellyFraction(IReadOnlyList<double> returns)
    {
        if (returns.Count == 0)
            return 0;

        var positive = returns.Where(r => r > 0).ToArray();
        if (positive.Length == 0)
            return 0;

        var winProb = (double)positive.Length / returns.Count;
        var avgWin = positive.Average();
        var avgLoss = returns.Where(r => r <= 0).Select(Math.Abs).DefaultIfEmpty().Average();
        if (avgLoss == 0)
            return 0;

        var odds = avgWin / avgLoss;
        return winProb - (1 - winProb) / odds;
    }
}
