namespace 币安量化机器人.Core.Risk;

/// <summary>
/// 杠杆控制服务接口
/// 负责管理保证金和杠杆倍数
/// </summary>
public interface ILeverageController
{
    /// <summary>
    /// 设置杠杆倍数
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="leverage">杠杆倍数（1-125）</param>
    Task<bool> SetLeverageAsync(string symbol, int leverage);

    /// <summary>
    /// 获取当前杠杆倍数
    /// </summary>
    /// <param name="symbol">交易对</param>
    Task<int> GetCurrentLeverageAsync(string symbol);

    /// <summary>
    /// 计算所需的保证金
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="quantity">持仓数量</param>
    /// <param name="price">持仓价格</param>
    /// <param name="leverage">杠杆倍数</param>
    Task<decimal> CalculateRequiredMarginAsync(
        string symbol,
        decimal quantity,
        decimal price,
        int leverage);

    /// <summary>
    /// 获取账户总保证金
    /// </summary>
    Task<decimal> GetTotalMarginAsync();

    /// <summary>
    /// 获取账户可用保证金
    /// </summary>
    Task<decimal> GetAvailableMarginAsync();

    /// <summary>
    /// 计算保证金率
    /// </summary>
    Task<decimal> CalculateMarginRatioAsync();

    /// <summary>
    /// 检查是否即将触发清算
    /// </summary>
    /// <param name="warningThreshold">警告阈值，如 0.2 表示保证金率低于 20%</param>
    Task<bool> IsLiquidationRiskAsync(decimal warningThreshold = 0.2m);

    /// <summary>
    /// 自动调整杠杆以维持目标保证金率
    /// </summary>
    /// <param name="targetMarginRatio">目标保证金率，如 0.5 表示 50%</param>
    Task<bool> AutoAdjustLeverageToTargetRatioAsync(decimal targetMarginRatio);

    /// <summary>
    /// 增加保证金（转入资金）
    /// </summary>
    /// <param name="amount">增加金额</param>
    Task<bool> AddMarginAsync(decimal amount);

    /// <summary>
    /// 减少保证金（转出资金）
    /// </summary>
    /// <param name="amount">减少金额</param>
    Task<bool> RemoveMarginAsync(decimal amount);

    /// <summary>
    /// 检查是否可以以给定杠杆开仓
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="quantity">计划持仓数量</param>
    /// <param name="price">计划持仓价格</param>
    /// <param name="leverage">计划杠杆倍数</param>
    Task<bool> CanOpenWithLeverageAsync(
        string symbol,
        decimal quantity,
        decimal price,
        int leverage);

    /// <summary>
    /// 获取杠杆信息总览
    /// </summary>
    Task<LeverageInfo> GetLeverageInfoAsync();

    /// <summary>
    /// 获取符号的最大可用杠杆
    /// </summary>
    /// <param name="symbol">交易对</param>
    Task<int> GetMaxLeverageAsync(string symbol);

    /// <summary>
    /// 获取历史杠杆调整记录
    /// </summary>
    /// <param name="limit">限制条数</param>
    Task<List<LeverageAdjustmentRecord>> GetLeverageHistoryAsync(int limit = 100);
}

/// <summary>
/// 杠杆信息
/// </summary>
public record LeverageInfo
{
    public decimal TotalMargin { get; init; }
    public decimal AvailableMargin { get; init; }
    public decimal UsedMargin { get; init; }
    public decimal MarginRatio { get; init; }
    public bool LiquidationRisk { get; init; }
    public Dictionary<string, SymbolLeverageInfo> SymbolLeverages { get; init; } = [];
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// 单个符号的杠杆信息
/// </summary>
public record SymbolLeverageInfo
{
    public string Symbol { get; init; } = string.Empty;
    public int CurrentLeverage { get; init; }
    public int MaxLeverage { get; init; }
    public decimal UsedMargin { get; init; }
    public decimal MaintenanceMargin { get; init; }
}

/// <summary>
/// 杠杆调整记录
/// </summary>
public record LeverageAdjustmentRecord
{
    public string Symbol { get; init; } = string.Empty;
    public int PreviousLeverage { get; init; }
    public int NewLeverage { get; init; }
    public AdjustmentReason Reason { get; init; }
    public DateTime AdjustedAt { get; init; }
    public bool Successful { get; init; }
}

/// <summary>
/// 调整原因
/// </summary>
public enum AdjustmentReason
{
    Manual = 1,
    AutoAdjustment = 2,
    RiskManagement = 3,
    MarginRecovery = 4,
    SystemRebalance = 5
}
