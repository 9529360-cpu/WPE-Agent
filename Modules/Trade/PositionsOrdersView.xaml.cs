using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Modules.Trade;

public partial class PositionsOrdersView : UserControl
{
    private readonly ObservableCollection<PositionSnapshot> _positions = new();
    private readonly ObservableCollection<OrderResponse> _orders = new();
    private readonly ObservableCollection<RecentExecutionLedgerRow> _trades = new();
    private readonly IAccountReader _accountReader = ServiceLocator.AccountReader;
    private readonly IOrderQueryReader _orderQueries = ServiceLocator.OrderQueries;
    private readonly AgentSqliteStore _ledger = new();

    public PositionsOrdersView()
    {
        InitializeComponent();
        PositionsGrid.ItemsSource = _positions;
        OrdersGrid.ItemsSource = _orders;
        TradesGrid.ItemsSource = _trades;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "状态：获取账户数据...";
            var positions = await _accountReader.GetPositionsAsync();
            var orders = await _orderQueries.GetOpenOrdersAsync();
            var trades = await _ledger.GetRecentExecutionLedgerAsync(100,default);

            _positions.Clear();
            foreach (var p in positions)
                _positions.Add(p);

            _orders.Clear();
            foreach (var o in orders)
                _orders.Add(o);

            _trades.Clear();
            foreach (var t in trades)
                _trades.Add(t);

            StatusText.Text = $"状态：持仓 {_positions.Count} · 未完成订单 {_orders.Count} · 最近成交 {_trades.Count}（含手续费）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：刷新失败";
            MessageBox.Show(ex.Message, "持仓与订单", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}
