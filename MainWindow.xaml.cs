using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Diagnostics;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Localization;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人;

public partial class MainWindow : Window
{
    public string UserName => _user;
    public Task RefreshAccessForReferenceAsync() => RefreshAccessAsync();
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
    private readonly ThemePreferenceStore _themeStore = new();
    private Button? _themeButton;

    public MainWindow(string user="")
    {
        InitializeComponent();
        StartStatusPulse(AiDot, TimeSpan.FromSeconds(1.8));
        StartStatusPulse(RuntimeDot, TimeSpan.FromSeconds(2.4));
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        InstallPluginNavigationButton();
        InstallThemeButton();
        ApplyTheme(_themeStore.Load());
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

    private static void StartStatusPulse(UIElement element, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = 0.45,
            To = 1.0,
            Duration = new Duration(duration),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowPage("LOGS");
            EventFilterBox.Focus();
            EventFilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void InstallPluginNavigationButton()
    {
        var navigation = FindNavigationPanel(this);
        if (navigation is null || navigation.Children.OfType<Button>().Any(x => Equals(x.Tag, "PLUGINS"))) return;
        var button = new Button
        {
            Style = (Style)FindResource("NavButton"),
            Tag = "PLUGINS",
            Content = I18n.T("Nav.Plugins")
        };
        button.Click += NavButton_Click;
        navigation.Children.Add(button);
        I18n.LanguageChanged += () => button.Content = I18n.T("Nav.Plugins");
    }

    private void InstallThemeButton()
    {
        if (LanguageButton.Parent is not StackPanel toolbar) return;
        _themeButton = new Button { Style = (Style)FindResource("CommandButton"), Content = I18n.T("Theme.Button"), ToolTip = I18n.T("Theme.Tooltip") };
        _themeButton.Click += (_, _) =>
        {
            var next = _themeStore.Load() switch { ThemeMode.Dark => ThemeMode.Light, ThemeMode.Light => ThemeMode.Auto, _ => ThemeMode.Dark };
            _themeStore.Save(next); ApplyTheme(next);
        };
        toolbar.Children.Insert(Math.Max(0, toolbar.Children.Count - 5), _themeButton);
        var referenceButton = new Button
        {
            Style = (Style)FindResource("CommandButton"),
            Content = "Reference UI",
            ToolTip = "Open the original desktop UI reference"
        };
        referenceButton.Click += (_, _) => new WpeAgent.ReferenceUiWindow(BuildRuntimeJson, OpenSetupWindow).Show();
        toolbar.Children.Insert(Math.Max(0, toolbar.Children.Count - 5), referenceButton);
    }

    private void OpenSetupWindow()
    {
        var setup = new SetupWindow(_user, true);
        setup.ShowDialog();
        _ = RefreshAccessAsync();
    }

    public string BuildRuntimeJson()
    {
        ServiceLocator.RuntimeAudit.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeEquity.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeStrategyRegistry.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeSkillCalls.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeMemory.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeAgentOperations.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeNotifications.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeAuthorization.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeHistoricalCollections.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        var snapshot = RuntimeSnapshotFactory.Create(
            ServiceLocator.SystemState,
            DateTime.UtcNow,
            ServiceLocator.RuntimeMarkets.Read(),
            ServiceLocator.RuntimeTrading.Read(),
            ServiceLocator.RuntimeEquity.Read(),
            ServiceLocator.RuntimeConnection.Read(),
            ServiceLocator.RuntimeStrategyRegistry.Read(),
            ServiceLocator.RuntimeSkillCalls.Read(),
            ServiceLocator.RuntimeMemory.Read(),
            ServiceLocator.RuntimeAgentOperations.Read(),
            ServiceLocator.RuntimeBacktests.Read(),
            ServiceLocator.RuntimeAudit.Read(),
            LlmRequestGovernor.Shared.GetTodaySnapshot(),
            LlmRequestGovernor.Shared.GetTodayBreakdown(),
            ServiceLocator.PluginRegistry.List(),
            ServiceLocator.RuntimeNotifications.Read(),
            ServiceLocator.RuntimeAuthorization.Read(),
            ServiceLocator.RuntimeEquityMarkets.Read(),
            ServiceLocator.RuntimeHistoricalCollections.Read(),
            ServiceLocator.RuntimeCrossAssetResearch.Read(),
            ServiceLocator.RuntimeDistribution.Read(),
            ServiceLocator.PublicMarket.Read(),
            ServiceLocator.SecurityStorage.Read());
        return System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
        });
        /* Legacy fields moved to RuntimeSnapshotFactory so this bridge never exposes SystemState directly.
        {
            btcPrice = (double)state.BtcPrice,
            ethPrice = (double)state.EthPrice,
            btcTrend = state.BtcTrend,
            ethTrend = state.EthTrend,
            marketRegime = state.MarketRegime,
            aiRuntimeMode = state.BrainMode.ToString(),
            aiRuntimeEffectiveMode = state.BrainEffectiveMode.ToString(),
            brainRemoteAllowed = state.BrainRemoteAllowed,
            brainFallbackReason = state.BrainFallbackReason,
            activeBrainProvider = state.ActiveBrainProvider,
            activeBrainModel = state.ActiveBrainModel,
            brainConfidence = state.BrainConfidence,
            decisionScore = state.DecisionScore,
            riskLoad = state.RiskLoad,
            conflictRate = state.ConflictRate,
            walletBalance = (double)state.WalletBalance,
            availableBalance = (double)state.AvailableBalance,
            positionQuantity = (double)state.PositionQuantity,
            workflowNode = state.WorkflowNode,
            status = state.Status.ToString(),
            environment = state.Mode.ToString(),
            runtimeFresh = DateTime.UtcNow - state.LastUpdated <= TimeSpan.FromSeconds(15),
            runtimeAgeSeconds = Math.Max(0, (DateTime.UtcNow - state.LastUpdated).TotalSeconds),
            thinkingProgress = state.ThinkingProgress,
            reflectionStatus = state.ReflectionStatus,
            reviewerStatus = state.ReviewerStatus,
            riskApprovalStatus = state.RiskApprovalStatus,
            executionApprovalStatus = state.ExecutionApprovalStatus,
            dataQualityScore = state.DataQualityScore,
            liquidityScore = state.LiquidityScore,
            volatilityPercent = state.VolatilityPercent,
            researchScore = state.ResearchScore,
            historicalCoverageDays = state.HistoricalCoverageDays,
            missingConditions = state.MissingConditions,
            riskSummary = state.RiskSummary,
            signalContributions = state.SignalContributions,
            lastUpdated = state.LastUpdated,
            lastDecision = state.LastDecision,
            lastReason = state.LastReason,
            decisionAuditSummary = state.DecisionAuditSummary,
            strategyStatus = state.StrategyStatus,
            strategySummary = state.StrategySummary,
            strategyCandidates = state.StrategyCandidates,
            plannedEntry = (double)state.PlannedEntry,
            plannedStop = (double)state.PlannedStop,
            plannedTakeProfit = (double)state.PlannedTakeProfit,
            plannedQuantity = (double)state.PlannedQuantity,
            riskRewardRatio = state.RiskRewardRatio,
            exchangeConnected = state.ExchangeConnected,
            brainConnected = state.BrainConnected,
            apiTradePermission = state.ApiTradePermission,
            riskReady = state.RiskReady,
            loggedInUser = state.LoggedInUser,
            realtimeStatus = state.RealtimeStatus,
            runtimeRecoveryStatus = state.RuntimeRecoveryStatus,
            newsFullTextDocuments = state.NewsFullTextDocuments,
            newsCorroboratingSources = state.NewsCorroboratingSources,
            positionsSummary = state.PositionsSummary,
            ordersSummary = state.OrdersSummary,
            marketSummary = state.MarketSummary,
            newsSummary = state.NewsSummary,
            decisionDiagnostics = state.DecisionDiagnostics,
            dailyPnl = state.DailyPnl,
            maxDrawdown = state.MaxDrawdown
            ,runtimeEventSequence = state.RuntimeEventSequence
            ,runtimeHeartbeatAtUtc = state.RuntimeHeartbeatAtUtc
        }); */
    }

    private void ApplyTheme(ThemeMode mode)
    {
        var light = mode == ThemeMode.Light || (mode == ThemeMode.Auto && DateTime.Now.Hour is >= 7 and < 19);
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#F3F7FA" : "#050A10"));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#102532" : "#E8F4FA"));
        if (_themeButton is not null) _themeButton.Content = $"{I18n.T("Theme.Button")} · {I18n.T("Theme." + mode)}";
    }

    private static StackPanel? FindNavigationPanel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is StackPanel panel && panel.Children.OfType<Button>().Any(x => Equals(x.Tag, "LOGS"))) return panel;
            var nested = FindNavigationPanel(child);
            if (nested is not null) return nested;
        }
        return null;
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
                MessageBox.Show(UiDiagnostic.FormatText(access.Summary,"Access readiness check failed.",access.CheckedAtUtc),I18n.T("Dialog.InitFailed"),MessageBoxButton.OK,MessageBoxImage.Warning);
                var setup=new SetupWindow(_user,true);setup.ShowDialog();UpdateInterface();return;
            }
            AutoTradingAgent.StartDefault();
            AddThought(I18n.T("Thought.Initializing"));
            UpdateInterface();
        }
        catch (Exception ex)
        {
            MessageBox.Show(UiDiagnostic.Format(ex,"Agent start failed."), I18n.T("Dialog.InitFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            catch(Exception ex){AddThought(UiDiagnostic.Format(ex,"Automatic start failed."));}
        }
        UpdateInterface();
    }
    private async Task RefreshAccessAsync(){try{var settings=_settingsStore.Load();var report=await _readiness.CheckAsync(settings);ApplyAccess(report,settings);UpdateInterface();}catch{}}
    private static void ApplyAccess(AccessReadinessReport report,AgentSettings settings){ServiceLocator.RuntimeConnection.Publish(report,settings);var state=ServiceLocator.SystemState;var runtimeMode=RuntimeModePolicy.Resolve(settings);state.ExchangeConnected=report.Checks.Any(x=>x.Key=="exchange"&&x.Passed);state.BrainConnected=report.Checks.Any(x=>x.Key=="brain"&&x.Passed);state.ApiTradePermission=report.Checks.Any(x=>x.Key=="trade_permission"&&x.Passed);state.RiskReady=report.Checks.Any(x=>x.Key=="risk"&&x.Passed);state.BrainMode=runtimeMode.RequestedMode;state.BrainEffectiveMode=runtimeMode.EffectiveMode;state.BrainRemoteAllowed=runtimeMode.AllowRemoteBrain;state.BrainFallbackReason=runtimeMode.FallbackReason;state.ActiveBrainProvider=runtimeMode.ProviderName;state.ActiveBrainModel=runtimeMode.ModelName;state.BrainName=runtimeMode.EffectiveMode==AiRuntimeMode.LocalOnly?"WPE Local Brain":runtimeMode.ProviderName;state.LastAccessCheckAtUtc=report.CheckedAtUtc;state.LoggedInUser=settings.ActiveUser;}

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
            var confirmation = AutoTradingAgent.CreateEmergencyCloseConfirmation();
            var result = await AutoTradingAgent.EmergencyCloseAllAsync(confirmation);
            AddThought(I18n.T("Dialog.EmergencyDone", result));
        }
        catch (Exception ex)
        {
            AddThought(UiDiagnostic.Format(ex,"Emergency close degraded."));
            MessageBox.Show(UiDiagnostic.Format(ex,"Emergency close failed."), I18n.T("Dialog.EmergencyTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        BtcTickerText.Text = BtcPriceText.Text;
        EthTickerText.Text = EthPriceText.Text;
        SetMarket(BtcDirection, BtcMetaText, state.BtcTrend, state.BtcRsi);
        SetMarket(EthDirection, EthMetaText, state.EthTrend, state.EthRsi);
        BtcTickerText.Foreground = state.BtcTrend > .02 ? _green : state.BtcTrend < -.02 ? _red : _orange;
        EthTickerText.Foreground = state.EthTrend > .02 ? _green : state.EthTrend < -.02 ? _red : _orange;
        BtcTickerText.ToolTip = $"BTC / USDT\nRSI: {state.BtcRsi:0.0}\nTrend: {state.BtcTrend:+0.00%;-0.00%;0.00%}\n{I18n.T("Market.Regime", state.MarketRegime)}";
        EthTickerText.ToolTip = $"ETH / USDT\nRSI: {state.EthRsi:0.0}\nTrend: {state.EthTrend:+0.00%;-0.00%;0.00%}\n{I18n.T("Market.Regime", state.MarketRegime)}";
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
        var breakdown = LlmRequestGovernor.Shared.GetTodayBreakdown();
        LlmModeText.ToolTip = $"Provider: {llm.TopProvider}\nTop purpose: {llm.TopPurpose}\nCalls: {llm.Calls:N0}\nTokens: {llm.Tokens:N0}\nCost: ${llm.CostUsd:0.00}\nTop agent: {breakdown.TopAgent} ({breakdown.TopAgentCalls:N0})\nTop tool: {breakdown.TopTool} ({breakdown.TopToolCalls:N0})";
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
        UpdateHealthTelemetry();
        var remain = ServiceLocator.SystemState.NextCycleAtUtc - System.DateTime.UtcNow;
        CycleText.Text = remain is { Ticks: > 0 } ? remain.Value.ToString("mm\\:ss") : "--:--";
    }

    private void UpdateHealthTelemetry()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var state = ServiceLocator.SystemState;
            RuntimeStatusText.ToolTip = string.Join(Environment.NewLine,
                "AGENT HEALTH",
                $"Status: {state.Status}",
                $"Workflow: {state.WorkflowNode}",
                $"Memory: {process.WorkingSet64 / 1024d / 1024d:0.0} MB",
                $"Threads: {process.Threads.Count}",
                $"GC: {GC.GetTotalMemory(false) / 1024d / 1024d:0.0} MB",
                $"Events: #{state.RuntimeEventSequence}",
                $"Last update: {I18n.DateTime(state.LastUpdated.ToLocalTime())}");
        }
        catch { }
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
        EventFilters.Visibility = tag is "LOGS" or "BRAIN" ? Visibility.Visible : Visibility.Collapsed;
        EventFilterBox.Text = string.Empty;
        var state = ServiceLocator.SystemState;
        var memoryStore = new AgentSqliteStore();
        var memory = tag == "BRAIN" ? await memoryStore.GetMemoryExplorerAsync(40, CancellationToken.None) : Array.Empty<string>();
        var memoryStats = tag == "BRAIN" ? await memoryStore.GetMemorySourceCountsAsync(CancellationToken.None) : new Dictionary<string,long>();
        IReadOnlyList<(string Source,int References)> memoryRefs = tag == "BRAIN" ? await memoryStore.GetMemoryReferenceCountsAsync(CancellationToken.None) : Array.Empty<(string Source,int References)>();
        (SecondaryTitle.Text, SecondarySubtitle.Text, SecondaryContent.Text) = tag switch
        {
            "MARKET" => (I18n.T("Secondary.MarketTitle"), I18n.T("Secondary.MarketSubtitle"), $"{I18n.T("Audit.Realtime")}  {state.RealtimeStatus}\n{I18n.T("Audit.FullTextNews")}  {state.NewsFullTextDocuments}\n{I18n.T("Audit.NewsSources")}  {state.NewsCorroboratingSources}\n\n{state.MarketSummary}\n\n{I18n.T("Signal.Title")}\n{state.DecisionDiagnostics}\n\n{state.NewsSummary}"),
            "POSITIONS" => (I18n.T("Secondary.PositionsTitle"), I18n.T("Secondary.PositionsSubtitle"), $"{I18n.T("Portfolio.Equity")}  {I18n.Number(state.WalletBalance)} USDT\n{I18n.T("Portfolio.Available")}  {I18n.Number(state.AvailableBalance)} USDT\n\n{state.PositionsSummary}\n\n{state.OrdersSummary}"),
            "RISK" => (I18n.T("Secondary.RiskTitle"), I18n.T("Secondary.RiskSubtitle"), $"{state.RiskSummary}\n{state.PortfolioRiskSummary}\n\n{I18n.T("Audit.DataQuality")}  {state.DataQualityScore}/100\n{I18n.T("Audit.Liquidity")}  {state.LiquidityScore:0}%\n{I18n.T("Audit.Volatility")}  {state.VolatilityPercent:0.00}%\n{I18n.T("Audit.Research")}  {state.ResearchScore:0}%\n{I18n.T("Audit.HistoricalCoverage")}  {state.HistoricalCoverageDays} d\n{I18n.T("Audit.VaR99")}  {state.PortfolioVaR99:0.00}%\n{I18n.T("Audit.CVaR99")}  {state.PortfolioCVaR99:0.00}%\n{I18n.T("Audit.Concentration")}  {state.PortfolioConcentration:0.0}%\n{I18n.T("Audit.Correlation")}  {state.PortfolioCorrelation:0.00}\n{I18n.T("Brain.RiskLoad")}  {state.RiskLoad:0}%\n{I18n.T("Brain.ConflictRate")}  {state.ConflictRate:0}%\n\n{I18n.T("Audit.Reviewer")}  {AuditStatus(state.ReviewerStatus)}\n{I18n.T("Audit.RiskApproval")}  {AuditStatus(state.RiskApprovalStatus)}\n{I18n.T("Audit.ExecutionApproval")}  {AuditStatus(state.ExecutionApprovalStatus)}\n{I18n.T("Audit.CircuitBreaker")}  {I18n.T(state.CircuitBreakerActive?"Audit.Status.ACTIVE":"Audit.Status.CLEAR")}\n\n{I18n.T("Audit.Missing")}\n{state.MissingConditions}"),
            "BRAIN" => (I18n.T("Secondary.BrainTitle"), I18n.T("Secondary.BrainSubtitle"), $"Brain  {state.BrainName}\n{state.Status}\n{I18n.T("Brain.Confidence")}  {state.BrainConfidence:0}%\n{I18n.T("Label.Market")}  {state.MarketRegime}\n{I18n.T("Label.Reflection")}  {state.ReflectionStatus}\n\n{I18n.T("Audit.Entry")}  {I18n.Number(state.PlannedEntry)}\n{I18n.T("Audit.Stop")}  {I18n.Number(state.PlannedStop)}\n{I18n.T("Audit.TakeProfit")}  {I18n.Number(state.PlannedTakeProfit)}\n{I18n.T("Audit.Quantity")}  {I18n.Number(state.PlannedQuantity)}\n{I18n.T("Audit.RiskReward")}  {state.RiskRewardRatio:0.00}\n\n{state.DecisionAuditSummary}\n\n{state.LastReason}\n\nMEMORY SOURCES\n{string.Join("  |  ", memoryStats.Select(x => $"{x.Key}: {x.Value:N0}"))}\nMEMORY DUPLICATE REFERENCES\n{(memoryRefs.Count==0 ? "none" : string.Join("\n", memoryRefs.Select(x => $"{x.Source}  refs={x.References}")))}\n\nMEMORY EXPLORER\n{string.Join("\n\n", memory)}\n\n{PluginCenterSnapshot.Build(_settingsStore.Load())}"),
            "PLUGINS" => (I18n.T("Secondary.PluginsTitle"), I18n.T("Secondary.PluginsSubtitle"), PluginCenterSnapshot.Build(_settingsStore.Load())),
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
            var directory = AppDataPaths.LogsDirectory;
            var file = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            return file is null ? I18n.T("Log.Empty") : string.Join(Environment.NewLine, File.ReadLines(file).TakeLast(180));
        }
        catch (Exception ex) { return UiDiagnostic.Format(ex,"Log view unavailable."); }
    }

    private async void EventRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_activePage == "LOGS") SecondaryContent.Text = await ReadRuntimeEventsAsync(EventFilterBox.Text);
        else if (_activePage == "BRAIN") SecondaryContent.Text = await ReadMemoryAsync(EventFilterBox.Text);
    }

    private async void EventFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && _activePage == "LOGS") SecondaryContent.Text = await ReadRuntimeEventsAsync(EventFilterBox.Text);
        else if (IsLoaded && _activePage == "BRAIN") SecondaryContent.Text = await ReadMemoryAsync(EventFilterBox.Text);
    }

    private static async Task<string> ReadRuntimeEventsAsync(string? filter = null)
    {
        try
        {
            var events = await new AgentSqliteStore().GetRecentRuntimeEventsAsync(80, filter, CancellationToken.None);
            var timeline = await new AgentSqliteStore().GetWorkflowTimelineAsync(80, filter, CancellationToken.None);
            var output = events.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, events) : ReadLogTail();
            return timeline.Count == 0 ? output : $"WORKFLOW TIMELINE\n{string.Join(Environment.NewLine + Environment.NewLine, timeline)}\n\nRUNTIME EVENTS\n{output}";
        }
        catch { return ReadLogTail(); }
    }

    private static async Task<string> ReadMemoryAsync(string? filter)
    {
        try
        {
            var memory = await new AgentSqliteStore().GetMemoryExplorerAsync(80, filter, CancellationToken.None);
            return memory.Count == 0 ? I18n.T("Log.Empty") : "MEMORY EXPLORER\n" + string.Join(Environment.NewLine + Environment.NewLine, memory);
        }
        catch { return I18n.T("Log.Empty"); }
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
