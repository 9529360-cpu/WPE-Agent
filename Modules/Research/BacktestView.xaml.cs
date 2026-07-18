using ScottPlot;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Research;

public partial class BacktestView : UserControl
{
    private readonly BinanceApiClient _api = ServiceLocator.Api;

    public BacktestView()
    {
        InitializeComponent();
        IntervalBox.SelectedIndex = 0;
        _ = LoadSymbolsAsync();
    }

    private async Task LoadSymbolsAsync()
    {
        try
        {
            var tickers = await _api.GetMiniTickersAsync();
            SymbolBox.ItemsSource = tickers.Select(t => t.Symbol).OrderBy(s => s).ToList();
            if (SymbolBox.Items.Count > 0)
                SymbolBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "加载交易对", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RunBacktest_Click(object sender, RoutedEventArgs e)
    {
        if (SymbolBox.SelectedItem is not string symbol)
        {
            MessageBox.Show("请选择交易对", "回测", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var interval = (IntervalBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1h";
            var fast = int.TryParse(FastPeriodBox.Text, out var f) ? Math.Max(2, f) : 9;
            var slow = int.TryParse(SlowPeriodBox.Text, out var s) ? Math.Max(fast + 1, s) : 26;

            var closes = (await _api.GetKlineClosesAsync(symbol, interval, 600)).Select(Convert.ToDouble).ToArray();
            if (closes.Length < slow)
                throw new InvalidOperationException("历史数据不足以运行均线策略");

            var initialCapital = double.TryParse(InitialCapitalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var capital)
                ? Math.Max(100d, capital)
                : 10_000d;

            var fastSma = MovingAverage(closes, fast);
            var slowSma = MovingAverage(closes, slow);
            var equity = Simulate(closes, fastSma, slowSma, initialCapital);

            var plt = EquityPlot.Plot;
            plt.Clear();
            plt.Add.Signal(equity);
            plt.Title($"{symbol} {interval} 双均线策略");
            plt.Axes.Left.Label.Text = "权益";
            plt.Axes.Bottom.Label.Text = "样本";
            EquityPlot.Refresh();

            var pnl = equity.Last() - initialCapital;
            var maxDrawdown = ComputeMaxDrawdown(equity);
            ResultText.Text = $"样本 {closes.Length} 根 · 初始资金 {initialCapital:F2} · 最终收益 {pnl:F2} · 最大回撤 {maxDrawdown:P2}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "回测失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static double[] MovingAverage(double[] values, int period)
    {
        var result = new double[values.Length];
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= period)
                sum -= values[i - period];
            result[i] = i >= period - 1 ? sum / period : double.NaN;
        }
        return result;
    }

    private static double[] Simulate(double[] closes, double[] fast, double[] slow, double initialCapital)
    {
        double position = 0;
        double equity = initialCapital;
        var equityCurve = new double[closes.Length];
        equityCurve[0] = equity;
        for (int i = 1; i < closes.Length; i++)
        {
            if (!double.IsNaN(fast[i]) && !double.IsNaN(slow[i]))
            {
                if (fast[i] > slow[i] && fast[i - 1] <= slow[i - 1])
                    position = 1;
                else if (fast[i] < slow[i] && fast[i - 1] >= slow[i - 1])
                    position = 0;
            }

            var ret = closes[i] / closes[i - 1] - 1;
            equity *= 1 + position * ret;
            equityCurve[i] = equity;
        }
        return equityCurve;
    }

    private static double ComputeMaxDrawdown(double[] equity)
    {
        double peak = equity[0];
        double maxDd = 0;
        for (int i = 1; i < equity.Length; i++)
        {
            peak = Math.Max(peak, equity[i]);
            var dd = peak == 0 ? 0 : 1 - equity[i] / peak;
            maxDd = Math.Max(maxDd, dd);
        }
        return maxDd;
    }
}
