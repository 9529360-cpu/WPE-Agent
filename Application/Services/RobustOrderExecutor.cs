namespace 币安量化机器人.Application.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using 币安量化机器人.Core.Execution;
using 币安量化机器人.Services;

/// <summary>
/// 健壮的订单执行器实现
/// 提供重试机制、错误处理和订单生命周期管理
/// </summary>
public class RobustOrderExecutor : IOrderExecutor
{
    private readonly BinanceApiClient _apiClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, OrderState> _orderStates = new();
    private readonly TimeSpan[] _retryDelays = 
    { 
        TimeSpan.FromMilliseconds(250), 
        TimeSpan.FromMilliseconds(500), 
        TimeSpan.FromMilliseconds(1000) 
    };

    public RobustOrderExecutor(BinanceApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = Log.ForContext<RobustOrderExecutor>();
    }

    public async Task<ExecutionResult> ExecuteMarketOrderAsync(
        MarketOrderRequest request, 
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInternalAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionResult> ExecuteLimitOrderAsync(
        LimitOrderRequest request, 
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInternalAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CancelOrderAsync(string symbol, long orderId)
    {
        try
        {
            await _apiClient.CancelOrderAsync(symbol, orderId).ConfigureAwait(false);
            _logger.Information("订单已取消: {OrderId}", orderId);
            _orderStates.TryRemove(orderId, out _);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "取消订单失败: {OrderId}", orderId);
            return false;
        }
    }

    public async Task<OrderStatus?> GetOrderStatusAsync(string symbol, long orderId)
    {
        try
        {
            var dto = await _apiClient.GetOrderAsync(symbol, orderId).ConfigureAwait(false);
            return MapOrderStatus(dto);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "获取订单状态失败: {OrderId}", orderId);
            return null;
        }
    }

    public async Task<List<ExecutionResult>> ExecuteBatchOrdersAsync(
        List<Core.Execution.OrderRequest> requests, 
        CancellationToken cancellationToken = default)
    {
        if (requests == null || requests.Count == 0)
            return new List<ExecutionResult>();

        try
        {
            var results = new List<ExecutionResult>();
            foreach (var request in requests)
            {
                var result = await ExecuteInternalAsync(request, cancellationToken).ConfigureAwait(false);
                results.Add(result);
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "批量订单执行失败");
            return requests.Select(r => new ExecutionResult 
            { 
                Success = false, 
                Symbol = r.Symbol, 
                Side = r.Side, 
                ErrorMessage = ex.Message, 
                ExecutedAt = DateTime.UtcNow 
            }).ToList();
        }
    }

    public async Task<ExecutionResult> ExecuteStopLossOrderAsync(
        StopLossOrderRequest request, 
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInternalAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionResult> ExecuteTakeProfitOrderAsync(
        TakeProfitOrderRequest request, 
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInternalAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ModifyOrderAsync(
        string symbol, 
        long orderId, 
        decimal? newQuantity = null, 
        decimal? newPrice = null)
    {
        try
        {
            var status = await GetOrderStatusAsync(symbol, orderId).ConfigureAwait(false);
            if (status == null)
                return false;

            var cancelled = await CancelOrderAsync(symbol, orderId).ConfigureAwait(false);
            if (!cancelled)
                return false;

            if (newPrice.HasValue)
            {
                var req = new LimitOrderRequest 
                { 
                    Symbol = symbol, 
                    Side = status.Side, 
                    Quantity = newQuantity ?? status.OrigQty, 
                    Price = newPrice.Value, 
                    TimeInForce = status.TimeInForce 
                };
                var res = await ExecuteLimitOrderAsync(req).ConfigureAwait(false);
                return res.Success;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "修改订单失败: {OrderId}", orderId);
            return false;
        }
    }

    public async Task<List<OrderStatus>> GetOpenOrdersAsync(string? symbol = null)
    {
        try
        {
            var list = await _apiClient.GetOpenOrdersAsync(symbol).ConfigureAwait(false);
            return list.Select(MapOrderStatus).ToList();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "获取待执行订单失败");
            return new List<OrderStatus>();
        }
    }

    public async Task<ExecutionResult> RetryOrderAsync(
        Core.Execution.OrderRequest request, 
        int maxAttempts = 3)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var res = await ExecuteInternalAsync(request).ConfigureAwait(false);
            if (res.Success)
                return res;

            await Task.Delay(_retryDelays[Math.Min(attempt, _retryDelays.Length - 1)]).ConfigureAwait(false);
        }

        return new ExecutionResult 
        { 
            Success = false, 
            Symbol = request.Symbol, 
            Side = request.Side, 
            ErrorMessage = "重试次数已用尽", 
            ExecutedAt = DateTime.UtcNow 
        };
    }

    // ============ Private Helpers ============

    private async Task<ExecutionResult> ExecuteInternalAsync(
        Core.Execution.OrderRequest request, 
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        for (int attempt = 0; attempt < _retryDelays.Length + 1; attempt++)
        {
            try
            {
                // 将 Core.Execution.OrderRequest 映射到 Models.OrderRequest
                var modelReq = MapToModelOrderRequest(request);
                var dto = await _apiClient.PlaceOrderAsync(modelReq, cancellationToken).ConfigureAwait(false);
                var result = MapExecutionResult(dto);
                
                if (result.OrderId.HasValue)
                {
                    _orderStates[result.OrderId.Value] = new OrderState { State = Core.Execution.OrderState.Filled };
                }

                return result;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _retryDelays.Length)
            {
                _logger.Warning(ex, "临时错误，准备重试。尝试次数: {Attempt}", attempt + 1);
                await Task.Delay(_retryDelays[attempt], cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "订单执行失败: {Symbol}", request.Symbol);
                return new ExecutionResult 
                { 
                    Success = false, 
                    Symbol = request.Symbol, 
                    Side = request.Side, 
                    ErrorMessage = ex.Message, 
                    ExecutedAt = DateTime.UtcNow 
                };
            }
        }

        return new ExecutionResult 
        { 
            Success = false, 
            Symbol = request.Symbol, 
            Side = request.Side, 
            ErrorMessage = "重试后仍然失败", 
            ExecutedAt = DateTime.UtcNow 
        };
    }

    private static Models.OrderRequest MapToModelOrderRequest(Core.Execution.OrderRequest request)
    {
        return new Models.OrderRequest
        {
            Symbol = request.Symbol,
            Side = request.Side == Core.Execution.OrderSide.Buy 
                ? Models.OrderSide.Buy 
                : Models.OrderSide.Sell,
            Quantity = request.Quantity,
            Type = request switch
            {
                LimitOrderRequest => Models.OrderType.Limit,
                StopLossOrderRequest => Models.OrderType.StopLoss,
                TakeProfitOrderRequest => Models.OrderType.TakeProfit,
                _ => Models.OrderType.Market
            },
            Price = request is LimitOrderRequest lr ? lr.Price : 0m,
            StopPrice = request is StopLossOrderRequest sl ? sl.StopPrice 
                : (request is TakeProfitOrderRequest tp ? tp.ProfitPrice : 0m),
            TimeInForce = request is LimitOrderRequest lreq 
                ? (Models.TimeInForce)lreq.TimeInForce 
                : Models.TimeInForce.Gtc
        };
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException 
            || ex is TaskCanceledException 
            || ex is InvalidOperationException;
    }

    private static ExecutionResult MapExecutionResult(Models.OrderResponse dto)
    {
        return new ExecutionResult
        {
            Success = true,
            OrderId = dto.OrderId,
            Symbol = dto.Symbol,
            Side = Core.Execution.OrderSide.Buy,
            ExecutedQuantity = dto.ExecutedQuantity,
            ExecutedPrice = dto.AvgPrice,
            CommissionAmount = 0,
            ErrorMessage = string.Empty,
            ExecutedAt = dto.Time,
            OrderStatus = new OrderStatus
            {
                OrderId = dto.OrderId,
                Symbol = dto.Symbol,
                Price = dto.Price,
                OrigQty = dto.CumulativeQuoteQuantity,
                ExecutedQty = dto.ExecutedQuantity,
                CummulativeQuoteQty = dto.CumulativeQuoteQuantity,
                Status = Core.Execution.OrderState.Filled,
                TimeInForce = Core.Execution.TimeInForce.GTC,
                Type = Core.Execution.OrderType.Market,
                Side = Core.Execution.OrderSide.Buy,
                Time = dto.Time,
                UpdateTime = dto.Time,
                IsWorking = false
            }
        };
    }

    private static OrderStatus MapOrderStatus(Models.OrderResponse dto)
    {
        return new OrderStatus
        {
            OrderId = dto.OrderId,
            Symbol = dto.Symbol,
            Price = dto.Price,
            OrigQty = dto.CumulativeQuoteQuantity,
            ExecutedQty = dto.ExecutedQuantity,
            CummulativeQuoteQty = dto.CumulativeQuoteQuantity,
            Status = Core.Execution.OrderState.New,
            TimeInForce = Core.Execution.TimeInForce.GTC,
            Type = Core.Execution.OrderType.Market,
            Side = Core.Execution.OrderSide.Buy,
            Time = dto.Time,
            UpdateTime = dto.Time,
            IsWorking = false
        };
    }

    private record OrderState
    {
        public Core.Execution.OrderState State { get; init; }
    }
}
