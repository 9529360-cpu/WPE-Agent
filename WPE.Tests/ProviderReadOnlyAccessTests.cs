using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ProviderReadOnlyAccessTests
{
    [Fact]
    public async Task Probe_UsesOnlyReadSurfaceAndDoesNotExposeReturnedData()
    {
        var provider=new RecordingProvider();
        await using var client=new ProviderReadOnlyAccessClient(provider);

        var report=await ProviderReadOnlyAccessRunner.ProbeAsync(client,["SOLUSDT"]);
        var json=JsonSerializer.Serialize(report);

        Assert.Equal(ProviderReadOnlyStatus.Pass,report.Status);
        Assert.Equal(0,provider.MutationCount);
        Assert.True(provider.CatalogCalls>=1);
        Assert.True(provider.PermissionCalls>=1);
        Assert.Equal(1,provider.AccountCalls);
        Assert.Equal(1,provider.PositionCalls);
        Assert.Equal(1,provider.OpenOrderCalls);
        Assert.Equal(1,provider.RecentOrderCalls);
        Assert.Equal(new ProviderReadOnlyEvidence(1,1,1),report.Evidence);
        Assert.DoesNotContain("account-private-7",json,StringComparison.Ordinal);
        Assert.DoesNotContain("12345.67",json,StringComparison.Ordinal);
        Assert.DoesNotContain("order-private",json,StringComparison.Ordinal);
        Assert.DoesNotContain("client-private",json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntentReconciliation_OnlyQueriesAndReportsCounts()
    {
        var provider=new RecordingProvider{FindResult=RecordingProvider.Order("order-private-find","client-private-find") with{Status="FILLED"}};
        await using var client=new ProviderReadOnlyAccessClient(provider);
        var intent=new ExecutionIntent("SOLUSDT",PositionSide.Long,1m,false,90m,110m,"client-private-query","test",DecisionAction.OpenLong);

        var report=await ProviderReadOnlyAccessRunner.ReconcileIntentsReadOnlyAsync(
            client,[new PersistedIntent("cycle-private",intent,"UNKNOWN",null)],CancellationToken.None);
        var json=JsonSerializer.Serialize(report);

        Assert.Equal(new ReadOnlyIntentReconciliation(1,1,0,0,0),report);
        Assert.Equal(1,provider.FindOrderCalls);
        Assert.Equal(0,provider.MutationCount);
        Assert.DoesNotContain("client-private",json,StringComparison.Ordinal);
        Assert.DoesNotContain("order-private",json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedEnvironmentStopsBeforeProviderReads()
    {
        var provider=new RecordingProvider
        {
            EnvironmentValidation=new(false,false,false,"untrusted endpoint")
        };
        await using var client=new ProviderReadOnlyAccessClient(provider);

        var report=await ProviderReadOnlyAccessRunner.ProbeAsync(client,["SOLUSDT"]);

        Assert.Equal(ProviderReadOnlyStatus.Pass,report.Status);
        Assert.Contains(report.Checks,x=>x.Check=="environment"&&x.Status==ProviderReadOnlyStatus.Unsupported);
        Assert.Equal(0,provider.TotalReadCalls);
        Assert.Equal(0,provider.MutationCount);
    }

    [Theory]
    [InlineData(ProviderCatalogState.Unsupported,ProviderReadOnlyStatus.Pass)]
    [InlineData(ProviderCatalogState.Stale,ProviderReadOnlyStatus.Stale)]
    [InlineData(ProviderCatalogState.Error,ProviderReadOnlyStatus.Fail)]
    public async Task NonAvailableCatalogStopsBeforePrivateReads(
        ProviderCatalogState state,string expectedStatus)
    {
        var provider=new RecordingProvider{CatalogState=state};
        await using var client=new ProviderReadOnlyAccessClient(provider);

        var report=await ProviderReadOnlyAccessRunner.ProbeAsync(client,["SOLUSDT"]);

        Assert.Equal(expectedStatus,report.Status);
        Assert.Equal(1,provider.CatalogCalls);
        Assert.Equal(0,provider.PermissionCalls);
        Assert.Equal(0,provider.AccountCalls);
        Assert.Equal(0,provider.MutationCount);
    }

    [Fact]
    public async Task UnknownTradePermissionDoesNotFailReadOnlyAccess()
    {
        var provider=new RecordingProvider
        {
            Permission=new(true,false,false,"account-private-7",["trade permission unknown"])
        };
        await using var client=new ProviderReadOnlyAccessClient(provider);

        var report=await ProviderReadOnlyAccessRunner.ProbeAsync(client,["SOLUSDT"]);

        Assert.Equal(ProviderReadOnlyStatus.Pass,report.Status);
        Assert.Equal(0,provider.MutationCount);
    }

    [Fact]
    public async Task FailureReportContainsOnlyDiagnosticCode()
    {
        var provider=new RecordingProvider
        {
            CatalogFailure=new InvalidOperationException(
                "Authorization: Bearer sk-provider-secret-1234567890 https://example.invalid?signature=secret")
        };
        await using var client=new ProviderReadOnlyAccessClient(provider);

        var report=await ProviderReadOnlyAccessRunner.ProbeAsync(client,["SOLUSDT"]);
        var json=JsonSerializer.Serialize(report);

        Assert.Equal(ProviderReadOnlyStatus.Fail,report.Status);
        Assert.DoesNotContain("sk-provider-secret",json,StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid",json,StringComparison.Ordinal);
        Assert.DoesNotContain("signature",json,StringComparison.OrdinalIgnoreCase);
        Assert.All(report.Checks,x=>Assert.Matches("^WPE-[A-F0-9]{8}$",x.DiagnosticCode));
    }

    private sealed class RecordingProvider:
        IExchangeProvider,IMarketDataProvider,IBrokerProvider,IProviderMarketCatalog,
        IProviderEnvironmentGuard,IRecentOrderProvider
    {
        public ProviderEnvironmentValidation EnvironmentValidation{get;init;}=new(true,true,true);
        public ProviderCatalogState CatalogState{get;init;}=ProviderCatalogState.Available;
        public Exception? CatalogFailure{get;init;}
        public ExchangePermissionSnapshot Permission{get;init;}=new(true,true,false,"account-private-7",[]);
        public int CatalogCalls{get;private set;}
        public int ClockCalls{get;private set;}
        public int PermissionCalls{get;private set;}
        public int AccountCalls{get;private set;}
        public int PositionCalls{get;private set;}
        public int OpenOrderCalls{get;private set;}
        public int RecentOrderCalls{get;private set;}
        public int FindOrderCalls{get;private set;}
        public ExchangeOrder? FindResult{get;init;}
        public int MutationCount{get;private set;}
        public int TotalReadCalls=>ClockCalls+PermissionCalls+AccountCalls+PositionCalls+OpenOrderCalls+RecentOrderCalls;
        public ExchangeEnvironment Environment=>ExchangeEnvironment.Testnet;
        public string ConnectionId=>"readonly-test";
        public string ProviderId=>"readonly-test";
        public ExchangeProviderDescriptor Descriptor{get;}=new(
            "readonly-test","Read only test",ExchangeAssetClass.CryptoCex,true,false,[],
            new HashSet<string>(["account","positions","query-order","candles","place-order","cancel-order","protection-orders"]));
        public ISymbolMapper Symbols{get;}=new ConventionSymbolMapper("readonly-test");
        public IMarketDataProvider MarketData=>this;
        public IBrokerProvider Broker=>this;
        public ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)=>EnvironmentValidation;
        public Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)
        {
            CatalogCalls++;
            if(CatalogFailure is not null)return Task.FromException<ProviderMarketCatalog>(CatalogFailure);
            if(CatalogState!=ProviderCatalogState.Available)
                return Task.FromResult(new ProviderMarketCatalog(CatalogState,[],DateTimeOffset.UtcNow,"catalog state"));
            var instrument=new Instrument("SOLUSDT","SOLUSDT","readonly-test","readonly-test",MarketType.Perpetual);
            return Task.FromResult(new ProviderMarketCatalog(
                ProviderCatalogState.Available,
                [new ProviderMarketCatalogEntry(instrument,CapabilityStatus.Available,true,true,true,DateTimeOffset.UtcNow)],
                DateTimeOffset.UtcNow));
        }
        public Task<DateTime> GetServerTimeAsync(CancellationToken ct){ClockCalls++;return Task.FromResult(DateTime.UtcNow);}
        public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct){PermissionCalls++;return Task.FromResult(Permission);}
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct){AccountCalls++;return Task.FromResult(new AccountSnapshot(12345.67m,12000m,12345.67m,DateTime.UtcNow));}
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct){PositionCalls++;return Task.FromResult<IReadOnlyList<ManagedPosition>>([new("SOLUSDT",PositionSide.Long,1m,100m,101m,1m,2m,true,50m)]);}
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? canonicalSymbol,CancellationToken ct){OpenOrderCalls++;return Task.FromResult<IReadOnlyList<ExchangeOrder>>([Order("order-private-open","client-private-open")]);}
        public Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(string canonicalSymbol,int limit,CancellationToken ct){RecentOrderCalls++;return Task.FromResult<IReadOnlyList<ExchangeOrder>>([Order("order-private-recent","client-private-recent")]);}
        public Task<bool> PingAsync(CancellationToken ct)=>Task.FromResult(true);
        public Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)=>throw new InvalidOperationException("HealthCheck must not be called by the read-only runner.");
        public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> canonicalSymbols,AgentSqliteStore database)=>throw new InvalidOperationException("Realtime feed must not be created by the read-only runner.");
        public Task<MarginSnapshot> GetMarginAsync(CancellationToken ct)=>throw new InvalidOperationException("Margin read is outside this runner.");
        public Task<TradingRule> GetRulesAsync(string canonicalSymbol,CancellationToken ct)=>Task.FromResult(new TradingRule(canonicalSymbol,.001m,.1m,.001m,5m,20));
        public Task<MarketEvidence> GetMarketAsync(string canonicalSymbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string canonicalSymbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string canonicalSymbol,string interval,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string canonicalSymbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public Task SetLeverageAsync(string canonicalSymbol,int leverage,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task SetMarginModeAsync(string canonicalSymbol,bool isolated,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task<ExchangeOrder> PlaceMarketAsync(string canonicalSymbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct){MutationCount++;return Task.FromResult(Order("mutation","mutation"));}
        public Task<ExchangeOrder> PlaceLimitAsync(string canonicalSymbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct){MutationCount++;return Task.FromResult(Order("mutation","mutation"));}
        public Task<ExchangeOrder> PlaceProtectionAsync(string canonicalSymbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct){MutationCount++;return Task.FromResult(Order("mutation","mutation"));}
        public Task<ExchangeOrder?> FindOrderAsync(string canonicalSymbol,string clientOrderId,CancellationToken ct){FindOrderCalls++;return Task.FromResult(FindResult);}
        public Task CancelOrderAsync(string canonicalSymbol,string orderId,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        internal static ExchangeOrder Order(string id,string clientId)=>new(
            "SOLUSDT",id,clientId,"NEW",0,0,"LIMIT",PositionSide.Long,false,DateTime.UtcNow);
    }
}
