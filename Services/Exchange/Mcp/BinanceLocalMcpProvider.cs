using System.Text.Json;
using System.Text.Json.Serialization;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed class BinanceLocalMcpProviderPlugin : IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor { get; }=new(
        "binance-mcp-local",
        "Binance Futures Testnet MCP",
        ExchangeAssetClass.CryptoCex,
        SupportsTestnet:true,
        SupportsMainnet:false,
        [],
        new HashSet<string>(
        [
            "account","balance","positions","margin","candles","funding","fees",
            "place-order","cancel-order","query-order","protection-orders","health","mcp"
        ]));

    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;

    public IExchangeProvider Create(
        ExchangeConnectionProfile profile,
        IReadOnlyDictionary<string,string> credentials)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if(string.IsNullOrWhiteSpace(profile.UpstreamConnectionId))
            throw new InvalidOperationException(
                "Binance MCP profile requires an explicit UpstreamConnectionId.");
        if(profile.UpstreamConnectionId.Equals(profile.Id,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Binance MCP profile cannot delegate to itself.");

        var writeGate=string.Equals(
            Environment.GetEnvironmentVariable("WPE_BINANCE_MCP_ALLOW_WRITE"),
            "1",
            StringComparison.Ordinal);
        var client=OfficialExchangeMcpClientFactory.CreateBinanceLocalTestnet(
            profile.UpstreamConnectionId,
            allowWrite:profile.ExecutionEnabled&&writeGate);
        return new BinanceLocalMcpProvider(profile,client);
    }
}

public sealed class BinanceLocalMcpProvider :
    IExchangeProvider,
    IMarketDataProvider,
    IBrokerProvider,
    IProviderEnvironmentGuard,
    IRecentOrderProvider,
    IProtectionFillEvidenceProvider,
    IExchangeOrderFeeEvidenceReader,
    IExchangeFundingIncomeReader
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive=true,
        Converters={new JsonStringEnumConverter()}
    };

    private readonly ExchangeConnectionProfile _profile;
    private readonly IExchangeMcpToolClient _mcp;

    public BinanceLocalMcpProvider(
        ExchangeConnectionProfile profile,
        IExchangeMcpToolClient mcp)
    {
        _profile=profile??throw new ArgumentNullException(nameof(profile));
        _mcp=mcp??throw new ArgumentNullException(nameof(mcp));
        Symbols=new ConventionSymbolMapper("binance-mcp-local",profile.SymbolMappings);
    }

    public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
    public string ConnectionId=>_profile.Id;
    public string ProviderId=>"binance-mcp-local";
    public ExchangeProviderDescriptor Descriptor=>BinanceLocalMcpProviderPlugin.ProviderDescriptor;
    public ISymbolMapper Symbols { get; }
    public IMarketDataProvider MarketData=>this;
    public IBrokerProvider Broker=>this;

    public ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)
    {
        if(!_profile.IsTestnet)
            return new(false,false,false,"Binance MCP local provider is Testnet-only.");
        if(!ProviderEndpointPolicy.IsOfficialHttpsOrigin(
               _profile.Endpoint,
               "testnet.binancefuture.com"))
            return new(false,false,false,"Binance MCP local provider requires the official Testnet endpoint.");
        if(string.IsNullOrWhiteSpace(_profile.UpstreamConnectionId))
            return new(false,false,true,"Binance MCP upstream connection is not configured.");

        var canTrade=_profile.ExecutionEnabled&&!_mcp.ReadOnly;
        return new(true,canTrade,true,canTrade?null:"MCP execution is disabled or the local write gate is closed.");
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        var tools=await _mcp.ListToolsAsync(ct);
        return tools.Any(tool=>tool.Name=="exchange_health");
    }

    public Task<DateTime> GetServerTimeAsync(CancellationToken ct)=>
        CallAsync<DateTime>("exchange_get_server_time",null,ct);

    public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)=>
        CallAsync<ExchangePermissionSnapshot>("exchange_get_permissions",null,ct);

    public Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)=>
        CallPropertyAsync<ExchangeHealthSnapshot>("exchange_health","health",null,ct);

    public IRealtimeMarketFeed? CreateRealtimeFeed(
        IEnumerable<string> canonicalSymbols,
        AgentSqliteStore database)=>null;

    public Task<TradingRule> GetRulesAsync(string canonicalSymbol,CancellationToken ct)=>
        CallAsync<TradingRule>(
            "exchange_get_rules",
            Args(("symbol",canonicalSymbol)),
            ct);

    public Task<MarketEvidence> GetMarketAsync(string canonicalSymbol,CancellationToken ct)=>
        CallAsync<MarketEvidence>(
            "exchange_get_market",
            Args(("symbol",canonicalSymbol)),
            ct);

    public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(
        string canonicalSymbol,
        CancellationToken ct)=>
        CallListAsync<DerivativesSnapshot>(
            "exchange_get_derivative_history",
            Args(("symbol",canonicalSymbol)),
            ct);

    public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(
        string canonicalSymbol,
        string interval,
        int limit,
        CancellationToken ct)=>
        CallListAsync<CandleEvidence>(
            "exchange_get_candles",
            Args(("symbol",canonicalSymbol),("interval",interval),("limit",limit)),
            ct);

    public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(
        string canonicalSymbol,
        string interval,
        DateTime start,
        DateTime end,
        int limit,
        CancellationToken ct)=>
        CallListAsync<CandleEvidence>(
            "exchange_get_candles_range",
            Args(
                ("symbol",canonicalSymbol),
                ("interval",interval),
                ("startUtc",start.ToUniversalTime().ToString("O")),
                ("endUtc",end.ToUniversalTime().ToString("O")),
                ("limit",limit)),
            ct);

    public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>
        CallAsync<AccountSnapshot>("exchange_get_account",null,ct);

    public Task<MarginSnapshot> GetMarginAsync(CancellationToken ct)=>
        CallAsync<MarginSnapshot>("exchange_get_margin",null,ct);

    public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>
        CallListAsync<ManagedPosition>("exchange_get_positions",null,ct);

    public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(
        string? canonicalSymbol,
        CancellationToken ct)=>
        CallListAsync<ExchangeOrder>(
            "exchange_get_open_orders",
            string.IsNullOrWhiteSpace(canonicalSymbol)
                ?null
                :Args(("symbol",canonicalSymbol)),
            ct);

    public Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(
        string canonicalSymbol,
        int limit,
        CancellationToken ct)=>
        CallListAsync<ExchangeOrder>(
            "exchange_get_recent_orders",
            Args(("symbol",canonicalSymbol),("limit",limit)),
            ct);

    public Task SetLeverageAsync(string canonicalSymbol,int leverage,CancellationToken ct)=>
        CallVoidAsync(
            "exchange_set_leverage",
            Args(("symbol",canonicalSymbol),("leverage",leverage)),
            ct);

    public Task SetMarginModeAsync(string canonicalSymbol,bool isolated,CancellationToken ct)=>
        CallVoidAsync(
            "exchange_set_margin_mode",
            Args(("symbol",canonicalSymbol),("isolated",isolated)),
            ct);

    public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>
        CallVoidAsync(
            "exchange_set_hedge_mode",
            Args(("enabled",enabled)),
            ct);

    public Task<ExchangeOrder> PlaceMarketAsync(
        string canonicalSymbol,
        PositionSide side,
        decimal quantity,
        string clientOrderId,
        bool reduceOnly,
        CancellationToken ct)=>
        CallAsync<ExchangeOrder>(
            "exchange_place_market",
            Args(
                ("symbol",canonicalSymbol),
                ("side",side.ToString()),
                ("quantity",quantity),
                ("clientOrderId",clientOrderId),
                ("reduceOnly",reduceOnly)),
            ct);

    public Task<ExchangeOrder> PlaceLimitAsync(
        string canonicalSymbol,
        PositionSide side,
        decimal quantity,
        decimal price,
        string clientOrderId,
        bool reduceOnly,
        CancellationToken ct)=>
        CallAsync<ExchangeOrder>(
            "exchange_place_limit",
            Args(
                ("symbol",canonicalSymbol),
                ("side",side.ToString()),
                ("quantity",quantity),
                ("price",price),
                ("clientOrderId",clientOrderId),
                ("reduceOnly",reduceOnly)),
            ct);

    public Task<ExchangeOrder> PlaceProtectionAsync(
        string canonicalSymbol,
        PositionSide sideToClose,
        decimal stopLoss,
        decimal takeProfit,
        string groupId,
        CancellationToken ct)=>
        CallAsync<ExchangeOrder>(
            "exchange_place_protection",
            Args(
                ("symbol",canonicalSymbol),
                ("side",sideToClose.ToString()),
                ("stopLoss",stopLoss),
                ("takeProfit",takeProfit),
                ("groupId",groupId)),
            ct);

    public Task<ExchangeOrder?> FindOrderAsync(
        string canonicalSymbol,
        string clientOrderId,
        CancellationToken ct)=>
        CallNullableAsync<ExchangeOrder>(
            "exchange_find_order",
            Args(("symbol",canonicalSymbol),("clientOrderId",clientOrderId)),
            ct);

    public Task CancelOrderAsync(
        string canonicalSymbol,
        string orderId,
        CancellationToken ct)=>
        CallVoidAsync(
            "exchange_cancel_order",
            Args(("symbol",canonicalSymbol),("orderId",orderId)),
            ct);

    public bool TryMatchProtectionFill(
        string parentClientOrderId,
        ExchangeOrder order,
        out ProtectionFillKind kind)=>
        BinanceFuturesAdapter.TryMatchProtectionFillIdentity(
            parentClientOrderId,
            order,
            out kind);

    public Task<ExchangeOrderFeeEvidenceV1> ReadOrderFeeEvidenceAsync(
        ExchangeOrder order,
        CancellationToken ct)=>
        CallAsync<ExchangeOrderFeeEvidenceV1>(
            "exchange_read_order_fee_evidence",
            Args(
                ("symbol",order.Symbol),
                ("orderId",order.OrderId),
                ("clientOrderId",order.ClientOrderId),
                ("status",order.Status),
                ("executedQuantity",order.ExecutedQuantity),
                ("avgPrice",order.AvgPrice),
                ("type",order.Type),
                ("positionSide",order.PositionSide?.ToString()??string.Empty),
                ("isProtection",order.IsProtection),
                ("updatedAtUtc",order.UpdatedAt.ToUniversalTime().ToString("O"))),
            ct);

    public Task<FundingObservationResultV1> ReadFundingIncomeAsync(
        string canonicalSymbol,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct)=>
        CallAsync<FundingObservationResultV1>(
            "exchange_read_funding_income",
            Args(
                ("symbol",canonicalSymbol),
                ("startUtc",startUtc.ToUniversalTime().ToString("O")),
                ("endUtc",endUtc.ToUniversalTime().ToString("O"))),
            ct);

    public async ValueTask DisposeAsync()=>await _mcp.DisposeAsync();

    private async Task<T> CallAsync<T>(
        string tool,
        IReadOnlyDictionary<string,object?>? arguments,
        CancellationToken ct)
    {
        var result=await _mcp.CallToolAsync(
            tool,
            arguments??new Dictionary<string,object?>(),
            ct);
        ThrowIfError(tool,result);
        return JsonSerializer.Deserialize<T>(result.Text,JsonOptions)
            ??throw new InvalidOperationException($"MCP tool '{tool}' returned an empty payload.");
    }

    private async Task<T> CallPropertyAsync<T>(
        string tool,
        string property,
        IReadOnlyDictionary<string,object?>? arguments,
        CancellationToken ct)
    {
        var result=await _mcp.CallToolAsync(
            tool,
            arguments??new Dictionary<string,object?>(),
            ct);
        ThrowIfError(tool,result);
        using var document=JsonDocument.Parse(result.Text);
        if(!document.RootElement.TryGetProperty(property,out var value))
            throw new InvalidOperationException(
                $"MCP tool '{tool}' payload does not contain '{property}'.");
        return value.Deserialize<T>(JsonOptions)
            ??throw new InvalidOperationException(
                $"MCP tool '{tool}' property '{property}' is empty.");
    }

    private async Task<T?> CallNullableAsync<T>(
        string tool,
        IReadOnlyDictionary<string,object?>? arguments,
        CancellationToken ct)
        where T:class
    {
        var result=await _mcp.CallToolAsync(
            tool,
            arguments??new Dictionary<string,object?>(),
            ct);
        ThrowIfError(tool,result);
        if(string.IsNullOrWhiteSpace(result.Text)||result.Text.Trim()=="null")return null;
        return JsonSerializer.Deserialize<T>(result.Text,JsonOptions);
    }

    private async Task<IReadOnlyList<T>> CallListAsync<T>(
        string tool,
        IReadOnlyDictionary<string,object?>? arguments,
        CancellationToken ct)
    {
        var values=await CallAsync<T[]>(tool,arguments,ct);
        return values;
    }

    private async Task CallVoidAsync(
        string tool,
        IReadOnlyDictionary<string,object?>? arguments,
        CancellationToken ct)
    {
        var result=await _mcp.CallToolAsync(
            tool,
            arguments??new Dictionary<string,object?>(),
            ct);
        ThrowIfError(tool,result);
    }

    private static void ThrowIfError(string tool,ExchangeMcpToolResult result)
    {
        if(result.IsError)
            throw new InvalidOperationException(
                $"MCP tool '{tool}' failed: {result.Text}");
    }

    private static Dictionary<string,object?> Args(
        params (string Key,object? Value)[] values)=>
        values.ToDictionary(value=>value.Key,value=>value.Value,StringComparer.Ordinal);
}
