using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;

namespace 币安量化机器人.Modules.Paper;

public partial class PaperTradeView : UserControl
{
    private readonly ObservableCollection<PaperTradeRecord> _records = new();
    private decimal _position;
    private decimal _cash;
    private DateTime _lastFundingTime;

    public PaperTradeView()
    {
        InitializeComponent();
        TradesGrid.ItemsSource = _records;
        ResetSimulation();
    }

    private void Buy_Click(object sender, RoutedEventArgs e) => Execute(OrderSide.Buy);
    private void Sell_Click(object sender, RoutedEventArgs e) => Execute(OrderSide.Sell);
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetSimulation();

    private void ResetSimulation()
    {
        _records.Clear();
        _cash = ParseDecimal(InitialCapitalBox.Text, 10_000m);
        _position = 0;
        _lastFundingTime = DateTime.UtcNow;
        UpdateStatus(ParseDecimal(PriceBox.Text, 30_000m));
    }

    private void Execute(OrderSide side)
    {
        var price = ParseDecimal(PriceBox.Text, 30_000m);
        var quantity = ParseDecimal(QuantityBox.Text, 0.01m);
        var signedQty = side == OrderSide.Buy ? quantity : -quantity;
        var now = DateTime.UtcNow;
        var funding = ApplyFunding(price, now);
        var feeRate = ParseDecimal(FeeRateBox.Text, 0.0004m);
        var fee = Math.Abs(quantity * price) * feeRate;

        _cash -= fee;
        _cash -= signedQty * price;
        _position += signedQty;

        var equity = CalculateEquity(price);
        _records.Add(new PaperTradeRecord
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Side = side == OrderSide.Buy ? "买入" : "卖出",
            Price = price,
            Quantity = signedQty,
            Fee = fee,
            Funding = funding,
            Cash = _cash,
            Position = _position,
            Equity = equity
        });

        UpdateStatus(price);
    }

    private static decimal ParseDecimal(string text, decimal fallback)
    {
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private decimal ApplyFunding(decimal markPrice, DateTime now)
    {
        if (_position == 0)
        {
            _lastFundingTime = now;
            return 0;
        }

        var elapsedHours = (decimal)(now - _lastFundingTime).TotalHours;
        _lastFundingTime = now;
        if (elapsedHours <= 0)
            return 0;

        var fundingRate = ParseDecimal(FundingRateBox.Text, 0.0001m);
        if (fundingRate == 0)
            return 0;

        var notional = Math.Abs(_position * markPrice);
        var direction = _position > 0 ? 1m : -1m;
        var funding = notional * fundingRate * elapsedHours * direction;
        _cash -= funding;
        return funding;
    }

    private decimal CalculateEquity(decimal markPrice) => _cash + _position * markPrice;

    private void UpdateStatus(decimal markPrice)
    {
        var equity = CalculateEquity(markPrice);
        StatusText.Text = $"状态：权益 {equity:F2} · 现金 {_cash:F2} · 仓位 {_position:F4}";
    }
}

public class PaperTradeRecord
{
    public string Time { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal Fee { get; set; }
    public decimal Funding { get; set; }
    public decimal Cash { get; set; }
    public decimal Position { get; set; }
    public decimal Equity { get; set; }
}
