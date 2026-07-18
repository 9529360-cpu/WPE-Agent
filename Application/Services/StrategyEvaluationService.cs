namespace 币安量化机器人.Application.Services;

using Serilog;
using 币安量化机器人.Core.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// 策略评估服务实现
/// 计算交易策略的各种性能指标
/// </summary>
public class StrategyEvaluationService : IStrategyEvaluationService
{
    private readonly ILogger _logger;
    private const decimal RiskFreeRateDefault = 0.02m; // 2% 年化无风险利率

    public StrategyEvaluationService()
    {
        _logger = Log.ForContext<StrategyEvaluationService>();
    }

    public async Task<StrategyPerformanceMetrics> EvaluatePerformanceAsync(
        List<TradeRecord> trades,
        decimal initialBalance)
    {
        if (trades == null || trades.Count == 0)
        {
            _logger.Warning("没有交易记录，无法评估性能");
            return new StrategyPerformanceMetrics();
        }

        return await Task.Run(() =>
        {
            var winningTrades = trades.Where(t => t.Profit > 0).ToList();
            var losingTrades = trades.Where(t => t.Profit < 0).ToList();
            var totalProfit = trades.Sum(t => t.Profit);

            // 构建权益曲线
            var equityCurve = BuildEquityCurve(trades, initialBalance);
            var maxDrawdown = CalculateDrawdown(equityCurve);
            var dailyReturns = CalculateDailyReturns(trades);

            var metrics = new StrategyPerformanceMetrics
            {
                TotalProfit = totalProfit,
                TotalReturn = equityCurve.Last(),
                AnnualizedReturn = CalculateAnnualizedReturn(equityCurve, trades),
                WinRate = trades.Count > 0 ? (decimal)winningTrades.Count / trades.Count * 100 : 0,
                LossRate = trades.Count > 0 ? (decimal)losingTrades.Count / trades.Count * 100 : 0,
                SharpeRatio = CalculateSharpeRatioInternal(dailyReturns),
                MaxDrawdown = maxDrawdown,
                ProfitFactor = CalculateProfitFactorInternal(trades),
                TotalTrades = trades.Count,
                WinningTrades = winningTrades.Count,
                LosingTrades = losingTrades.Count,
                AverageWin = winningTrades.Count > 0 ? winningTrades.Sum(t => t.Profit) / winningTrades.Count : 0,
                AverageLoss = losingTrades.Count > 0 ? losingTrades.Sum(t => t.Profit) / losingTrades.Count : 0,
                ExpectancyPerTrade = trades.Count > 0 ? totalProfit / trades.Count : 0,
                InformationRatio = 0 // 需要基准数据计算
            };

            _logger.Information(
                "?? 策略评估完成: 总交易={Trades}, 胜率={WinRate}%, 夏普={Sharpe}, 最大回撤={Drawdown}%",
                metrics.TotalTrades,
                metrics.WinRate,
                metrics.SharpeRatio,
                metrics.MaxDrawdown);

            return metrics;
        });
    }

    public async Task<decimal> CalculateSharpeRatioAsync(List<decimal> returns, decimal riskFreeRate = 0.02m)
    {
        if (returns == null || returns.Count < 2)
        {
            _logger.Warning("返回数据不足，无法计算夏普比率");
            return 0;
        }

        return await Task.Run(() =>
        {
            var avgReturn = returns.Average();
            var stdDev = CalculateStandardDeviation(returns);

            if (stdDev == 0)
            {
                return avgReturn > 0 ? decimal.MaxValue : decimal.MinValue;
            }

            var sharpeRatio = (avgReturn - (riskFreeRate / 252)) / stdDev; // 252 是交易日数
            return Math.Round(sharpeRatio, 4);
        });
    }

    public async Task<decimal> CalculateMaxDrawdownAsync(List<decimal> equityCurve)
    {
        if (equityCurve == null || equityCurve.Count < 2)
        {
            _logger.Warning("权益曲线数据不足");
            return 0;
        }

        return await Task.Run(() => CalculateDrawdown(equityCurve));
    }

    public async Task<decimal> CalculateWinRateAsync(List<TradeRecord> trades)
    {
        if (trades == null || trades.Count == 0)
        {
            return 0;
        }

        return await Task.Run(() =>
        {
            var winningTrades = trades.Count(t => t.Profit > 0);
            return trades.Count > 0 ? (decimal)winningTrades / trades.Count * 100 : 0;
        });
    }

    public async Task<decimal> CalculateProfitFactorAsync(List<TradeRecord> trades)
    {
        if (trades == null || trades.Count == 0)
        {
            return 0;
        }

        return await Task.Run(() => CalculateProfitFactorInternal(trades));
    }

    public async Task<decimal> CalculateROIAsync(decimal totalProfit, decimal initialBalance)
    {
        if (initialBalance <= 0)
        {
            return 0;
        }

        return await Task.Run(() => (totalProfit / initialBalance) * 100);
    }

    public async Task<decimal> CalculateInformationRatioAsync(
        List<decimal> strategyReturns,
        List<decimal> benchmarkReturns)
    {
        if (strategyReturns == null || benchmarkReturns == null || 
            strategyReturns.Count < 2 || benchmarkReturns.Count < 2)
        {
            _logger.Warning("返回数据不足");
            return 0;
        }

        return await Task.Run(() =>
        {
            var activeReturns = strategyReturns
                .Zip(benchmarkReturns, (s, b) => s - b)
                .ToList();

            var avgActiveReturn = activeReturns.Average();
            var trackingError = CalculateStandardDeviation(activeReturns);

            return trackingError > 0 ? avgActiveReturn / trackingError : 0;
        });
    }

    public async Task<StrategyEvaluationReport> GetEvaluationReportAsync(
        string strategyId,
        DateRange evaluationPeriod)
    {
        return await Task.Run(() =>
        {
            var report = new StrategyEvaluationReport
            {
                StrategyId = strategyId,
                EvaluationPeriod = evaluationPeriod,
                GeneratedAt = DateTime.UtcNow,
                Summary = "评估报告已生成"
            };

            _logger.Information("?? 生成策略评估报告: {StrategyId}", strategyId);
            return report;
        });
    }

    // ============ Private Helper Methods ============

    private List<decimal> BuildEquityCurve(List<TradeRecord> trades, decimal initialBalance)
    {
        var curve = new List<decimal> { initialBalance };
        var currentBalance = initialBalance;

        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            currentBalance += trade.Profit;
            curve.Add(currentBalance);
        }

        return curve;
    }

    private decimal CalculateDrawdown(List<decimal> equityCurve)
    {
        if (equityCurve.Count < 2)
            return 0;

        decimal maxDrawdown = 0;
        decimal peak = equityCurve[0];

        foreach (var value in equityCurve)
        {
            if (value > peak)
                peak = value;

            var drawdown = (peak - value) / peak * 100;
            if (drawdown > maxDrawdown)
                maxDrawdown = drawdown;
        }

        return Math.Round(maxDrawdown, 2);
    }

    private List<decimal> CalculateDailyReturns(List<TradeRecord> trades)
    {
        var dailyReturns = new Dictionary<DateTime, decimal>();

        foreach (var trade in trades)
        {
            var date = trade.ExitTime.Date;
            if (!dailyReturns.ContainsKey(date))
                dailyReturns[date] = 0;

            dailyReturns[date] += trade.Profit;
        }

        return dailyReturns.Values.ToList();
    }

    private decimal CalculateAnnualizedReturn(List<decimal> equityCurve, List<TradeRecord> trades)
    {
        if (equityCurve.Count < 2 || trades.Count == 0)
            return 0;

        var totalReturn = (equityCurve.Last() - equityCurve.First()) / equityCurve.First();
        var tradingDays = (trades.Last().ExitTime - trades.First().EntryTime).TotalDays;
        var years = tradingDays / 365;

        if (years <= 0)
            return 0;

        return (decimal)Math.Pow((double)(1 + totalReturn), 1 / years) - 1;
    }

    private decimal CalculateSharpeRatioInternal(List<decimal> returns)
    {
        if (returns.Count < 2)
            return 0;

        var avgReturn = returns.Average();
        var stdDev = CalculateStandardDeviation(returns);

        if (stdDev == 0)
            return avgReturn > 0 ? 100 : -100;

        return (avgReturn - (RiskFreeRateDefault / 252)) / stdDev;
    }

    private decimal CalculateProfitFactorInternal(List<TradeRecord> trades)
    {
        var wins = trades.Where(t => t.Profit > 0).Sum(t => t.Profit);
        var losses = Math.Abs(trades.Where(t => t.Profit < 0).Sum(t => t.Profit));

        return losses > 0 ? wins / losses : (wins > 0 ? decimal.MaxValue : 1);
    }

    private decimal CalculateStandardDeviation(List<decimal> values)
    {
        if (values.Count < 2)
            return 0;

        var avg = values.Average();
        var variance = values.Sum(v => (v - avg) * (v - avg)) / values.Count;
        return (decimal)Math.Sqrt((double)variance);
    }
}
