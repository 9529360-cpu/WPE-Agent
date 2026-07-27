using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Localization;
using WpeAgent.Notifications;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人;

public partial class SetupWindow:Window
{
    private readonly CheckBox _notifyMarketBrief=new(){Tag=NotificationEventKind.MarketBrief.ToString(),Margin=new(0,4,18,4)};
    private readonly CheckBox _notifyTeacherMorning=new(){Tag=NotificationEventKind.TeacherMorningLesson.ToString(),Margin=new(0,4,18,4)};
    private readonly CheckBox _notifyTeacherAfternoon=new(){Tag=NotificationEventKind.TeacherAfternoonLesson.ToString(),Margin=new(0,4,18,4)};
    private readonly CheckBox _notifyTeacherEvening=new(){Tag=NotificationEventKind.TeacherEveningLesson.ToString(),Margin=new(0,4,18,4)};
    private readonly CheckBox _notifyTeacherEvent=new(){Tag=NotificationEventKind.TeacherEventLesson.ToString(),Margin=new(0,4,18,4)};
    private readonly CheckBox _notifyTeacherRecommendation=new(){Tag=NotificationEventKind.TeacherRecommendation.ToString(),Margin=new(0,4,18,4)};
    private readonly CheckBox _notifyTeacherCorrection=new(){Tag=NotificationEventKind.TeacherCorrection.ToString(),Margin=new(0,4,18,4)};
    private readonly ListView _telegramSubscriberList=new(){Height=180,Margin=new(0,8,0,8)};
    private readonly Button _approveTelegramSubscriber=new(){Content="批准所选订阅者",Margin=new(0,0,8,0)};
    private readonly Button _disableTelegramSubscriber=new(){Content="停用所选订阅者",Margin=new(0,0,8,0)};
    private readonly Button _refreshTelegramSubscribers=new(){Content="刷新订阅者"};
    private readonly AgentSettingsStore _store=new();private readonly AgentSqliteStore _auditStore=new();private readonly AccessReadinessService _readiness=new();private readonly RuntimeAuthorizationStateStore _authorizationState;private readonly LocalTradingReviewContextProvider _reviewContext;private readonly TradingReviewApprovalService _reviewApprovals;private AgentSettings _settings;private AccessReadinessReport? _last;private static LocalizationService I18n=>LocalizationService.Current;public bool SetupCompleted{get;private set;}
    public SetupWindow(string user,bool configurationMode=false,int? selectedTab=null){InitializeComponent();if(!ProviderBox.Items.OfType<ComboBoxItem>().Any(x=>string.Equals(x.Content?.ToString(),"WPE Local Brain",StringComparison.OrdinalIgnoreCase)))ProviderBox.Items.Insert(0,new ComboBoxItem{Content="WPE Local Brain"});_settings=_store.Load();UseLocalBrainWhenRemoteIsUnconfigured();_settings.ActiveUser=user;_reviewContext=new LocalTradingReviewContextProvider(_store,_auditStore);_reviewApprovals=new TradingReviewApprovalService(_auditStore,_reviewContext);_authorizationState=new RuntimeAuthorizationStateStore(_store,_auditStore);UserText.Text="● "+user;LoadSettings();InitializeTelegramSubscriberManagement();if(selectedTab is not null){WizardTabs.SelectedIndex=selectedTab.Value;}else if(configurationMode){WizardTabs.SelectedIndex=1;}I18n.LanguageChanged+=LanguageChanged;}
    private void UseLocalBrainWhenRemoteIsUnconfigured(){if(_settings.Brains.TryGetValue(_settings.ActiveBrain,out var active)&&!active.IsLocal&&!string.IsNullOrWhiteSpace(active.EncryptedKey))return;const string name="WPE Local Brain";if(!_settings.Brains.ContainsKey(name))_settings.Brains[name]=new BrainSlot{Provider=name,Endpoint=string.Empty,Model="deterministic-local-v1",IsLocal=true,PromptVersion="wpe-local-deterministic-v1",EnableFallback=false};_settings.ActiveBrain=name;_store.Save(_settings);}
    protected override void OnClosed(EventArgs e){I18n.LanguageChanged-=LanguageChanged;base.OnClosed(e);}
    protected override void OnContentRendered(EventArgs e){base.OnContentRendered(e);AuthorizationModeBox.IsEnabled=false;AuthorizationReasonBox.IsEnabled=false;ApprovePendingReviewButton.Visibility=Visibility.Collapsed;RejectPendingReviewButton.Visibility=Visibility.Collapsed;RevokePendingReviewButton.Visibility=Visibility.Collapsed;PendingReviewReasonBox.IsEnabled=false;PendingReviewStatusText.Text="Historical Review records are read-only. Testnet automatic authorization is established only by successful initial readiness setup.";}
    private void LoadSettings(){var s=_store.GetActiveExchange(_settings,false);ApiBaseBox.Text=s.Endpoint;ReceiveWindowBox.Text=s.ReceiveWindow.ToString();ExchangeTimeoutBox.Text=s.TimeoutSeconds.ToString();ProxyCheck.IsChecked=s.UseProxy;ProxyBox.Text=s.ProxyUrl;ApiKeyHint.Text=Mask(s.EncryptedCredentials.GetValueOrDefault("apiKey")??string.Empty,"Access.StoredCredential");ApiSecretHint.Text=Mask(s.EncryptedCredentials.GetValueOrDefault("secret")??string.Empty,"Access.StoredCredential");if(!_settings.Brains.TryGetValue(_settings.ActiveBrain,out var b)){b=new BrainSlot();_settings.Brains[_settings.ActiveBrain]=b;}SelectProvider(b.Provider);BrainEndpointBox.Text=b.Endpoint;BrainModelBox.Text=b.Model;BrainKeyHint.Text=Mask(b.EncryptedKey,"Access.StoredCredential");MaxTokenBox.Text=b.MaxTokens.ToString();TemperatureBox.Text=b.Temperature.ToString(CultureInfo.InvariantCulture);BrainTimeoutBox.Text=b.TimeoutSeconds.ToString();RetryBox.Text=b.RetryCount.ToString();PromptVersionBox.Text=b.PromptVersion;ContextLimitBox.Text=b.ContextLimit.ToString();LocalBrainCheck.IsChecked=b.IsLocal;FallbackCheck.IsChecked=b.EnableFallback;SymbolsBox.Text=string.Join(", ",_settings.Symbols);RealtimeDataCheck.IsChecked=_settings.DataSources.BinanceRealtimeEnabled;NewsDataCheck.IsChecked=_settings.DataSources.NewsEnabled;NotificationCheck.IsChecked=_settings.Notification.Enabled;LeverageBox.Text=_settings.Risk.Leverage.ToString();TradeRiskBox.Text=(_settings.Risk.MaxRiskPerTrade*100).ToString("0.##",CultureInfo.InvariantCulture);AccountExposureBox.Text=(_settings.Risk.MaxAccountExposure*100).ToString("0.##",CultureInfo.InvariantCulture);SelectAuthorizationMode(_settings.AuthorizationMode);RefreshAuthorizationModeAvailability();LoadNotificationSettings();}
    private void SelectAuthorizationMode(TradingAuthorizationMode mode){foreach(var item in AuthorizationModeBox.Items.OfType<ComboBoxItem>())if(string.Equals(item.Tag?.ToString(),mode.ToString(),StringComparison.Ordinal)){AuthorizationModeBox.SelectedItem=item;return;}AuthorizationModeBox.SelectedIndex=2;}
    private bool? ActiveProfileIsTestnet(){try{return _store.GetActiveExchange(_settings,false).IsTestnet;}catch{return null;}}
    private void RefreshAuthorizationModeAvailability(){var testnet=_settings.Environment==ExchangeEnvironment.Testnet&&ActiveProfileIsTestnet()==true;AutoAuthorizationItem.IsEnabled=testnet;var mode=(AuthorizationModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??TradingAuthorizationMode.Review.ToString();AuthorizationModeHint.Text=I18n.T(mode==TradingAuthorizationMode.Auto.ToString()?testnet?"Authorization.AutoHelp":"Authorization.AutoUnavailable":$"Authorization.{mode}Help");}
    private void AuthorizationMode_Changed(object sender,SelectionChangedEventArgs e){if(IsLoaded)RefreshAuthorizationModeAvailability();}
    private void SaveAuthorizationMode_Click(object sender,RoutedEventArgs e)
    {
        AuthorizationModeStatus.Text="Authorization mode is read-only after setup.";
    }
    private async Task RefreshPendingReviewsAsync()
    {
        DisablePendingReviewActions("Review queue unavailable.");PendingReviewList.ItemsSource=null;RefreshPendingReviewButton.IsEnabled=false;
        try
        {
            await _authorizationState.RefreshAsync(CancellationToken.None);var state=_authorizationState.Read();var now=DateTimeOffset.UtcNow;
            if(state.State!=RuntimeCollectionState.Available||state.UpdatedAt is null||now-state.UpdatedAt.Value>RuntimeAuthorizationStateStore.StaleAfter||!string.Equals(state.Mode,nameof(TradingAuthorizationMode.Review),StringComparison.Ordinal))
            {PendingReviewStatusText.Text=state.State==RuntimeCollectionState.Available?"Review actions require a current Review-mode projection.":"Review projection is unavailable.";return;}
            _settings=_store.Load();var profile=_store.GetActiveExchange(_settings,false);
            if(_store.LastLoadDiagnostic is not null||_settings.Environment!=ExchangeEnvironment.Testnet||!profile.IsTestnet){PendingReviewStatusText.Text="Review actions require a trusted Testnet profile.";return;}
            var queue=await _auditStore.GetTradingReviewQueueAsync(null,RuntimeAuthorizationStateStore.PendingLimit,0,CancellationToken.None);
            var projected=state.Pending.GroupBy(x=>x.ApprovalId,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.ToArray(),StringComparer.Ordinal);var rows=new List<PendingReviewRow>();
            foreach(var item in queue.Where(x=>x.Status is TradingReviewQueueStatus.Pending or TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Claimed))
            {
                var approvalId=SensitiveDataRedactor.MaskIdentifier(item.RequestId,"approval");
                if(!projected.TryGetValue(approvalId,out var matches)||matches.Length!=1)continue;var projection=matches[0];
                if(projection.CreatedAtUtc>now||projection.ExpiresAtUtc<=now||projection.Status is not "Pending"||projection.ReasonCode!="approval.awaiting-user"||!item.ArtifactValid||item.Artifact is null)continue;
                rows.Add(new(item.RequestId,approvalId,item.Status.ToString(),projection.CreatedAtUtc.ToLocalTime().ToString("g"),projection.ExpiresAtUtc.ToLocalTime().ToString("g"),projection.ReasonCode,item.Artifact.ProviderId));
            }
            PendingReviewList.ItemsSource=rows;RefreshPendingReviewButton.IsEnabled=true;PendingReviewStatusText.Text=rows.Count==0?"No actionable review requests.":$"{rows.Count} review request(s).";
        }
        catch(Exception ex){PendingReviewStatusText.Text=UiDiagnostic.Format(ex,"Review queue refresh failed.");}
        finally{RefreshPendingReviewButton.IsEnabled=true;UpdatePendingReviewActions();}
    }
    private void DisablePendingReviewActions(string message){ApprovePendingReviewButton.IsEnabled=false;RejectPendingReviewButton.IsEnabled=false;RevokePendingReviewButton.IsEnabled=false;PendingReviewReasonBox.IsEnabled=false;_reviewContext.Clear();PendingReviewStatusText.Text=message;}
    private void UpdatePendingReviewActions()
    {
        var row=PendingReviewList.SelectedItem as PendingReviewRow;_reviewContext.Select(row);
        PendingReviewReasonBox.IsEnabled=row is not null;ApprovePendingReviewButton.IsEnabled=row?.Status==nameof(TradingReviewQueueStatus.Pending);RejectPendingReviewButton.IsEnabled=row?.Status==nameof(TradingReviewQueueStatus.Pending);RevokePendingReviewButton.IsEnabled=row?.Status is nameof(TradingReviewQueueStatus.Pending) or nameof(TradingReviewQueueStatus.Approved) or nameof(TradingReviewQueueStatus.Claimed);
    }
    private async void RefreshPendingReviews_Click(object sender,RoutedEventArgs e)=>await RefreshPendingReviewsAsync();
    private void PendingReviewList_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdatePendingReviewActions();
    private Task HandlePendingReviewAsync(TradingReviewApprovalAction action)
    {
        DisablePendingReviewActions("Historical Review records are read-only.");return Task.CompletedTask;
    }
    private async void ApprovePendingReview_Click(object sender,RoutedEventArgs e)=>await HandlePendingReviewAsync(TradingReviewApprovalAction.Approve);
    private async void RejectPendingReview_Click(object sender,RoutedEventArgs e)=>await HandlePendingReviewAsync(TradingReviewApprovalAction.Reject);
    private async void RevokePendingReview_Click(object sender,RoutedEventArgs e)=>await HandlePendingReviewAsync(TradingReviewApprovalAction.Revoke);
    private sealed record PendingReviewRow(string RequestId,string ApprovalId,string Status,string CreatedDisplay,string ExpiresDisplay,string ReasonCode,string ProviderId);
    private sealed class LocalTradingReviewContextProvider(AgentSettingsStore store,AgentSqliteStore database):ITrustedTradingReviewContextProvider
    {
        private PendingReviewRow? _selected;public void Select(PendingReviewRow? row)=>_selected=row;public void Clear()=>_selected=null;
        public async ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var row=_selected;if(row is null)return null;var settings=store.Load();if(store.LastLoadDiagnostic is not null)return null;
            var profile=store.GetActiveExchange(settings,false);if(settings.AuthorizationMode!=TradingAuthorizationMode.Review||settings.Environment!=ExchangeEnvironment.Testnet||!profile.IsTestnet||!string.Equals(profile.ProviderId,row.ProviderId,StringComparison.OrdinalIgnoreCase))return null;
            var request=await database.GetTradingApprovalRequestAsync(row.RequestId,ct);if(request is null||!string.Equals(request.UserId,settings.ActiveUser,StringComparison.Ordinal)||!string.Equals(request.DeviceId,DeviceLicenseService.GetCurrentDeviceCode(),StringComparison.Ordinal))return null;
            return new(settings.ActiveUser,request.DeviceId,request.SessionId,settings.AuthorizationMode,true,profile.ProviderId);
        }
    }
    private void InitializeTelegramSubscriberManagement()
    {
        if(TelegramEnabledBox.Parent is not Panel panel)return;
        panel.Children.Add(new TextBlock{Text="Bot 订阅者",FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new(0,18,0,4)});
        panel.Children.Add(new TextBlock{Text="用户发送 /start 后进入待批准列表。批准时使用上方当前勾选的通知事件范围；订阅不获得任何交易或账户权限。",TextWrapping=TextWrapping.Wrap,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(84,125,141))});
        _telegramSubscriberList.DisplayMemberPath=nameof(TelegramSubscriberRow.Display);
        panel.Children.Add(_telegramSubscriberList);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};actions.Children.Add(_approveTelegramSubscriber);actions.Children.Add(_disableTelegramSubscriber);actions.Children.Add(_refreshTelegramSubscribers);panel.Children.Add(actions);
        _approveTelegramSubscriber.Click+=async(_,_)=>await ApproveTelegramSubscriberAsync();
        _disableTelegramSubscriber.Click+=async(_,_)=>await DisableTelegramSubscriberAsync();
        _refreshTelegramSubscribers.Click+=async(_,_)=>await RefreshTelegramSubscribersAsync();
        Loaded+=async(_,_)=>await RefreshTelegramSubscribersAsync();
    }
    private async Task RefreshTelegramSubscribersAsync()
    {
        try
        {
            var values=await new TelegramSubscriberStore().ListAsync(CancellationToken.None);
            _telegramSubscriberList.ItemsSource=values.Select(x=>new TelegramSubscriberRow(x.SubscriberKey,$"tg#{x.SubscriberKey[..12].ToLowerInvariant()} · {x.ChatType} · {x.State} · {(x.AllowedEventKinds.Count==0?"未授权事件":string.Join(", ",x.AllowedEventKinds))}")).ToArray();
        }
        catch(Exception ex){NotificationStatus.Text=UiDiagnostic.Format(ex,"Telegram subscriber refresh failed.");}
    }
    private async Task ApproveTelegramSubscriberAsync()
    {
        if(_telegramSubscriberList.SelectedItem is not TelegramSubscriberRow row){NotificationStatus.Text="请选择订阅者。";return;}
        var kinds=NotificationEventBoxes().Where(x=>x.IsChecked==true).Select(x=>Enum.Parse<NotificationEventKind>((string)x.Tag)).Where(x=>x!=NotificationEventKind.Test).ToHashSet();
        if(kinds.Count==0){NotificationStatus.Text="批准前请至少勾选一个非测试通知事件。";return;}
        try{await new TelegramSubscriberStore().ApproveAsync(row.SubscriberKey,kinds,_settings.ActiveUser,CancellationToken.None);await RefreshTelegramSubscribersAsync();NotificationStatus.Text="订阅者已批准。";}catch(Exception ex){NotificationStatus.Text=UiDiagnostic.Format(ex,"Telegram subscriber approval failed.");}
    }
    private async Task DisableTelegramSubscriberAsync()
    {
        if(_telegramSubscriberList.SelectedItem is not TelegramSubscriberRow row){NotificationStatus.Text="请选择订阅者。";return;}
        try{await new TelegramSubscriberStore().DisableAsync(row.SubscriberKey,_settings.ActiveUser,CancellationToken.None);await RefreshTelegramSubscribersAsync();NotificationStatus.Text="订阅者已停用。";}catch(Exception ex){NotificationStatus.Text=UiDiagnostic.Format(ex,"Telegram subscriber disable failed.");}
    }
    private sealed record TelegramSubscriberRow(string SubscriberKey,string Display);
    private CheckBox[] NotificationEventBoxes()
    {
        _notifyMarketBrief.Content=I18n.CurrentCode switch{"zh_CN"=>"每日市场简报","zh_TW"=>"每日市場簡報","ja_JP"=>"毎日のマーケットレポート","ko_KR"=>"일일 시장 브리핑","it_IT"=>"Briefing giornaliero di mercato",_=>"Daily market brief"};
        var chinese=I18n.CurrentCode.StartsWith("zh",StringComparison.OrdinalIgnoreCase);_notifyTeacherMorning.Content=chinese?"老师早课":"Teacher morning lesson";_notifyTeacherAfternoon.Content=chinese?"老师午课":"Teacher afternoon lesson";_notifyTeacherEvening.Content=chinese?"老师晚课":"Teacher evening lesson";_notifyTeacherEvent.Content=chinese?"老师市场异动课":"Teacher market event lesson";_notifyTeacherRecommendation.Content=chinese?"老师研究候选":"Teacher research candidate";_notifyTeacherCorrection.Content=chinese?"老师更正":"Teacher correction";
        if(NotifyTest.Parent is Panel panel)foreach(var box in new[]{_notifyMarketBrief,_notifyTeacherMorning,_notifyTeacherAfternoon,_notifyTeacherEvening,_notifyTeacherEvent,_notifyTeacherRecommendation,_notifyTeacherCorrection})if(box.Parent is null)panel.Children.Insert(Math.Max(0,panel.Children.IndexOf(NotifyTest)),box);
        return[NotifyOrderFilled,NotifyPositionOpened,NotifyPositionClosed,NotifyProtection,NotifyProtectionFailed,NotifyRiskBlocked,NotifyAgentDegraded,_notifyMarketBrief,_notifyTeacherMorning,_notifyTeacherAfternoon,_notifyTeacherEvening,_notifyTeacherEvent,_notifyTeacherRecommendation,_notifyTeacherCorrection,NotifyTest];
    }
    private void LoadNotificationSettings()
    {
        var n=_settings.Notification;NotificationsEnabledBox.IsChecked=n.Enabled;TelegramEnabledBox.IsChecked=n.Telegram.Enabled;WhatsAppEnabledBox.IsChecked=n.WhatsApp.Enabled;QuietHoursBox.IsChecked=n.QuietHoursEnabled;QuietStartBox.Text=n.QuietHoursStart;QuietEndBox.Text=n.QuietHoursEnd;
        QuietTimeZoneBox.ItemsSource=TimeZoneInfo.GetSystemTimeZones().Select(x=>x.Id);QuietTimeZoneBox.Text=n.QuietHoursTimeZone;
        foreach(var box in NotificationEventBoxes())box.IsChecked=n.EventKinds.Contains(box.Tag?.ToString()??string.Empty,StringComparer.OrdinalIgnoreCase);
        TelegramTokenHint.Text=SecretHint(n.Telegram.EncryptedAccessToken);TelegramDestinationHint.Text=SecretHint(n.Telegram.EncryptedDestination);
        WhatsAppTokenHint.Text=SecretHint(n.WhatsApp.EncryptedAccessToken);WhatsAppDestinationHint.Text=SecretHint(n.WhatsApp.EncryptedDestination);WhatsAppPhoneNumberIdHint.Text=SecretHint(n.WhatsApp.EncryptedPhoneNumberId);
        WhatsAppTemplateBox.Text=n.WhatsApp.TemplateName;WhatsAppLanguageBox.Text=n.WhatsApp.LanguageCode;WhatsAppApiVersionBox.Text=n.WhatsApp.ApiVersion;
        TelegramTokenBox.Clear();TelegramDestinationBox.Clear();WhatsAppTokenBox.Clear();WhatsAppDestinationBox.Clear();WhatsAppPhoneNumberIdBox.Clear();
    }
    private static string SecretHint(string encrypted)=>string.IsNullOrWhiteSpace(encrypted)?I18n.T("Access.NotConfigured"):I18n.T("Access.StoredCredential");
    private NotificationConfigurationInput NotificationInput()=>new(
        NotificationsEnabledBox.IsChecked==true,NotificationEventBoxes().Where(x=>x.IsChecked==true).Select(x=>Enum.Parse<NotificationEventKind>((string)x.Tag)).ToHashSet(),
        QuietHoursBox.IsChecked==true,QuietStartBox.Text.Trim(),QuietEndBox.Text.Trim(),QuietTimeZoneBox.Text.Trim(),
        new(TelegramEnabledBox.IsChecked==true,TelegramTokenBox.Password,TelegramDestinationBox.Password,_settings.Notification.Telegram.TimeoutSeconds,_settings.Notification.Telegram.MaxRequestsPerMinute),
        new(WhatsAppEnabledBox.IsChecked==true,WhatsAppTokenBox.Password,WhatsAppDestinationBox.Password,WhatsAppPhoneNumberIdBox.Password,WhatsAppTemplateBox.Text.Trim(),WhatsAppLanguageBox.Text.Trim(),WhatsAppApiVersionBox.Text.Trim(),_settings.Notification.WhatsApp.TimeoutSeconds,_settings.Notification.WhatsApp.MaxRequestsPerMinute));
    private bool SaveNotificationsConfirmed()
    {
        if(MessageBox.Show(I18n.T("Notification.SaveConfirm"),I18n.T("Notification.Title"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return false;
        new NotificationConfigurationService(_store).SaveConfirmed(NotificationInput(),true,"SAVE NOTIFICATION CONFIG");_settings=_store.Load();LoadNotificationSettings();NotificationStatus.Text=I18n.T("Setup.Saved");return true;
    }
    private void SaveNotifications_Click(object sender,RoutedEventArgs e)=>Try(()=>SaveNotificationsConfirmed());
    private void ClearTelegram_Click(object sender,RoutedEventArgs e)=>ClearNotification(NotificationChannel.Telegram);
    private void ClearWhatsApp_Click(object sender,RoutedEventArgs e)=>ClearNotification(NotificationChannel.WhatsApp);
    private void ClearNotification(NotificationChannel channel)
    {
        if(MessageBox.Show(I18n.T("Notification.ClearConfirm",channel),I18n.T("Notification.Title"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        new NotificationConfigurationService(_store).ClearChannelConfirmed(channel,true,$"CLEAR {channel.ToString().ToUpperInvariant()} NOTIFICATION");_settings=_store.Load();LoadNotificationSettings();NotificationStatus.Text=I18n.T("Notification.Cleared");
    }
    private async void TestTelegram_Click(object sender,RoutedEventArgs e)=>await TestNotificationAsync(NotificationChannel.Telegram);
    private async void TestWhatsApp_Click(object sender,RoutedEventArgs e)=>await TestNotificationAsync(NotificationChannel.WhatsApp);
    private async Task TestNotificationAsync(NotificationChannel channel)
    {
        try
        {
            if(!SaveNotificationsConfirmed())return;
            if(MessageBox.Show(I18n.T("Notification.NetworkConfirm",channel),I18n.T("Notification.Title"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            var store=new NotificationOutboxStore();var configuration=new AgentNotificationConfigurationProvider(_store);var requestId=Guid.NewGuid().ToString("N");
            var queued=await new NotificationTestEventService(store,configuration).QueueAsync(new(true,"SEND TEST NOTIFICATION","wpe-ui","Testnet",channel,requestId),CancellationToken.None);
            if(!AutoTradingAgent.IsRunning)
            {
                var dispatcher=new NotificationDispatcher(store,configuration,[new TelegramNotificationTransport(),new WhatsAppCloudNotificationTransport()]);await dispatcher.DispatchDueAsync(CancellationToken.None);
            }
            var projection=await store.ReadProjectionAsync(20,5,CancellationToken.None);var row=projection.Recent.FirstOrDefault(x=>x.Channel==channel&&x.Kind==NotificationEventKind.Test);
            NotificationStatus.Text=row is null?(queued?I18n.T("Notification.Queued"):I18n.T("Notification.Duplicate")):I18n.T("Notification.TestState",row.UiState,row.DiagnosticCode??string.Empty);
        }
        catch(Exception ex){NotificationStatus.Text=UiDiagnostic.Format(ex,"Notification test failed.");}
    }
    private static string Mask(string encrypted,string key)
    {
        if(string.IsNullOrWhiteSpace(encrypted))return I18n.T("Access.NotConfigured");
        try{var value=SecretVaultService.Decrypt(encrypted);return value.Length>8?$"{value[..4]}********{value[^4..]}":I18n.T(key);}catch{return I18n.T("Access.CredentialUnavailable");}
    }
    private void SaveExchange(){if(!Uri.TryCreate(ApiBaseBox.Text,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new InvalidOperationException(I18n.T("Setup.HttpsOnly"));var s=_store.GetActiveExchange(_settings,false);var apiKey=ApiKeyBox.Password.Length>0?ApiKeyBox.Password:null;var apiSecret=ApiSecretBox.Password.Length>0?ApiSecretBox.Password:null;s.Endpoint=uri.ToString().TrimEnd('/');s.UseProxy=ProxyCheck.IsChecked==true;s.ProxyUrl=ProxyBox.Text.Trim();s.ReceiveWindow=int.TryParse(ReceiveWindowBox.Text,out var rw)?Math.Clamp(rw,1000,60000):5000;s.TimeoutSeconds=int.TryParse(ExchangeTimeoutBox.Text,out var timeout)?Math.Clamp(timeout,5,120):20;s.IsTestnet=true;_settings.Environment=ExchangeEnvironment.Testnet;_settings.EnvironmentMode="ProviderTestnet";_store.SaveEnvironmentExchange(_settings,s,apiKey,apiSecret);}
    private void SaveBrain(){var provider=(ProviderBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"WPE Local Brain";var local=provider.Equals("WPE Local Brain",StringComparison.OrdinalIgnoreCase);Uri? uri=null;if(!local&&!Uri.TryCreate(BrainEndpointBox.Text,UriKind.Absolute,out uri))throw new InvalidOperationException(I18n.T("Setup.EndpointInvalid"));var name=provider;var old=_settings.Brains.GetValueOrDefault(name)??new BrainSlot();old.Provider=provider;old.Endpoint=local?string.Empty:uri!.ToString();old.Model=local?"deterministic-local-v1":BrainModelBox.Text.Trim();if(!local&&BrainKeyBox.Password.Length>0)old.EncryptedKey=SecretVaultService.Encrypt(BrainKeyBox.Password.Trim());if(local)old.EncryptedKey=string.Empty;old.MaxTokens=Int(MaxTokenBox.Text,1200,128,32768);old.Temperature=Double(TemperatureBox.Text,.1,0,2);old.TimeoutSeconds=Int(BrainTimeoutBox.Text,75,5,300);old.RetryCount=Int(RetryBox.Text,2,0,5);old.PromptVersion=BrainPromptVersionOrDefault(local);old.ContextLimit=Int(ContextLimitBox.Text,32000,2048,1000000);old.IsLocal=local||provider.Contains("Ollama",StringComparison.OrdinalIgnoreCase);old.EnableFallback=local?false:FallbackCheck.IsChecked==true;AgentSettingsStore.ValidateBrainEndpoint(old);_settings.Brains[name]=old;_settings.ActiveBrain=name;_store.Save(_settings);}
    private string BrainPromptVersionOrDefault(bool local)=>local?"wpe-local-deterministic-v1":PromptVersionBox.Text.Trim();
    private async Task CheckAll(){SaveExchange();SaveBrain();SaveAdvanced();HealthText.Text=I18n.T("Setup.Checking");_last=await _readiness.CheckAsync(_settings);HealthText.Text=string.Join("\n",_last.Checks.Select(SafeCheck));_settings.LastAccessCheckAtUtc=_last.CheckedAtUtc;_store.Save(_settings);}
    private void SaveExchange_Click(object sender,RoutedEventArgs e){Try(()=>{SaveExchange();ExchangeStatus.Text=I18n.T("Setup.Saved");});}
    private void OpenExchangeManager_Click(object sender,RoutedEventArgs e){new ExchangeManagerWindow{Owner=this}.ShowDialog();_settings=_store.Load();LoadSettings();}
    private async void TestExchange_Click(object sender,RoutedEventArgs e){try{SaveExchange();var report=await _readiness.CheckAsync(_settings);var exchange=report.Checks.Where(x=>x.Key is "exchange" or "trade_permission" or "withdraw_permission" or "clock");ExchangeStatus.Text=string.Join("\n",exchange.Select(SafeCheck));}catch(Exception ex){ExchangeStatus.Text=UiDiagnostic.Format(ex,"Exchange check failed.");}}
    private void SaveBrain_Click(object sender,RoutedEventArgs e){Try(()=>{SaveBrain();BrainStatus.Text=I18n.T("Setup.Saved");});}
    private void RevealExchange_Changed(object sender,RoutedEventArgs e){var profile=_store.GetActiveExchange(_settings,false);if(RevealExchangeCheck.IsChecked==true){ApiKeyBox.Password=Decrypt(profile.EncryptedCredentials.GetValueOrDefault("apiKey")??string.Empty);ApiSecretBox.Password=Decrypt(profile.EncryptedCredentials.GetValueOrDefault("secret")??string.Empty);}else{ApiKeyBox.Clear();ApiSecretBox.Clear();}}
    private void RevealBrain_Changed(object sender,RoutedEventArgs e){if(RevealBrainCheck.IsChecked==true&&_settings.Brains.TryGetValue(_settings.ActiveBrain,out var brain))BrainKeyBox.Password=Decrypt(brain.EncryptedKey);else BrainKeyBox.Clear();}
    private void DeleteExchange_Click(object sender,RoutedEventArgs e){if(MessageBox.Show(I18n.T("Access.DeleteConfirm"),I18n.T("Setup.ExchangeTitle"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;var s=_store.GetActiveExchange(_settings,false);s.EncryptedCredentials.Clear();s.ReadPermission=s.TradePermission=s.WithdrawPermission=false;s.AccountId=string.Empty;_settings.SetupCompleted=false;_store.Save(_settings);ApiKeyBox.Clear();ApiSecretBox.Clear();LoadSettings();}
    private void DeleteBrain_Click(object sender,RoutedEventArgs e){if(MessageBox.Show(I18n.T("Access.DeleteConfirm"),I18n.T("Setup.BrainTitle"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;if(_settings.Brains.TryGetValue(_settings.ActiveBrain,out var b))b.EncryptedKey=string.Empty;_settings.SetupCompleted=false;_store.Save(_settings);BrainKeyBox.Clear();LoadSettings();}
    private void SaveAdvanced(){_settings.Symbols=SymbolsBox.Text.Split([',',';',' '],StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim().ToUpperInvariant()).Where(x=>x.EndsWith("USDT",StringComparison.Ordinal)&&x.Length is >=7 and <=20).Distinct().Take(8).ToList();_settings.DataSources.BinanceRealtimeEnabled=RealtimeDataCheck.IsChecked==true;_settings.DataSources.NewsEnabled=NewsDataCheck.IsChecked==true;_settings.Notification.Enabled=NotificationCheck.IsChecked==true;_settings.Risk.Leverage=Int(LeverageBox.Text,5,1,20);_settings.Risk.MaxRiskPerTrade=(decimal)(Double(TradeRiskBox.Text,1,.25,2)/100);_settings.Risk.MaxAccountExposure=(decimal)(Double(AccountExposureBox.Text,60,10,60)/100);_store.Save(_settings);}
    private void SaveAdvanced_Click(object sender,RoutedEventArgs e){Try(()=>{SaveAdvanced();AdvancedStatus.Text=I18n.T("Setup.Saved");});}
    private void Backup_Click(object sender,RoutedEventArgs e){Try(()=>{SaveAdvanced();AdvancedStatus.Text=I18n.T("Setup.BackupCreated",CreateBackup());});}
    private void Restore_Click(object sender,RoutedEventArgs e){var dialog=new OpenFileDialog{Filter="WPE Backup (*.zip)|*.zip",InitialDirectory=AppDataPaths.BackupsDirectory};if(dialog.ShowDialog()!=true)return;if(MessageBox.Show(I18n.T("Setup.RestoreConfirm"),I18n.T("Setup.Restore"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;Try(()=>{var safety=CreateBackup();RestoreBackup(dialog.FileName);_settings=_store.Load();LoadSettings();AdvancedStatus.Text=I18n.T("Setup.Restored",safety);});}
    private void OpenLogs_Click(object sender,RoutedEventArgs e){var path=AppDataPaths.LogsDirectory;Process.Start(new ProcessStartInfo("explorer.exe",$"\"{path}\""){UseShellExecute=true});}
    private static string CreateBackup(){var data=AppDataPaths.DataDirectory;var path=AppDataPaths.BackupFile($"wpe-access-{DateTime.Now:yyyyMMdd-HHmmss}.zip");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);foreach(var name in new[]{"agent-settings.json","local-accounts.json"}){var source=Path.Combine(data,name);if(File.Exists(source))zip.CreateEntryFromFile(source,name,CompressionLevel.Optimal);}return path;}
    private static void RestoreBackup(string archive){var data=AppDataPaths.DataDirectory;using var zip=ZipFile.OpenRead(archive);foreach(var name in new[]{"agent-settings.json","local-accounts.json"}){var entry=zip.GetEntry(name);if(entry is null)continue;var target=AppDataPaths.File(name);entry.ExtractToFile(target,true);}}
    private async void TestBrain_Click(object sender,RoutedEventArgs e){try{SaveBrain();var report=await _readiness.CheckAsync(_settings);var b=report.Checks.First(x=>x.Key=="brain");BrainStatus.Text=SafeCheck(b);}catch(Exception ex){BrainStatus.Text=UiDiagnostic.Format(ex,"Brain check failed.");}}
    private async void CheckAll_Click(object sender,RoutedEventArgs e){try{await CheckAll();}catch(Exception ex){HealthText.Text=UiDiagnostic.Format(ex,"Readiness check failed.");}}
    private async void Finish_Click(object sender,RoutedEventArgs e){try{await CheckAll();if(_last?.Ready!=true){FinishStatus.Text=I18n.T("Setup.NotReady");WizardTabs.SelectedIndex=3;return;}if(RiskConfirmBox.IsChecked!=true){FinishStatus.Text=I18n.T("Setup.ConfirmRequired");WizardTabs.SelectedIndex=4;return;}var profile=_store.GetActiveExchange(_settings,false);if(_settings.Environment!=ExchangeEnvironment.Testnet||!profile.IsTestnet||_settings.LastAccessCheckAtUtc is null){FinishStatus.Text=I18n.T("Setup.NotReady");return;}var authorization=await _store.ChangeAuthorizationModeAfterReadinessAsync(_settings,_settings.ActiveUser,DeviceLicenseService.GetCurrentDeviceCode(),"Initial setup readiness verified for automatic Testnet execution.",_auditStore,CancellationToken.None);if(!authorization.Changed&&authorization.Code!="authorization.mode-unchanged"){FinishStatus.Text=authorization.Code;return;}if(_settings.AuthorizationMode!=TradingAuthorizationMode.Auto){FinishStatus.Text="authorization.auto-required";return;}_settings.SetupCompleted=true;_settings.SetupCompletedAtUtc=DateTime.UtcNow;_store.Save(_settings);SetupCompleted=true;DialogResult=true;}catch(Exception ex){FinishStatus.Text=UiDiagnostic.Format(ex,"Setup completion failed.");}}
    private void Environment_Changed(object sender,SelectionChangedEventArgs e){if(!IsLoaded)return;var tag=(EnvironmentBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();if(tag=="Mainnet"){EnvironmentBox.SelectedIndex=0;MessageBox.Show(I18n.T("Setup.MainnetDisabled"));}}
    private void Provider_Changed(object sender,SelectionChangedEventArgs e){if(!IsLoaded)return;var p=(ProviderBox.SelectedItem as ComboBoxItem)?.Content?.ToString();if(p=="WPE Local Brain"){BrainEndpointBox.Text=string.Empty;BrainModelBox.Text="deterministic-local-v1";BrainKeyBox.Clear();LocalBrainCheck.IsChecked=true;FallbackCheck.IsChecked=false;}else if(p=="DeepSeek"){BrainEndpointBox.Text="https://api.deepseek.com/chat/completions";BrainModelBox.Text="deepseek-chat";LocalBrainCheck.IsChecked=false;}else if(p=="OpenAI"){BrainEndpointBox.Text="https://api.openai.com/v1/responses";BrainModelBox.Text="gpt-5";}else if(p=="Anthropic"){BrainEndpointBox.Text="https://api.anthropic.com/v1/messages";BrainModelBox.Text="claude-sonnet-4-5";}else if(p=="Local Ollama"){BrainEndpointBox.Text="http://127.0.0.1:11434/v1/chat/completions";BrainModelBox.Text="qwen3";LocalBrainCheck.IsChecked=true;}}
    private void SelectProvider(string provider){foreach(var item in ProviderBox.Items.OfType<ComboBoxItem>())if(string.Equals(item.Content?.ToString(),provider,StringComparison.OrdinalIgnoreCase)){ProviderBox.SelectedItem=item;return;}}
    private void Nav_Click(object sender,RoutedEventArgs e){if(int.TryParse((sender as Button)?.Tag?.ToString(),out var i))WizardTabs.SelectedIndex=i;}
    private void Previous_Click(object sender,RoutedEventArgs e)=>WizardTabs.SelectedIndex=Math.Max(0,WizardTabs.SelectedIndex-1);private void Next_Click(object sender,RoutedEventArgs e)=>WizardTabs.SelectedIndex=Math.Min(6,WizardTabs.SelectedIndex+1);
    private void Language_Click(object sender,RoutedEventArgs e){var menu=new ContextMenu{PlacementTarget=LanguageButton};foreach(var language in I18n.AvailableLanguages){var item=new MenuItem{Header=language.DisplayName,Tag=language.Code};item.Click+=(_,_)=>I18n.SetLanguage((string)item.Tag);menu.Items.Add(item);}LanguageButton.ContextMenu=menu;menu.IsOpen=true;}
    private void LanguageChanged()=>Dispatcher.Invoke(()=>{Language=System.Windows.Markup.XmlLanguage.GetLanguage(I18n.Culture.IetfLanguageTag);LoadSettings();});private static string Decrypt(string encrypted){try{return string.IsNullOrWhiteSpace(encrypted)?string.Empty:SecretVaultService.Decrypt(encrypted);}catch{return string.Empty;}}private static void Try(Action action){try{action();}catch(Exception ex){MessageBox.Show(UiDiagnostic.Format(ex,"Operation failed."));}}private static string SafeCheck(AccessCheck check)=>$"{(check.Passed?"●":"○")} {check.Key.Replace('_',' ')} {(check.Passed?"passed":"failed")}";private static int Int(string s,int fallback,int min,int max)=>int.TryParse(s,out var v)?Math.Clamp(v,min,max):fallback;private static double Double(string s,double fallback,double min,double max)=>double.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?Math.Clamp(v,min,max):fallback;
}
