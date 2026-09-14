namespace 币安量化机器人.Core.Persistence;

using 币安量化机器人.Core.Execution;

/// <summary>
/// 交易记录器接口
/// 负责持久化交易数据到数据库
/// </summary>
public interface ITradingRecorder
{
    /// <summary>
    /// 记录已完成的交易
    /// </summary>
    /// <param name="trade">交易记录</param>
    Task<bool> RecordTradeAsync(TradingLog trade);

    /// <summary>
    /// 记录订单执行事件
    /// </summary>
    /// <param name="orderEvent">订单事件</param>
    Task<bool> RecordOrderEventAsync(OrderEvent orderEvent);

    /// <summary>
    /// 记录账户状态快照
    /// </summary>
    /// <param name="snapshot">账户快照</param>
    Task<bool> RecordAccountSnapshotAsync(AccountSnapshot snapshot);

    /// <summary>
    /// 查询交易历史
    /// </summary>
    /// <param name="filter">查询过滤器</param>
    Task<List<TradingLog>> QueryTradesAsync(TradeQueryFilter filter);

    /// <summary>
    /// 获取日期范围内的交易总和
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    Task<TradingSummary> GetTradingSummaryAsync(DateTime startDate, DateTime endDate);

    /// <summary>
    /// 获取特定交易对的统计
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    Task<SymbolTradingStats> GetSymbolStatsAsync(string symbol, DateTime startDate, DateTime endDate);

    /// <summary>
    /// 获取收益曲线数据
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <param name="intervalMinutes">间隔分钟数</param>
    Task<List<EquityCurvePoint>> GetEquityCurveAsync(
        DateTime startDate,
        DateTime endDate,
        int intervalMinutes = 60);

    /// <summary>
    /// 导出交易数据为 CSV
    /// </summary>
    /// <param name="filter">查询过滤器</param>
    /// <param name="filePath">输出文件路径</param>
    Task<bool> ExportTradesToCsvAsync(TradeQueryFilter filter, string filePath);

    /// <summary>
    /// 删除旧交易记录
    /// </summary>
    /// <param name="olderThan">删除早于此日期的记录</param>
    Task<int> DeleteOldTradesAsync(DateTime olderThan);

    /// <summary>
    /// 获取最后一条交易记录
    /// </summary>
    Task<TradingLog?> GetLastTradeAsync();

    /// <summary>
    /// 清空所有交易记录
    /// </summary>
    Task<bool> ClearAllTradesAsync();
}

/// <summary>
/// 交易日志记录
/// </summary>
public record TradingLog
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Symbol { get; init; } = string.Empty;
    public TradeType Type { get; init; }
    public DateTime EntryTime { get; init; }
    public DateTime ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal Commission { get; init; }
    public decimal RealizedProfit { get; init; }
    public string StrategyId { get; init; } = string.Empty;
    public Dictionary<string, object> Metadata { get; init; } = [];
}

/// <summary>
/// 交易类型
/// </summary>
public enum TradeType
{
    Long = 1,
    Short = 2
}

/// <summary>
/// 订单事件
/// </summary>
public record OrderEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public long OrderId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public OrderEventType EventType { get; init; }
    public OrderState OrderState { get; init; }
    public decimal Price { get; init; }
    public decimal Quantity { get; init; }
    public decimal ExecutedQuantity { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Message { get; init; }
}

/// <summary>
/// 订单事件类型
/// </summary>
public enum OrderEventType
{
    Created = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Canceled = 4,
    Rejected = 5,
    Expired = 6
}

/// <summary>
/// 账户快照
/// </summary>
public record AccountSnapshot
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public decimal TotalBalance { get; init; }
    public decimal AvailableBalance { get; init; }
    public decimal TotalOpenPositionValue { get; init; }
    public decimal UnrealizedProfit { get; init; }
    public decimal RealizedProfit { get; init; }
    public Dictionary<string, AssetBalance> Assets { get; init; } = [];
}

/// <summary>
/// 资产余额
/// </summary>
public record AssetBalance
{
    public string Asset { get; init; } = string.Empty;
    public decimal Free { get; init; }
    public decimal Locked { get; init; }
    public decimal Total => Free + Locked;
}

/// <summary>
/// 交易查询过滤器
/// </summary>
public record TradeQueryFilter
{
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string? Symbol { get; init; }
    public string? StrategyId { get; init; }
    public TradeType? Type { get; init; }
    public int? Limit { get; init; } = 1000;
    public int? Offset { get; init; } = 0;
}

/// <summary>
/// 交易总结
/// </summary>
public record TradingSummary
{
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public decimal TotalProfit { get; init; }
    public decimal TotalCommission { get; init; }
    public decimal NetProfit => TotalProfit - TotalCommission;
    public decimal WinRate => TotalTrades > 0 ? (decimal)WinningTrades / TotalTrades : 0;
    public decimal AverageWin => WinningTrades > 0 ? TotalProfit / WinningTrades : 0;
    public decimal AverageLoss => LosingTrades > 0 ? -TotalProfit / LosingTrades : 0;
    public decimal ProfitFactor => AverageLoss != 0 ? AverageWin / -AverageLoss : 0;
}

/// <summary>
/// 符号交易统计
/// </summary>
public record SymbolTradingStats
{
    public string Symbol { get; init; } = string.Empty;
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public decimal TotalVolume { get; init; }
    public decimal TotalProfit { get; init; }
    public decimal AverageTradeDuration => TotalTrades > 0 ? 100 : 0;
}

/// <summary>
/// 权益曲线点
/// </summary>
public record EquityCurvePoint
{
    public DateTime Timestamp { get; init; }
    public decimal Balance { get; init; }
    public decimal UnrealizedProfit { get; init; }
    public decimal TotalValue => Balance + UnrealizedProfit;
}
