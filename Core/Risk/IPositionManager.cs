namespace 币安量化机器人.Core.Risk;

/// <summary>
/// 仓位管理服务接口
/// 负责跟踪和管理持仓的大小和杠杆比例
/// </summary>
public interface IPositionManager
{
    /// <summary>
    /// 开仓
    /// </summary>
    /// <param name="position">仓位信息</param>
    Task<bool> OpenPositionAsync(Position position);

    /// <summary>
    /// 平仓
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="quantity">平仓数量（如为 0 则全部平仓）</param>
    Task<bool> ClosePositionAsync(string symbol, decimal quantity = 0);

    /// <summary>
    /// 部分平仓
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="quantity">平仓数量</param>
    Task<bool> PartialCloseAsync(string symbol, decimal quantity);

    /// <summary>
    /// 获取持仓信息
    /// </summary>
    /// <param name="symbol">交易对（为空则返回所有）</param>
    Task<List<Position>> GetPositionsAsync(string? symbol = null);

    /// <summary>
    /// 获取单个持仓信息
    /// </summary>
    /// <param name="symbol">交易对</param>
    Task<Position?> GetPositionAsync(string symbol);

    /// <summary>
    /// 计算持仓的未实现利润
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="currentPrice">当前价格</param>
    Task<decimal> CalculateUnrealizedProfitAsync(string symbol, decimal currentPrice);

    /// <summary>
    /// 更新持仓价格（用于标记）
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="markPrice">标记价格</param>
    Task<bool> UpdatePositionMarkPriceAsync(string symbol, decimal markPrice);

    /// <summary>
    /// 获取总持仓价值
    /// </summary>
    Task<decimal> GetTotalPositionValueAsync();

    /// <summary>
    /// 获取持仓数量
    /// </summary>
    Task<int> GetOpenPositionCountAsync();

    /// <summary>
    /// 检查是否可以开仓
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="quantity">计划开仓数量</param>
    /// <param name="maxPositionSize">最大持仓大小限制</param>
    Task<bool> CanOpenPositionAsync(string symbol, decimal quantity, decimal maxPositionSize);

    /// <summary>
    /// 调整止损价格
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="newStopLossPrice">新的止损价格</param>
    Task<bool> UpdateStopLossAsync(string symbol, decimal newStopLossPrice);

    /// <summary>
    /// 调整止盈价格
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="newTakeProfitPrice">新的止盈价格</param>
    Task<bool> UpdateTakeProfitAsync(string symbol, decimal newTakeProfitPrice);

    /// <summary>
    /// 获取持仓历史
    /// </summary>
    /// <param name="symbol">交易对（可选）</param>
    /// <param name="limit">限制条数</param>
    Task<List<PositionHistory>> GetPositionHistoryAsync(string? symbol = null, int limit = 100);

    /// <summary>
    /// 清空所有持仓
    /// </summary>
    Task<bool> CloseAllPositionsAsync();
}

/// <summary>
/// 仓位信息
/// </summary>
public record Position
{
    public string Symbol { get; init; } = string.Empty;
    public PositionDirection Direction { get; init; }
    public decimal Quantity { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal CurrentPrice { get; init; }
    public decimal StopLossPrice { get; init; }
    public decimal TakeProfitPrice { get; init; }
    public decimal Leverage { get; init; } = 1;
    public decimal Commission { get; init; }
    public DateTime OpenTime { get; init; }
    public DateTime? LastUpdateTime { get; init; }
    public PositionStatus Status { get; init; }

    /// <summary>
    /// 计算未实现利润
    /// </summary>
    public decimal UnrealizedProfit => Direction switch
    {
        PositionDirection.Long => (CurrentPrice - EntryPrice) * Quantity - Commission,
        PositionDirection.Short => (EntryPrice - CurrentPrice) * Quantity - Commission,
        _ => 0
    };

    /// <summary>
    /// 计算未实现利润百分比
    /// </summary>
    public decimal UnrealizedProfitPercent => EntryPrice > 0
        ? (UnrealizedProfit / (EntryPrice * Quantity)) * 100
        : 0;
}

/// <summary>
/// 仓位方向
/// </summary>
public enum PositionDirection
{
    Long = 1,
    Short = 2
}

/// <summary>
/// 仓位状态
/// </summary>
public enum PositionStatus
{
    Open = 1,
    PartiallyFilled = 2,
    ClosePending = 3,
    Closed = 4
}

/// <summary>
/// 仓位历史记录
/// </summary>
public record PositionHistory
{
    public string Symbol { get; init; } = string.Empty;
    public PositionDirection Direction { get; init; }
    public decimal Quantity { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal RealizedProfit { get; init; }
    public DateTime OpenTime { get; init; }
    public DateTime CloseTime { get; init; }
    public int DurationHours => (int)(CloseTime - OpenTime).TotalHours;
    public decimal ROI => EntryPrice > 0 ? (RealizedProfit / (EntryPrice * Quantity)) * 100 : 0;
}
