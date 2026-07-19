using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Localization;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人;

public partial class MainWindow : Window
{
    private static LocalizationService I18n => LocalizationService.Current;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<string> _thoughts = [];
    private readonly List<double> _equity = [];
    private Point _dragStart;
    private Border? _dragCard;
    private string _activePage = "HOME";
    private readonly SolidColorBrush _cyan = new(Color.FromRgb(24, 216, 242));
    private readonly SolidColorBrush _green = new(Color.FromRgb(53, 230, 160));
    private readonly SolidColorBrush _orange = new(Color.FromRgb(255, 170, 76));
    private readonly SolidColorBrush _red = new(Color.FromRgb(255, 83, 112));
    private readonly string _user;
    private readonly AgentSettingsStore _settingsStore = new();
    private readonly AccessReadinessService _readiness = new();

    public MainWindow(string user="")
    {
        InitializeComponent();
        _user=user;
        ServiceLocator.SystemState.LoggedInUser=user;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(I18n.Culture.IetfLanguageTag);
        ThoughtList.ItemsSource = _thoughts;
        AutoTradingAgent.StateChanged += OnAgentStateChanged;
        I18n.LanguageChanged += OnLanguageChanged;
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        AddThought(I18n.T("Thought.Initialized"));
        UpdateInterface();
        Loaded+=async(_,_)=>await InitializeAndStartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        AutoTradingAgent.StateChanged -= OnAgentStateChanged;
        I18n.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private async void StartAgent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartButton.IsEnabled=false;
            var settings=_settingsStore.Load();
            var access=await _readiness.CheckAsync(settings);
            ApplyAccess(access,settings);
            if(!settings.SetupCompleted||!access.Ready)
            {
                MessageBox.Show(I18n.T("Access.StartBlocked",access.Summary),I18n.T("Dialog.InitFailed"),MessageBoxButton.OK,MessageBoxImage.Warning);
                var setup=new SetupWindow(_user,true);setup.ShowDialog();UpdateInterface();return;
            }
            AutoTradingAgent.StartDefault();
            AddThought(I18n.T("Thought.Initializing"));
            UpdateInterface();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, I18n.T("Dialog.InitFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { if(!AutoTradingAgent.IsRunning)StartButton.IsEnabled=true; }
    }

    private void Settings_Click(object sender,RoutedEventArgs e){var setup=new SetupWindow(_user,true);setup.ShowDialog();_ = RefreshAccessAsync();}
    private async Task InitializeAndStartAsync()
    {
        await RefreshAccessAsync();
        var settings=_settingsStore.Load();
        var access=await _readiness.CheckAsync(settings);
        settings.LastAccessCheckAtUtc=access.CheckedAtUtc;
        _settingsStore.Save(settings);
        if(settings.SetupCompleted&&access.Ready&&!AutoTradingAgent.IsRunning)
        {
            try{AutoTradingAgent.StartDefault();AddThought(I18n.T("Thought.Initializing"));}
            catch(Exception ex){AddThought(ex.Message);}
        }
        UpdateInterface();
    }
    private async Task RefreshAccessAsync(){try{var settings=_settingsStore.Load();var report=await _readiness.CheckAsync(settings);ApplyAccess(report,settings);UpdateInterface();}catch{}}
    private static void ApplyAccess(AccessReadinessReport report,AgentSettings settings){var state=ServiceLocator.SystemState;state.ExchangeConnected=report.Checks.Any(x=>x.Key=="exchange"&&x.Passed);state.BrainConnected=report.Checks.Any(x=>x.Key=="brain"&&x.Passed);state.ApiTradePermission=report.Checks.Any(x=>x.Key=="trade_permission"&&x.Passed);state.RiskReady=report.Checks.Any(x=>x.Key=="risk"&&x.Passed);state.LastAccessCheckAtUtc=report.CheckedAtUtc;state.LoggedInUser=settings.ActiveUser;}

    private void PauseAgent_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceLocator.SystemState.Status == AgentStatus.Paused)
        {
            AutoTradingAgent.Resume();
            AddThought(I18n.T("Thought.Resumed"));
        }
        else
        {
            AutoTradingAgent.Pause();
            AddThought(I18n.T("Thought.Paused"));
        }
        UpdateInterface();
    }

    private async void StopAgent_Click(object sender, RoutedEventArgs e)
    {
        await AutoTradingAgent.StopAsync();
        AddThought(I18n.T("Thought.Stopped"));
        UpdateInterface();
    }

    private async void EmergencyClose_Click(object sender, RoutedEventArgs e)
    {
        if (!AutoTradingAgent.CanEmergencyClose)
        {
            MessageBox.Show(I18n.T("Dialog.ExchangeInactive"), I18n.T("Dialog.EmergencyTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(I18n.T("Dialog.EmergencyConfirm"), I18n.T("Dialog.EmergencyConfirmTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        EmergencyButton.IsEnabled = false;
        try
        {
            var result = await AutoTradingAgent.EmergencyCloseAllAsync();
            AddThought(I18n.T("Dialog.EmergencyDone", result));
        }
        catch (Exception ex)
        {
            AddThought(I18n.T("Dialog.EmergencyDegraded", ex.Message));
            MessageBox.Show(ex.Message, I18n.T("Dialog.EmergencyTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EmergencyButton.IsEnabled = true;
            UpdateInterface();
        }
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LanguageButton };
        foreach (var language in I18n.AvailableLanguages)
        {
            var item = new MenuItem { Header = language.DisplayName, IsCheckable = true, IsChecked = language.Code == I18n.CurrentCode, Tag = language.Code };
            item.Click += (_, _) => I18n.SetLanguage((string)item.Tag);
            menu.Items.Add(item);
        }
        LanguageButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnLanguageChanged()
    {
        Dispatcher.Invoke(() =>
        {
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(I18n.Culture.IetfLanguageTag);
            _thoughts.Clear();
            AddThought(I18n.T("Thought.LanguageChanged", I18n.CurrentLanguage.DisplayName));
            AddThought(BuildThought());
            UpdateInterface();
            ShowPage(_activePage);
        });
    }

    private void OnAgentStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        AddThought(BuildThought());
        UpdateInterface();
    }, DispatcherPriority.Background);

    private string BuildThought()
    {
        var state = ServiceLocator.SystemState;
        var key = state.WorkflowNode switch
        {
            "OBSERVATION" => "Thought.Observation", "PLANNER" => "Thought.Planner", "CRITIC" or "REVIEWER" => "Thought.Critic",
            "RISK" => "Thought.Risk", "EXECUTION" => "Thought.Execution", "REFLECTION" => "Thought.Reflection", _ => string.Empty
        };
        return key.Length > 0 ? I18n.T(key) : state.LastMessage;
    }

    private void AddThought(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || (_thoughts.Count > 0 && _thoughts[^1].EndsWith(text, StringComparison.Ordinal))) return;
        _thoughts.Add($"{System.DateTime.Now.ToString("T", I18n.Culture)}  {text}");
        while (_thoughts.Count > 7) _thoughts.RemoveAt(0);
    }

    private void UpdateInterface()
    {
        var state = ServiceLocator.SystemState;
        var running = AutoTradingAgent.IsRunning;
        StartButton.Content = I18n.T(running ? "Command.Running" : "Command.Initialize");
        StartButton.IsEnabled = !running;
        PauseButton.Content = I18n.T(state.Status == AgentStatus.Paused ? "Command.Resume" : "Command.Pause");
        PauseButton.IsEnabled = running;
        StopButton.IsEnabled = running;
        EmergencyButton.IsEnabled = AutoTradingAgent.CanEmergencyClose;
        AiStatusText.Text = I18n.T(running ? "Status.AiOnline" : "Status.AiStandby");
        AiDot.Fill = running ? _green : _orange;
        BrainStatusText.Text = state.BrainConnected ? I18n.T("Status.BrainConnected",string.IsNullOrWhiteSpace(state.BrainName)?_settingsStore.Load().ActiveBrain:state.BrainName.ToUpperInvariant()) : I18n.T("Status.BrainOffline");BrainStatusText.Foreground=state.BrainConnected?_green:_red;
        UserStatusText.Text=I18n.T("Access.UserOnline",string.IsNullOrWhiteSpace(state.LoggedInUser)?_user:state.LoggedInUser);
        ExchangeStatusText.Text=I18n.T(state.ExchangeConnected?"Access.BinanceConnected":"Access.BinanceOffline");ExchangeStatusText.Foreground=state.ExchangeConnected?_green:_red;
        PermissionStatusText.Text=I18n.T(state.ApiTradePermission?"Access.PermissionReady":"Access.PermissionBlocked");PermissionStatusText.Foreground=state.ApiTradePermission?_green:_red;
         RiskReadyStatusText.Text=I18n.T(state.RiskReady?"Access.RiskReady":"Access.RiskBlocked");RiskReadyStatusText.Foreground=state.RiskReady?_green:_red;
         var runtimeHealthy=running&&state.RuntimeHeartbeatAtUtc is not null&&DateTime.UtcNow-state.RuntimeHeartbeatAtUtc<TimeSpan.FromSeconds(15);
         RuntimeStatusText.Text=I18n.T(runtimeHealthy?"Status.RuntimeHealthy":running?"Status.RuntimeDegraded":"Status.RuntimeStandby");RuntimeStatusText.Foreground=runtimeHealthy?_green:running?_red:_orange;RuntimeDot.Fill=RuntimeStatusText.Foreground;RuntimeStatusText.ToolTip=$"Run {ShortId(state.RuntimeRunId)} · {state.RuntimeRecoveryStatus} · #{state.RuntimeEventSequence}";
         StrategyStatusText.Text=string.IsNullOrWhiteSpace(state.StrategySummary)?I18n.T("Status.StrategyStandby"):$"{state.StrategyStatus} · {state.StrategySummary}";StrategyStatusText.ToolTip=$"{state.StrategyCandidates} local strategy candidates";
        StageText.Text = " " + I18n.T(state.SkillStageKey);
        LastDecisionText.Text = " " + state.LastDecision.ToUpperInvariant();
        LastDecisionText.Foreground = state.LastDecision.Contains("Open", StringComparison.OrdinalIgnoreCase) ? _green : _orange;
        RegimeText.Text = " " + state.MarketRegime;
        ReflectionText.Text = " " + (state.ReflectionStatus == "EXPERIENCE COMMITTED" ? I18n.T("Agent.ExperienceCommitted") : I18n.T("Status.MemoryLoaded"));
        ConfidenceGauge.Value = state.BrainConfidence;
        DecisionGauge.Value = Math.Abs(state.DecisionScore);
        RiskGauge.Value = state.RiskLoad;
        ConflictGauge.Value = state.ConflictRate;
        ThinkingBar.Value = state.ThinkingProgress;
        ThinkingPercent.Text = $"{state.ThinkingProgress}%";
        MarketStateText.Text = I18n.T("Market.Regime", state.MarketRegime);
        BtcPriceText.Text = state.BtcPrice > 0 ? I18n.Number(state.BtcPrice) : "--";
        EthPriceText.Text = state.EthPrice > 0 ? I18n.Number(state.EthPrice) : "--";
        SetMarket(BtcDirection, BtcMetaText, state.BtcTrend, state.BtcRsi);
        SetMarket(EthDirection, EthMetaText, state.EthTrend, state.EthRsi);
        AccountSideText.Text = $"{I18n.Number(state.WalletBalance)} USDT";
        EquityText.Text = I18n.Number(state.WalletBalance);
        AvailableText.Text = I18n.Number(state.AvailableBalance);
        PositionText.Text = Math.Abs(state.PositionQuantity) < .0000001m ? I18n.T("Portfolio.Flat") : I18n.Number(state.PositionQuantity, 4);
        EvidenceText.Text = $"{state.EvidenceCompleteness}/100";
        var exposure = state.WalletBalance > 0 ? (double)Math.Min(1, Math.Abs(state.PositionQuantity * state.CurrentPrice) / state.WalletBalance) : 0;
        ExposureText.Text = I18n.T("Portfolio.Exposure", exposure);
        ExposureText.Foreground = exposure > .5 ? _red : _green;
        if (state.WalletBalance > 0 && (_equity.Count == 0 || Math.Abs(_equity[^1] - (double)state.WalletBalance) > .0001))
        {
            _equity.Add((double)state.WalletBalance);
            while (_equity.Count > 80) _equity.RemoveAt(0);
            EquityChart.Points = _equity.ToArray();
        }
        SetSignals(state.SignalContributions);
        HighlightWorkflow(state.WorkflowNode);
        WorkflowStateText.Text = state.WorkflowNode == "IDLE" ? I18n.T("Workflow.Idle") : state.WorkflowNode;
        var llm = LlmRequestGovernor.Shared.GetTodaySnapshot();
        LlmModeText.Text = llm.Calls == 0 ? "LOCAL" : llm.TopProvider.ToUpperInvariant();
        LlmModeText.Foreground = llm.BudgetBlocks > 0 ? _red : _green;
        LlmCallsText.Text = llm.Calls.ToString("N0", I18n.Culture);
        LlmTokensText.Text = llm.Tokens.ToString("N0", I18n.Culture);
        LlmCostText.Text = llm.CostUsd.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        LlmCacheText.Text = llm.CacheHits.ToString("N0", I18n.Culture);
        LlmBlockedText.Text = llm.BudgetBlocks.ToString("N0", I18n.Culture);
        DashboardPage.BeginAnimation(OpacityProperty, new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(300)));
        UpdateClock();
    }

    private void SetMarket(TextBlock direction, TextBlock meta, double trend, double rsi)
    {
        var key = trend > .02 ? "Market.Bullish" : trend < -.02 ? "Market.Bearish" : "Market.Neutral";
        direction.Text = I18n.T(key);
        direction.Foreground = trend > .02 ? _green : trend < -.02 ? _red : _orange;
        meta.Text = I18n.T("Market.Meta", rsi, trend);
    }

    private void SetSignals(IReadOnlyDictionary<string, double> signals)
    {
        double Value(params string[] keys) => signals.FirstOrDefault(x => keys.Any(k => x.Key.Contains(k, StringComparison.OrdinalIgnoreCase))).Value;
        SetSignal(Signal15m, Signal15mValue, Value("trend_15m"));
        SetSignal(Signal1h, Signal1hValue, Value("trend_1h"));
        SetSignal(Signal4h, Signal4hValue, Value("trend_4h"));
        SetSignal(SignalRsi, SignalRsiValue, Value("RSI", "rsi"));
        SetSignal(SignalFlow, SignalFlowValue, Value("order_flow"));
        SetSignal(SignalFunding, SignalFundingValue, Value("funding"));
        SetSignal(SignalOi, SignalOiValue, Value("crowd", "oi"));
        SetSignal(SignalNews, SignalNewsValue, Value("basis"));
    }

    private void SetSignal(ProgressBar bar, TextBlock text, double value)
    {
        bar.Value = Math.Min(100, Math.Abs(value) * 350);
        bar.Foreground = value > .0001 ? _green : value < -.0001 ? _red : _cyan;
        text.Text = value.ToString("+0.00;-0.00;0.00", I18n.Culture);
        text.Foreground = bar.Foreground;
    }

    private void HighlightWorkflow(string node)
    {
        var map = new Dictionary<string, Border> { ["OBSERVATION"] = NodeObservation, ["PLANNER"] = NodePlanner, ["CRITIC"] = NodeCritic,
            ["REVIEWER"] = NodeReviewer, ["RISK"] = NodeRisk, ["EXECUTION"] = NodeExecution, ["REFLECTION"] = NodeReflection };
        foreach (var item in map)
        {
            var active = item.Key == node || (node == "CRITIC" && item.Key == "REVIEWER");
            item.Value.Background = new SolidColorBrush(active ? Color.FromRgb(13, 55, 68) : Color.FromRgb(13, 25, 34));
            item.Value.BorderBrush = active ? _cyan : new SolidColorBrush(Color.FromRgb(40, 65, 79));
            item.Value.Effect = active ? new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(24, 216, 242), BlurRadius = 18, ShadowDepth = 0, Opacity = .45 } : null;
        }
    }

    private void UpdateClock()
    {
        ClockText.Text = I18n.DateTime(System.DateTime.Now);
        var remain = ServiceLocator.SystemState.NextCycleAtUtc - System.DateTime.UtcNow;
        CycleText.Text = remain is { Ticks: > 0 } ? remain.Value.ToString("mm\\:ss") : "--:--";
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) ShowPage(tag);
    }

    private async void ShowPage(string tag)
    {
        _activePage = tag;
        if (tag == "HOME")
        {
            DashboardPage.Visibility = Visibility.Visible;
            SecondaryPage.Visibility = Visibility.Collapsed;
            return;
        }
        DashboardPage.Visibility = Visibility.Collapsed;
        SecondaryPage.Visibility = Visibility.Visible;
        var state = ServiceLocator.SystemState;
        var memory = tag == "BRAIN" ? await new AgentSqliteStore().GetMemoryExplorerAsync(40, CancellationToken.None) : Array.Empty<string>();
        (SecondaryTitle.Text, SecondarySubtitle.Text, SecondaryContent.Text) = tag switch
        {
            "MARKET" => (I18n.T("Secondary.MarketTitle"), I18n.T("Secondary.MarketSubtitle"), $"{I18n.T("Audit.Realtime")}  {state.RealtimeStatus}\n{I18n.T("Audit.FullTextNews")}  {state.NewsFullTextDocuments}\n{I18n.T("Audit.NewsSources")}  {state.NewsCorroboratingSources}\n\n{state.MarketSummary}\n\n{I18n.T("Signal.Title")}\n{state.DecisionDiagnostics}\n\n{state.NewsSummary}"),
            "POSITIONS" => (I18n.T("Secondary.PositionsTitle"), I18n.T("Secondary.PositionsSubtitle"), $"{I18n.T("Portfolio.Equity")}  {I18n.Number(state.WalletBalance)} USDT\n{I18n.T("Portfolio.Available")}  {I18n.Number(state.AvailableBalance)} USDT\n\n{state.PositionsSummary}\n\n{state.OrdersSummary}"),
            "RISK" => (I18n.T("Secondary.RiskTitle"), I18n.T("Secondary.RiskSubtitle"), $"{state.RiskSummary}\n{state.PortfolioRiskSummary}\n\n{I18n.T("Audit.DataQuality")}  {state.DataQualityScore}/100\n{I18n.T("Audit.Liquidity")}  {state.LiquidityScore:0}%\n{I18n.T("Audit.Volatility")}  {state.VolatilityPercent:0.00}%\n{I18n.T("Audit.Research")}  {state.ResearchScore:0}%\n{I18n.T("Audit.HistoricalCoverage")}  {state.HistoricalCoverageDays} d\n{I18n.T("Audit.VaR99")}  {state.PortfolioVaR99:0.00}%\n{I18n.T("Audit.CVaR99")}  {state.PortfolioCVaR99:0.00}%\n{I18n.T("Audit.Concentration")}  {state.PortfolioConcentration:0.0}%\n{I18n.T("Audit.Correlation")}  {state.PortfolioCorrelation:0.00}\n{I18n.T("Brain.RiskLoad")}  {state.RiskLoad:0}%\n{I18n.T("Brain.ConflictRate")}  {state.ConflictRate:0}%\n\n{I18n.T("Audit.Reviewer")}  {AuditStatus(state.ReviewerStatus)}\n{I18n.T("Audit.RiskApproval")}  {AuditStatus(state.RiskApprovalStatus)}\n{I18n.T("Audit.ExecutionApproval")}  {AuditStatus(state.ExecutionApprovalStatus)}\n{I18n.T("Audit.CircuitBreaker")}  {I18n.T(state.CircuitBreakerActive?"Audit.Status.ACTIVE":"Audit.Status.CLEAR")}\n\n{I18n.T("Audit.Missing")}\n{state.MissingConditions}"),
            "BRAIN" => (I18n.T("Secondary.BrainTitle"), I18n.T("Secondary.BrainSubtitle"), $"Brain  {state.BrainName}\n{state.Status}\n{I18n.T("Brain.Confidence")}  {state.BrainConfidence:0}%\n{I18n.T("Label.Market")}  {state.MarketRegime}\n{I18n.T("Label.Reflection")}  {state.ReflectionStatus}\n\n{I18n.T("Audit.Entry")}  {I18n.Number(state.PlannedEntry)}\n{I18n.T("Audit.Stop")}  {I18n.Number(state.PlannedStop)}\n{I18n.T("Audit.TakeProfit")}  {I18n.Number(state.PlannedTakeProfit)}\n{I18n.T("Audit.Quantity")}  {I18n.Number(state.PlannedQuantity)}\n{I18n.T("Audit.RiskReward")}  {state.RiskRewardRatio:0.00}\n\n{state.DecisionAuditSummary}\n\n{state.LastReason}\n\nMEMORY EXPLORER\n{string.Join("\n\n", memory)}\n\n{PluginCenterSnapshot.Build(_settingsStore.Load())}"),
            "LOGS" => (I18n.T("Secondary.EventsTitle"), I18n.T("Secondary.EventsSubtitle"), await ReadRuntimeEventsAsync()),
            _ => (I18n.T("Secondary.Module"), I18n.T("Secondary.NoTelemetry"), state.LastMessage)
        };
    }

    private static string AuditStatus(string status)=>I18n.T("Audit.Status."+status);
    private static string ShortId(string value)=>string.IsNullOrWhiteSpace(value)?"--":value[..Math.Min(8,value.Length)];

    private static string ReadLogTail()
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "logs");
            var file = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            return file is null ? I18n.T("Log.Empty") : string.Join(Environment.NewLine, File.ReadLines(file).TakeLast(180));
        }
        catch (Exception ex) { return I18n.T("Log.Unavailable", ex.Message); }
    }

    private static async Task<string> ReadRuntimeEventsAsync()
    {
        try
        {
            var events = await new AgentSqliteStore().GetRecentRuntimeEventsAsync(80, CancellationToken.None);
            return events.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, events) : ReadLogTail();
        }
        catch { return ReadLogTail(); }
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e) { _dragStart = e.GetPosition(null); _dragCard = sender as Border; }
    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCard is null) return;
        var point = e.GetPosition(null);
        if (Math.Abs(point.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            DragDrop.DoDragDrop(_dragCard, _dragCard, DragDropEffects.Move);
    }
    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border target || e.Data.GetData(typeof(Border)) is not Border source || target == source) return;
        var from = DashboardCards.Children.IndexOf(source);
        var to = DashboardCards.Children.IndexOf(target);
        if (from < 0 || to < 0) return;
        DashboardCards.Children.RemoveAt(from);
        DashboardCards.Children.Insert(from < to ? to - 1 : to, source);
    }
}
