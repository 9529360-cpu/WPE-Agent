using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

public sealed class BrainSlot { public string Provider { get; set; } = "DeepSeek"; public string Endpoint { get; set; } = "https://api.deepseek.com/chat/completions"; public string Model { get; set; } = "deepseek-chat"; public string EncryptedKey { get; set; } = string.Empty; public int MaxTokens { get; set; }=1200; public double Temperature { get; set; }=.1; public int TimeoutSeconds { get; set; }=75; public int RetryCount { get; set; }=2; public bool EnableFallback { get; set; } public string FallbackBrain { get; set; }=string.Empty; public bool IsLocal { get; set; } public string PromptVersion { get; set; }="wpe-core-v3"; public int ContextLimit { get; set; }=32000; }
public sealed class EnvironmentSlot { public string EncryptedApiKey { get; set; } = string.Empty; public string EncryptedApiSecret { get; set; } = string.Empty; public bool UseProxy { get; set; } public string ProxyUrl { get; set; }=string.Empty; public string ApiBaseUrl { get; set; }="https://testnet.binancefuture.com"; public int ReceiveWindow { get; set; }=5000; public int TimeoutSeconds { get; set; }=20; public DateTime? LastVerifiedAtUtc { get; set; } public bool ReadPermission { get; set; } public bool TradePermission { get; set; } public bool WithdrawPermission { get; set; } public string AccountId { get; set; }=string.Empty; }
public sealed class NotificationSlot { public bool Enabled { get; set; } public string Channel { get; set; }="Local"; public string EncryptedEndpoint { get; set; }=string.Empty; }
public sealed class DataSourceSlot { public bool BinanceRealtimeEnabled { get; set; }=true; public bool NewsEnabled { get; set; }=true; }
public sealed class AgentSettings { public ExchangeEnvironment Environment { get; set; } = ExchangeEnvironment.Testnet; public string EnvironmentMode { get; set; }="FuturesTestnet"; public string ActiveBrain { get; set; } = "DeepSeek"; public string ActiveUser { get; set; }=string.Empty; public bool SetupCompleted { get; set; } public DateTime? SetupCompletedAtUtc { get; set; } public DateTime? LastAccessCheckAtUtc { get; set; } public List<string> Symbols { get; set; } = ["BTCUSDT","ETHUSDT"]; public Dictionary<string, BrainSlot> Brains { get; set; } = new(StringComparer.OrdinalIgnoreCase); public List<ExchangeConnectionProfile> Exchanges { get; set; }=[]; public string ActiveExecutionConnectionId { get; set; }=string.Empty; public EnvironmentSlot Testnet { get; set; } = new(); public EnvironmentSlot Mainnet { get; set; } = new(){ApiBaseUrl="https://fapi.binance.com"}; public bool MainnetTradingConfirmed { get; set; } public DateTime? MainnetConfirmedAtUtc { get; set; } public RiskLimits Risk { get; set; } = new(); public DecisionPolicy Decision { get; set; } = new(); public NotificationSlot Notification { get; set; }=new(); public DataSourceSlot DataSources { get; set; }=new(); }
public sealed class AgentSettingsStore
{
    private readonly string _path = AppDataPaths.File("agent-settings.json");
    public AgentSettings Load() { try { if (File.Exists(_path)) return Normalize(JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(_path)) ?? Defaults()); } catch { } return Defaults(); }
    public void Save(AgentSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented=true })); }
    public void ImportDesktopTestnetIfEmpty(AgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Testnet.EncryptedApiKey)) return;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = new[] { "币安API.txt", "币安API模板.txt" }.Select(x => Path.Combine(desktop,x)).FirstOrDefault(File.Exists);
        if (path is null) return;
        var v = File.ReadLines(path).Where(x => x.Contains('=' )).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0].Trim().ToUpperInvariant(),x=>x[1].Trim());
        if (v.TryGetValue("API_KEY",out var key) && v.TryGetValue("API_SECRET",out var secret)) { settings.Testnet.EncryptedApiKey=SecretVaultService.Encrypt(key); settings.Testnet.EncryptedApiSecret=SecretVaultService.Encrypt(secret); EnsureExchangeProfiles(settings);var profile=settings.Exchanges.First(x=>x.ProviderId=="binance-futures"&&x.IsTestnet);profile.EncryptedCredentials["apiKey"]=settings.Testnet.EncryptedApiKey;profile.EncryptedCredentials["secret"]=settings.Testnet.EncryptedApiSecret;Save(settings); }
    }
    public void ImportDesktopDeepSeekIfEmpty(AgentSettings settings)
    {
        if(!settings.Brains.TryGetValue("DeepSeek",out var slot)){slot=new BrainSlot();settings.Brains["DeepSeek"]=slot;}if(!string.IsNullOrWhiteSpace(slot.EncryptedKey))return;
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"API.txt");if(!File.Exists(path))return;var key=File.ReadAllText(path).Trim();if(key.Length>10){slot.EncryptedKey=SecretVaultService.Encrypt(key);Save(settings);}
    }
    public (string Key,string Secret) GetCredentials(AgentSettings s)
    {
        var slot=s.Environment==ExchangeEnvironment.Testnet?s.Testnet:s.Mainnet;
        try{return(SecretVaultService.Decrypt(slot.EncryptedApiKey),SecretVaultService.Decrypt(slot.EncryptedApiSecret));}
        catch(CryptographicException){return(string.Empty,string.Empty);}
        catch(FormatException){return(string.Empty,string.Empty);}
    }
    public ExchangeConnectionProfile GetActiveExchange(AgentSettings settings)
    {
        EnsureExchangeProfiles(settings);return settings.Exchanges.FirstOrDefault(x=>x.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase)&&x.Enabled&&x.ExecutionEnabled)??settings.Exchanges.FirstOrDefault(x=>x.Enabled&&x.ExecutionEnabled)??throw new InvalidOperationException("No enabled execution exchange provider is configured.");
    }
    public IReadOnlyDictionary<string,string> GetExchangeCredentials(ExchangeConnectionProfile profile)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var item in profile.EncryptedCredentials)try{result[item.Key]=SecretVaultService.Decrypt(item.Value);}catch{result[item.Key]=string.Empty;}return result;
    }
    public void ConfirmMainnet(AgentSettings settings,string confirmation)
    {
        if(!string.Equals(confirmation,"ENABLE MAINNET",StringComparison.Ordinal))throw new InvalidOperationException("主网确认短语不匹配");settings.MainnetTradingConfirmed=true;settings.MainnetConfirmedAtUtc=DateTime.UtcNow;Save(settings);
    }
    public void RevokeMainnet(AgentSettings settings){settings.MainnetTradingConfirmed=false;settings.MainnetConfirmedAtUtc=null;if(settings.Environment==ExchangeEnvironment.Mainnet)settings.Environment=ExchangeEnvironment.Testnet;Save(settings);}
    private static AgentSettings Normalize(AgentSettings settings)
    {
        settings.Risk??=new();settings.Decision??=new();settings.Notification??=new();settings.DataSources??=new();settings.Brains??=new(StringComparer.OrdinalIgnoreCase);settings.Testnet??=new();settings.Mainnet??=new(){ApiBaseUrl="https://fapi.binance.com"};settings.Testnet.ApiBaseUrl=SafeEndpoint(settings.Testnet.ApiBaseUrl,"https://testnet.binancefuture.com");settings.Mainnet.ApiBaseUrl=SafeEndpoint(settings.Mainnet.ApiBaseUrl,"https://fapi.binance.com");settings.Testnet.ReceiveWindow=Math.Clamp(settings.Testnet.ReceiveWindow,1000,60000);settings.Mainnet.ReceiveWindow=Math.Clamp(settings.Mainnet.ReceiveWindow,1000,60000);settings.Testnet.TimeoutSeconds=Math.Clamp(settings.Testnet.TimeoutSeconds,5,120);settings.Mainnet.TimeoutSeconds=Math.Clamp(settings.Mainnet.TimeoutSeconds,5,120);settings.Symbols=settings.Symbols?.Select(x=>x.Trim().ToUpperInvariant()).Where(x=>x.EndsWith("USDT",StringComparison.Ordinal)&&x.Length is >=7 and <=20).Distinct().Take(8).ToList()??[];if(settings.Symbols.Count==0)settings.Symbols=["BTCUSDT","ETHUSDT"];EnsureExchangeProfiles(settings);
        foreach(var brain in settings.Brains.Values){brain.MaxTokens=Math.Clamp(brain.MaxTokens,128,32768);brain.Temperature=Math.Clamp(brain.Temperature,0,2);brain.TimeoutSeconds=Math.Clamp(brain.TimeoutSeconds,5,300);brain.RetryCount=Math.Clamp(brain.RetryCount,0,5);brain.ContextLimit=Math.Clamp(brain.ContextLimit,2048,1000000);}
        settings.Decision.MinimumConfidence=Math.Clamp(settings.Decision.MinimumConfidence,.50,.95);
        settings.Decision.MinimumDirectionalScore=Math.Clamp(settings.Decision.MinimumDirectionalScore,.10,.80);
        settings.Decision.MaximumConflictRatio=Math.Clamp(settings.Decision.MaximumConflictRatio,.10,.90);
        settings.Decision.MinimumEvidenceCompleteness=Math.Clamp(settings.Decision.MinimumEvidenceCompleteness,60,100);
        settings.Decision.MaximumEvidenceAgeMinutes=Math.Clamp(settings.Decision.MaximumEvidenceAgeMinutes,1,30);
        settings.Decision.MinimumMarketQuality=Math.Clamp(settings.Decision.MinimumMarketQuality,50,95);
        settings.Decision.MinimumResearchScore=Math.Clamp(settings.Decision.MinimumResearchScore,.20,.90);
        settings.Risk.Leverage=Math.Clamp(settings.Risk.Leverage,1,20);
        settings.Risk.MaxMargin=Math.Clamp(settings.Risk.MaxMargin,.10m,.50m);
        settings.Risk.DailyDrawdownLimit=Math.Clamp(settings.Risk.DailyDrawdownLimit,.02m,.10m);
        settings.Risk.MaxRiskPerTrade=Math.Clamp(settings.Risk.MaxRiskPerTrade,.0025m,.02m);
        settings.Risk.MaxSymbolExposure=Math.Clamp(settings.Risk.MaxSymbolExposure,.05m,.35m);
        settings.Risk.MaxAccountExposure=Math.Clamp(settings.Risk.MaxAccountExposure,.10m,.60m);
        settings.Risk.MaxDailyLoss=Math.Clamp(settings.Risk.MaxDailyLoss,.01m,.08m);
        settings.Risk.MaxConsecutiveLosses=Math.Clamp(settings.Risk.MaxConsecutiveLosses,2,8);
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
    private static string SafeEndpoint(string? value,string fallback)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps?uri.ToString().TrimEnd('/'):fallback;
    private static void EnsureExchangeProfiles(AgentSettings settings)
    {
        settings.Exchanges??=[];foreach(var profile in settings.Exchanges){profile.EncryptedCredentials??=new(StringComparer.OrdinalIgnoreCase);profile.SymbolMappings??=new(StringComparer.OrdinalIgnoreCase);profile.TimeoutSeconds=Math.Clamp(profile.TimeoutSeconds,5,120);profile.ReceiveWindow=Math.Clamp(profile.ReceiveWindow,1000,60000);}
        if(settings.Exchanges.Count==0){var profile=new ExchangeConnectionProfile{Id="binance-testnet-default",ProviderId="binance-futures",DisplayName="Binance Futures Testnet",IsTestnet=true,Endpoint=settings.Testnet.ApiBaseUrl,UseProxy=settings.Testnet.UseProxy,ProxyUrl=settings.Testnet.ProxyUrl,ReceiveWindow=settings.Testnet.ReceiveWindow,TimeoutSeconds=settings.Testnet.TimeoutSeconds,LastVerifiedAtUtc=settings.Testnet.LastVerifiedAtUtc,ReadPermission=settings.Testnet.ReadPermission,TradePermission=settings.Testnet.TradePermission,WithdrawPermission=settings.Testnet.WithdrawPermission,AccountId=settings.Testnet.AccountId,EncryptedCredentials=new(StringComparer.OrdinalIgnoreCase){{"apiKey",settings.Testnet.EncryptedApiKey},{"secret",settings.Testnet.EncryptedApiSecret}}};settings.Exchanges.Add(profile);settings.ActiveExecutionConnectionId=profile.Id;}
        if(string.IsNullOrWhiteSpace(settings.ActiveExecutionConnectionId)||settings.Exchanges.All(x=>!x.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase)))settings.ActiveExecutionConnectionId=settings.Exchanges.First().Id;
    }
    private static AgentSettings Defaults() => new() { Brains = new(StringComparer.OrdinalIgnoreCase) { ["DeepSeek"] = new BrainSlot() } };
}
