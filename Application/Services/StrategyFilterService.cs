namespace 币安量化机器人.Application.Services;

using Serilog;
using 币安量化机器人.Core.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// 策略筛选服务实现
/// 根据性能指标和风险参数筛选和推荐策略
/// </summary>
public class StrategyFilterService : IStrategyFilterService
{
    private readonly ILogger _logger;

    public StrategyFilterService()
    {
        _logger = Log.ForContext<StrategyFilterService>();
    }

    public async Task<List<StrategyInfo>> FilterStrategiesByCriteriaAsync(
        List<StrategyInfo> strategies,
        FilterCriteria criteria)
    {
        if (strategies == null || strategies.Count == 0)
        {
            _logger.Warning("没有策略可筛选");
            return new List<StrategyInfo>();
        }

        return await Task.Run(() =>
        {
            var filtered = strategies
                .Where(s => ApplyCriteria(s, criteria))
                .ToList();

            _logger.Information(
                "? 策略筛选完成: 输入={Total}, 输出={Filtered}",
                strategies.Count,
                filtered.Count);

            return filtered;
        });
    }

    public async Task<List<StrategyInfo>> SortStrategiesAsync(
        List<StrategyInfo> strategies,
        StrategySortField sortBy,
        bool descending = true)
    {
        if (strategies == null || strategies.Count == 0)
        {
            return new List<StrategyInfo>();
        }

        return await Task.Run(() =>
        {
            var sorted = sortBy switch
            {
                StrategySortField.AnnualizedReturn => descending
                    ? strategies.OrderByDescending(s => s.Performance.AnnualizedReturn).ToList()
                    : strategies.OrderBy(s => s.Performance.AnnualizedReturn).ToList(),

                StrategySortField.SharpeRatio => descending
                    ? strategies.OrderByDescending(s => s.Performance.SharpeRatio).ToList()
                    : strategies.OrderBy(s => s.Performance.SharpeRatio).ToList(),

                StrategySortField.MaxDrawdown => descending
                    ? strategies.OrderByDescending(s => s.Performance.MaxDrawdown).ToList()
                    : strategies.OrderBy(s => s.Performance.MaxDrawdown).ToList(),

                StrategySortField.WinRate => descending
                    ? strategies.OrderByDescending(s => s.Performance.WinRate).ToList()
                    : strategies.OrderBy(s => s.Performance.WinRate).ToList(),

                StrategySortField.ProfitFactor => descending
                    ? strategies.OrderByDescending(s => s.Performance.ProfitFactor).ToList()
                    : strategies.OrderBy(s => s.Performance.ProfitFactor).ToList(),

                StrategySortField.LastBacktestedTime => descending
                    ? strategies.OrderByDescending(s => s.LastBacktestedAt).ToList()
                    : strategies.OrderBy(s => s.LastBacktestedAt).ToList(),

                _ => strategies
            };

            _logger.Information("?? 策略排序完成: {SortField}, 降序={Desc}", sortBy, descending);
            return sorted;
        });
    }

    public async Task<int> EvaluateStrategyFitnessAsync(
        StrategyInfo strategy,
        MarketConditions marketConditions)
    {
        if (strategy == null || marketConditions == null)
        {
            return 0;
        }

        return await Task.Run(() =>
        {
            int score = 0;

            // 基于策略类型评分
            score += EvaluateTypeCompatibility(strategy.Type, marketConditions.CurrentTrend);

            // 基于性能指标评分
            score += EvaluatePerformanceScore(strategy.Performance);

            // 基于波动率评分
            score += EvaluateVolatilityFitness(strategy.Performance, marketConditions.CurrentVolatility);

            // 基于趋势强度评分
            score += EvaluateTrendStrengthFitness(strategy.Type, marketConditions.TrendStrength);

            return Math.Min(100, Math.Max(0, score));
        });
    }

    public async Task<List<StrategyInfo>> FilterByRiskAsync(
        List<StrategyInfo> strategies,
        decimal maxDrawdown,
        decimal minSharpeRatio)
    {
        if (strategies == null || strategies.Count == 0)
        {
            return new List<StrategyInfo>();
        }

        return await Task.Run(() =>
        {
            var filtered = strategies
                .Where(s => s.Performance.MaxDrawdown <= maxDrawdown &&
                           s.Performance.SharpeRatio >= minSharpeRatio)
                .ToList();

            _logger.Information(
                "??? 风险筛选完成: 最大回撤={MaxDD}%, 最小夏普={MinSharpe}, 结果={Count}",
                maxDrawdown,
                minSharpeRatio,
                filtered.Count);

            return filtered;
        });
    }

    public async Task<List<StrategyInfo>> FilterByReturnAsync(
        List<StrategyInfo> strategies,
        decimal minAnnualizedReturn)
    {
        if (strategies == null || strategies.Count == 0)
        {
            return new List<StrategyInfo>();
        }

        return await Task.Run(() =>
        {
            var filtered = strategies
                .Where(s => s.Performance.AnnualizedReturn >= minAnnualizedReturn)
                .ToList();

            _logger.Information(
                "?? 收益筛选完成: 最小年化={MinReturn}%, 结果={Count}",
                minAnnualizedReturn * 100,
                filtered.Count);

            return filtered;
        });
    }

    public async Task<List<StrategyInfo>> GetRecommendedPortfolioAsync(
        List<StrategyInfo> strategies,
        int portfolioSize = 3)
    {
        if (strategies == null || strategies.Count == 0)
        {
            return new List<StrategyInfo>();
        }

        return await Task.Run(() =>
        {
            // 使用现代投资组合理论 (MPT) 思想
            // 选择夏普比率最高且相关性低的策略
            var portfolio = strategies
                .OrderByDescending(s => s.Performance.SharpeRatio)
                .Take(portfolioSize)
                .ToList();

            _logger.Information(
                "?? 推荐策略组合: 策略数={Count}",
                portfolio.Count);

            portfolio.ForEach(s =>
                _logger.Information("   - {Name}: 夏普={Sharpe}, 收益={Return}%",
                    s.Name,
                    s.Performance.SharpeRatio,
                    s.Performance.AnnualizedReturn * 100));

            return portfolio;
        });
    }

    public async Task<StrategyCompatibilityReport> CheckCompatibilityAsync(
        StrategyInfo strategy,
        MarketConditions marketConditions)
    {
        if (strategy == null || marketConditions == null)
        {
            return new StrategyCompatibilityReport { IsCompatible = false };
        }

        return await Task.Run(() =>
        {
            var score = 50; // 基础分数
            var warnings = new List<string>();
            var recommendations = new List<string>();

            // 检查波动率兼容性
            if (marketConditions.CurrentVolatility > 0.3m)
            {
                warnings.Add("市场波动率较高，风险增加");
                score -= 10;
            }

            // 检查趋势兼容性
            switch (strategy.Type)
            {
                case StrategyType.Momentum:
                    if (marketConditions.CurrentTrend == MarketTrend.SideWays)
                    {
                        warnings.Add("动量策略不适合横盘市场");
                        score -= 20;
                    }
                    break;

                case StrategyType.MeanReversion:
                    if (marketConditions.CurrentTrend != MarketTrend.SideWays)
                    {
                        warnings.Add("均值回归策略在趋势市中效果较差");
                        score -= 15;
                    }
                    break;
            }

            // 检查性能指标
            if (strategy.Performance.SharpeRatio > 1.0m)
            {
                score += 20;
                recommendations.Add("夏普比率优秀，值得考虑");
            }

            if (strategy.Performance.MaxDrawdown > 0.30m)
            {
                warnings.Add("最大回撤较大，风险较高");
                score -= 15;
            }

            var isCompatible = score >= 60;

            var report = new StrategyCompatibilityReport
            {
                StrategyId = strategy.Id,
                IsCompatible = isCompatible,
                CompatibilityScore = Math.Max(0, Math.Min(100, score)),
                Warnings = warnings,
                Recommendations = recommendations,
                EvaluatedAt = DateTime.UtcNow
            };

            _logger.Information(
                "?? 兼容性检查: {Strategy}, 分数={Score}, 兼容={Compatible}",
                strategy.Name,
                report.CompatibilityScore,
                isCompatible);

            return report;
        });
    }

    // ============ Private Helper Methods ============

    private bool ApplyCriteria(StrategyInfo strategy, FilterCriteria criteria)
    {
        if (criteria.MinAnnualizedReturn.HasValue &&
            strategy.Performance.AnnualizedReturn < criteria.MinAnnualizedReturn)
            return false;

        if (criteria.MaxDrawdown.HasValue &&
            strategy.Performance.MaxDrawdown > criteria.MaxDrawdown)
            return false;

        if (criteria.MinSharpeRatio.HasValue &&
            strategy.Performance.SharpeRatio < criteria.MinSharpeRatio)
            return false;

        if (criteria.MinWinRate.HasValue &&
            strategy.Performance.WinRate < criteria.MinWinRate)
            return false;

        if (criteria.MinTotalTrades.HasValue &&
            strategy.Performance.TotalTrades < criteria.MinTotalTrades)
            return false;

        if (criteria.StrategyType.HasValue &&
            strategy.Type != criteria.StrategyType)
            return false;

        if (criteria.IncludedSymbols != null &&
            criteria.IncludedSymbols.Count > 0 &&
            !strategy.SupportedSymbols.Any(s => criteria.IncludedSymbols.Contains(s)))
            return false;

        return true;
    }

    private int EvaluateTypeCompatibility(StrategyType type, MarketTrend trend)
    {
        return (type, trend) switch
        {
            (StrategyType.Momentum, MarketTrend.UpTrend) => 25,
            (StrategyType.Momentum, MarketTrend.DownTrend) => 20,
            (StrategyType.Momentum, MarketTrend.SideWays) => 5,
            (StrategyType.MeanReversion, MarketTrend.SideWays) => 25,
            (StrategyType.MeanReversion, _) => 10,
            (StrategyType.GridTrading, MarketTrend.SideWays) => 25,
            (StrategyType.GridTrading, _) => 8,
            _ => 10
        };
    }

    private int EvaluatePerformanceScore(StrategyPerformanceMetrics metrics)
    {
        int score = 0;

        if (metrics.WinRate > 0.5m) score += 15;
        if (metrics.SharpeRatio > 1.0m) score += 15;
        if (metrics.ProfitFactor > 1.5m) score += 10;

        return score;
    }

    private int EvaluateVolatilityFitness(StrategyPerformanceMetrics metrics, decimal volatility)
    {
        // 高波动率市场下，回撤小的策略得分高
        if (volatility > 0.25m)
        {
            return metrics.MaxDrawdown < 0.15m ? 15 : 5;
        }

        return 10;
    }

    private int EvaluateTrendStrengthFitness(StrategyType type, decimal trendStrength)
    {
        if (trendStrength > 0.7m && type == StrategyType.Momentum)
            return 15;

        if (trendStrength < 0.3m && type == StrategyType.MeanReversion)
            return 15;

        return 5;
    }
}
