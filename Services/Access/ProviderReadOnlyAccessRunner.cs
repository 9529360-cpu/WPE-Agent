using System.IO;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Access;

public static class ProviderReadOnlyStatus
{
    public const string Pass="pass";
    public const string Fail="fail";
    public const string Unsupported="unsupported";
    public const string Stale="stale";
}

public sealed record ProviderReadOnlyCheck(string Check,string Status,string DiagnosticCode);
public sealed record ProviderReadOnlyEvidence(int PositionCount,int OpenOrderCount,int RecentOrderCount);
public sealed record ReadOnlyIntentReconciliation(int RequestedCount,int FoundTerminalCount,int FoundNonTerminalCount,int MissingCount,int QueryFailureCount);
public sealed record ProviderReadOnlyReport(
    string Status,IReadOnlyList<ProviderReadOnlyCheck> Checks,ProviderReadOnlyEvidence? Evidence=null,
    IntentStateSummary? IntentState=null,ReadOnlyIntentReconciliation? Reconciliation=null);
public sealed record ProviderReadOnlyAccessResult(bool Success,string ReportPath);

public interface IProviderReadOnlyAccessClient:IAsyncDisposable
{
    ProviderEnvironmentValidation ValidateEnvironment();
    ExchangeProviderDescriptor Descriptor{get;}
    Task<DateTime> GetServerTimeAsync(CancellationToken ct);
    Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct);
    Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct);
    Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);
    Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken ct);
    bool SupportsRecentOrders{get;}
    Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(string symbol,int limit,CancellationToken ct);
    Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct);
    Task<IReadOnlyDictionary<string,ExchangeCapability>> ProbeCapabilitiesAsync(
        IEnumerable<string> canonicalSymbols,CancellationToken ct);
}

public sealed class ProviderReadOnlyAccessClient(IExchangeProvider provider):IProviderReadOnlyAccessClient
{
    public ExchangeProviderDescriptor Descriptor=>provider.Descriptor;
    public bool SupportsRecentOrders=>provider is IRecentOrderProvider;
    public ProviderEnvironmentValidation ValidateEnvironment()=>provider is IProviderEnvironmentGuard guard
        ?guard.ValidateEnvironment(requireTestnet:true)
        :new(false,false,false,"provider environment validation is not implemented");
    public Task<DateTime> GetServerTimeAsync(CancellationToken ct)=>provider.GetServerTimeAsync(ct);
    public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)=>provider.CheckPermissionsAsync(ct);
    public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)=>provider.GetAccountAsync(ct);
    public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>provider.GetPositionsAsync(ct);
    public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(CancellationToken ct)=>provider.GetOpenOrdersAsync(null,ct);
    public Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)=>provider is IProviderMarketCatalog catalog
        ?catalog.DiscoverMarketCatalogAsync(ct)
        :Task.FromResult(new ProviderMarketCatalog(ProviderCatalogState.Unsupported,[],DateTimeOffset.UtcNow));
    public Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(string symbol,int limit,CancellationToken ct)=>provider is IRecentOrderProvider recent
        ?recent.GetRecentOrdersAsync(symbol,limit,ct)
        :Task.FromResult<IReadOnlyList<ExchangeOrder>>([]);
    public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>provider.FindOrderAsync(symbol,clientOrderId,ct);
    public Task<IReadOnlyDictionary<string,ExchangeCapability>> ProbeCapabilitiesAsync(
        IEnumerable<string> canonicalSymbols,CancellationToken ct)=>
        new ProviderCapabilityProbe().ProbeAsync(provider,canonicalSymbols,true,ct);
    public ValueTask DisposeAsync()=>provider.DisposeAsync();
}

public static class ProviderReadOnlyAccessRunner
{
    public static async Task<ProviderReadOnlyAccessResult> RunConfiguredAsync(CancellationToken ct=default)
    {
        var directory=Path.Combine(AppDataPaths.TestArtifactsDirectory,"provider-readonly-access");
        Directory.CreateDirectory(directory);
        var reportPath=Path.Combine(directory,$"provider-readonly-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        ProviderReadOnlyReport report;
        try
        {
            var store=new AgentSettingsStore();
            var settings=store.Load();
            var profile=settings.Exchanges.FirstOrDefault(x=>
                x.Enabled&&x.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase))
                ??throw new InvalidOperationException("The active configured provider profile is unavailable.");
            AgentSettingsStore.ValidateExchangeProfile(profile);
            if(!profile.IsTestnet)throw new InvalidOperationException("Mainnet is not supported by the read-only access runner.");
            var credentials=store.GetExchangeCredentials(profile);
            var descriptor=new ExchangeProviderCatalog().Installed.SingleOrDefault(x=>x.Id.Equals(profile.ProviderId,StringComparison.OrdinalIgnoreCase))
                ??throw new NotSupportedException("Configured provider adapter is not installed.");
            if(descriptor.CredentialFields.Where(x=>x.Required)
                .Any(x=>!credentials.TryGetValue(x.Key,out var value)||string.IsNullOrWhiteSpace(value)))
                throw new InvalidOperationException("Configured provider credentials are incomplete.");

            var provider=new ExchangeProviderCatalog().Create(profile,credentials);
            await using var client=new ProviderReadOnlyAccessClient(provider);
            report=await ProbeAsync(client,settings.Symbols,ct);
            var database=new AgentSqliteStore();var intentState=await database.GetIntentStateSummaryAsync(ct);var recoverable=await database.GetRecoverableIntentsAsync(ct);
            var reconciliation=await ReconcileIntentsReadOnlyAsync(client,recoverable,ct);
            var checks=report.Checks.ToList();checks.Add(intentState.RecoverableCount==0
                ?Pass("intent_state")
                :Fail("intent_state",$"recoverable={intentState.RecoverableCount}; missing={reconciliation.MissingCount}; nonterminal={reconciliation.FoundNonTerminalCount}; queryFailures={reconciliation.QueryFailureCount}"));
            report=Complete(checks) with{Evidence=report.Evidence,IntentState=intentState,Reconciliation=reconciliation};
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            report=Failure("configuration",ex);
        }

        await SensitiveDataRedactor.WriteRedactedJsonAsync(reportPath,report,ct);
        return new(report.Status==ProviderReadOnlyStatus.Pass,reportPath);
    }

    public static async Task<ProviderReadOnlyReport> ProbeAsync(
        IProviderReadOnlyAccessClient client,IReadOnlyList<string> symbols,CancellationToken ct=default)
    {
        var checks=new List<ProviderReadOnlyCheck>();
        var environment=client.ValidateEnvironment();
        checks.Add(environment.CanRead&&environment.TestnetAvailable
            ?Pass("environment")
            :Unsupported("environment",environment.Failure));
        checks.Add(client.Descriptor.Installed?Pass("adapter"):Unsupported("adapter","adapter is not installed"));
        if(checks.Any(x=>x.Check=="environment"&&x.Status!=ProviderReadOnlyStatus.Pass))
            return Complete(checks);

        ProviderMarketCatalog? catalog=null;
        var positionCount=0;var openOrderCount=0;var recentOrderCount=0;
        await Capture(checks,"catalog",async()=>
        {
            catalog=await client.DiscoverMarketCatalogAsync(ct);
            checks.Add(catalog.State switch
            {
                ProviderCatalogState.Available=>Pass("catalog_state"),
                ProviderCatalogState.Stale=>Stale("catalog_state",catalog.Failure),
                ProviderCatalogState.Unsupported=>Unsupported("catalog_state",catalog.Failure),
                _=>Fail("catalog_state",catalog.Failure)
            });
        },addPass:false);
        if(catalog?.State!=ProviderCatalogState.Available)
            return Complete(checks);

        await Capture(checks,"clock",async()=>_ =await client.GetServerTimeAsync(ct));
        await Capture(checks,"permissions",async()=>
        {
            var permission=await client.CheckPermissionsAsync(ct);
            if(!permission.CanRead)throw new InvalidOperationException("Provider read permission is unavailable.");
        });
        await Capture(checks,"account",async()=>_ =await client.GetAccountAsync(ct));
        await Capture(checks,"positions",async()=>positionCount=(await client.GetPositionsAsync(ct)).Count);
        await Capture(checks,"open_orders",async()=>openOrderCount=(await client.GetOpenOrdersAsync(ct)).Count);

        if(client.SupportsRecentOrders&&symbols.Count>0)
            await Capture(checks,"recent_orders",async()=>
            {
                foreach(var symbol in symbols) recentOrderCount+=(await client.GetRecentOrdersAsync(symbol,20,ct)).Count;
            });
        else
            checks.Add(Unsupported("recent_orders","recent order reader is not implemented"));

        if(symbols.Count==0)
            checks.Add(Unsupported("capability","no configured symbols"));
        else
            await Capture(checks,"capability",async()=>
            {
                var capabilities=await client.ProbeCapabilitiesAsync(symbols,ct);
                if(capabilities.Count!=symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                    throw new InvalidOperationException("Capability observations are incomplete.");
                if(capabilities.Values.Any(x=>x.Status==CapabilityStatus.Error))
                    throw new InvalidOperationException("Capability observation failed.");
                if(capabilities.Values.Any(x=>x.Status==CapabilityStatus.Stale))
                    checks.Add(Stale("capability_state","capability observation is stale"));
                else if(capabilities.Values.Any(x=>x.Status==CapabilityStatus.Unsupported))
                    checks.Add(Unsupported("capability_state","capability is unsupported"));
                else
                    checks.Add(Pass("capability_state"));
            },addPass:false);

        return Complete(checks) with{Evidence=new(positionCount,openOrderCount,recentOrderCount)};
    }

    internal static async Task<ReadOnlyIntentReconciliation> ReconcileIntentsReadOnlyAsync(
        IProviderReadOnlyAccessClient client,IReadOnlyList<PersistedIntent> intents,CancellationToken ct)
    {
        var terminal=0;var nonterminal=0;var missing=0;var failures=0;
        foreach(var saved in intents)try
        {
            var order=await client.FindOrderAsync(saved.Intent.Symbol,saved.Intent.ClientOrderId,ct);
            if(order is null)missing++;
            else if(order.Status is "FILLED" or "CANCELED" or "REJECTED" or "EXPIRED")terminal++;
            else nonterminal++;
        }
        catch(OperationCanceledException){throw;}
        catch{failures++;}
        return new(intents.Count,terminal,nonterminal,missing,failures);
    }

    private static async Task Capture(
        ICollection<ProviderReadOnlyCheck> checks,string key,Func<Task> action,bool addPass=true)
    {
        try
        {
            await action();
            if(addPass)checks.Add(Pass(key));
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            checks.Add(Fail(key,ex.ToString()));
        }
    }

    private static ProviderReadOnlyReport Complete(IReadOnlyList<ProviderReadOnlyCheck> checks)
    {
        var status=checks.Any(x=>x.Status==ProviderReadOnlyStatus.Fail)?ProviderReadOnlyStatus.Fail
            :checks.Any(x=>x.Status==ProviderReadOnlyStatus.Stale)?ProviderReadOnlyStatus.Stale
            :ProviderReadOnlyStatus.Pass;
        return new(status,checks);
    }

    private static ProviderReadOnlyReport Failure(string key,Exception ex)=>
        new(ProviderReadOnlyStatus.Fail,[Fail(key,ex.ToString())]);
    private static ProviderReadOnlyCheck Pass(string key)=>new(key,ProviderReadOnlyStatus.Pass,Code(key,"pass"));
    private static ProviderReadOnlyCheck Fail(string key,string? diagnostic)=>new(key,ProviderReadOnlyStatus.Fail,Code(key,diagnostic));
    private static ProviderReadOnlyCheck Unsupported(string key,string? diagnostic)=>new(key,ProviderReadOnlyStatus.Unsupported,Code(key,diagnostic));
    private static ProviderReadOnlyCheck Stale(string key,string? diagnostic)=>new(key,ProviderReadOnlyStatus.Stale,Code(key,diagnostic));
    private static string Code(string key,string? diagnostic)=>UiDiagnostic.FromText(key+":"+diagnostic,"Provider read-only access").Code;
}
