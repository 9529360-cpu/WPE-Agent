using System.Diagnostics;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Access;

public sealed record AccessCheck(string Key,bool Passed,bool Critical,string Detail,long DurationMs=0);
public sealed class AccessReadinessReport
{
    public DateTime CheckedAtUtc { get; init; }=DateTime.UtcNow;public List<AccessCheck> Checks { get; }=[];public bool Ready=>Checks.Count>0&&Checks.Where(x=>x.Critical).All(x=>x.Passed);public string Summary=>string.Join(Environment.NewLine,Checks.Select(x=>$"{(x.Passed?"●":"○")} {x.Detail}"));
}
public sealed class AccessReadinessService
{
    public async Task<AccessReadinessReport> CheckAsync(AgentSettings settings,CancellationToken ct=default)
    {
        var report=new AccessReadinessReport();void Add(string key,bool pass,bool critical,string detail,long ms=0)=>report.Checks.Add(new(key,pass,critical,detail,ms));
        Add("environment",settings.Environment==ExchangeEnvironment.Testnet,true,settings.Environment==ExchangeEnvironment.Testnet?"Testnet 隔离已启用":"Mainnet 当前被禁止");
        var slot=settings.Testnet;string key="",secret="";try{key=SecretVaultService.Decrypt(slot.EncryptedApiKey);secret=SecretVaultService.Decrypt(slot.EncryptedApiSecret);}catch{}Add("credentials",key.Length>8&&secret.Length>8,true,key.Length>8&&secret.Length>8?"Binance 凭据已加密加载":"Binance 凭据不完整");
        if(key.Length>8&&secret.Length>8)try
        {
            var sw=Stopwatch.StartNew();using var api=new BinanceApiClient(useTestnet:true,endpoint:slot.ApiBaseUrl,timeoutSeconds:slot.TimeoutSeconds,useProxy:slot.UseProxy,proxyUrl:slot.ProxyUrl,receiveWindow:slot.ReceiveWindow);api.SetApiCredentials(key,secret);using var timeDoc=JsonDocument.Parse(await api.GetPublicRawAsync("/fapi/v1/time",null,ct));var exchangeTime=DateTimeOffset.FromUnixTimeMilliseconds(timeDoc.RootElement.GetProperty("serverTime").GetInt64()).UtcDateTime;var skew=Math.Abs((DateTime.UtcNow-exchangeTime).TotalMilliseconds);using var account=JsonDocument.Parse(await api.GetSignedRawAsync("/fapi/v2/account",null,ct));var root=account.RootElement;var canTrade=!root.TryGetProperty("canTrade",out var trade)||trade.GetBoolean();var canWithdraw=root.TryGetProperty("canWithdraw",out var withdraw)&&withdraw.GetBoolean();var alias=root.TryGetProperty("accountAlias",out var aliasNode)?aliasNode.GetString()??"Testnet":"Testnet";var balance=root.GetProperty("assets").EnumerateArray().Where(x=>x.GetProperty("asset").GetString()=="USDT").Select(x=>x.GetProperty("availableBalance").GetString()).FirstOrDefault()??"0";slot.ReadPermission=true;slot.TradePermission=canTrade;slot.WithdrawPermission=canWithdraw;slot.AccountId=alias;slot.LastVerifiedAtUtc=DateTime.UtcNow;Add("exchange",true,true,$"Binance Futures Testnet 已连接 · {alias} · 可用 {balance} USDT",sw.ElapsedMilliseconds);Add("trade_permission",canTrade,true,canTrade?"API 下单权限可用":"API 缺少下单权限");Add("withdraw_permission",!canWithdraw,false,canWithdraw?"警告：检测到提现权限，请关闭":"未检测到提现权限");Add("clock",skew<3000,true,$"交易所时间偏差 {skew:F0} ms");
        }catch(Exception ex){Add("exchange",false,true,"Binance 连接失败："+Safe(ex.Message));}
        if(settings.Brains.TryGetValue(settings.ActiveBrain,out var brain))try{var sw=Stopwatch.StartNew();var brainKey=SecretVaultService.Decrypt(brain.EncryptedKey);var health=await new HttpBrainProvider(brain,brainKey).HealthCheckAsync(ct);Add("brain",health.Healthy,true,$"{brain.Provider} / {brain.Model} · {health.Message} · {sw.ElapsedMilliseconds} ms",sw.ElapsedMilliseconds);}catch(Exception ex){Add("brain",false,true,"AI Brain 连接失败："+Safe(ex.Message));}else Add("brain",false,true,"AI Brain 未配置");
        try{var db=new AgentSqliteStore();await db.SetStateAsync("access-health",DateTime.UtcNow.ToString("O"),ct);Add("database",true,true,"本地数据库可读写");}catch(Exception ex){Add("database",false,true,"本地数据库不可用："+Safe(ex.Message));}
        var risk=settings.Risk.MaxRiskPerTrade>0&&settings.Risk.MaxAccountExposure<=.60m&&settings.Risk.Leverage<=20;Add("risk",risk,true,risk?"Risk Manager 参数有效":"风控参数越界");Add("data",report.Checks.Any(x=>x.Key=="exchange"&&x.Passed),true,report.Checks.Any(x=>x.Key=="exchange"&&x.Passed)?"行情数据源健康":"行情数据源不可用");return report;
    }
    private static string Safe(string value)=>value.Replace("\r"," ").Replace("\n"," ")[..Math.Min(180,value.Length)];
}
