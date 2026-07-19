using System.Diagnostics;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Access;

public sealed record AccessCheck(string Key,bool Passed,bool Critical,string Detail,long DurationMs=0);
public sealed class AccessReadinessReport
{
    public DateTime CheckedAtUtc { get; init; }=DateTime.UtcNow;
    public List<AccessCheck> Checks { get; }=[];
    public bool Ready=>Checks.Count>0&&Checks.Where(x=>x.Critical).All(x=>x.Passed);
    public string Summary=>string.Join(Environment.NewLine,Checks.Select(x=>$"{(x.Passed?"●":"○")} {x.Detail}"));
}

public sealed class AccessReadinessService
{
    public async Task<AccessReadinessReport> CheckAsync(AgentSettings settings,CancellationToken ct=default)
    {
        var report=new AccessReadinessReport();
        void Add(string key,bool pass,bool critical,string detail,long ms=0)=>report.Checks.Add(new(key,pass,critical,detail,ms));

        var settingsStore=new AgentSettingsStore();
        var profile=settingsStore.GetActiveExchange(settings);
        Add("environment",profile?.IsTestnet==true,true,profile?.IsTestnet==true?"Testnet 隔离已启用":"Mainnet 当前被禁用");
        var catalog=new ExchangeProviderCatalog();
        var installed=profile is not null&&catalog.IsInstalled(profile.ProviderId);
        Add("provider",installed,true,profile is null?"未选择执行交易所":installed?$"{profile.DisplayName} 适配器已加载":$"{profile.DisplayName} 适配器尚未安装");

        IReadOnlyDictionary<string,string> credentials=new Dictionary<string,string>();
        if(profile is not null)try{credentials=settingsStore.GetExchangeCredentials(profile);}catch{}
        var descriptor=profile is null?null:catalog.All.FirstOrDefault(x=>x.Id.Equals(profile.ProviderId,StringComparison.OrdinalIgnoreCase));
        var credentialsReady=descriptor is not null&&descriptor.CredentialFields.Where(x=>x.Required).All(x=>credentials.TryGetValue(x.Key,out var value)&&value.Length>4);
        Add("credentials",credentialsReady,true,credentialsReady?$"{profile!.DisplayName} 凭据已从安全存储加载":"交易所凭据不完整");

        if(profile is not null&&profile.IsTestnet&&installed&&credentialsReady)try
        {
            var sw=Stopwatch.StartNew();
            await using var provider=catalog.Create(profile,credentials);
            var health=await provider.HealthCheckAsync(ct);
            var permission=await provider.CheckPermissionsAsync(ct);
            var account=await provider.GetAccountAsync(ct);
            profile.ReadPermission=permission.CanRead;
            profile.TradePermission=permission.CanTrade;
            profile.WithdrawPermission=permission.CanWithdraw;
            profile.AccountId=permission.AccountId;
            profile.LastVerifiedAtUtc=DateTime.UtcNow;
            Add("exchange",health.Healthy,true,$"{profile.DisplayName} 已连接 · {permission.AccountId} · 可用 {account.AvailableBalance:N2}",sw.ElapsedMilliseconds);
            Add("trade_permission",permission.CanTrade,true,permission.CanTrade?"API 下单权限可用":"API 缺少下单权限");
            Add("withdraw_permission",!permission.CanWithdraw,false,permission.CanWithdraw?"警告：检测到提现权限，请关闭":"未检测到提现权限");
            Add("clock",health.ClockSkewMs<3000,true,$"交易所时间偏差 {health.ClockSkewMs} ms");
        }
        catch(Exception ex){Add("exchange",false,true,$"{profile.DisplayName} 连接失败："+Safe(ex.Message));}

        if(settings.Brains.TryGetValue(settings.ActiveBrain,out var brain))try
        {
            var sw=Stopwatch.StartNew();var brainKey=SecretVaultService.Decrypt(brain.EncryptedKey);var health=await new HttpBrainProvider(brain,brainKey).HealthCheckAsync(ct);
            Add("brain",health.Healthy,true,$"{brain.Provider} / {brain.Model} · {health.Message} · {sw.ElapsedMilliseconds} ms",sw.ElapsedMilliseconds);
        }
        catch(Exception ex){Add("brain",false,true,"AI Brain 连接失败："+Safe(ex.Message));}
        else Add("brain",false,true,"AI Brain 未配置");

        try{var db=new AgentSqliteStore();await db.SetStateAsync("access-health",DateTime.UtcNow.ToString("O"),ct);Add("database",true,true,"本地数据库可读写");}
        catch(Exception ex){Add("database",false,true,"本地数据库不可用："+Safe(ex.Message));}
        var risk=settings.Risk.MaxRiskPerTrade>0&&settings.Risk.MaxAccountExposure<=.60m&&settings.Risk.Leverage<=20;
        Add("risk",risk,true,risk?"Risk Manager 参数有效":"风控参数越界");
        Add("data",report.Checks.Any(x=>x.Key=="exchange"&&x.Passed),true,report.Checks.Any(x=>x.Key=="exchange"&&x.Passed)?"市场数据源健康":"市场数据源不可用");
        var brainIndex=report.Checks.FindIndex(x=>x.Key=="brain");
        if(brainIndex>=0&&credentialsReady)report.Checks[brainIndex]=report.Checks[brainIndex] with{Critical=false};
        Add("local_brain",true,true,"WPE Local Brain / deterministic rules ready");
        return report;
    }

    private static string Safe(string value)=>value.Replace("\r"," ").Replace("\n"," ")[..Math.Min(180,value.Length)];
}
