using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ProviderConformanceTests
{
    private static readonly string[] ReadCapabilities = ["account","positions","query-order","candles"];
    private static readonly string[] MutationCapabilities = ["place-order","cancel-order","protection-orders"];

    public static TheoryData<string,string,IReadOnlyDictionary<string,string>> InstalledProviders => new()
    {
        {"binance-futures","https://testnet.binancefuture.com",Credentials("apiKey","secret")},
        {"okx","https://www.okx.com",Credentials("apiKey","secret","passphrase")},
        {"bybit","https://api-testnet.bybit.com",Credentials("apiKey","secret")},
        {"gate","https://api-testnet.gateapi.io",Credentials("apiKey","secret")},
        {"bitget","https://api.bitget.com",Credentials("apiKey","secret","passphrase")}
    };

    [Theory]
    [MemberData(nameof(InstalledProviders))]
    public async Task InstalledProvider_MatrixMatchesImplementedContract(
        string providerId,string endpoint,IReadOnlyDictionary<string,string> credentials)
    {
        var catalog=new ExchangeProviderCatalog();
        var descriptor=Assert.Single(catalog.Installed,x=>x.Id==providerId);

        Assert.True(descriptor.SupportsTestnet);
        Assert.False(descriptor.SupportsMainnet);
        Assert.All(ReadCapabilities,capability=>Assert.Contains(capability,descriptor.Capabilities));
        Assert.All(MutationCapabilities,capability=>Assert.Contains(capability,descriptor.Capabilities));

        var profile=new ExchangeConnectionProfile
        {
            ProviderId=providerId,
            IsTestnet=true,
            ExecutionEnabled=true,
            Endpoint=endpoint
        };
        await using var provider=catalog.Create(profile,credentials);
        var guard=Assert.IsAssignableFrom<IProviderEnvironmentGuard>(provider);
        var validation=guard.ValidateEnvironment(requireTestnet:true);

        Assert.True(validation.CanRead,validation.Failure);
        Assert.True(validation.CanTrade,validation.Failure);
        Assert.True(validation.TestnetAvailable,validation.Failure);
    }

    [Theory]
    [MemberData(nameof(InstalledProviders))]
    public async Task InstalledProvider_RejectsUnverifiedTestnetEndpointBeforeNetwork(
        string providerId,string _,IReadOnlyDictionary<string,string> credentials)
    {
        var catalog=new ExchangeProviderCatalog();
        var profile=new ExchangeConnectionProfile
        {
            ProviderId=providerId,
            IsTestnet=true,
            ExecutionEnabled=true,
            Endpoint="https://example.invalid"
        };
        await using var provider=catalog.Create(profile,credentials);

        var validation=Assert.IsAssignableFrom<IProviderEnvironmentGuard>(provider).ValidateEnvironment(true);

        Assert.False(validation.CanRead);
        Assert.False(validation.CanTrade);
        Assert.False(validation.TestnetAvailable);
        Assert.False(string.IsNullOrWhiteSpace(validation.Failure));
        if(provider is RestExchangeProviderBase)
            await Assert.ThrowsAsync<InvalidOperationException>(
                ()=>provider.GetServerTimeAsync(CancellationToken.None));
    }

    [Fact]
    public void PlannedProviders_DoNotClaimUnimplementedCapabilities()
    {
        var planned=new ExchangeProviderCatalog().All.Where(x=>!x.Installed).ToArray();

        Assert.NotEmpty(planned);
        Assert.All(planned,provider=>
        {
            Assert.False(provider.SupportsTestnet);
            Assert.False(provider.SupportsMainnet);
            Assert.Empty(provider.Capabilities);
            Assert.Contains("UNVERIFIED",provider.Status,StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ProvidersWithoutPermissionIntrospection_RemainReadOnly()
    {
        var gate=GateExchangeProvider.ConservativePermissionSnapshot();
        var bitget=BitgetExchangeProvider.ConservativePermissionSnapshot();

        Assert.True(gate.CanRead);
        Assert.False(gate.CanTrade);
        Assert.True(bitget.CanRead);
        Assert.False(bitget.CanTrade);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"canWithdraw\":false}")]
    [InlineData("{\"canTrade\":false}")]
    public void Binance_MissingOrFalseTradePermissionFailsClosed(string json)
    {
        using var document=JsonDocument.Parse(json);
        Assert.False(BinanceFuturesAdapter.ParsePermissionSnapshot(document.RootElement).CanTrade);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"readOnly\":\"1\"}")]
    [InlineData("{\"readOnly\":\"unknown\"}")]
    public void Bybit_MissingReadOnlyOrUnknownPermissionFailsClosed(string json)
    {
        using var document=JsonDocument.Parse(json);
        Assert.False(BybitExchangeProvider.ParsePermissionSnapshot(document.RootElement).CanTrade);
    }

    [Fact]
    public void ExplicitWritablePermissionValuesRemainTradeable()
    {
        using var binance=JsonDocument.Parse("{\"canTrade\":true}");
        using var bybit=JsonDocument.Parse("{\"readOnly\":\"0\"}");

        Assert.True(BinanceFuturesAdapter.ParsePermissionSnapshot(binance.RootElement).CanTrade);
        Assert.True(BybitExchangeProvider.ParsePermissionSnapshot(bybit.RootElement).CanTrade);
    }

    [Fact]
    public void Binance_AccountWithdrawalStatusDoesNotGrantApiKeyWithdrawalPermission()
    {
        using var document=JsonDocument.Parse("{\"canTrade\":true,\"canWithdraw\":true}");

        var result=BinanceFuturesAdapter.ParsePermissionSnapshot(document.RootElement);

        Assert.True(result.CanTrade);
        Assert.False(result.CanWithdraw);
        Assert.Contains(result.Warnings,x=>x.Contains("not API key permission evidence",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1000,true)]
    [InlineData(1001,false)]
    [InlineData(2068,false)]
    public void AccessReadiness_UsesTradingClockTolerance(long skewMilliseconds,bool expected)
    {
        Assert.Equal(expected,global::币安量化机器人.Services.Access.AccessReadinessService.ClockWithinTradingTolerance(skewMilliseconds));
    }

    [Theory]
    [InlineData(true,true)]
    [InlineData(false,false)]
    public void AccessReadiness_DataStatusTracksReadPermissionNotTradeHealth(bool canRead,bool expected)
    {
        Assert.Equal(expected,global::币安量化机器人.Services.Access.AccessReadinessService.ProviderReadPathAvailable(canRead));
    }

    [Fact]
    public void Binance_ClockSkewDoesNotObscureAlreadyMissingTradePermission()
    {
        var observed=new DateTime(2026,7,22,0,0,0,DateTimeKind.Utc);
        var permission=new ExchangePermissionSnapshot(true,false,false,"test",[]);

        var result=BinanceFuturesAdapter.ApplyClockSkew(permission,observed.AddMilliseconds(-2068),observed);

        Assert.False(result.CanTrade);
        Assert.Empty(result.Warnings);
        Assert.Equal("API trade permission is missing",global::币安量化机器人.Services.Access.AccessReadinessService.TradePermissionDetail(result));
    }

    [Fact]
    public void Binance_ClockGateReportsRawTradePermissionWasEnabled()
    {
        var observed=new DateTime(2026,7,22,0,0,0,DateTimeKind.Utc);
        var permission=new ExchangePermissionSnapshot(true,true,false,"test",[]);

        var result=BinanceFuturesAdapter.ApplyClockSkew(permission,observed.AddMilliseconds(-2068),observed);

        Assert.False(result.CanTrade);
        Assert.Contains(result.Warnings,x=>x.Contains("clock skew",StringComparison.OrdinalIgnoreCase));
        Assert.Contains("permission is enabled",global::币安量化机器人.Services.Access.AccessReadinessService.TradePermissionDetail(result),StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_UnsupportedEnvironmentFailsBeforeCatalogOrPermissionCalls()
    {
        var provider=new ProbeProvider(
            new ProviderEnvironmentValidation(false,false,false,"unverified endpoint"),
            FullCapabilities());

        var result=await new ProviderCapabilityProbe().ProbeAsync(provider,["SOLUSDT"],true,CancellationToken.None);

        Assert.Equal(CapabilityStatus.Unsupported,result["SOLUSDT"].Status);
        Assert.False(result["SOLUSDT"].CanTrade);
        Assert.Equal(0,provider.CatalogCalls);
        Assert.Equal(0,provider.PermissionCalls);
    }

    [Fact]
    public async Task Probe_CatalogErrorFailsBeforePrivatePermissionCall()
    {
        var provider=new ProbeProvider(new ProviderEnvironmentValidation(true,true,true),FullCapabilities())
        {
            CatalogState=ProviderCatalogState.Error
        };

        var capability=(await new ProviderCapabilityProbe().ProbeAsync(provider,["SOLUSDT"],true,CancellationToken.None))["SOLUSDT"];

        Assert.Equal(CapabilityStatus.Error,capability.Status);
        Assert.Equal(1,provider.CatalogCalls);
        Assert.Equal(0,provider.PermissionCalls);
    }

    [Fact]
    public async Task Probe_UnknownTradePermissionRemainsReadOnly()
    {
        var provider=new ProbeProvider(
            new ProviderEnvironmentValidation(true,true,true),
            FullCapabilities(),
            new ExchangePermissionSnapshot(true,false,false,"test",["trade permission unknown"]));

        var capability=(await new ProviderCapabilityProbe().ProbeAsync(provider,["SOLUSDT"],true,CancellationToken.None))["SOLUSDT"];

        Assert.Equal(CapabilityStatus.Available,capability.Status);
        Assert.True(capability.CanRead);
        Assert.False(capability.CanTrade);
        Assert.True(capability.TestnetAvailable);
    }

    [Fact]
    public async Task Probe_MissingDeclaredMutationCapabilityCannotTrade()
    {
        var capabilities=FullCapabilities();
        capabilities.Remove("protection-orders");
        var provider=new ProbeProvider(new ProviderEnvironmentValidation(true,true,true),capabilities);

        var capability=(await new ProviderCapabilityProbe().ProbeAsync(provider,["SOLUSDT"],true,CancellationToken.None))["SOLUSDT"];

        Assert.True(capability.CanRead);
        Assert.False(capability.CanTrade);
    }

    [Fact]
    public async Task Probe_PermissionFailureReturnsErrorWithoutCapabilities()
    {
        var provider=new ProbeProvider(new ProviderEnvironmentValidation(true,true,true),FullCapabilities())
        {
            PermissionFailure=new InvalidOperationException("permission endpoint failed")
        };

        var capability=(await new ProviderCapabilityProbe().ProbeAsync(provider,["SOLUSDT"],true,CancellationToken.None))["SOLUSDT"];

        Assert.Equal(CapabilityStatus.Error,capability.Status);
        Assert.False(capability.CanRead);
        Assert.False(capability.CanTrade);
        Assert.Contains("permission endpoint failed",capability.Failure);
    }

    private static IReadOnlyDictionary<string,string> Credentials(params string[] fields)=>
        fields.ToDictionary(field=>field,field=>"test-"+field,StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> FullCapabilities()=>new(
        [..ReadCapabilities,..MutationCapabilities],StringComparer.OrdinalIgnoreCase);

    private sealed class ProbeProvider:IExchangeProvider,IMarketDataProvider,IBrokerProvider,IProviderMarketCatalog,IProviderEnvironmentGuard
    {
        private readonly ProviderEnvironmentValidation _environment;
        private readonly ExchangePermissionSnapshot _permissions;
        public ProbeProvider(
            ProviderEnvironmentValidation environment,
            IReadOnlySet<string> capabilities,
            ExchangePermissionSnapshot? permissions=null)
        {
            _environment=environment;
            _permissions=permissions??new ExchangePermissionSnapshot(true,true,false,"test",[]);
            Descriptor=new("probe","Probe",ExchangeAssetClass.CryptoCex,true,false,[],capabilities);
        }

        public int CatalogCalls{get;private set;}
        public int PermissionCalls{get;private set;}
        public Exception? PermissionFailure{get;init;}
        public ProviderCatalogState CatalogState{get;init;}=ProviderCatalogState.Available;
        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public string ConnectionId=>"probe";
        public string ProviderId=>"probe";
        public ExchangeProviderDescriptor Descriptor{get;}
        public ISymbolMapper Symbols{get;}=new ConventionSymbolMapper("probe");
        public IMarketDataProvider MarketData=>this;
        public IBrokerProvider Broker=>this;
        public ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)=>_environment;
        public Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)
        {
            CatalogCalls++;
            if(CatalogState!=ProviderCatalogState.Available)
                return Task.FromResult(new ProviderMarketCatalog(CatalogState,[],DateTimeOffset.UtcNow,"catalog failed"));
            var instrument=new Instrument("SOLUSDT","SOLUSDT","probe","probe",MarketType.Perpetual);
            return Task.FromResult(new ProviderMarketCatalog(
                ProviderCatalogState.Available,
                [new ProviderMarketCatalogEntry(instrument,CapabilityStatus.Available,true,true,true,DateTimeOffset.UtcNow)],
                DateTimeOffset.UtcNow));
        }
        public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)
        {
            PermissionCalls++;
            return PermissionFailure is null?Task.FromResult(_permissions):Task.FromException<ExchangePermissionSnapshot>(PermissionFailure);
        }
        public Task<bool> PingAsync(CancellationToken ct)=>Task.FromResult(true);
        public Task<DateTime> GetServerTimeAsync(CancellationToken ct)=>Task.FromResult(DateTime.UtcNow);
        public Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)=>throw new NotSupportedException();
        public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> canonicalSymbols,AgentSqliteStore database)=>null;
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<MarginSnapshot> GetMarginAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? canonicalSymbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string canonicalSymbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string canonicalSymbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string canonicalSymbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string canonicalSymbol,string interval,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string canonicalSymbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetLeverageAsync(string canonicalSymbol,int leverage,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetMarginModeAsync(string canonicalSymbol,bool isolated,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceMarketAsync(string canonicalSymbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceLimitAsync(string canonicalSymbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder> PlaceProtectionAsync(string canonicalSymbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ExchangeOrder?> FindOrderAsync(string canonicalSymbol,string clientOrderId,CancellationToken ct)=>throw new NotSupportedException();
        public Task CancelOrderAsync(string canonicalSymbol,string orderId,CancellationToken ct)=>throw new NotSupportedException();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
