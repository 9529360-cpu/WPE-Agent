using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;

namespace 币安量化机器人.Modules.Strategy;

public partial class SettingsView : UserControl
{
    private readonly ObservableCollection<StrategyParameterRow> _parameters = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly string _configDirectory = Path.Combine(AppContext.BaseDirectory, "Configs");

    public SettingsView()
    {
        InitializeComponent();
        ParametersGrid.ItemsSource = _parameters;
        LoadDefaultParameters();
    }

    private void LoadDefaultParameters()
    {
        _parameters.Clear();
        _parameters.Add(new StrategyParameterRow { Parameter = "ma_fast", Value = "21", Description = "快速均线周期" });
        _parameters.Add(new StrategyParameterRow { Parameter = "ma_slow", Value = "55", Description = "慢速均线周期" });
        _parameters.Add(new StrategyParameterRow { Parameter = "atr_period", Value = "14", Description = "ATR 风险控制" });
        _parameters.Add(new StrategyParameterRow { Parameter = "risk_per_trade", Value = "0.01", Description = "单笔风险占资金比例" });

        StrategyNameBox.Text = "DualMA-Alpha";
        SymbolBox.Text = "BTCUSDT";
        TimeframeBox.SelectedIndex = 3; // 1h
        CapitalBox.Text = "10000";
        LeverageSlider.Value = 3;
        MaxPositionsBox.Text = "3";
        StopLossBox.Text = "1.5";
        TakeProfitBox.Text = "3";
        VenueBox.SelectedIndex = 0;
        AutoTradeToggle.IsChecked = true;
        HedgeToggle.IsChecked = false;

        StatusText.Text = "状态：已加载默认参数";
    }

    private void LoadDefaults_Click(object sender, RoutedEventArgs e) => LoadDefaultParameters();

    private void AddParameter_Click(object sender, RoutedEventArgs e)
    {
        _parameters.Add(new StrategyParameterRow
        {
            Parameter = $"param_{_parameters.Count + 1}",
            Value = "0",
            Description = "自定义参数"
        });
    }

    private StrategyConfig BuildConfig()
    {
        double.TryParse(CapitalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double capital);
        int.TryParse(MaxPositionsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxPositions);
        double.TryParse(StopLossBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double stopLoss);
        double.TryParse(TakeProfitBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double takeProfit);

        var timeframe = (TimeframeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var venue = (VenueBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        return new StrategyConfig
        {
            StrategyName = StrategyNameBox.Text.Trim(),
            Symbol = SymbolBox.Text.Trim(),
            Timeframe = timeframe,
            Capital = capital,
            Leverage = (int)Math.Round(LeverageSlider.Value),
            MaxPositions = maxPositions,
            StopLossPercent = stopLoss,
            TakeProfitPercent = takeProfit,
            EnableAutoTrade = AutoTradeToggle.IsChecked ?? false,
            EnableHedgeMode = HedgeToggle.IsChecked ?? false,
            ExecutionVenue = venue,
            Parameters = _parameters
                .Where(p => !string.IsNullOrWhiteSpace(p.Parameter))
                .Select(p => new StrategyParameterRow
                {
                    Parameter = p.Parameter,
                    Value = p.Value,
                    Description = p.Description
                })
                .ToList()
        };
    }

    private static void ValidateConfig(StrategyConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.StrategyName))
            throw new InvalidOperationException("策略名称不能为空");
        if (string.IsNullOrWhiteSpace(config.Symbol))
            throw new InvalidOperationException("交易对不能为空");
        if (string.IsNullOrWhiteSpace(config.Timeframe))
            throw new InvalidOperationException("请选择时间框架");
        if (config.Capital <= 0)
            throw new InvalidOperationException("投入资金需大于 0");
        if (config.StopLossPercent <= 0)
            throw new InvalidOperationException("止损百分比需大于 0");
        if (config.TakeProfitPercent <= 0)
            throw new InvalidOperationException("止盈百分比需大于 0");
        if (config.Parameters.Count == 0)
            throw new InvalidOperationException("至少保留一个策略参数");
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfig();
            ValidateConfig(config);
            StatusText.Text = "状态：配置校验通过";
            MessageBox.Show("策略配置已校验，可导出或提交到后端。", "保存配置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：保存失败";
            MessageBox.Show(ex.Message, "保存配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfig();
            ValidateConfig(config);

            Directory.CreateDirectory(_configDirectory);
            var fileName = $"{SanitizeFileName(config.StrategyName)}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var path = Path.Combine(_configDirectory, fileName);
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);

            StatusText.Text = $"状态：已导出 {fileName}";
            MessageBox.Show($"配置已导出到 {path}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：导出失败";
            MessageBox.Show(ex.Message, "导出配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var ch in string.IsNullOrWhiteSpace(name) ? "strategy" : name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
