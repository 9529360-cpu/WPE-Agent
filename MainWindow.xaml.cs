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

namespace 币安量化机器人;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly ObservableCollection<string> _thoughts=[];
    private readonly List<double> _equity=[];
    private Point _dragStart;private Border? _dragCard;
    private readonly SolidColorBrush _cyan=new(Color.FromRgb(24,216,242));
    private readonly SolidColorBrush _green=new(Color.FromRgb(53,230,160));
    private readonly SolidColorBrush _orange=new(Color.FromRgb(255,170,76));
    private readonly SolidColorBrush _red=new(Color.FromRgb(255,83,112));

    public MainWindow()
    {
        InitializeComponent();ThoughtList.ItemsSource=_thoughts;AutoTradingAgent.StateChanged+=OnAgentStateChanged;_clock.Tick+=(_,_)=>UpdateClock();_clock.Start();AddThought("Command Center initialized. Awaiting agent activation.");UpdateInterface();
    }

    protected override void OnClosed(EventArgs e){_clock.Stop();AutoTradingAgent.StateChanged-=OnAgentStateChanged;base.OnClosed(e);}
    private void StartAgent_Click(object sender,RoutedEventArgs e){try{AutoTradingAgent.StartDefault();AddThought("Initialization requested. Loading memory, skills and exchange state…");UpdateInterface();}catch(Exception ex){MessageBox.Show(ex.Message,"AGENT INITIALIZATION FAILED",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void PauseAgent_Click(object sender,RoutedEventArgs e){if(ServiceLocator.SystemState.Status==AgentStatus.Paused){AutoTradingAgent.Resume();AddThought("Agent resumed. Decision loop restored.");}else{AutoTradingAgent.Pause();AddThought("Agent paused. New cycles are suspended.");}UpdateInterface();}
    private async void StopAgent_Click(object sender,RoutedEventArgs e){await AutoTradingAgent.StopAsync();AddThought("Agent stopped safely. Memory remains loaded.");UpdateInterface();}
    private async void EmergencyClose_Click(object sender,RoutedEventArgs e)
    {
        if(!AutoTradingAgent.CanEmergencyClose){MessageBox.Show("Exchange link is not active.","EMERGENCY CLOSE",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        if(MessageBox.Show("Immediately pause the Agent, close every Testnet position and cancel protection orders?","CONFIRM EMERGENCY CLOSE",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        EmergencyButton.IsEnabled=false;try{var result=await AutoTradingAgent.EmergencyCloseAllAsync();AddThought("Emergency protocol completed: "+result);}catch(Exception ex){AddThought("Emergency protocol degraded: "+ex.Message);MessageBox.Show(ex.Message,"EMERGENCY CLOSE INCOMPLETE",MessageBoxButton.OK,MessageBoxImage.Error);}finally{EmergencyButton.IsEnabled=true;UpdateInterface();}
    }

    private void OnAgentStateChanged()=>Dispatcher.BeginInvoke(()=>{AddThought(BuildThought());UpdateInterface();},DispatcherPriority.Background);
    private string BuildThought(){var s=ServiceLocator.SystemState;return s.WorkflowNode switch{"OBSERVATION"=>"Observation is synchronizing market, account and derivatives evidence…","PLANNER"=>"Planner is composing a candidate decision from weighted evidence…","CRITIC"=>"Critic and Reviewer are challenging direction, confidence and conflict…","RISK"=>"Risk Manager is validating margin, protection and circuit breakers…","EXECUTION"=>"Execution is verifying idempotent order state and protection…","REFLECTION"=>"Reflection committed the decision audit and experience replay.",_=>$"{s.WorkflowNode}: {s.LastMessage}"};}
    private void AddThought(string text){if(string.IsNullOrWhiteSpace(text)||(_thoughts.Count>0&&_thoughts[^1].EndsWith(text,StringComparison.Ordinal)))return;_thoughts.Add($"{DateTime.Now:HH:mm:ss}  {text}");while(_thoughts.Count>7)_thoughts.RemoveAt(0);}

    private void UpdateInterface()
    {
        var s=ServiceLocator.SystemState;var running=AutoTradingAgent.IsRunning;
        StartButton.Content=running?"RUNNING":"INITIALIZE";StartButton.IsEnabled=!running;PauseButton.Content=s.Status==AgentStatus.Paused?"RESUME":"PAUSE";PauseButton.IsEnabled=running;StopButton.IsEnabled=running;EmergencyButton.IsEnabled=AutoTradingAgent.CanEmergencyClose;
        AiStatusText.Text=running?"AI ONLINE":"AI STANDBY";AiDot.Fill=running?_green:_orange;BrainStatusText.Text=s.BrainName=="未配置"?"BRAIN READY":$"{s.BrainName.ToUpperInvariant()} CONNECTED";
        StageText.Text=" "+s.SkillStage.ToUpperInvariant();LastDecisionText.Text=" "+s.LastDecision.ToUpperInvariant();LastDecisionText.Foreground=s.LastDecision.Contains("Open",StringComparison.OrdinalIgnoreCase)?_green:_orange;RegimeText.Text=" "+s.MarketRegime;ReflectionText.Text=" "+s.ReflectionStatus;
        ConfidenceGauge.Value=s.BrainConfidence;DecisionGauge.Value=Math.Abs(s.DecisionScore);RiskGauge.Value=s.RiskLoad;ConflictGauge.Value=s.ConflictRate;ThinkingBar.Value=s.ThinkingProgress;ThinkingPercent.Text=$"{s.ThinkingProgress}%";
        MarketStateText.Text=$"REGIME // {s.MarketRegime}";BtcPriceText.Text=s.BtcPrice>0?$"{s.BtcPrice:N2}":"--";EthPriceText.Text=s.EthPrice>0?$"{s.EthPrice:N2}":"--";SetMarket(BtcDirection,BtcMetaText,s.BtcTrend,s.BtcRsi);SetMarket(EthDirection,EthMetaText,s.EthTrend,s.EthRsi);
        AccountSideText.Text=$"{s.WalletBalance:N2} USDT";EquityText.Text=s.WalletBalance.ToString("N2");AvailableText.Text=s.AvailableBalance.ToString("N2");PositionText.Text=Math.Abs(s.PositionQuantity)<.0000001m?"FLAT":s.PositionQuantity.ToString("N4");EvidenceText.Text=$"{s.EvidenceCompleteness}/100";var exposure=s.WalletBalance>0?(double)Math.Min(1,Math.Abs(s.PositionQuantity*s.CurrentPrice)/s.WalletBalance):0;ExposureText.Text=$"EXPOSURE {exposure:P0}";ExposureText.Foreground=exposure>.5?_red:_green;
        if(s.WalletBalance>0&&(_equity.Count==0||Math.Abs(_equity[^1]-(double)s.WalletBalance)>.0001)){_equity.Add((double)s.WalletBalance);while(_equity.Count>80)_equity.RemoveAt(0);EquityChart.Points=_equity.ToArray();}
        SetSignals(s.SignalContributions);HighlightWorkflow(s.WorkflowNode);WorkflowStateText.Text=s.WorkflowNode;
        var fade=new DoubleAnimation(.72,1,TimeSpan.FromMilliseconds(300));DashboardPage.BeginAnimation(OpacityProperty,fade);
        UpdateClock();
    }

    private void SetMarket(TextBlock direction,TextBlock meta,double trend,double rsi){direction.Text=trend>.02?"BULLISH":trend<-.02?"BEARISH":"NEUTRAL";direction.Foreground=trend>.02?_green:trend<-.02?_red:_orange;meta.Text=$"RSI {rsi:0.0}  •  15m {trend:+0.00;-0.00;0.00}%";}
    private void SetSignals(IReadOnlyDictionary<string,double> signals)
    {
        double V(string key)=>signals.FirstOrDefault(x=>x.Key.Contains(key,StringComparison.OrdinalIgnoreCase)).Value;
        SetSignal(Signal15m,Signal15mValue,V("15分钟"));SetSignal(Signal1h,Signal1hValue,V("1小时"));SetSignal(Signal4h,Signal4hValue,V("4小时"));SetSignal(SignalRsi,SignalRsiValue,V("RSI"));SetSignal(SignalFlow,SignalFlowValue,V("主动买卖"));SetSignal(SignalFunding,SignalFundingValue,V("资金费率"));SetSignal(SignalOi,SignalOiValue,V("账户"));SetSignal(SignalNews,SignalNewsValue,V("基差"));
    }
    private void SetSignal(ProgressBar bar,TextBlock text,double value){bar.Value=Math.Min(100,Math.Abs(value)*350);bar.Foreground=value>.0001?_green:value<-.0001?_red:_cyan;text.Text=$"{value:+0.00;-0.00;0.00}";text.Foreground=bar.Foreground;}
    private void HighlightWorkflow(string node)
    {
        var map=new Dictionary<string,Border>{{"OBSERVATION",NodeObservation},{"PLANNER",NodePlanner},{"CRITIC",NodeCritic},{"REVIEWER",NodeReviewer},{"RISK",NodeRisk},{"EXECUTION",NodeExecution},{"REFLECTION",NodeReflection}};
        foreach(var item in map){var active=item.Key==node||(node=="CRITIC"&&item.Key=="REVIEWER");item.Value.Background=new SolidColorBrush(active?Color.FromRgb(13,55,68):Color.FromRgb(13,25,34));item.Value.BorderBrush=active?_cyan:new SolidColorBrush(Color.FromRgb(40,65,79));item.Value.Effect=active?new System.Windows.Media.Effects.DropShadowEffect{Color=Color.FromRgb(24,216,242),BlurRadius=18,ShadowDepth=0,Opacity=.45}:null;}
    }
    private void UpdateClock(){ClockText.Text=DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");var remain=ServiceLocator.SystemState.NextCycleAtUtc-DateTime.UtcNow;CycleText.Text=remain is{Ticks:>0}?remain.Value.ToString("mm\\:ss"):"--:--";}

    private void NavButton_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button{Tag:string tag})return;if(tag=="HOME"){DashboardPage.Visibility=Visibility.Visible;SecondaryPage.Visibility=Visibility.Collapsed;return;}DashboardPage.Visibility=Visibility.Collapsed;SecondaryPage.Visibility=Visibility.Visible;var s=ServiceLocator.SystemState;
        (SecondaryTitle.Text,SecondarySubtitle.Text,SecondaryContent.Text)=tag switch
        {
            "MARKET"=>("MARKET PULSE","Multi-timeframe structure, derivatives and verified evidence.",$"{s.MarketSummary}\n\nSIGNAL INTELLIGENCE\n{s.DecisionDiagnostics}\n\nNEWS STREAM\n{s.NewsSummary}"),
            "POSITIONS"=>("POSITION MATRIX","Exchange state is the final source of truth.",$"EQUITY  {s.WalletBalance:N2} USDT\nAVAILABLE  {s.AvailableBalance:N2} USDT\n\n{s.PositionsSummary}\n\nACTIVE ORDERS\n{s.OrdersSummary}"),
            "RISK"=>("RISK MATRIX","Deterministic constraints that the Brain cannot bypass.",$"{s.RiskSummary}\n\nRISK LOAD  {s.RiskLoad:0}%\nCONFLICT  {s.ConflictRate:0}%\nEVIDENCE  {s.EvidenceCompleteness}/100\n\nMARGIN TIERS  20% / 40% / 60%\nDAILY DRAWDOWN CIRCUIT  30%\nLIQUIDATION BUFFER  30%"),
            "BRAIN"=>("BRAIN & MEMORY","Planner, Critic, Reviewer and long-term experience.",$"BRAIN  {s.BrainName}\nSTATE  {s.Status}\nCONFIDENCE  {s.BrainConfidence:0}%\nMARKET REGIME  {s.MarketRegime}\nREFLECTION  {s.ReflectionStatus}\n\nLATEST DECISION\n{s.LastReason}"),
            "LOGS"=>("EVENT STREAM","Local, read-only runtime telemetry.",ReadLogTail()),
            _=>("SYSTEM MODULE","No telemetry available.",s.LastMessage)
        };
    }
    private static string ReadLogTail(){try{var dir=Path.Combine(AppContext.BaseDirectory,"logs");var file=Directory.Exists(dir)?Directory.GetFiles(dir,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault():null;return file is null?"No runtime events yet.":string.Join(Environment.NewLine,File.ReadLines(file).TakeLast(180));}catch(Exception ex){return"Log stream unavailable: "+ex.Message;}}

    private void Card_MouseDown(object sender,MouseButtonEventArgs e){_dragStart=e.GetPosition(null);_dragCard=sender as Border;}
    private void Card_MouseMove(object sender,MouseEventArgs e){if(e.LeftButton!=MouseButtonState.Pressed||_dragCard is null)return;var p=e.GetPosition(null);if(Math.Abs(p.X-_dragStart.X)>SystemParameters.MinimumHorizontalDragDistance||Math.Abs(p.Y-_dragStart.Y)>SystemParameters.MinimumVerticalDragDistance)DragDrop.DoDragDrop(_dragCard,_dragCard,DragDropEffects.Move);}
    private void Card_Drop(object sender,DragEventArgs e){if(sender is not Border target||e.Data.GetData(typeof(Border)) is not Border source||target==source)return;var a=DashboardCards.Children.IndexOf(source);var b=DashboardCards.Children.IndexOf(target);if(a<0||b<0)return;DashboardCards.Children.RemoveAt(a);DashboardCards.Children.Insert(a<b?b-1:b,source);}
}
