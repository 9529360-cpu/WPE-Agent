namespace 币安量化机器人.Core.Strategy;

/// <summary>
/// 策略筛选服务接口
/// 负责根据性能指标和风险参数筛选合适的策略
/// </summary>
public interface IStrategyFilterService
{
    /// <summary>
    /// 根据性能阈值筛选策略
    /// </summary>
    /// <param name="strategies">待筛选策略列表</param>
    /// <param name="criteria">筛选条件</param>
    /// <returns>通过筛选的策略列表</returns>
    Task<List<StrategyInfo>> FilterStrategiesByCriteriaAsync(
        List<StrategyInfo> strategies,
        FilterCriteria criteria);

    /// <summary>
    /// 排序策略
    /// </summary>
    /// <param name="strategies">策略列表</param>
    /// <param name="sortBy">排序字段</param>
    /// <param name="descending">是否降序</param>
    Task<List<StrategyInfo>> SortStrategiesAsync(
        List<StrategyInfo> strategies,
        StrategySortField sortBy,
        bool descending = true);

    /// <summary>
    /// 评估策略的适用性
    /// </summary>
    /// <param name="strategy">策略信息</param>
    /// <param name="marketConditions">市场条件</param>
    /// <returns>适用性评分 (0-100)</returns>
    Task<int> EvaluateStrategyFitnessAsync(
        StrategyInfo strategy,
        MarketConditions marketConditions);

    /// <summary>
    /// 过滤高风险策略
    /// </summary>
    /// <param name="strategies">策略列表</param>
    /// <param name="maxDrawdown">最大允许回撤</param>
    /// <param name="minSharpeRatio">最小夏普比率</param>
    Task<List<StrategyInfo>> FilterByRiskAsync(
        List<StrategyInfo> strategies,
        decimal maxDrawdown,
        decimal minSharpeRatio);

    /// <summary>
    /// 过滤低收益策略
    /// </summary>
    /// <param name="strategies">策略列表</param>
    /// <param name="minAnnualizedReturn">最小年化收益率</param>
    Task<List<StrategyInfo>> FilterByReturnAsync(
        List<StrategyInfo> strategies,
        decimal minAnnualizedReturn);

    /// <summary>
    /// 获取推荐的策略组合
    /// </summary>
    /// <param name="strategies">可用策略列表</param>
    /// <param name="portfolioSize">组合中策略数量</param>
    Task<List<StrategyInfo>> GetRecommendedPortfolioAsync(
        List<StrategyInfo> strategies,
        int portfolioSize = 3);

    /// <summary>
    /// 检查策略在给定市场条件下的兼容性
    /// </summary>
    /// <param name="strategy">策略</param>
    /// <param name="marketConditions">市场条件</param>
    Task<StrategyCompatibilityReport> CheckCompatibilityAsync(
        StrategyInfo strategy,
        MarketConditions marketConditions);
}

/// <summary>
/// 策略信息
/// </summary>
public record StrategyInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public StrategyType Type { get; init; }
    public StrategyPerformanceMetrics Performance { get; init; } = new();
    public StrategyParameters Parameters { get; init; } = new();
    public DateTime LastBacktestedAt { get; init; }
    public bool IsActive { get; init; }
    public List<string> SupportedSymbols { get; init; } = [];
}

/// <summary>
/// 策略类型
/// </summary>
public enum StrategyType
{
    MeanReversion = 1,
    Momentum = 2,
    ArbitrageAcrossExchanges = 3,
    GridTrading = 4,
    DCADollarCostAveraging = 5,
    MachinelearningBased = 6,
    Custom = 7
}

/// <summary>
/// 策略参数
/// </summary>
public record StrategyParameters
{
    public Dictionary<string, object> Parameters { get; init; } = [];

    public T? GetParameter<T>(string key) where T : class
    {
        return Parameters.TryGetValue(key, out var value) ? value as T : null;
    }

    public T GetParameter<T>(string key, T defaultValue) where T : struct
    {
        if (Parameters.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }
}

/// <summary>
/// 筛选条件
/// </summary>
public record FilterCriteria
{
    public decimal? MinAnnualizedReturn { get; init; }
    public decimal? MaxDrawdown { get; init; }
    public decimal? MinSharpeRatio { get; init; }
    public decimal? MinWinRate { get; init; }
    public int? MinTotalTrades { get; init; }
    public StrategyType? StrategyType { get; init; }
    public List<string>? IncludedSymbols { get; init; }
}

/// <summary>
/// 策略排序字段
/// </summary>
public enum StrategySortField
{
    AnnualizedReturn,
    SharpeRatio,
    MaxDrawdown,
    WinRate,
    ProfitFactor,
    LastBacktestedTime
}

/// <summary>
/// 市场条件
/// </summary>
public record MarketConditions
{
    public decimal CurrentVolatility { get; init; }
    public decimal TrendStrength { get; init; }
    public MarketTrend CurrentTrend { get; init; }
    public decimal AverageVolume24h { get; init; }
    public DateTime MeasuredAt { get; init; }
}

/// <summary>
/// 市场趋势
/// </summary>
public enum MarketTrend
{
    UpTrend = 1,
    DownTrend = 2,
    SideWays = 3
}

/// <summary>
/// 策略兼容性报告
/// </summary>
public record StrategyCompatibilityReport
{
    public string StrategyId { get; init; } = string.Empty;
    public bool IsCompatible { get; init; }
    public int CompatibilityScore { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Recommendations { get; init; } = [];
    public DateTime EvaluatedAt { get; init; }
}
