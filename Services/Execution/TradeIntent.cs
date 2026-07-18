using System;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services.Execution;

public record TradeIntent(
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? Price = null,
    decimal? StopPrice = null,
    TimeInForce TimeInForce = TimeInForce.Gtc)
{
    public static TradeIntent FromOrderRequest(OrderRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        decimal? price = RequiresLimitPrice(request.Type) && request.Price > 0 ? request.Price : null;
        decimal? stop = RequiresStopPrice(request.Type) && request.StopPrice > 0 ? request.StopPrice : null;

        return new TradeIntent(
            request.Symbol,
            request.Side,
            request.Type,
            request.Quantity,
            price,
            stop,
            request.TimeInForce);
    }

    public OrderRequest ToOrderRequest()
    {
        var request = new OrderRequest
        {
            Symbol = Symbol,
            Side = Side,
            Type = Type,
            Quantity = Quantity,
            TimeInForce = TimeInForce
        };

        if (Price is { } limitPrice)
            request.Price = limitPrice;

        if (StopPrice is { } triggerPrice)
            request.StopPrice = triggerPrice;

        return request;
    }

    private static bool RequiresLimitPrice(OrderType type)
        => type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit;

    private static bool RequiresStopPrice(OrderType type)
        => type is OrderType.StopLoss or OrderType.StopLossLimit or OrderType.TakeProfit or OrderType.TakeProfitLimit;
}
