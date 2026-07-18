using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace 币安量化机器人.Models;

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderType
{
    Market,
    Limit,
    StopLoss,
    StopLossLimit,
    TakeProfit,
    TakeProfitLimit
}

public class OrderRequest : INotifyPropertyChanged
{
    private string _symbol = string.Empty;
    private OrderSide _side;
    private OrderType _type;
    private decimal _quantity;
    private decimal _price;
    private decimal _stopPrice;
    private TimeInForce _timeInForce = TimeInForce.Gtc;

    public string Symbol
    {
        get => _symbol;
        set => SetField(ref _symbol, value);
    }

    public OrderSide Side
    {
        get => _side;
        set => SetField(ref _side, value);
    }

    public OrderType Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    public decimal Quantity
    {
        get => _quantity;
        set => SetField(ref _quantity, value);
    }

    public decimal Price
    {
        get => _price;
        set => SetField(ref _price, value);
    }

    public decimal StopPrice
    {
        get => _stopPrice;
        set => SetField(ref _stopPrice, value);
    }

    public TimeInForce TimeInForce
    {
        get => _timeInForce;
        set => SetField(ref _timeInForce, value);
    }

    public string ClientOrderId { get; set; } = string.Empty;
    public string PositionSide { get; set; } = string.Empty;
    public bool ReduceOnly { get; set; }
    public bool ClosePosition { get; set; }
    public string WorkingType { get; set; } = "MARK_PRICE";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;

        field = value!;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class BatchOrderRequest
{
    public IReadOnlyList<OrderRequest> Orders { get; init; } = Array.Empty<OrderRequest>();
    public bool UseOneWayTrigger { get; init; }
    public bool UseHedgeMode { get; init; }
}

public class OrderResponse
{
    public string Symbol { get; init; } = string.Empty;
    public long OrderId { get; init; }
    public string ClientOrderId { get; init; } = string.Empty;
    public decimal ExecutedQuantity { get; init; }
    public decimal CumulativeQuoteQuantity { get; init; }
    public decimal Price { get; init; }
    public decimal AvgPrice { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string PositionSide { get; init; } = string.Empty;
    public bool ReduceOnly { get; init; }
    public bool ClosePosition { get; init; }
    public DateTime Time { get; init; }
}

public enum TimeInForce
{
    Gtc,
    Ioc,
    Fok
}

public class PositionSnapshot
{
    public string Symbol { get; init; } = string.Empty;
    public decimal PositionAmt { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal MarkPrice { get; init; }
    public decimal UnrealizedProfit { get; init; }
    public decimal Leverage { get; init; }
    public decimal MaintenanceMargin { get; init; }
    public bool IsIsolated { get; init; }
    public string PositionSide { get; init; } = string.Empty;
    public decimal LiquidationPrice { get; init; }
}

public class TradeExecution
{
    public string Symbol { get; init; } = string.Empty;
    public OrderSide Side { get; init; }
    public decimal Quantity { get; init; }
    public decimal Price { get; init; }
    public DateTime Time { get; init; }
}
