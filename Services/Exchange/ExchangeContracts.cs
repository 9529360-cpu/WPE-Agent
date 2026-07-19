using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public enum ExchangeAssetClass { CryptoCex,CryptoDex,Securities,Futures,Forex }
public enum ExchangeCredentialKind { ApiKey,Secret,Passphrase,WalletAddress,PrivateKey,OAuthToken }

public sealed record ExchangeCredentialField(string Key,string Label,ExchangeCredentialKind Kind,bool Required=true,bool Secret=true,string Help="");
public sealed record ExchangeProviderDescriptor(
    string Id,string DisplayName,ExchangeAssetClass AssetClass,bool SupportsTestnet,bool SupportsMainnet,
    IReadOnlyList<ExchangeCredentialField> CredentialFields,IReadOnlySet<string> Capabilities,bool Installed=true,string Status="READY");
public sealed record ExchangePermissionSnapshot(bool CanRead,bool CanTrade,bool CanWithdraw,string AccountId,IReadOnlyList<string> Warnings);
public sealed record ExchangeHealthSnapshot(bool Healthy,long LatencyMs,long ClockSkewMs,string Message,DateTime CheckedAtUtc);
public sealed record MarginSnapshot(decimal WalletBalance,decimal AvailableMargin,decimal UsedMargin,decimal MaintenanceMargin,decimal MarginRatio);

public sealed class ExchangeConnectionProfile
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; }="binance-futures";
    public string DisplayName { get; set; }="Binance Futures Testnet";
    public bool Enabled { get; set; }=true;
    public bool ExecutionEnabled { get; set; }=true;
    public bool IsTestnet { get; set; }=true;
    public string Endpoint { get; set; }="https://testnet.binancefuture.com";
    public bool UseProxy { get; set; }
    public string ProxyUrl { get; set; }=string.Empty;
    public int ReceiveWindow { get; set; }=5000;
    public int TimeoutSeconds { get; set; }=20;
    public string SubAccount { get; set; }=string.Empty;
    public Dictionary<string,string> EncryptedCredentials { get; set; }=new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,string> SymbolMappings { get; set; }=new(StringComparer.OrdinalIgnoreCase);
    public DateTime? LastVerifiedAtUtc { get; set; }
    public bool ReadPermission { get; set; }
    public bool TradePermission { get; set; }
    public bool WithdrawPermission { get; set; }
    public string AccountId { get; set; }=string.Empty;
}

public interface IMarketDataProvider
{
    Task<TradingRule> GetRulesAsync(string canonicalSymbol,CancellationToken ct);
    Task<MarketEvidence> GetMarketAsync(string canonicalSymbol,CancellationToken ct);
    Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string canonicalSymbol,CancellationToken ct);
    Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string canonicalSymbol,string interval,int limit,CancellationToken ct);
    Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string canonicalSymbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct);
}

public interface IBrokerProvider
{
    Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);
    Task<MarginSnapshot> GetMarginAsync(CancellationToken ct);
    Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? canonicalSymbol,CancellationToken ct);
    Task SetLeverageAsync(string canonicalSymbol,int leverage,CancellationToken ct);
    Task SetMarginModeAsync(string canonicalSymbol,bool isolated,CancellationToken ct);
    Task SetHedgeModeAsync(bool enabled,CancellationToken ct);
    Task<ExchangeOrder> PlaceMarketAsync(string canonicalSymbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct);
    Task<ExchangeOrder> PlaceLimitAsync(string canonicalSymbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct);
    Task<ExchangeOrder> PlaceProtectionAsync(string canonicalSymbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct);
    Task<ExchangeOrder?> FindOrderAsync(string canonicalSymbol,string clientOrderId,CancellationToken ct);
    Task CancelOrderAsync(string canonicalSymbol,string orderId,CancellationToken ct);
}

public interface IRealtimeMarketFeed:IAsyncDisposable
{
    string Status { get; }
    bool Healthy { get; }
    Task StartAsync(CancellationToken ct);
    Task<string?> WaitForTriggerAsync(TimeSpan timeout,CancellationToken ct);
    RealtimeMarketSnapshot? GetSnapshot(string canonicalSymbol);
    MarketEvidence Enrich(MarketEvidence market);
}

public interface IExchangeProvider:IExchangeAdapter
{
    string ConnectionId { get; }
    string ProviderId { get; }
    ExchangeProviderDescriptor Descriptor { get; }
    ISymbolMapper Symbols { get; }
    IMarketDataProvider MarketData { get; }
    IBrokerProvider Broker { get; }
    Task<bool> PingAsync(CancellationToken ct);
    Task<DateTime> GetServerTimeAsync(CancellationToken ct);
    Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct);
    Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct);
    IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> canonicalSymbols,AgentSqliteStore database);
}

public interface IExchangeProviderPlugin
{
    ExchangeProviderDescriptor Descriptor { get; }
    IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials);
}

public sealed class PollingRealtimeFeed:IRealtimeMarketFeed
{
    public string Status=>"POLLING FALLBACK";
    public bool Healthy=>true;
    public Task StartAsync(CancellationToken ct)=>Task.CompletedTask;
    public async Task<string?> WaitForTriggerAsync(TimeSpan timeout,CancellationToken ct){await Task.Delay(timeout,ct);return"polling_cycle";}
    public RealtimeMarketSnapshot? GetSnapshot(string canonicalSymbol)=>null;
    public MarketEvidence Enrich(MarketEvidence market)=>market;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
