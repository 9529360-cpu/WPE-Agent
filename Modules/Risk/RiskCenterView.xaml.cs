using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Risk;

public partial class RiskCenterView : UserControl
{
    private readonly ObservableCollection<RiskMetrics> _metrics = new();
    private readonly ObservableCollection<StressTestResult> _stress = new();
    private readonly RiskEngine _riskEngine = ServiceLocator.Risk;
    private readonly BinanceApiClient _api = ServiceLocator.Api;

    public RiskCenterView()
    {
        InitializeComponent();
        RiskGrid.ItemsSource = _metrics;
        StressGrid.ItemsSource = _stress;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var symbols = await _api.GetMiniTickersAsync();
            BenchmarkBox.ItemsSource = symbols.Select(s => s.Symbol).OrderBy(s => s).ToList();
            if (BenchmarkBox.Items.Count > 0)
                BenchmarkBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：加载基准失败";
            MessageBox.Show(ex.Message, "风险中心", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Evaluate_Click(object sender, RoutedEventArgs e)
    {
        if (BenchmarkBox.SelectedItem is not string benchmark)
        {
            MessageBox.Show("请选择风控基准合约", "风险中心", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            StatusText.Text = "状态：正在计算 VaR / CVaR";
            var positions = await _api.GetPositionsAsync();
            var rules = new[]
            {
                new RiskRule { Name = "VaR 限制", Threshold = 500, Comparator = ">", Action = "减少仓位", IsActive = true },
                new RiskRule { Name = "杠杆限制", Threshold = 8, Comparator = "LEV>", Action = "降低杠杆", IsActive = true }
            };
            var report = await _riskEngine.GenerateReportAsync(positions, rules, benchmark, 10_000m);

            _metrics.Clear();
            foreach (var metric in report.Metrics)
                _metrics.Add(metric);

            _stress.Clear();
            foreach (var scenario in report.StressTests)
                _stress.Add(scenario);

            RuleText.Text = report.BreachedRules.Any()
                ? string.Join("; ", report.BreachedRules.Select(r => $"{r.Name} 触发，建议：{r.Action}"))
                : "所有风控规则均正常";

            StatusText.Text = $"状态：风险评估完成，共 {_metrics.Count} 条持仓";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：评估失败";
            MessageBox.Show(ex.Message, "风险评估", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
