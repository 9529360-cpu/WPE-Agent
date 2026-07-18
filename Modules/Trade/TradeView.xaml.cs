using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Execution;

namespace 币安量化机器人.Modules.Trade;

public partial class TradeView : UserControl
{
    private readonly ObservableCollection<OrderRequest> _batchOrders = new();
    private readonly BinanceApiClient _api = ServiceLocator.Api;
    private readonly ExecutionService _execution = ServiceLocator.Execution;

    public TradeView()
    {
        InitializeComponent();
        BatchGrid.ItemsSource = _batchOrders;
        SideBox.SelectedIndex = 0;
        TypeBox.SelectedIndex = 0;
        TifBox.SelectedIndex = 0;
        _ = LoadSymbolsAsync();
    }

    private async Task LoadSymbolsAsync()
    {
        try
        {
            StatusText.Text = "状态：加载交易对...";
            var tickers = await _api.GetMiniTickersAsync();
            SymbolBox.ItemsSource = tickers.Select(t => t.Symbol).OrderBy(s => s).ToList();
            if (SymbolBox.Items.Count > 0)
                SymbolBox.SelectedIndex = 0;
            StatusText.Text = "状态：交易对已刷新";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：交易对获取失败";
            MessageBox.Show(ex.Message, "交易对", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private OrderRequest BuildRequest()
    {
        var request = new OrderRequest
        {
            Symbol = SymbolBox.Text.Trim().ToUpperInvariant(),
            Side = (SideBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Sell" ? OrderSide.Sell : OrderSide.Buy,
            Type = Enum.TryParse<OrderType>((TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var type) ? type : OrderType.Market,
            TimeInForce = Enum.TryParse<TimeInForce>((TifBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var tif) ? tif : TimeInForce.Gtc
        };

        if (decimal.TryParse(QuantityBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
            request.Quantity = qty;
        if (decimal.TryParse(PriceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
            request.Price = price;
        if (decimal.TryParse(StopPriceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var stop))
            request.StopPrice = stop;

        return request;
    }

    private async void SubmitOrder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = BuildRequest();
            ValidateRequest(request);
            StatusText.Text = $"状态：正在发送 {request.Symbol} 单笔订单";
            var intent = TradeIntent.FromOrderRequest(request);
            var result = await _execution.SubmitAsync(intent);
            StatusText.Text = $"状态：订单 {result.OrderId} 已提交，成交 {result.FilledQuantity}";
            MessageBox.Show($"订单 {result.OrderId} 状态：{result.Status}\n成交量：{result.FilledQuantity}\n均价：{result.AveragePrice:F4}", "下单成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：下单失败";
            MessageBox.Show(ex.Message, "下单失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddBatch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = BuildRequest();
            ValidateRequest(request);
            _batchOrders.Add(request);
            StatusText.Text = $"状态：已加入批量（{_batchOrders.Count}）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "参数校验", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExecuteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_batchOrders.Count == 0)
        {
            MessageBox.Show("请先添加批量订单。", "批量下单", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            StatusText.Text = "状态：执行批量订单中...";
            int success = 0;
            var total = _batchOrders.Count;
            foreach (var order in _batchOrders.ToArray())
            {
                try
                {
                    ValidateRequest(order);
                    await _execution.SubmitAsync(TradeIntent.FromOrderRequest(order));
                    success++;
                }
                catch (Exception batchEx)
                {
                    MessageBox.Show($"订单 {order.Symbol} 执行失败：{batchEx.Message}", "批量下单", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            _batchOrders.Clear();
            StatusText.Text = $"状态：批量下单完成（成功 {success} / {total}）";
            MessageBox.Show($"批量下单完成，成功 {success} 条。", "批量下单", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：批量下单失败";
            MessageBox.Show(ex.Message, "批量下单", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is OrderRequest req)
        {
            _batchOrders.Remove(req);
            StatusText.Text = $"状态：已移除一条批量订单（剩余 {_batchOrders.Count}）";
        }
    }

    private void ClearBatch_Click(object sender, RoutedEventArgs e)
    {
        _batchOrders.Clear();
        StatusText.Text = "状态：批量列表已清空";
    }

    private void RefreshSymbols_Click(object sender, RoutedEventArgs e) => _ = LoadSymbolsAsync();

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var type = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var requiresPrice = type is "Limit" or "StopLossLimit" or "TakeProfitLimit";
        PriceBox.IsEnabled = requiresPrice;
        StopPriceBox.IsEnabled = type is "StopLoss" or "StopLossLimit" or "TakeProfit" or "TakeProfitLimit";
    }

    private static void ValidateRequest(OrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            throw new InvalidOperationException("请填写交易对");
        if (request.Quantity <= 0)
            throw new InvalidOperationException("数量需大于 0");
        if (request.Type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit)
        {
            if (request.Price <= 0)
                throw new InvalidOperationException("限价单需要填写价格");
        }
        if (request.Type is OrderType.StopLoss or OrderType.StopLossLimit or OrderType.TakeProfit or OrderType.TakeProfitLimit)
        {
            if (request.StopPrice <= 0)
                throw new InvalidOperationException("触发单需要填写触发价");
        }
    }
}
