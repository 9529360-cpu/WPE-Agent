namespace 币安量化机器人.Application.ClosedLoopOrchestration;

using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Core.Execution;

/// <summary>
/// 自动化交易引擎接口
/// 核心闭环编排模块，协调所有交易组件
/// </summary>
public interface IAutomatedTradingEngine
{
    /// <summary>
    /// 启动交易引擎
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止交易引擎
    /// </summary>
    Task<bool> StopAsync();

    /// <summary>
    /// 暂停交易
    /// </summary>
    Task<bool> PauseAsync();

    /// <summary>
    /// 恢复交易
    /// </summary>
    Task<bool> ResumeAsync();

    /// <summary>
    /// 执行单个交易循环
    /// 1. 采集数据
    /// 2. 评估策略
    /// 3. 筛选策略
    /// 4. 执行交易
    /// 5. 管理风险
    /// 6. 记录交易
    /// </summary>
    Task<TradingCycleResult> ExecuteTradingCycleAsync();

    /// <summary>
    /// 添加策略到引擎
    /// </summary>
    /// <param name="strategy">策略信息</param>
    Task<bool> AddStrategyAsync(StrategyInfo strategy);

    /// <summary>
    /// 移除策略
    /// </summary>
    /// <param name="strategyId">策略 ID</param>
    Task<bool> RemoveStrategyAsync(string strategyId);

    /// <summary>
    /// 启用策略
    /// </summary>
    /// <param name="strategyId">策略 ID</param>
    Task<bool> EnableStrategyAsync(string strategyId);

    /// <summary>
    /// 禁用策略
    /// </summary>
    /// <param name="strategyId">策略 ID</param>
    Task<bool> DisableStrategyAsync(string strategyId);

    /// <summary>
    /// 获取引擎状态
    /// </summary>
    Task<EngineStatus> GetStatusAsync();

    /// <summary>
    /// 获取活跃策略列表
    /// </summary>
    Task<List<StrategyInfo>> GetActiveStrategiesAsync();

    /// <summary>
    /// 获取引擎的性能统计
    /// </summary>
    Task<EnginePerformanceStats> GetPerformanceStatsAsync();

    /// <summary>
    /// 手动触发交易信号
    /// </summary>
    /// <param name="signal">交易信号</param>
    Task<ExecutionResult> ManualTradeAsync(TradeSignal signal);

    /// <summary>
    /// 订阅引擎事件
    /// </summary>
    /// <param name="eventType">事件类型</param>
    /// <param name="handler">事件处理器</param>
    void SubscribeToEvent(EngineEventType eventType, Func<EngineEvent, Task> handler);

    /// <summary>
    /// 取消订阅引擎事件
    /// </summary>
    /// <param name="eventType">事件类型</param>
    void UnsubscribeFromEvent(EngineEventType eventType);

    /// <summary>
    /// 获取引擎日志
    /// </summary>
    /// <param name="limit">日志条数限制</param>
    Task<List<EngineLog>> GetLogsAsync(int limit = 100);

    /// <summary>
    /// 清空引擎日志
    /// </summary>
    Task<bool> ClearLogsAsync();
}

/// <summary>
/// 交易循环结果
/// </summary>
public record TradingCycleResult
{
    public int CycleNumber { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public bool Success { get; init; }
    public int StrategiesEvaluated { get; init; }
    public int TradesExecuted { get; init; }
    public List<ExecutionResult> ExecutedTrades { get; init; } = [];
    public Dictionary<string, object> Metrics { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 引擎状态
/// </summary>
public record EngineStatus
{
    public TradingEngineState State { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? StoppedAt { get; init; }
    public DateTime LastCycleTime { get; init; }
    public int TotalCyclesExecuted { get; init; }
    public int ActiveStrategies { get; init; }
    public int OpenPositions { get; init; }
    public decimal AccountBalance { get; init; }
    public decimal UnrealizedProfit { get; init; }
    public decimal TodayProfit { get; init; }
    public bool IsHealthy { get; init; }
    public string? WarningMessage { get; init; }
}

/// <summary>
/// 交易引擎状态枚举
/// </summary>
public enum TradingEngineState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Paused = 3,
    Stopping = 4,
    Error = 5
}

/// <summary>
/// 引擎性能统计
/// </summary>
public record EnginePerformanceStats
{
    public int TotalCycles { get; init; }
    public int SuccessfulCycles { get; init; }
    public int FailedCycles { get; init; }
    public double AverageCycleDurationMs { get; init; }
    public int TotalTradesExecuted { get; init; }
    public decimal TotalProfit { get; init; }
    public decimal WinRate { get; init; }
    public double Uptime { get; init; }
    public DateTime LastUpdatedAt { get; init; }
}

/// <summary>
/// 交易信号
/// </summary>
public record TradeSignal
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Symbol { get; init; } = string.Empty;
    public SignalType Type { get; init; }
    public decimal TargetQuantity { get; init; }
    public decimal? TargetPrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public string? StrategyId { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// 信号类型
/// </summary>
public enum SignalType
{
    Buy = 1,
    Sell = 2,
    Close = 3,
    PartialClose = 4
}

/// <summary>
/// 引擎事件
/// </summary>
public record EngineEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public EngineEventType Type { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Message { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}

/// <summary>
/// 引擎事件类型
/// </summary>
public enum EngineEventType
{
    Started = 1,
    Stopped = 2,
    CycleStarted = 3,
    CycleCompleted = 4,
    TradeExecuted = 5,
    StrategyAdded = 6,
    StrategyRemoved = 7,
    StrategyEnabled = 8,
    StrategyDisabled = 9,
    Error = 10,
    Warning = 11
}

/// <summary>
/// 引擎日志
/// </summary>
public record EngineLog
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Category { get; init; }
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>
/// 日志级别
/// </summary>
public enum LogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}
