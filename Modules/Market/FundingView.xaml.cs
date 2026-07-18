using ScottPlot;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using 币安量化机器人.Models;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Market;

public partial class FundingView : UserControl
{
    private readonly ObservableCollection<FundingRateSnapshot> _items = new();
    private readonly BinanceApiClient _api = ServiceLocator.Api;
    private readonly DataCacheService _cache = ServiceLocator.Cache;
    private ICollectionView? _view;
    private bool _initialized;

    public FundingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            StatusText.Text = "状态：正在同步 Binance 资金费率...";
            var data = await _api.GetFundingRatesAsync(limit: 96, cancellationToken: CancellationToken.None);
            await _cache.SaveFundingRatesAsync(data);

            _items.Clear();
            foreach (var item in data)
                _items.Add(item);

            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = FilterFunding;
            FundingGrid.ItemsSource = _view;
            _view.Refresh();

            StatusText.Text = $"状态：已加载 {_items.Count} 条合约 · 数据来源 Binance";
            if (_items.Count > 0)
                FundingGrid.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：加载失败";
            MessageBox.Show(ex.Message, "资金费率", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool FilterFunding(object obj)
    {
        if (obj is not FundingRateSnapshot snapshot)
            return false;

        string keyword = SearchBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(keyword))
            return true;

        return snapshot.Symbol.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || snapshot.Pair.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || snapshot.BaseAsset.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || snapshot.QuoteAsset.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        StatusText.Text = _view is null
            ? "状态：未初始化"
            : $"状态：过滤后剩余 {_view.Cast<object>().Count()} 条";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDataAsync();

    private void FundingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FundingGrid.SelectedItem is FundingRateSnapshot snapshot)
            UpdateDetails(snapshot);
    }

    private void UpdateDetails(FundingRateSnapshot snapshot)
    {
        DetailTitle.Text = $"{snapshot.Pair} · 最新资金率 {snapshot.LastFundingRate:P4}";

        var remaining = snapshot.TimeUntilNextFunding;
        string remainingText = remaining.TotalSeconds <= 0
            ? "已过最近结算窗口"
            : remaining.TotalHours > 24
                ? $"约 {remaining.TotalDays:F1} 天后结算"
                : $"约 {remaining.TotalHours:F1} 小时后结算";

        NextFundingText.Text = $"下一次结算：{snapshot.NextFundingTime:yyyy-MM-dd HH:mm} UTC（{remainingText}）";
        BasisText.Text = $"标记价 {snapshot.MarkPrice:F4}，指数价 {snapshot.IndexPrice:F4}，基差 {snapshot.BasisSpread:F4} USDT";

        ContractText.Text = snapshot.ContractType;
        BasisPctText.Text = snapshot.BasisSpreadPct.ToString("P3");
        IndexPriceText.Text = snapshot.IndexPrice.ToString("F4");
        MarkPriceText.Text = snapshot.MarkPrice.ToString("F4");

        var avg = snapshot.History.Count == 0 ? 0 : snapshot.History.Average(h => h.FundingRate);
        StatusDetailText.Text =
            $"预测资金率 {snapshot.PredictedFundingRate:P4} · 7日均值 {snapshot.Avg7dFundingRate:P4} · 历史均值 {avg:P4} · 未平仓量 {snapshot.OpenInterest:N0} USDT";

        RenderHistory(snapshot);
    }

    private void RenderHistory(FundingRateSnapshot snapshot)
    {
        var history = snapshot.History
            .OrderBy(h => h.Timestamp)
            .ToArray();

        var plt = FundingPlot.Plot;
        plt.Clear();

        if (history.Length == 0)
        {
            plt.Title("暂无历史样本");
            FundingPlot.Refresh();
            return;
        }

        double[] xs = Enumerable.Range(0, history.Length).Select(i => (double)i).ToArray();
        double[] ys = history.Select(h => h.FundingRate).ToArray();

        var scatter = plt.Add.Scatter(xs, ys);
        scatter.LineWidth = 2;
        scatter.MarkerSize = 4;
        scatter.MarkerShape = MarkerShape.FilledCircle;

        plt.Title($"{snapshot.Pair} 资金率走势");
        plt.Axes.Left.Label.Text = "资金率";
        plt.Axes.Bottom.Label.Text = "样本序号";

        FundingPlot.Refresh();
    }
}
