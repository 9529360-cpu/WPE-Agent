using System.Text.Json;
using System.IO;
using 币安量化机器人.Services;

namespace 币安量化机器人.Services.Agent;

public sealed class BrainSlot { public string Provider { get; set; } = "DeepSeek"; public string Endpoint { get; set; } = "https://api.deepseek.com/chat/completions"; public string Model { get; set; } = "deepseek-chat"; public string EncryptedKey { get; set; } = string.Empty; }
public sealed class EnvironmentSlot { public string EncryptedApiKey { get; set; } = string.Empty; public string EncryptedApiSecret { get; set; } = string.Empty; }
public sealed class AgentSettings { public ExchangeEnvironment Environment { get; set; } = ExchangeEnvironment.Testnet; public string ActiveBrain { get; set; } = "DeepSeek"; public List<string> Symbols { get; set; } = ["BTCUSDT","ETHUSDT"]; public Dictionary<string, BrainSlot> Brains { get; set; } = new(StringComparer.OrdinalIgnoreCase); public EnvironmentSlot Testnet { get; set; } = new(); public EnvironmentSlot Mainnet { get; set; } = new(); public bool MainnetTradingConfirmed { get; set; } public DateTime? MainnetConfirmedAtUtc { get; set; } public RiskLimits Risk { get; set; } = new(); public DecisionPolicy Decision { get; set; } = new(); }
public sealed class AgentSettingsStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "Data", "agent-settings.json");
    public AgentSettings Load() { try { if (File.Exists(_path)) return Normalize(JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(_path)) ?? Defaults()); } catch { } return Defaults(); }
    public void Save(AgentSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented=true })); }
    public void ImportDesktopTestnetIfEmpty(AgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Testnet.EncryptedApiKey)) return;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = new[] { "币安API.txt", "币安API模板.txt" }.Select(x => Path.Combine(desktop,x)).FirstOrDefault(File.Exists);
        if (path is null) return;
        var v = File.ReadLines(path).Where(x => x.Contains('=' )).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0].Trim().ToUpperInvariant(),x=>x[1].Trim());
        if (v.TryGetValue("API_KEY",out var key) && v.TryGetValue("API_SECRET",out var secret)) { settings.Testnet.EncryptedApiKey=SecretVaultService.Encrypt(key); settings.Testnet.EncryptedApiSecret=SecretVaultService.Encrypt(secret); Save(settings); }
    }
    public void ImportDesktopDeepSeekIfEmpty(AgentSettings settings)
    {
        if(!settings.Brains.TryGetValue("DeepSeek",out var slot)){slot=new BrainSlot();settings.Brains["DeepSeek"]=slot;}if(!string.IsNullOrWhiteSpace(slot.EncryptedKey))return;
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"API.txt");if(!File.Exists(path))return;var key=File.ReadAllText(path).Trim();if(key.Length>10){slot.EncryptedKey=SecretVaultService.Encrypt(key);Save(settings);}
    }
    public (string Key,string Secret) GetCredentials(AgentSettings s) { var slot=s.Environment==ExchangeEnvironment.Testnet?s.Testnet:s.Mainnet; return (SecretVaultService.Decrypt(slot.EncryptedApiKey),SecretVaultService.Decrypt(slot.EncryptedApiSecret)); }
    public void ConfirmMainnet(AgentSettings settings,string confirmation)
    {
        if(!string.Equals(confirmation,"ENABLE MAINNET",StringComparison.Ordinal))throw new InvalidOperationException("主网确认短语不匹配");settings.MainnetTradingConfirmed=true;settings.MainnetConfirmedAtUtc=DateTime.UtcNow;Save(settings);
    }
    public void RevokeMainnet(AgentSettings settings){settings.MainnetTradingConfirmed=false;settings.MainnetConfirmedAtUtc=null;if(settings.Environment==ExchangeEnvironment.Mainnet)settings.Environment=ExchangeEnvironment.Testnet;Save(settings);}
    private static AgentSettings Normalize(AgentSettings settings)
    {
        settings.Risk??=new();settings.Decision??=new();settings.Brains??=new(StringComparer.OrdinalIgnoreCase);settings.Testnet??=new();settings.Mainnet??=new();settings.Symbols=settings.Symbols?.Select(x=>x.Trim().ToUpperInvariant()).Where(x=>x.EndsWith("USDT",StringComparison.Ordinal)&&x.Length is >=7 and <=20).Distinct().Take(8).ToList()??[];if(settings.Symbols.Count==0)settings.Symbols=["BTCUSDT","ETHUSDT"];
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
        settings.Risk.MarginTiers=settings.Risk.MarginTiers?.Where(x=>x>0).Select(x=>Math.Min(x,settings.Risk.MaxMargin)).Distinct().OrderBy(x=>x).Take(3).ToArray()??[];if(settings.Risk.MarginTiers.Length==0)settings.Risk.MarginTiers=[.10m,.20m,.35m];
        return settings;
    }
    private static AgentSettings Defaults() => new() { Brains = new(StringComparer.OrdinalIgnoreCase) { ["DeepSeek"] = new BrainSlot() } };
}
