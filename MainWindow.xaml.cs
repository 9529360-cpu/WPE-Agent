using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;

namespace 币安量化机器人;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock=new(){Interval=TimeSpan.FromSeconds(1)};
    private string _currentPage="总览";
    private TextBlock? _phaseText;
    private TextBlock? _updatedText;
    private TextBlock? _accountText;
    private TextBlock? _decisionText;

    public MainWindow()
    {
        InitializeComponent();AutoTradingAgent.StateChanged+=OnAgentStateChanged;_clock.Tick+=(_,_)=>UpdateTopBar();_clock.Start();ShowPage("总览");
    }

    protected override void OnClosed(EventArgs e){_clock.Stop();AutoTradingAgent.StateChanged-=OnAgentStateChanged;base.OnClosed(e);}
    private void StartAgent_Click(object sender,RoutedEventArgs e){try{AutoTradingAgent.StartDefault();UpdateAgentStatus();}catch(Exception ex){MessageBox.Show(ex.Message,"无法启动智能体",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void PauseAgent_Click(object sender,RoutedEventArgs e){if(ServiceLocator.SystemState.Status==AgentStatus.Paused){AutoTradingAgent.Resume();PauseButton.Content="暂停";}else{AutoTradingAgent.Pause();PauseButton.Content="继续";}UpdateAgentStatus();}
    private async void StopAgent_Click(object sender,RoutedEventArgs e){await AutoTradingAgent.StopAsync();PauseButton.Content="暂停";UpdateAgentStatus();}
    private async void EmergencyClose_Click(object sender,RoutedEventArgs e)
    {
        if(!AutoTradingAgent.CanEmergencyClose){MessageBox.Show("智能体尚未连接交易所。请先启动并完成账户接管。","无法紧急全平",MessageBoxButton.OK,MessageBoxImage.Warning);return;}var confirm=MessageBox.Show("这会立即暂停 Agent，并按交易所当前持仓逐腿执行市价全平，同时撤销对应保护单。\n\n确定继续吗？","确认紧急全平",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No);if(confirm!=MessageBoxResult.Yes)return;
        EmergencyButton.IsEnabled=false;try{var result=await AutoTradingAgent.EmergencyCloseAllAsync();MessageBox.Show(result,"紧急全平结果",MessageBoxButton.OK,MessageBoxImage.Information);}catch(Exception ex){MessageBox.Show(ex.Message,"紧急全平未完全确认",MessageBoxButton.OK,MessageBoxImage.Error);}finally{EmergencyButton.IsEnabled=true;UpdateAgentStatus();}
    }
    private void OnAgentStateChanged()=>Dispatcher.BeginInvoke(UpdateAgentStatus,DispatcherPriority.Background);
    private void NavButton_Click(object sender,RoutedEventArgs e){if(sender is Button{Tag:string tag})ShowPage(tag);}

    private void ShowPage(string page)
    {
        _currentPage=page;MainContentHost.Children.Clear();StatusText.Text=$"状态：{page}";var s=ServiceLocator.SystemState;
        switch(page)
        {
            case "市场证据":SectionTitle.Text="市场与证据";ShowCards("BTC/ETH 分品种聚合 · 市场状态 · 可解释冲突",("市场结构",s.MarketSummary),("新闻证据",s.NewsSummary),("证据质量",$"完整度 {s.EvidenceCompleteness}/100\n{s.RiskSummary}"),("决策透明度",s.DecisionDiagnostics));break;
            case "持仓订单":SectionTitle.Text="持仓与订单";ShowCards("交易所状态是最终事实来源",("持仓",s.PositionsSummary),("活动订单",s.OrdersSummary),("账户",$"钱包 {s.WalletBalance:F2} USDT\n可用 {s.AvailableBalance:F2} USDT"),("保护状态",s.LastMessage));break;
            case "风控设置":SectionTitle.Text="风控与设置";ShowCards("确定性身体规则，不由大脑绕过",("仓位档位","20% / 40% / 60% · 每周期最多提升一档"),("硬限制","聚合保证金 ≤ 60% · 当日回撤 30% 熔断"),("执行保护","50x 默认并受交易所上限约束\n止损距估算强平价保留30%缓冲"),("当前风险",s.RiskSummary));break;
            case "账户大脑":SectionTitle.Text="账户与大脑";ShowCards("密钥仅以 DPAPI 加密保存，界面不显示明文",("环境",$"{s.Mode}\n主网必须独立显式确认"),("账户",$"钱包 {s.WalletBalance:F2} USDT\n可用 {s.AvailableBalance:F2} USDT"),("当前唯一大脑",s.BrainName),("大脑职责","只输出判断、档位和保护价格；数量与交易规则由身体计算。"));break;
            case "日志":SectionTitle.Text="运行日志";ShowLogPage();break;
            default:SectionTitle.Text="智能体总览";ShowDashboard();break;
        }
        UpdateTopBar();
    }

    private void ShowDashboard()
    {
        var root=CreateRoot("WPE Agent", "15分钟执行周期 · 1小时/4小时背景 · 默认 Binance Futures Testnet");var grid=CreateCardGrid();AddCard(grid,0,0,"技能阶段",out _phaseText);AddCard(grid,0,1,"最近更新时间",out _updatedText);AddCard(grid,1,0,"账户与仓位",out _accountText);AddCard(grid,1,1,"最近决策",out _decisionText);root.Children.Add(grid);MainContentHost.Children.Add(new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});UpdateDashboard();
    }

    private void ShowCards(string subtitle,params (string Title,string Value)[] cards)
    {
        var root=CreateRoot(SectionTitle.Text,subtitle);var grid=CreateCardGrid();for(var i=0;i<cards.Length;i++){AddCard(grid,i/2,i%2,cards[i].Title,out var value);value.Text=cards[i].Value;}root.Children.Add(grid);MainContentHost.Children.Add(new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
    }

    private void ShowLogPage()
    {
        var root=CreateRoot("运行日志","只读展示本地最近日志，不包含 API Key");var box=new TextBox{Text=ReadLogTail(),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas"),MinHeight=480,Background=new SolidColorBrush(Color.FromRgb(247,249,251)),BorderBrush=new SolidColorBrush(Color.FromRgb(215,224,231)),Padding=new Thickness(12)};root.Children.Add(box);MainContentHost.Children.Add(root);
    }

    private static string ReadLogTail()
    {
        try{var dirs=new[]{Path.Combine(AppContext.BaseDirectory,"logs"),Path.Combine(Directory.GetCurrentDirectory(),"logs")};var file=dirs.Where(Directory.Exists).SelectMany(x=>Directory.GetFiles(x,"*.log")).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();if(file is null)return"尚未生成运行日志。";return string.Join(Environment.NewLine,File.ReadLines(file).TakeLast(200));}catch(Exception ex){return $"日志读取失败：{ex.Message}";}
    }

    private static StackPanel CreateRoot(string title,string subtitle){var root=new StackPanel{Margin=new Thickness(28)};root.Children.Add(new TextBlock{Text=title,FontSize=24,FontWeight=FontWeights.Bold});root.Children.Add(new TextBlock{Text=subtitle,Margin=new Thickness(0,8,0,18),Foreground=Brushes.SlateGray,TextWrapping=TextWrapping.Wrap});return root;}
    private static Grid CreateCardGrid(){var grid=new Grid();for(var i=0;i<2;i++){grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.ColumnDefinitions.Add(new ColumnDefinition());}return grid;}
    private static void AddCard(Grid grid,int row,int column,string title,out TextBlock value){var border=new Border{Background=new SolidColorBrush(Color.FromRgb(243,247,250)),CornerRadius=new CornerRadius(8),Padding=new Thickness(14),Margin=new Thickness(0,0,10,10),MinHeight=118};var stack=new StackPanel();stack.Children.Add(new TextBlock{Text=title,Foreground=Brushes.SlateGray});value=new TextBlock{Text="等待智能体",FontSize=15,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0)};stack.Children.Add(value);border.Child=stack;Grid.SetRow(border,row);Grid.SetColumn(border,column);grid.Children.Add(border);}

    private void UpdateAgentStatus()
    {
        var s=ServiceLocator.SystemState;var running=AutoTradingAgent.IsRunning;AgentStatusText.Text=$"智能体：{s.Status} · {s.Mode} · {s.Symbol}@{s.Timeframe}";TopStageText.Text=s.SkillStage;StartButton.Content=running?"运行中":"启动";StartButton.IsEnabled=!running;PauseButton.Content=s.Status==AgentStatus.Paused?"继续":"暂停";PauseButton.IsEnabled=running;StopButton.IsEnabled=running;EmergencyButton.IsEnabled=AutoTradingAgent.CanEmergencyClose;UpdateDashboard();UpdateTopBar();if(_currentPage!="总览")ShowPage(_currentPage);
    }
    private void UpdateDashboard(){var s=ServiceLocator.SystemState;_phaseText?.SetCurrentValue(TextBlock.TextProperty,$"{s.SkillStage}\n{s.LastMessage}");_updatedText?.SetCurrentValue(TextBlock.TextProperty,$"{s.LastUpdated.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n证据 {s.EvidenceCompleteness}/100");_accountText?.SetCurrentValue(TextBlock.TextProperty,$"钱包 {s.WalletBalance:F2} · 可用 {s.AvailableBalance:F2}\n{s.PositionsSummary}");_decisionText?.SetCurrentValue(TextBlock.TextProperty,$"{s.LastDecision}\n{s.LastReason}");}
    private void UpdateTopBar(){var s=ServiceLocator.SystemState;EnvironmentText.Text=$"环境：{s.Mode}";AccountTopText.Text=$"账户：{s.WalletBalance:F2} USDT";BrainTopText.Text=$"大脑：{s.BrainName}";var remain=s.NextCycleAtUtc-DateTime.UtcNow;CycleText.Text=remain is{Ticks:>0}?$"下一周期：{remain.Value:mm\\:ss}":"下一周期：--:--";}
}
