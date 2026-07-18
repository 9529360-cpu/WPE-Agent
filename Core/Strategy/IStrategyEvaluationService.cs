namespace 币安量化机器人.Core.Strategy;

/// <summary>
/// 策略评估服务接口
/// 负责对策略的性能指标进行评估
/// </summary>
public interface IStrategyEvaluationService
{
    /// <summary>
    /// 评估策略在历史数据上的性能
    /// </summary>
    /// <param name="trades">历史交易记录</param>
    /// <param name="initialBalance">初始余额</param>
    /// <returns>性能评估结果</returns>
    Task<StrategyPerformanceMetrics> EvaluatePerformanceAsync(
        List<TradeRecord> trades,
        decimal initialBalance);

    /// <summary>
    /// 计算夏普比率 (Sharpe Ratio)
    /// </summary>
    /// <param name="returns">收益率序列</param>
    /// <param name="riskFreeRate">无风险利率，默认 2% 年化</param>
    Task<decimal> CalculateSharpeRatioAsync(List<decimal> returns, decimal riskFreeRate = 0.02m);

    /// <summary>
    /// 计算最大回撤 (Maximum Drawdown)
    /// </summary>
    /// <param name="equityCurve">权益曲线</param>
    Task<decimal> CalculateMaxDrawdownAsync(List<decimal> equityCurve);

    /// <summary>
    /// 计算胜率 (Win Rate)
    /// </summary>
    /// <param name="trades">交易记录</param>
    Task<decimal> CalculateWinRateAsync(List<TradeRecord> trades);

    /// <summary>
    /// 计算利润因子 (Profit Factor)
    /// 总利润 / 总亏损的绝对值
    /// </summary>
    /// <param name="trades">交易记录</param>
    Task<decimal> CalculateProfitFactorAsync(List<TradeRecord> trades);

    /// <summary>
    /// 计算回报率 (Return on Investment)
    /// </summary>
    /// <param name="totalProfit">总利润</param>
    /// <param name="initialBalance">初始余额</param>
    Task<decimal> CalculateROIAsync(decimal totalProfit, decimal initialBalance);

    /// <summary>
    /// 计算信息比率 (Information Ratio)
    /// </summary>
    /// <param name="strategyReturns">策略收益率</param>
    /// <param name="benchmarkReturns">基准收益率</param>
    Task<decimal> CalculateInformationRatioAsync(List<decimal> strategyReturns, List<decimal> benchmarkReturns);

    /// <summary>
    /// 获取策略评估报告
    /// </summary>
    /// <param name="strategyId">策略 ID</param>
    /// <param name="evaluationPeriod">评估时间段</param>
    Task<StrategyEvaluationReport> GetEvaluationReportAsync(string strategyId, DateRange evaluationPeriod);
}

/// <summary>
/// 交易记录
/// </summary>
public record TradeRecord
{
    public string Id { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public DateTime EntryTime { get; init; }
    public DateTime ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal Commission { get; init; }
    public TradeDirection Direction { get; init; }
    
    public decimal Profit => Direction switch
    {
        TradeDirection.Long => (ExitPrice - EntryPrice) * Quantity - Commission,
        TradeDirection.Short => (EntryPrice - ExitPrice) * Quantity - Commission,
        _ => 0
    };

    public decimal ReturnPercent => EntryPrice > 0
        ? (Profit / (EntryPrice * Quantity)) * 100
        : 0;
}

/// <summary>
/// 交易方向
/// </summary>
public enum TradeDirection
{
    Long = 1,
    Short = 2
}

/// <summary>
/// 策略性能指标
/// </summary>
public record StrategyPerformanceMetrics
{
    public decimal TotalProfit { get; init; }
    public decimal TotalReturn { get; init; }
    public decimal AnnualizedReturn { get; init; }
    public decimal WinRate { get; init; }
    public decimal LossRate { get; init; }
    public decimal SharpeRatio { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal ProfitFactor { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public decimal ExpectancyPerTrade { get; init; }
    public decimal InformationRatio { get; init; }
}

/// <summary>
/// 策略评估报告
/// </summary>
public record StrategyEvaluationReport
{
    public string StrategyId { get; init; } = string.Empty;
    public DateRange EvaluationPeriod { get; init; } = new();
    public StrategyPerformanceMetrics Metrics { get; init; } = new();
    public List<decimal> EquityCurve { get; init; } = [];
    public List<decimal> DailyReturns { get; init; } = [];
    public DateTime GeneratedAt { get; init; }
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// 日期范围
/// </summary>
public record DateRange
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public TimeSpan Duration => EndDate - StartDate;
}
