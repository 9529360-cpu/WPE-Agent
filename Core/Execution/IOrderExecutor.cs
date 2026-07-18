namespace 币安量化机器人.Core.Execution;

/// <summary>
/// 订单执行服务接口
/// 负责向交易所提交和管理订单的生命周期
/// </summary>
public interface IOrderExecutor
{
    /// <summary>
    /// 执行市价单
    /// </summary>
    /// <param name="request">市价单请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>执行结果</returns>
    Task<ExecutionResult> ExecuteMarketOrderAsync(
        MarketOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行限价单
    /// </summary>
    /// <param name="request">限价单请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ExecutionResult> ExecuteLimitOrderAsync(
        LimitOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消订单
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="orderId">订单 ID</param>
    Task<bool> CancelOrderAsync(string symbol, long orderId);

    /// <summary>
    /// 获取订单状态
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="orderId">订单 ID</param>
    Task<OrderStatus?> GetOrderStatusAsync(string symbol, long orderId);

    /// <summary>
    /// 批量执行订单
    /// </summary>
    /// <param name="requests">订单请求列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<List<ExecutionResult>> ExecuteBatchOrdersAsync(
        List<OrderRequest> requests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行止损单
    /// </summary>
    /// <param name="request">止损单请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ExecutionResult> ExecuteStopLossOrderAsync(
        StopLossOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行止盈单
    /// </summary>
    /// <param name="request">止盈单请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ExecutionResult> ExecuteTakeProfitOrderAsync(
        TakeProfitOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改订单
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="orderId">订单 ID</param>
    /// <param name="newQuantity">新数量（可选）</param>
    /// <param name="newPrice">新价格（限价单）</param>
    Task<bool> ModifyOrderAsync(
        string symbol,
        long orderId,
        decimal? newQuantity = null,
        decimal? newPrice = null);

    /// <summary>
    /// 获取待执行订单列表
    /// </summary>
    /// <param name="symbol">交易对（可选，为空则返回所有）</param>
    Task<List<OrderStatus>> GetOpenOrdersAsync(string? symbol = null);
}

/// <summary>
/// 基础订单请求
/// </summary>
public abstract record OrderRequest
{
    public string Symbol { get; init; } = string.Empty;
    public OrderSide Side { get; init; }
    public decimal Quantity { get; init; }
    public string? ClientOrderId { get; init; }
}

/// <summary>
/// 市价单请求
/// </summary>
public record MarketOrderRequest : OrderRequest
{
}

/// <summary>
/// 限价单请求
/// </summary>
public record LimitOrderRequest : OrderRequest
{
    public decimal Price { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.GTC;
}

/// <summary>
/// 止损单请求
/// </summary>
public record StopLossOrderRequest : OrderRequest
{
    public decimal StopPrice { get; init; }
}

/// <summary>
/// 止盈单请求
/// </summary>
public record TakeProfitOrderRequest : OrderRequest
{
    public decimal ProfitPrice { get; init; }
}

/// <summary>
/// 订单方向
/// </summary>
public enum OrderSide
{
    Buy = 1,
    Sell = 2
}

/// <summary>
/// 价格有效期
/// </summary>
public enum TimeInForce
{
    /// <summary>
    /// Good-Till-Canceled 订单未被成交一直有效
    /// </summary>
    GTC = 1,

    /// <summary>
    /// Immediate-or-Cancel 订单立即成交，未成交部分取消
    /// </summary>
    IOC = 2,

    /// <summary>
    /// Fill-or-Kill 订单必须全部成交，否则全部取消
    /// </summary>
    FOK = 3
}

/// <summary>
/// 执行结果
/// </summary>
public record ExecutionResult
{
    public bool Success { get; init; }
    public long? OrderId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public OrderSide Side { get; init; }
    public decimal ExecutedQuantity { get; init; }
    public decimal ExecutedPrice { get; init; }
    public decimal CommissionAmount { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public DateTime ExecutedAt { get; init; }
    public OrderStatus? OrderStatus { get; init; }
}

/// <summary>
/// 订单状态
/// </summary>
public record OrderStatus
{
    public long OrderId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public decimal OrigQty { get; init; }
    public decimal ExecutedQty { get; init; }
    public decimal CummulativeQuoteQty { get; init; }
    public OrderState Status { get; init; }
    public TimeInForce TimeInForce { get; init; }
    public OrderType Type { get; init; }
    public OrderSide Side { get; init; }
    public DateTime Time { get; init; }
    public DateTime UpdateTime { get; init; }
    public bool IsWorking { get; init; }
}

/// <summary>
/// 订单状态枚举
/// </summary>
public enum OrderState
{
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Canceled = 4,
    PendingCancel = 5,
    Rejected = 6,
    Expired = 7
}

/// <summary>
/// 订单类型
/// </summary>
public enum OrderType
{
    Limit = 1,
    Market = 2,
    StopLoss = 3,
    StopLossLimit = 4,
    TakeProfit = 5,
    TakeProfitLimit = 6,
    LimitMaker = 7
}
