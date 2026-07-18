namespace 币安量化机器人.Core.Execution;

/// <summary>
/// 错误恢复处理接口
/// 负责处理交易执行中的异常和故障恢复
/// </summary>
public interface IErrorRecoveryHandler
{
    /// <summary>
    /// 注册错误处理器
    /// </summary>
    /// <param name="errorType">错误类型</param>
    /// <param name="handler">处理函数</param>
    void RegisterErrorHandler(
        ErrorType errorType,
        Func<ExecutionError, Task<RecoveryAction>> handler);

    /// <summary>
    /// 处理执行错误
    /// </summary>
    /// <param name="error">执行错误信息</param>
    /// <returns>恢复操作</returns>
    Task<RecoveryAction> HandleErrorAsync(ExecutionError error);

    /// <summary>
    /// 尝试恢复失败的订单
    /// </summary>
    /// <param name="failedOrder">失败的订单</param>
    /// <param name="maxRetries">最大重试次数</param>
    Task<RecoveryResult> RecoverFailedOrderAsync(
        ExecutionResult failedOrder,
        int maxRetries = 3);

    /// <summary>
    /// 处理网络超时
    /// </summary>
    /// <param name="originalRequest">原始请求</param>
    /// <param name="timeoutMs">超时毫秒数</param>
    Task<RecoveryAction> HandleTimeoutAsync(
        OrderRequest originalRequest,
        int timeoutMs);

    /// <summary>
    /// 处理余额不足错误
    /// </summary>
    /// <param name="order">订单信息</param>
    /// <param name="requiredAmount">所需金额</param>
    /// <param name="availableAmount">可用金额</param>
    Task<RecoveryAction> HandleInsufficientBalanceAsync(
        OrderRequest order,
        decimal requiredAmount,
        decimal availableAmount);

    /// <summary>
    /// 处理市场异常（如价格波动过大）
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="priceDeviation">价格偏差百分比</param>
    Task<RecoveryAction> HandleMarketAnomalyAsync(
        string symbol,
        decimal priceDeviation);

    /// <summary>
    /// 处理API限流
    /// </summary>
    /// <param name="retryAfterMs">建议重试等待时间（毫秒）</param>
    Task<RecoveryAction> HandleRateLimitAsync(int retryAfterMs);

    /// <summary>
    /// 获取错误恢复历史
    /// </summary>
    /// <param name="limit">限制条数</param>
    Task<List<ErrorRecoveryRecord>> GetRecoveryHistoryAsync(int limit = 100);

    /// <summary>
    /// 清除过期的错误记录
    /// </summary>
    /// <param name="olderThan">清除早于此时间的记录</param>
    Task ClearOldErrorRecordsAsync(DateTime olderThan);

    /// <summary>
    /// 检查系统是否处于健康状态
    /// </summary>
    Task<SystemHealth> CheckSystemHealthAsync();
}

/// <summary>
/// 执行错误
/// </summary>
public record ExecutionError
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public ErrorType Type { get; init; }
    public ErrorSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public OrderRequest? RelatedOrder { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> Context { get; init; } = [];
}

/// <summary>
/// 错误类型
/// </summary>
public enum ErrorType
{
    NetworkTimeout = 1,
    InvalidOrder = 2,
    InsufficientBalance = 3,
    OrderRejected = 4,
    ExchangeUnavailable = 5,
    APILimitReached = 6,
    MarketAnomaly = 7,
    SystemError = 8,
    UnknownError = 9,
    OrderNotFound = 10,
    DuplicateOrder = 11
}

/// <summary>
/// 错误严重级别
/// </summary>
public enum ErrorSeverity
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>
/// 恢复操作
/// </summary>
public record RecoveryAction
{
    public RecoveryStrategy Strategy { get; init; }
    public int? RetryCount { get; init; }
    public int? DelayMs { get; init; }
    public bool ShouldCancelOrder { get; init; }
    public bool ShouldNotifyUser { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, object>? AdditionalData { get; init; }
}

/// <summary>
/// 恢复策略
/// </summary>
public enum RecoveryStrategy
{
    Retry = 1,
    Delay = 2,
    CancelAndRetry = 3,
    Cancel = 4,
    Manual = 5,
    Ignore = 6,
    Escalate = 7
}

/// <summary>
/// 恢复结果
/// </summary>
public record RecoveryResult
{
    public bool Succeeded { get; init; }
    public long? RecoveredOrderId { get; init; }
    public int AttemptsUsed { get; init; }
    public decimal? FinalExecutedQuantity { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
}

/// <summary>
/// 错误恢复记录
/// </summary>
public record ErrorRecoveryRecord
{
    public string ErrorId { get; init; } = string.Empty;
    public ErrorType ErrorType { get; init; }
    public ErrorSeverity Severity { get; init; }
    public RecoveryStrategy UsedStrategy { get; init; }
    public bool RecoverySucceeded { get; init; }
    public int RetryAttempts { get; init; }
    public DateTime ErrorTime { get; init; }
    public DateTime? RecoveryCompletedTime { get; init; }
    public string Details { get; init; } = string.Empty;
}

/// <summary>
/// 系统健康状态
/// </summary>
public record SystemHealth
{
    public bool IsHealthy { get; init; }
    public decimal HealthScore { get; init; }
    public int RecentErrorCount { get; init; }
    public int RecentSuccessCount { get; init; }
    public decimal ErrorRate { get; init; }
    public string? WarningMessage { get; init; }
    public DateTime LastCheckedAt { get; init; }
    public Dictionary<string, object> Details { get; init; } = [];
}
