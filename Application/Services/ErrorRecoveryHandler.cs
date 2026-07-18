namespace 币安量化机器人.Application.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using 币安量化机器人.Core.Execution;
using 币安量化机器人.Services;

/// <summary>
/// 错误恢复处理器实现
/// 负责处理交易执行中的错误和故障恢复
/// </summary>
public class ErrorRecoveryHandler : IErrorRecoveryHandler
{
    private readonly BinanceApiClient _apiClient;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<ErrorRecoveryRecord> _history = new();

    public ErrorRecoveryHandler(BinanceApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = Log.ForContext<ErrorRecoveryHandler>();
    }

    public void RegisterErrorHandler(
        ErrorType errorType, 
        Func<ExecutionError, Task<RecoveryAction>> handler)
    {
        _logger.Debug("注册错误处理器: {ErrorType}", errorType);
    }

    public async Task<RecoveryAction> HandleErrorAsync(ExecutionError error)
    {
        _logger.Warning("处理执行错误: {ErrorId} {Message}", error.Id, error.Message);
        switch (error.Type)
        {
            case ErrorType.NetworkTimeout:
                return new RecoveryAction 
                { 
                    Strategy = RecoveryStrategy.Retry, 
                    RetryCount = 3, 
                    DelayMs = 1000 
                };
            case ErrorType.APILimitReached:
                return new RecoveryAction 
                { 
                    Strategy = RecoveryStrategy.Delay, 
                    DelayMs = 60000 
                };
            case ErrorType.InsufficientBalance:
                return new RecoveryAction 
                { 
                    Strategy = RecoveryStrategy.Escalate, 
                    ShouldNotifyUser = true 
                };
            default:
                return new RecoveryAction 
                { 
                    Strategy = RecoveryStrategy.Manual, 
                    ShouldNotifyUser = true 
                };
        }
    }

    public async Task<RecoveryResult> RecoverFailedOrderAsync(
        ExecutionResult failedOrder, 
        int maxRetries = 3)
    {
        _logger.Warning("尝试恢复失败订单: {Symbol}", failedOrder.Symbol);
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                var retryReq = new Models.OrderRequest
                {
                    Symbol = failedOrder.Symbol,
                    Quantity = failedOrder.ExecutedQuantity,
                    Price = failedOrder.ExecutedPrice,
                    Side = failedOrder.Side == Core.Execution.OrderSide.Buy 
                        ? Models.OrderSide.Buy 
                        : Models.OrderSide.Sell,
                    Type = Models.OrderType.Market
                };

                var res = await _apiClient.PlaceOrderAsync(retryReq).ConfigureAwait(false);

                return new RecoveryResult 
                { 
                    Succeeded = true, 
                    RecoveredOrderId = res.OrderId, 
                    AttemptsUsed = i + 1, 
                    FinalExecutedQuantity = res.ExecutedQuantity, 
                    CompletedAt = DateTime.UtcNow 
                };
            }
            catch
            {
                await Task.Delay(250 * (i + 1)).ConfigureAwait(false);
            }
        }

        var record = new ErrorRecoveryRecord 
        { 
            ErrorId = Guid.NewGuid().ToString(), 
            ErrorType = ErrorType.UnknownError, 
            Severity = ErrorSeverity.High, 
            UsedStrategy = RecoveryStrategy.Retry, 
            RecoverySucceeded = false, 
            RetryAttempts = maxRetries, 
            ErrorTime = DateTime.UtcNow, 
            Details = "恢复尝试已用尽" 
        };
        _history.Enqueue(record);

        return new RecoveryResult 
        { 
            Succeeded = false, 
            AttemptsUsed = maxRetries, 
            CompletedAt = DateTime.UtcNow, 
            Message = "无法恢复订单" 
        };
    }

    public async Task<RecoveryAction> HandleTimeoutAsync(
        OrderRequest originalRequest, 
        int timeoutMs)
    {
        _logger.Warning("处理超时: {Symbol}", originalRequest.Symbol);
        return new RecoveryAction 
        { 
            Strategy = RecoveryStrategy.Retry, 
            RetryCount = 2, 
            DelayMs = 500 
        };
    }

    public async Task<RecoveryAction> HandleInsufficientBalanceAsync(
        OrderRequest order, 
        decimal requiredAmount, 
        decimal availableAmount)
    {
        _logger.Error(
            "余额不足: {Symbol} 需要={Required} 可用={Available}", 
            order.Symbol, 
            requiredAmount, 
            availableAmount);
        return new RecoveryAction 
        { 
            Strategy = RecoveryStrategy.Escalate, 
            ShouldNotifyUser = true 
        };
    }

    public async Task<RecoveryAction> HandleMarketAnomalyAsync(
        string symbol, 
        decimal priceDeviation)
    {
        _logger.Warning("市场异常: {Symbol} 偏差={Deviation}", symbol, priceDeviation);
        return new RecoveryAction 
        { 
            Strategy = RecoveryStrategy.Delay, 
            DelayMs = 60000 
        };
    }

    public async Task<RecoveryAction> HandleRateLimitAsync(int retryAfterMs)
    {
        _logger.Warning("API 限流。建议重试等待: {Ms}ms", retryAfterMs);
        return new RecoveryAction 
        { 
            Strategy = RecoveryStrategy.Delay, 
            DelayMs = retryAfterMs 
        };
    }

    public Task<List<ErrorRecoveryRecord>> GetRecoveryHistoryAsync(int limit = 100)
    {
        return Task.FromResult(_history.ToArray().Take(limit).ToList());
    }

    public Task ClearOldErrorRecordsAsync(DateTime olderThan)
    {
        return Task.CompletedTask;
    }

    public async Task<SystemHealth> CheckSystemHealthAsync()
    {
        var recentErrors = _history.Count;
        return new SystemHealth 
        { 
            IsHealthy = recentErrors < 10, 
            HealthScore = Math.Max(0, 100 - recentErrors), 
            RecentErrorCount = recentErrors, 
            RecentSuccessCount = 0, 
            ErrorRate = recentErrors / (recentErrors + 1m), 
            LastCheckedAt = DateTime.UtcNow 
        };
    }
}
