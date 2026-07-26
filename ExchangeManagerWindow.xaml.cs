using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人;

public partial class ExchangeManagerWindow:Window
{
    private readonly AgentSettingsStore _store=new();
    private readonly ExchangeProviderCatalog _catalog=new();
    private AgentSettings _settings;
    private readonly Dictionary<string,PasswordBox> _credentialInputs=new(StringComparer.OrdinalIgnoreCase);
    private ExchangeConnectionProfile? Selected=>ConnectionsList.SelectedItem as ExchangeConnectionProfile;
    private ExchangeProviderDescriptor? Descriptor=>ProviderBox.SelectedItem as ExchangeProviderDescriptor;
    private static LocalizationService I18n=>LocalizationService.Current;

    public ExchangeManagerWindow(){InitializeComponent();_settings=_store.Load();ProviderBox.ItemsSource=_catalog.All.OrderByDescending(x=>x.Installed).ThenBy(x=>x.DisplayName).ToArray();RefreshConnections(_settings.ActiveExecutionConnectionId);I18n.LanguageChanged+=LanguageChanged;}
    protected override void OnClosed(EventArgs e){I18n.LanguageChanged-=LanguageChanged;base.OnClosed(e);}
    private void LanguageChanged()=>Dispatcher.Invoke(LoadSelected);
    private void RefreshConnections(string? selectId=null){ConnectionsList.ItemsSource=null;ConnectionsList.ItemsSource=_settings.Exchanges;ConnectionsList.SelectedItem=_settings.Exchanges.FirstOrDefault(x=>x.Id==selectId)??_settings.Exchanges.FirstOrDefault();}
    private void ConnectionsList_SelectionChanged(object sender,SelectionChangedEventArgs e)=>LoadSelected();
    private void LoadSelected(){var profile=Selected;if(profile is null)return;ProviderBox.SelectedItem=_catalog.All.FirstOrDefault(x=>x.Id.Equals(profile.ProviderId,StringComparison.OrdinalIgnoreCase));NameBox.Text=profile.DisplayName;EnabledCheck.IsChecked=profile.Enabled;ExecutionCheck.IsChecked=profile.ExecutionEnabled;EndpointBox.Text=profile.Endpoint;SubAccountBox.Text=profile.SubAccount;TimeoutBox.Text=profile.TimeoutSeconds.ToString();ProxyCheck.IsChecked=profile.UseProxy;ProxyBox.Text=profile.ProxyUrl;ProxyBox.IsEnabled=profile.UseProxy;BuildCredentialFields(profile);UpdateAdapterStatus();}
    private void ProviderBox_SelectionChanged(object sender,SelectionChangedEventArgs e){if(!IsLoaded||Descriptor is null)return;if(Selected is null||!Descriptor.Id.Equals(Selected.ProviderId,StringComparison.OrdinalIgnoreCase)){NameBox.Text=Descriptor.DisplayName+" Testnet";EndpointBox.Text=DefaultEndpoint(Descriptor.Id);BuildCredentialFields(null);}UpdateAdapterStatus();}

    private void BuildCredentialFields(ExchangeConnectionProfile? profile)
    {
        CredentialPanel.Children.Clear();_credentialInputs.Clear();if(Descriptor is null)return;
        foreach(var field in Descriptor.CredentialFields){CredentialPanel.Children.Add(new TextBlock{Text=field.Label+(field.Required?" *":""),Foreground=new SolidColorBrush(Color.FromRgb(199,234,244))});var input=new PasswordBox{Tag=field.Key};_credentialInputs[field.Key]=input;CredentialPanel.Children.Add(input);var encrypted=profile?.EncryptedCredentials.GetValueOrDefault(field.Key);if(!string.IsNullOrWhiteSpace(encrypted))CredentialPanel.Children.Add(new TextBlock{Text=Mask(encrypted),Foreground=new SolidColorBrush(Color.FromRgb(84,125,141)),Margin=new Thickness(0,-7,0,8)});}
    }
    private void UpdateAdapterStatus(){if(Descriptor is null)return;AdapterStatusText.Text=Descriptor.Installed?I18n.T("ExchangeManager.AdapterReady",Descriptor.DisplayName):I18n.T("ExchangeManager.AdapterMissing",Descriptor.DisplayName);AdapterStatusText.Foreground=new SolidColorBrush(Descriptor.Installed?Color.FromRgb(53,230,160):Color.FromRgb(255,178,96));}
    private void Add_Click(object sender,RoutedEventArgs e){var descriptor=_catalog.All.FirstOrDefault(x=>x.Installed)??_catalog.All.First();var profile=new ExchangeConnectionProfile{ProviderId=descriptor.Id,DisplayName=descriptor.DisplayName+" Testnet",Endpoint=DefaultEndpoint(descriptor.Id),IsTestnet=true,Enabled=true,ExecutionEnabled=false};_settings.Exchanges.Add(profile);RefreshConnections(profile.Id);}
    private void Save_Click(object sender,RoutedEventArgs e){try{SaveSelected(true);}catch(Exception ex){StatusText.Text=ex.Message;}}
    private void SaveSelected(bool showStatus)
    {
        var profile=Selected??throw new InvalidOperationException(I18n.T("ExchangeManager.SelectConnection"));var descriptor=Descriptor??throw new InvalidOperationException(I18n.T("ExchangeManager.SelectProvider"));if(!Uri.TryCreate(EndpointBox.Text.Trim(),UriKind.Absolute,out var endpoint)||endpoint.Scheme!=Uri.UriSchemeHttps)throw new InvalidOperationException(I18n.T("ExchangeManager.HttpsOnly"));
        profile.ProviderId=descriptor.Id;profile.DisplayName=string.IsNullOrWhiteSpace(NameBox.Text)?descriptor.DisplayName+" Testnet":NameBox.Text.Trim();profile.Enabled=EnabledCheck.IsChecked==true;profile.ExecutionEnabled=ExecutionCheck.IsChecked==true;profile.IsTestnet=true;profile.Endpoint=endpoint.ToString().TrimEnd('/');profile.SubAccount=SubAccountBox.Text.Trim();profile.TimeoutSeconds=int.TryParse(TimeoutBox.Text,out var timeout)?Math.Clamp(timeout,5,120):20;profile.UseProxy=ProxyCheck.IsChecked==true;profile.ProxyUrl=ProxyBox.Text.Trim();foreach(var item in _credentialInputs.Where(x=>x.Value.Password.Length>0))profile.EncryptedCredentials[item.Key]=SecretVaultService.Encrypt(item.Value.Password.Trim());if(profile.ExecutionEnabled){foreach(var other in _settings.Exchanges.Where(x=>x.Id!=profile.Id))other.ExecutionEnabled=false;_settings.ActiveExecutionConnectionId=profile.Id;}_settings.Environment=ExchangeEnvironment.Testnet;AgentSettingsStore.ValidateExchangeProfile(profile);if(profile.ExecutionEnabled)_store.SyncActiveExchangeToEnvironmentSlot(_settings,profile);_store.Save(_settings);RefreshConnections(profile.Id);if(showStatus)StatusText.Text=I18n.T("ExchangeManager.Saved");
    }
    private async void Test_Click(object sender,RoutedEventArgs e)
    {
        try{SaveSelected(false);var profile=Selected!;if(!_catalog.IsInstalled(profile.ProviderId))throw new NotSupportedException(I18n.T("ExchangeManager.AdapterMissing",profile.DisplayName));var credentials=_store.GetExchangeCredentials(profile);StatusText.Text=I18n.T("ExchangeManager.Testing");await using var provider=_catalog.Create(profile,credentials);var health=await provider.HealthCheckAsync(CancellationToken.None);var permission=await provider.CheckPermissionsAsync(CancellationToken.None);var account=await provider.GetAccountAsync(CancellationToken.None);profile.LastVerifiedAtUtc=DateTime.UtcNow;profile.ReadPermission=permission.CanRead;profile.TradePermission=permission.CanTrade;profile.WithdrawPermission=permission.CanWithdraw;profile.AccountId=permission.AccountId;_store.Save(_settings);StatusText.Text=I18n.T("ExchangeManager.TestResult",health.Healthy,health.LatencyMs,permission.CanRead,permission.CanTrade,permission.CanWithdraw,account.AvailableBalance);}
        catch(Exception ex){StatusText.Text=I18n.T("ExchangeManager.TestFailed",Safe(ex.Message));}
    }
    private void SetExecution_Click(object sender,RoutedEventArgs e){var profile=Selected;if(profile is null)return;if(!_catalog.IsInstalled(profile.ProviderId)){StatusText.Text=I18n.T("ExchangeManager.AdapterMissing",profile.DisplayName);return;}foreach(var other in _settings.Exchanges)other.ExecutionEnabled=other.Id==profile.Id;profile.Enabled=true;_settings.ActiveExecutionConnectionId=profile.Id;_store.Save(_settings);LoadSelected();StatusText.Text=I18n.T("ExchangeManager.ExecutionSelected",profile.DisplayName);}
    private void Remove_Click(object sender,RoutedEventArgs e){var profile=Selected;if(profile is null)return;if(MessageBox.Show(I18n.T("ExchangeManager.RemoveConfirm",profile.DisplayName),I18n.T("ExchangeManager.Title"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;if(_settings.Exchanges.Count==1){StatusText.Text=I18n.T("ExchangeManager.KeepOne");return;}_settings.Exchanges.Remove(profile);if(_settings.ActiveExecutionConnectionId==profile.Id){var next=_settings.Exchanges.FirstOrDefault(x=>x.Enabled)??_settings.Exchanges[0];next.Enabled=true;next.ExecutionEnabled=true;_settings.ActiveExecutionConnectionId=next.Id;}_store.Save(_settings);RefreshConnections(_settings.ActiveExecutionConnectionId);}
    private void Proxy_Changed(object sender,RoutedEventArgs e){if(ProxyBox is not null)ProxyBox.IsEnabled=ProxyCheck.IsChecked==true;}
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private static string DefaultEndpoint(string providerId)=>providerId switch{"binance-futures"=>"https://testnet.binancefuture.com","okx"=>"https://www.okx.com","bybit"=>"https://api-testnet.bybit.com","bitget"=>"https://api.bitget.com","gate"=>"https://api-testnet.gateapi.io","kucoin"=>"https://api-sandbox-futures.kucoin.com","coinbase"=>"https://api-public.sandbox.exchange.coinbase.com","kraken"=>"https://demo-futures.kraken.com","deribit"=>"https://test.deribit.com","hyperliquid"=>"https://api.hyperliquid-testnet.xyz","dydx"=>"https://indexer.v4testnet.dydx.exchange",_=>"https://testnet.invalid"};
    private static string Mask(string encrypted){try{var value=SecretVaultService.Decrypt(encrypted);return value.Length>8?$"{value[..4]}********{value[^4..]}":I18n.T("Access.StoredCredential");}catch{return I18n.T("Access.CredentialUnavailable");}}
    private static string Safe(string value)=>value.Replace("\r"," ").Replace("\n"," ")[..Math.Min(220,value.Length)];
}
