using System.Text.Json;
using System.IO;
using 币安量化机器人.Services;

namespace 币安量化机器人.Services.Agent;

public sealed class BrainSlot { public string Provider { get; set; } = "DeepSeek"; public string Endpoint { get; set; } = "https://api.deepseek.com/chat/completions"; public string Model { get; set; } = "deepseek-chat"; public string EncryptedKey { get; set; } = string.Empty; }
public sealed class EnvironmentSlot { public string EncryptedApiKey { get; set; } = string.Empty; public string EncryptedApiSecret { get; set; } = string.Empty; }
public sealed class AgentSettings { public ExchangeEnvironment Environment { get; set; } = ExchangeEnvironment.Testnet; public string ActiveBrain { get; set; } = "DeepSeek"; public Dictionary<string, BrainSlot> Brains { get; set; } = new(StringComparer.OrdinalIgnoreCase); public EnvironmentSlot Testnet { get; set; } = new(); public EnvironmentSlot Mainnet { get; set; } = new(); public bool MainnetTradingConfirmed { get; set; } public DateTime? MainnetConfirmedAtUtc { get; set; } public RiskLimits Risk { get; set; } = new(); }
public sealed class AgentSettingsStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "Data", "agent-settings.json");
    public AgentSettings Load() { try { if (File.Exists(_path)) return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(_path)) ?? Defaults(); } catch { } return Defaults(); }
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
    private static AgentSettings Defaults() => new() { Brains = new(StringComparer.OrdinalIgnoreCase) { ["DeepSeek"] = new BrainSlot() } };
}
