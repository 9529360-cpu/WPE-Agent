using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Account;

public partial class AccountFundsView : UserControl
{
    private readonly ObservableCollection<AccountBalance> _balances = new();
    private readonly BinanceApiClient _api = ServiceLocator.Api;

    public AccountFundsView()
    {
        InitializeComponent();
        BalanceGrid.ItemsSource = _balances;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "状态：获取资产余额...";
            var balances = await _api.GetAccountBalancesAsync();
            _balances.Clear();
            foreach (var balance in balances.Where(b => b.WalletBalance != 0 || b.AvailableBalance != 0))
                _balances.Add(balance);

            var totalWallet = _balances.Sum(b => b.WalletBalance);
            var totalMargin = _balances.Sum(b => b.MarginBalance);
            StatusText.Text = $"状态：资产 {totalWallet.ToString("F2", CultureInfo.InvariantCulture)} · 保证金 {totalMargin.ToString("F2", CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：刷新失败";
            MessageBox.Show(ex.Message, "账户资金", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}
