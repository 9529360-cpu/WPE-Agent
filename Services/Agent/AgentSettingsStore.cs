using System.Text.Json;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Exchange;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public sealed class BrainSlot { public string Provider { get; set; } = "WPE Local Brain"; public string Endpoint { get; set; } = string.Empty; public string Model { get; set; } = "deterministic-local-v1"; public string EncryptedKey { get; set; } = string.Empty; public int MaxTokens { get; set; }=1200; public double Temperature { get; set; }=.1; public int TimeoutSeconds { get; set; }=75; public int RetryCount { get; set; }=2; public bool EnableFallback { get; set; } public string FallbackBrain { get; set; }=string.Empty; public bool IsLocal { get; set; }=true; public string PromptVersion { get; set; }="wpe-local-deterministic-v1"; public int ContextLimit { get; set; }=32000; }
public sealed class EnvironmentSlot { public string EncryptedApiKey { get; set; } = string.Empty; public string EncryptedApiSecret { get; set; } = string.Empty; public bool UseProxy { get; set; } public string ProxyUrl { get; set; }=string.Empty; public string ApiBaseUrl { get; set; }="https://testnet.binancefuture.com"; public int ReceiveWindow { get; set; }=5000; public int TimeoutSeconds { get; set; }=20; public DateTime? LastVerifiedAtUtc { get; set; } public bool ReadPermission { get; set; } public bool TradePermission { get; set; } public bool WithdrawPermission { get; set; } public string AccountId { get; set; }=string.Empty; }
public sealed class TelegramNotificationSlot
{
    public bool Enabled{get;set;}
    public string EncryptedAccessToken{get;set;}=string.Empty;
    public string EncryptedDestination{get;set;}=string.Empty;
    public int TimeoutSeconds{get;set;}=15;
    public int MaxRequestsPerMinute{get;set;}=20;
}
public sealed class WhatsAppNotificationSlot
{
    public bool Enabled{get;set;}
    public string EncryptedAccessToken{get;set;}=string.Empty;
    public string EncryptedDestination{get;set;}=string.Empty;
    public string EncryptedPhoneNumberId{get;set;}=string.Empty;
    public string TemplateName{get;set;}=string.Empty;
    public string LanguageCode{get;set;}="en_US";
    public string ApiVersion{get;set;}="v22.0";
    public int TimeoutSeconds{get;set;}=15;
    public int MaxRequestsPerMinute{get;set;}=20;
}
public sealed class NotificationSlot
{
    public bool Enabled{get;set;}
    public List<string> EventKinds{get;set;}=[];
    public bool QuietHoursEnabled{get;set;}
    public string QuietHoursStart{get;set;}="22:00";
    public string QuietHoursEnd{get;set;}="07:00";
    public string QuietHoursTimeZone{get;set;}="UTC";
    public bool LegacyMigrationPending{get;set;}
    public string? LegacyMigrationDiagnosticCode{get;set;}
    public TelegramNotificationSlot Telegram{get;set;}=new();
    public WhatsAppNotificationSlot WhatsApp{get;set;}=new();
}
public sealed class DataSourceSlot { public bool BinanceRealtimeEnabled { get; set; }=true; public bool NewsEnabled { get; set; }=true; }
public sealed class AgentSettings { public ExchangeEnvironment Environment { get; set; } = ExchangeEnvironment.Testnet; public string EnvironmentMode { get; set; }="FuturesTestnet"; public string ActiveBrain { get; set; } = "WPE Local Brain"; public global::币安量化机器人.Core.Models.AiRuntimeMode AiMode { get; set; } = global::币安量化机器人.Core.Models.AiRuntimeMode.LocalOnly; [System.Text.Json.Serialization.JsonConverter(typeof(TradingAuthorizationModeJsonConverter))] public TradingAuthorizationMode AuthorizationMode { get; set; } = TradingAuthorizationMode.Review; public string ActiveUser { get; set; }=string.Empty; public bool SetupCompleted { get; set; } public DateTime? SetupCompletedAtUtc { get; set; } public DateTime? LastAccessCheckAtUtc { get; set; } public List<string> Symbols { get; set; } = []; public Dictionary<string, BrainSlot> Brains { get; set; } = new(StringComparer.OrdinalIgnoreCase); public List<ExchangeConnectionProfile> Exchanges { get; set; }=[]; public string ActiveExecutionConnectionId { get; set; }=string.Empty; public EnvironmentSlot Testnet { get; set; } = new(); public EnvironmentSlot Mainnet { get; set; } = new(){ApiBaseUrl="https://fapi.binance.com"}; public bool MainnetTradingConfirmed { get; set; } public DateTime? MainnetConfirmedAtUtc { get; set; } public RiskLimits Risk { get; set; } = new(); public DecisionPolicy Decision { get; set; } = new(); public NotificationSlot Notification { get; set; }=new(); public DataSourceSlot DataSources { get; set; }=new(); }
public sealed record AgentSettingsLoadDiagnostic(string ErrorType,string ConfigurationPath);
public sealed class AgentSettingsStore
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    public AgentSettingsStore(string? path=null):this(path,null){}
    internal AgentSettingsStore(string? path,Func<DateTimeOffset>? utcNow){_path=path??AppDataPaths.File("agent-settings.json");_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);}
    public AgentSettingsLoadDiagnostic? LastLoadDiagnostic { get; private set; }
    public AgentSettings Load()
    {
        LastLoadDiagnostic=null;
        try
        {
            if(!File.Exists(_path))return Defaults();
            return Normalize(JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(_path))??throw new JsonException("Agent settings document is null"));
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadDiagnostic=new(ex.GetType().Name,_path);
            return Defaults();
        }
    }
    public void Save(AgentSettings settings)
        =>SaveCore(settings,false);
    private void SaveCore(AgentSettings settings,bool allowAuthorizationModeChange)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if(!allowAuthorizationModeChange)settings.AuthorizationMode=ReadPersistedAuthorizationMode();
        var directory=Path.GetDirectoryName(_path)!;Directory.CreateDirectory(directory);
        var temp=Path.Combine(directory,$".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try{File.WriteAllText(temp,JsonSerializer.Serialize(Normalize(settings),new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));File.Move(temp,_path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public async Task<TradingAuthorizationModeChangeResult> ChangeAuthorizationModeAsync(
        AgentSettings settings,TradingAuthorizationMode requestedMode,bool? activeProfileIsTestnet,bool autoConfirmed,
        string userId,string deviceId,string reason,AgentSqliteStore auditStore,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);ArgumentNullException.ThrowIfNull(auditStore);
        var persisted=ReadPersistedAuthorizationMode();var oldMode=Enum.IsDefined(settings.AuthorizationMode)?settings.AuthorizationMode:TradingAuthorizationMode.Review;
        if(oldMode!=persisted)return new(false,"authorization.mode-settings-stale",oldMode,requestedMode);
        if(!Enum.IsDefined(requestedMode))return new(false,"authorization.mode-invalid",oldMode,TradingAuthorizationMode.Review);
        if(oldMode==requestedMode)return new(false,"authorization.mode-unchanged",oldMode,requestedMode);
        if(string.IsNullOrWhiteSpace(userId)||string.IsNullOrWhiteSpace(deviceId)||string.IsNullOrWhiteSpace(reason))return new(false,"authorization.mode-context-invalid",oldMode,requestedMode);
        if(requestedMode==TradingAuthorizationMode.Auto)
        {
            if(settings.Environment!=ExchangeEnvironment.Testnet||activeProfileIsTestnet!=true)return new(false,"authorization.auto-testnet-required",oldMode,requestedMode);
            if(!autoConfirmed)return new(false,"authorization.auto-confirmation-required",oldMode,requestedMode);
        }
        var changedAt=_utcNow().ToUniversalTime();var audit=new TradingAuthorizationModeChangeAudit(
            Guid.NewGuid().ToString("N"),oldMode,requestedMode,userId,deviceId,changedAt,SensitiveDataRedactor.ForLog(reason,240));
        settings.AuthorizationMode=requestedMode;
        try
        {
            // The audit is the write-ahead record. A crash can leave the old mode with
            // an audit entry, but can never expose Auto without its SQLite audit.
            await auditStore.RecordTradingAuthorizationModeChangeAsync(audit,ct);
            SaveCore(settings,true);
            return new(true,"authorization.mode-changed",oldMode,requestedMode);
        }
        catch
        {
            settings.AuthorizationMode=oldMode;
            throw;
        }
    }
    public Task<TradingAuthorizationModeChangeResult> ChangeAuthorizationModeAfterReadinessAsync(
        AgentSettings settings,string userId,string deviceId,string reason,AgentSqliteStore auditStore,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);var now=_utcNow().ToUniversalTime();ExchangeConnectionProfile profile;
        if(LastLoadDiagnostic is not null||settings.SetupCompleted&&settings.AuthorizationMode!=TradingAuthorizationMode.Auto)
            return Task.FromResult(new TradingAuthorizationModeChangeResult(false,"authorization.auto-legacy-or-corrupt-blocked",settings.AuthorizationMode,TradingAuthorizationMode.Auto));
        try{profile=GetActiveExchange(settings);}
        catch{return Task.FromResult(new TradingAuthorizationModeChangeResult(false,"authorization.auto-readiness-required",settings.AuthorizationMode,TradingAuthorizationMode.Auto));}
        DateTimeOffset? access=settings.LastAccessCheckAtUtc is null?null:new DateTimeOffset(DateTime.SpecifyKind(settings.LastAccessCheckAtUtc.Value,DateTimeKind.Utc));
        DateTimeOffset? verified=profile.LastVerifiedAtUtc is null?null:new DateTimeOffset(DateTime.SpecifyKind(profile.LastVerifiedAtUtc.Value,DateTimeKind.Utc));
        if(settings.Environment!=ExchangeEnvironment.Testnet||!profile.IsTestnet||!profile.Enabled||!profile.ExecutionEnabled||!profile.ReadPermission||!profile.TradePermission||access is null||verified is null||now-access.Value>TimeSpan.FromMinutes(30)||now-verified.Value>TimeSpan.FromMinutes(30)||access.Value>now||verified.Value>now)
            return Task.FromResult(new TradingAuthorizationModeChangeResult(false,"authorization.auto-readiness-required",settings.AuthorizationMode,TradingAuthorizationMode.Auto));
        return ChangeAuthorizationModeAsync(settings,TradingAuthorizationMode.Auto,true,true,userId,deviceId,reason,auditStore,ct);
    }
    private TradingAuthorizationMode ReadPersistedAuthorizationMode()
    {
        if(!File.Exists(_path))return TradingAuthorizationMode.Review;
        try
        {
            using var document=JsonDocument.Parse(File.ReadAllText(_path));
            if(!document.RootElement.TryGetProperty(nameof(AgentSettings.AuthorizationMode),out var value))return TradingAuthorizationMode.Review;
            if(value.ValueKind==JsonValueKind.String&&Enum.TryParse<TradingAuthorizationMode>(value.GetString(),true,out var named)&&Enum.IsDefined(named))return named;
            if(value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var numeric)&&Enum.IsDefined(typeof(TradingAuthorizationMode),numeric))return(TradingAuthorizationMode)numeric;
        }
        catch(JsonException){}
        catch(IOException){}
        catch(UnauthorizedAccessException){}
        return TradingAuthorizationMode.Review;
    }
    public (string Key,string Secret) GetCredentials(AgentSettings s)
    {
        var slot=s.Environment==ExchangeEnvironment.Testnet?s.Testnet:s.Mainnet;
        return(SecretVaultService.Decrypt(slot.EncryptedApiKey),SecretVaultService.Decrypt(slot.EncryptedApiSecret));
    }
    public ExchangeConnectionProfile GetActiveExchange(AgentSettings settings,bool validate=true)
    {
        EnsureExchangeProfiles(settings);var profile=settings.Exchanges.FirstOrDefault(x=>x.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase)&&x.Enabled&&x.ExecutionEnabled)??settings.Exchanges.FirstOrDefault(x=>x.Enabled&&x.ExecutionEnabled)??throw new InvalidOperationException("No enabled execution exchange provider is configured.");if(validate)ValidateExchangeProfile(profile);return profile;
    }
    public IReadOnlyDictionary<string,string> GetExchangeCredentials(ExchangeConnectionProfile profile)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var item in profile.EncryptedCredentials)result[item.Key]=SecretVaultService.Decrypt(item.Value);return result;
    }
    public void SaveExchangeCredentials(AgentSettings settings,string apiKey,string apiSecret)
    {
        if(string.IsNullOrWhiteSpace(apiKey)||string.IsNullOrWhiteSpace(apiSecret))throw new InvalidOperationException("Exchange credentials cannot be empty.");
        var slot=settings.Environment==ExchangeEnvironment.Testnet?settings.Testnet:settings.Mainnet;slot.EncryptedApiKey=SecretVaultService.Encrypt(apiKey.Trim());slot.EncryptedApiSecret=SecretVaultService.Encrypt(apiSecret.Trim());EnsureExchangeProfiles(settings);var isTestnet=settings.Environment==ExchangeEnvironment.Testnet;var profile=settings.Exchanges.FirstOrDefault(x=>x.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase)&&x.IsTestnet==isTestnet);if(profile is null){profile=new ExchangeConnectionProfile{ProviderId="binance-futures",DisplayName=isTestnet?"Binance Futures Testnet":"Binance Futures",IsTestnet=isTestnet,Endpoint=slot.ApiBaseUrl};settings.Exchanges.Add(profile);}profile.EncryptedCredentials["apiKey"]=slot.EncryptedApiKey;profile.EncryptedCredentials["secret"]=slot.EncryptedApiSecret;Save(settings);
    }
    public void SyncActiveExchangeToEnvironmentSlot(AgentSettings settings,ExchangeConnectionProfile profile)
    {
        ValidateExchangeProfile(profile);var expectedTestnet=settings.Environment==ExchangeEnvironment.Testnet;if(profile.IsTestnet!=expectedTestnet)throw new InvalidOperationException("Active exchange profile does not match the selected environment.");if(!profile.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase))return;var slot=expectedTestnet?settings.Testnet:settings.Mainnet;slot.EncryptedApiKey=profile.EncryptedCredentials.GetValueOrDefault("apiKey")??string.Empty;slot.EncryptedApiSecret=profile.EncryptedCredentials.GetValueOrDefault("secret")??string.Empty;slot.ApiBaseUrl=profile.Endpoint;slot.UseProxy=profile.UseProxy;slot.ProxyUrl=profile.ProxyUrl;slot.ReceiveWindow=profile.ReceiveWindow;slot.TimeoutSeconds=profile.TimeoutSeconds;slot.LastVerifiedAtUtc=profile.LastVerifiedAtUtc;slot.ReadPermission=profile.ReadPermission;slot.TradePermission=profile.TradePermission;slot.WithdrawPermission=profile.WithdrawPermission;slot.AccountId=profile.AccountId;
    }
    public void SaveEnvironmentExchange(AgentSettings settings,ExchangeConnectionProfile profile,string? apiKey,string? apiSecret)
    {
        ValidateExchangeProfile(profile);var expectedTestnet=settings.Environment==ExchangeEnvironment.Testnet;if(profile.IsTestnet!=expectedTestnet)throw new InvalidOperationException("Active exchange profile does not match the selected environment.");var slot=expectedTestnet?settings.Testnet:settings.Mainnet;if(!string.IsNullOrWhiteSpace(apiKey))slot.EncryptedApiKey=SecretVaultService.Encrypt(apiKey.Trim());if(!string.IsNullOrWhiteSpace(apiSecret))slot.EncryptedApiSecret=SecretVaultService.Encrypt(apiSecret.Trim());slot.ApiBaseUrl=profile.Endpoint;slot.UseProxy=profile.UseProxy;slot.ProxyUrl=profile.ProxyUrl;slot.ReceiveWindow=profile.ReceiveWindow;slot.TimeoutSeconds=profile.TimeoutSeconds;profile.EncryptedCredentials["apiKey"]=slot.EncryptedApiKey;profile.EncryptedCredentials["secret"]=slot.EncryptedApiSecret;Save(settings);
    }
    public static void ValidateExchangeProfile(ExchangeConnectionProfile profile)
    {
        if(!Uri.TryCreate(profile.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.Scheme!=Uri.UriSchemeHttps||!string.IsNullOrEmpty(endpoint.UserInfo)||!endpoint.IsDefaultPort||endpoint.AbsolutePath.TrimEnd('/').Length>0||!string.IsNullOrEmpty(endpoint.Query)||!string.IsNullOrEmpty(endpoint.Fragment))throw new InvalidOperationException("Exchange endpoint must be a credential-free HTTPS origin.");
        var expectedHost=profile.IsTestnet
            ?profile.ProviderId.ToLowerInvariant() switch
            {
                "binance-futures"=>"testnet.binancefuture.com",
                "binance-mcp-local"=>"testnet.binancefuture.com",
                "okx"=>"www.okx.com",
                "okx-mcp"=>"www.okx.com",
                "bybit"=>"api-testnet.bybit.com",
                "gate"=>"api-testnet.gateapi.io",
                "bitget"=>"api.bitget.com",
                _=>null
            }
            :profile.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase)?"fapi.binance.com":null;
        if(expectedHost is null||!endpoint.Host.Equals(expectedHost,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(profile.IsTestnet
                ?$"{profile.ProviderId} Testnet endpoint is not an official allowlisted host."
                :$"{profile.ProviderId} Mainnet endpoint is not enabled.");
    }
    public static void ValidateBrainEndpoint(BrainSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if(!string.Equals(slot.Provider,"WPE Local Brain",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Remote Brain providers are retired; trading uses WPE Local Brain only.");
        if(!slot.IsLocal||!string.IsNullOrWhiteSpace(slot.Endpoint)||!string.IsNullOrWhiteSpace(slot.EncryptedKey))
            throw new InvalidOperationException("WPE Local Brain is built in and does not accept an endpoint or API key.");
        if(!string.Equals(slot.Model,"deterministic-local-v1",StringComparison.Ordinal))
            throw new InvalidOperationException("WPE Local Brain must use deterministic-local-v1.");
    }
    public void ConfirmMainnet(AgentSettings settings,string confirmation)
    {
        if(!string.Equals(confirmation,"ENABLE MAINNET",StringComparison.Ordinal))throw new InvalidOperationException("主网确认短语不匹配");settings.MainnetTradingConfirmed=true;settings.MainnetConfirmedAtUtc=DateTime.UtcNow;Save(settings);
    }
    public void RevokeMainnet(AgentSettings settings){settings.MainnetTradingConfirmed=false;settings.MainnetConfirmedAtUtc=null;if(settings.Environment==ExchangeEnvironment.Mainnet)settings.Environment=ExchangeEnvironment.Testnet;Save(settings);}
    private static AgentSettings Normalize(AgentSettings settings)
    {
        settings.Risk??=new();settings.Decision??=new();settings.Notification??=new();settings.DataSources??=new();settings.Brains??=new(StringComparer.OrdinalIgnoreCase);settings.Testnet??=new();settings.Mainnet??=new(){ApiBaseUrl="https://fapi.binance.com"};settings.AiMode=Enum.IsDefined(typeof(global::币安量化机器人.Core.Models.AiRuntimeMode),settings.AiMode)?settings.AiMode:global::币安量化机器人.Core.Models.AiRuntimeMode.LocalOnly;settings.AuthorizationMode=Enum.IsDefined(settings.AuthorizationMode)?settings.AuthorizationMode:TradingAuthorizationMode.Review;settings.Testnet.ApiBaseUrl=SafeEndpoint(settings.Testnet.ApiBaseUrl,"https://testnet.binancefuture.com");settings.Mainnet.ApiBaseUrl=SafeEndpoint(settings.Mainnet.ApiBaseUrl,"https://fapi.binance.com");settings.Testnet.ReceiveWindow=Math.Clamp(settings.Testnet.ReceiveWindow,1000,60000);settings.Mainnet.ReceiveWindow=Math.Clamp(settings.Mainnet.ReceiveWindow,1000,60000);settings.Testnet.TimeoutSeconds=Math.Clamp(settings.Testnet.TimeoutSeconds,5,120);settings.Mainnet.TimeoutSeconds=Math.Clamp(settings.Mainnet.TimeoutSeconds,5,120);settings.Symbols=settings.Symbols?.Select(x=>x.Trim().ToUpperInvariant()).Where(x=>x.EndsWith("USDT",StringComparison.Ordinal)&&x.Length is >=7 and <=20).Distinct().Take(8).ToList()??[];EnsureExchangeProfiles(settings);
        foreach(var brain in settings.Brains.Values){brain.MaxTokens=Math.Clamp(brain.MaxTokens,128,32768);brain.Temperature=Math.Clamp(brain.Temperature,0,2);brain.TimeoutSeconds=Math.Clamp(brain.TimeoutSeconds,5,300);brain.RetryCount=Math.Clamp(brain.RetryCount,0,5);brain.ContextLimit=Math.Clamp(brain.ContextLimit,2048,1000000);}
        EnsureLocalBrain(settings);
        settings.Notification.Telegram??=new();settings.Notification.WhatsApp??=new();settings.Notification.EventKinds??=[];
        settings.Notification.Telegram.TimeoutSeconds=Math.Clamp(settings.Notification.Telegram.TimeoutSeconds,1,60);
        settings.Notification.Telegram.MaxRequestsPerMinute=Math.Clamp(settings.Notification.Telegram.MaxRequestsPerMinute,1,600);
        settings.Notification.WhatsApp.TimeoutSeconds=Math.Clamp(settings.Notification.WhatsApp.TimeoutSeconds,1,60);
        settings.Notification.WhatsApp.MaxRequestsPerMinute=Math.Clamp(settings.Notification.WhatsApp.MaxRequestsPerMinute,1,600);
        if(!System.Text.RegularExpressions.Regex.IsMatch(settings.Notification.WhatsApp.ApiVersion??string.Empty,"^v[0-9]{1,2}\\.[0-9]{1,2}$"))settings.Notification.WhatsApp.ApiVersion="v22.0";
        settings.Decision.MinimumEvidenceCompleteness=Math.Clamp(settings.Decision.MinimumEvidenceCompleteness,60,100);
        settings.Decision.MaximumEvidenceAgeMinutes=Math.Clamp(settings.Decision.MaximumEvidenceAgeMinutes,1,30);
        settings.Decision.MinimumMarketQuality=Math.Clamp(settings.Decision.MinimumMarketQuality,50,95);
        settings.Risk.Leverage=Math.Clamp(settings.Risk.Leverage,1,20);
        settings.Risk.MaxMargin=Math.Clamp(settings.Risk.MaxMargin,.10m,.50m);
        settings.Risk.DailyDrawdownLimit=Math.Clamp(settings.Risk.DailyDrawdownLimit,.02m,.10m);
        settings.Risk.MaxRiskPerTrade=Math.Clamp(settings.Risk.MaxRiskPerTrade,.0025m,.02m);
        settings.Risk.MaxSymbolExposure=Math.Clamp(settings.Risk.MaxSymbolExposure,.05m,.35m);
        settings.Risk.MaxAccountExposure=Math.Clamp(settings.Risk.MaxAccountExposure,.10m,.60m);
        settings.Risk.MaxDailyLoss=Math.Clamp(settings.Risk.MaxDailyLoss,.01m,.08m);
        settings.Risk.MaxAtrPercent=Math.Clamp(settings.Risk.MaxAtrPercent,.01,.12);
        settings.Risk.MinimumLiquidityScore=Math.Clamp(settings.Risk.MinimumLiquidityScore,.20,.95);
        settings.Risk.MaximumSpreadBps=Math.Clamp(settings.Risk.MaximumSpreadBps,1,30);
        settings.Risk.MaximumSlippageBps=Math.Clamp(settings.Risk.MaximumSlippageBps,2,50);
        settings.Risk.MinimumRiskReward=Math.Clamp(settings.Risk.MinimumRiskReward,1.2,4.0);
        settings.Risk.ApiFailureThreshold=Math.Clamp(settings.Risk.ApiFailureThreshold,2,10);
        settings.Risk.MaxPortfolioVaR99=Math.Clamp(settings.Risk.MaxPortfolioVaR99,.005,.10);
        settings.Risk.MaxPortfolioCVaR99=Math.Clamp(settings.Risk.MaxPortfolioCVaR99,.01,.15);
        settings.Risk.MaxLargestPositionShare=Math.Clamp(settings.Risk.MaxLargestPositionShare,.30,1);
        settings.Risk.MaxCorrelatedExposure=Math.Clamp(settings.Risk.MaxCorrelatedExposure,.10,.60);
        settings.Risk.MinimumHistoricalDays=Math.Clamp(settings.Risk.MinimumHistoricalDays,90,1095);
        settings.Risk.MinimumBacktestTrades=Math.Clamp(settings.Risk.MinimumBacktestTrades,10,200);
        settings.Risk.MarginTiers=settings.Risk.MarginTiers?.Where(x=>x>0).Select(x=>Math.Min(x,settings.Risk.MaxMargin)).Distinct().OrderBy(x=>x).Take(3).ToArray()??[];if(settings.Risk.MarginTiers.Length==0)settings.Risk.MarginTiers=[.10m,.20m,.35m];
        return settings;
    }
    private static void EnsureLocalBrain(AgentSettings settings)
    {
        const string name="WPE Local Brain";
        var local=settings.Brains.TryGetValue(name,out var existing)
            ?existing
            :new BrainSlot();

        local.Provider=name;
        local.Endpoint=string.Empty;
        local.Model="deterministic-local-v1";
        local.EncryptedKey=string.Empty;
        local.IsLocal=true;
        local.EnableFallback=false;
        local.FallbackBrain=string.Empty;
        local.PromptVersion="wpe-local-deterministic-v1";

        settings.Brains=new Dictionary<string,BrainSlot>(StringComparer.OrdinalIgnoreCase)
        {
            [name]=local
        };
        settings.ActiveBrain=name;
    }
    private static string SafeEndpoint(string? value,string fallback)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps?uri.ToString().TrimEnd('/'):fallback;
    private static void EnsureExchangeProfiles(AgentSettings settings)
    {
        settings.Exchanges??=[];
        foreach(var profile in settings.Exchanges)
        {
            profile.EncryptedCredentials??=new(StringComparer.OrdinalIgnoreCase);
            profile.SymbolMappings??=new(StringComparer.OrdinalIgnoreCase);
            profile.TimeoutSeconds=Math.Clamp(profile.TimeoutSeconds,5,120);
            profile.ReceiveWindow=Math.Clamp(profile.ReceiveWindow,1000,60000);
        }

        if(settings.Exchanges.Count==0)
        {
            var profile=new ExchangeConnectionProfile
            {
                Id="binance-testnet-default",
                ProviderId="binance-futures",
                DisplayName="Binance Futures Testnet",
                IsTestnet=true,
                Endpoint=settings.Testnet.ApiBaseUrl,
                UseProxy=settings.Testnet.UseProxy,
                ProxyUrl=settings.Testnet.ProxyUrl,
                ReceiveWindow=settings.Testnet.ReceiveWindow,
                TimeoutSeconds=settings.Testnet.TimeoutSeconds,
                LastVerifiedAtUtc=settings.Testnet.LastVerifiedAtUtc,
                ReadPermission=settings.Testnet.ReadPermission,
                TradePermission=settings.Testnet.TradePermission,
                WithdrawPermission=settings.Testnet.WithdrawPermission,
                AccountId=settings.Testnet.AccountId,
                EncryptedCredentials=new(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"]=settings.Testnet.EncryptedApiKey,
                    ["secret"]=settings.Testnet.EncryptedApiSecret
                }
            };
            settings.Exchanges.Add(profile);
            settings.ActiveExecutionConnectionId=profile.Id;
        }

        var nativeCandidates=settings.Exchanges
            .Where(profile=>
                profile.Enabled&&
                profile.IsTestnet&&
                profile.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var upstream=nativeCandidates.FirstOrDefault(profile=>
            profile.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase));
        if(upstream is null&&nativeCandidates.Length==1)upstream=nativeCandidates[0];

        var localMcp=settings.Exchanges.FirstOrDefault(profile=>
            profile.ProviderId.Equals("binance-mcp-local",StringComparison.OrdinalIgnoreCase));
        if(localMcp is null&&upstream is not null)
        {
            localMcp=new ExchangeConnectionProfile
            {
                Id="binance-mcp-testnet-default",
                ProviderId="binance-mcp-local",
                DisplayName="Binance Futures Testnet MCP",
                Enabled=true,
                ExecutionEnabled=false,
                IsTestnet=true,
                Endpoint=upstream.Endpoint,
                UseProxy=upstream.UseProxy,
                ProxyUrl=upstream.ProxyUrl,
                ReceiveWindow=upstream.ReceiveWindow,
                TimeoutSeconds=upstream.TimeoutSeconds,
                UpstreamConnectionId=upstream.Id,
                EncryptedCredentials=new(StringComparer.OrdinalIgnoreCase),
                SymbolMappings=new(StringComparer.OrdinalIgnoreCase)
            };
            settings.Exchanges.Add(localMcp);
        }
        else if(localMcp is not null&&
                string.IsNullOrWhiteSpace(localMcp.UpstreamConnectionId)&&
                upstream is not null&&
                !localMcp.Id.Equals(upstream.Id,StringComparison.OrdinalIgnoreCase))
        {
            localMcp.UpstreamConnectionId=upstream.Id;
        }

        if(string.IsNullOrWhiteSpace(settings.ActiveExecutionConnectionId)||
           settings.Exchanges.All(profile=>
               !profile.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase)))
            settings.ActiveExecutionConnectionId=settings.Exchanges.First().Id;
    }
    private static AgentSettings Defaults() => new() { Brains = new(StringComparer.OrdinalIgnoreCase) { ["WPE Local Brain"] = new BrainSlot() } };
}

public enum ThemeMode { Dark, Light, Auto }

public sealed class ThemePreferenceStore
{
    private readonly string _path = AppDataPaths.File("ui-preferences.json");
    public ThemeMode Load()
    {
        try
        {
            if (!File.Exists(_path)) return ThemeMode.Dark;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            return Enum.TryParse<ThemeMode>(doc.RootElement.GetProperty("theme").GetString(), true, out var mode) ? mode : ThemeMode.Dark;
        }
        catch { return ThemeMode.Dark; }
    }
    public void Save(ThemeMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new { theme = mode.ToString() }));
    }
}
