using System.Reflection;

namespace 币安量化机器人.Services.Exchange;

public sealed class ExchangeProviderCatalog
{
    private readonly Dictionary<string,IExchangeProviderPlugin> _plugins;
    public ExchangeProviderCatalog(IEnumerable<Assembly>? assemblies=null)
    {
        // Executable exchange adapters are a privileged boundary. The default catalog only
        // trusts providers compiled into the WPE host; external plugin metadata is handled by
        // LocalPluginRegistry and must not cause arbitrary DLLs to be loaded into this process.
        var source=(assemblies?.ToArray()??[typeof(ExchangeProviderCatalog).Assembly]).Distinct().ToArray();
        _plugins=source.SelectMany(SafeTypes).Where(t=>!t.IsAbstract&&typeof(IExchangeProviderPlugin).IsAssignableFrom(t)&&t.GetConstructor(Type.EmptyTypes)is not null)
            .Select(t=>(IExchangeProviderPlugin)Activator.CreateInstance(t)!).GroupBy(x=>x.Descriptor.Id,StringComparer.OrdinalIgnoreCase).ToDictionary(x=>x.Key,x=>x.First(),StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyList<ExchangeProviderDescriptor> Installed=>_plugins.Values.Select(x=>x.Descriptor).OrderBy(x=>x.DisplayName).ToArray();
    public IReadOnlyList<ExchangeProviderDescriptor> All=>Installed.Concat(ProviderTemplates.Planned.Where(p=>!_plugins.ContainsKey(p.Id))).ToArray();
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)
    {
        if(!_plugins.TryGetValue(profile.ProviderId,out var plugin))throw new NotSupportedException($"Exchange adapter '{profile.ProviderId}' is not installed.");
        return plugin.Create(profile,credentials);
    }
    public bool IsInstalled(string providerId)=>_plugins.ContainsKey(providerId);
    private static IEnumerable<Type> SafeTypes(Assembly assembly){try{return assembly.GetTypes();}catch(ReflectionTypeLoadException ex){return ex.Types.OfType<Type>();}}
}

public static class ProviderTemplates
{
    private static readonly ExchangeCredentialField Key=new("apiKey","API Key",ExchangeCredentialKind.ApiKey);
    private static readonly ExchangeCredentialField Secret=new("secret","API Secret",ExchangeCredentialKind.Secret);
    private static ExchangeProviderDescriptor PlannedProvider(string id,string name,ExchangeAssetClass assetClass=ExchangeAssetClass.CryptoCex,params ExchangeCredentialField[] extra)=>new(id,name,assetClass,false,false,[Key,Secret,..extra],new HashSet<string>(),false,"ADAPTER NOT INSTALLED - CAPABILITIES UNVERIFIED");
    private static ExchangeProviderDescriptor PlannedDex(string id,string name)=>new(id,name,ExchangeAssetClass.CryptoDex,false,false,[new("wallet","Wallet Address",ExchangeCredentialKind.WalletAddress),new("privateKey","Signing Key",ExchangeCredentialKind.PrivateKey)],new HashSet<string>(),false,"ADAPTER NOT INSTALLED - CAPABILITIES UNVERIFIED");
    public static IReadOnlyList<ExchangeProviderDescriptor> Planned { get; }=
    [
        PlannedProvider("okx","OKX",ExchangeAssetClass.CryptoCex,new ExchangeCredentialField("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)),
        PlannedProvider("bybit","Bybit"),PlannedProvider("bitget","Bitget",ExchangeAssetClass.CryptoCex,new ExchangeCredentialField("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)),
        PlannedProvider("gate","Gate.io"),PlannedProvider("kucoin","KuCoin",ExchangeAssetClass.CryptoCex,new ExchangeCredentialField("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)),
        PlannedProvider("coinbase","Coinbase",ExchangeAssetClass.CryptoCex,new ExchangeCredentialField("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)),
        PlannedProvider("kraken","Kraken"),PlannedProvider("deribit","Deribit"),PlannedDex("hyperliquid","Hyperliquid"),PlannedDex("dydx","dYdX")
    ];
}