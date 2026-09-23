using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed class OkxOfficialMcpProviderPlugin : IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor { get; }=new(
        "okx-mcp",
        "OKX Official MCP",
        ExchangeAssetClass.CryptoCex,
        SupportsTestnet:true,
        SupportsMainnet:false,
        [
            new("apiKey","API Key",ExchangeCredentialKind.ApiKey),
            new("secret","API Secret",ExchangeCredentialKind.Secret),
            new("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)
        ],
        new HashSet<string>(
        [
            "account","balance","positions","margin","candles","funding","open-interest",
            "index-price","mark-price","fees","place-order","cancel-order","query-order",
            "protection-orders","health","mcp"
        ]));

    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;

    public IExchangeProvider Create(
        ExchangeConnectionProfile profile,
        IReadOnlyDictionary<string,string> credentials)
    {
        var client=OfficialExchangeMcpClientFactory.CreateOkxDemo(
            profile,
            credentials,
            allowWrite:profile.ExecutionEnabled);
        return new OkxOfficialMcpProvider(profile,client);
    }
}

public sealed partial class OkxOfficialMcpProvider :
    IExchangeProvider,
    IMarketDataProvider,
    IBrokerProvider,
    IProviderEnvironmentGuard,
    IRecentOrderProvider,
    IProtectionFillEvidenceProvider,
    IExchangeOrderFeeEvidenceReader
{
    private readonly ExchangeConnectionProfile _profile;
    private readonly IExchangeMcpToolClient _mcp;
    private readonly ConcurrentDictionary<string,decimal> _contractValues=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,byte> _algoIds=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,bool> _isolated=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,int> _leverage=new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _hedgeMode=true;

    public OkxOfficialMcpProvider(
        ExchangeConnectionProfile profile,
        IExchangeMcpToolClient mcp)
    {
        _profile=profile??throw new ArgumentNullException(nameof(profile));
        _mcp=mcp??throw new ArgumentNullException(nameof(mcp));
        Symbols=new ConventionSymbolMapper("okx-mcp",profile.SymbolMappings);
    }

    public ExchangeEnvironment Environment=>_profile.IsTestnet
        ?ExchangeEnvironment.Testnet
        :ExchangeEnvironment.Mainnet;
    public string ConnectionId=>_profile.Id;
    public string ProviderId=>"okx-mcp";
    public ExchangeProviderDescriptor Descriptor=>OkxOfficialMcpProviderPlugin.ProviderDescriptor;
    public ISymbolMapper Symbols { get; }
    public IMarketDataProvider MarketData=>this;
    public IBrokerProvider Broker=>this;

    public ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)
    {
        if(Environment!=ExchangeEnvironment.Testnet)
            return new(false,false,false,"OKX MCP Mainnet execution is not enabled.");
        if(!ProviderEndpointPolicy.IsOfficialHttpsOrigin(_profile.Endpoint,"www.okx.com"))
            return new(false,false,false,"OKX MCP Demo Trading requires the official https://www.okx.com endpoint.");

        var canExecute=_profile.ExecutionEnabled&&!_mcp.ReadOnly;
        return new(true,canExecute,true,canExecute?null:"Execution is disabled for this MCP connection.");
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        var tools=await _mcp.ListToolsAsync(ct);
        return tools.Any(tool=>tool.Name=="market_get_ticker");
    }

    public async Task<DateTime> GetServerTimeAsync(CancellationToken ct)
    {
        var rows=DataArray(await _mcp.CallToolAsync(
            "market_get_ticker",
            new Dictionary<string,object?> { ["instId"]="BTC-USDT-SWAP", ["demo"]=true },
            ct));
        if(rows.GetArrayLength()==0)
            throw new InvalidOperationException("OKX MCP ticker response is empty.");
        var timestamp=Str(rows[0],"ts");
        if(!long.TryParse(timestamp,NumberStyles.Integer,CultureInfo.InvariantCulture,out var milliseconds))
            throw new InvalidOperationException("OKX MCP ticker did not return exchange timestamp.");
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    public async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)
    {
        var tools=await _mcp.ListToolsAsync(ct);
        var names=tools.Select(tool=>tool.Name).ToHashSet(StringComparer.Ordinal);
        var canRead=names.Contains("account_get_balance");
        var canTrade=_profile.ExecutionEnabled&&
                     !_mcp.ReadOnly&&
                     names.Contains("swap_place_order");
        var warnings=new List<string>();
        if(!canRead)warnings.Add("OKX account read tools are not exposed by the official MCP server.");
        if(!canTrade)warnings.Add("OKX MCP trade tools are not enabled for this connection.");

        var accountId=string.Empty;
        if(canRead&&names.Contains("account_get_config"))
        {
            try
            {
                var rows=DataArray(await _mcp.CallToolAsync(
                    "account_get_config",
                    new Dictionary<string,object?>(),
                    ct));
                if(rows.GetArrayLength()>0)
                    accountId=Str(rows[0],"uid");
            }
            catch(Exception ex)
            {
                warnings.Add($"Account identity unavailable: {Safe(ex.Message)}");
            }
        }

        return new(canRead,canTrade,false,accountId,warnings);
    }

    public async Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)
    {
        var checkedAt=DateTime.UtcNow;
        var stopwatch=Stopwatch.StartNew();
        try
        {
            var server=await GetServerTimeAsync(ct);
            var permission=await CheckPermissionsAsync(ct);
            var skew=(long)Math.Abs((DateTime.UtcNow-server).TotalMilliseconds);
            return new(
                permission.CanRead,
                stopwatch.ElapsedMilliseconds,
                skew,
                $"OKX Official MCP · read={permission.CanRead} trade={permission.CanTrade}",
                checkedAt);
        }
        catch(Exception ex)
        {
            return new(false,stopwatch.ElapsedMilliseconds,0,Safe(ex.Message),checkedAt);
        }
    }

    public IRealtimeMarketFeed? CreateRealtimeFeed(
        IEnumerable<string> canonicalSymbols,
        AgentSqliteStore database)=>null;


    public async ValueTask DisposeAsync()=>await _mcp.DisposeAsync();
}
