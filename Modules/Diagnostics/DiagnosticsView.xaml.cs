using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Diagnostics;

public partial class DiagnosticsView : UserControl
{
    private readonly ObservableCollection<DiagnosticsEventItem> _events = new();

    public DiagnosticsView()
    {
        InitializeComponent();
        EventsGrid.ItemsSource = _events;
        Refresh();
        _ = StartEventListenerAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"运行目录: {AppContext.BaseDirectory}");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"环境: {ServiceLocator.Settings.Environment}");
        sb.AppendLine($"自动重连: {ServiceLocator.Settings.AutoReconnect}");
        EnvironmentText.Text = sb.ToString();

        var logPath = AppDataPaths.LogFile("terminal.log");
        if (File.Exists(logPath))
            LogBox.Text = File.ReadAllText(logPath);
        else
            LogBox.Text = "暂无日志文件（Data/terminal.log）";

        StatusText.Text = "状态：诊断信息已刷新";
    }

    private async System.Threading.Tasks.Task StartEventListenerAsync()
    {
        var reader = ServiceLocator.MonitoringHub is Monitoring.InMemoryTradeMonitoringHub hub
            ? hub.Reader
            : null;
        if (reader is null)
            return;

        await foreach (var evt in reader.ReadAllAsync())
        {
            Dispatcher.Invoke(() =>
            {
                if (evt.Signal is TradeSignal s)
                {
                    _events.Insert(0, new DiagnosticsEventItem
                    {
                        Timestamp = System.DateTime.Now.ToString("HH:mm:ss"),
                        Type = "Signal",
                        Symbol = s.Symbol,
                        Message = $"{s.Action.ActionType} qty={s.Action.Quantity} reason={s.Action.Reason}"
                    });
                }
                else if (evt.Position is PositionSnapshot p)
                {
                    _events.Insert(0, new DiagnosticsEventItem
                    {
                        Timestamp = System.DateTime.Now.ToString("HH:mm:ss"),
                        Type = "Position",
                        Symbol = p.Symbol,
                        Message = $"pos={p.Quantity} entry={p.EntryPrice} pnl={p.UnrealizedPnl}"
                    });
                }
                else if (evt.Risk is PortfolioRiskSnapshot r)
                {
                    _events.Insert(0, new DiagnosticsEventItem
                    {
                        Timestamp = System.DateTime.Now.ToString("HH:mm:ss"),
                        Type = "Risk",
                        Symbol = string.Empty,
                        Message = $"wallet={r.WalletBalance} dd≈{r.UnrealizedPnl} lev={r.GrossLeverage}"
                    });
                }
                else if (evt.Metrics is StrategyPerformanceSnapshot m)
                {
                    _events.Insert(0, new DiagnosticsEventItem
                    {
                        Timestamp = System.DateTime.Now.ToString("HH:mm:ss"),
                        Type = "Metrics",
                        Symbol = m.Strategy,
                        Message = $"equity={m.Equity} dd={m.Drawdown} win={m.WinRate:P1}"
                    });
                }

                while (_events.Count > 200)
                    _events.RemoveAt(_events.Count - 1);
            });
        }
    }
}

public class DiagnosticsEventItem
{
    public string Timestamp { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
