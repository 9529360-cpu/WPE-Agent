using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services;

namespace 币安量化机器人.Services.Exchange;

/// <summary>Live, fail-closed capability probe. It uses provider permissions and symbol rules; no catalog is inferred.</summary>
public sealed class ProviderCapabilityProbe
{
    public async Task<IReadOnlyDictionary<string,ExchangeCapability>> ProbeAsync(
        IExchangeProvider provider,IEnumerable<string> canonicalSymbols,bool testnet,CancellationToken ct)
    {
        var result=new Dictionary<string,ExchangeCapability>(StringComparer.OrdinalIgnoreCase);
        var requested=canonicalSymbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var environment=provider is IProviderEnvironmentGuard guard
            ?guard.ValidateEnvironment(testnet)
            :new ProviderEnvironmentValidation(false,false,false,"provider environment validation is not implemented");
        if(!environment.CanRead)
        {
            AddBlocked(result,provider,requested,CapabilityStatus.Unsupported,
                environment.Failure??"provider environment is unsupported");
            return result;
        }

        var catalog=provider is IProviderMarketCatalog catalogProvider
            ?await catalogProvider.DiscoverMarketCatalogAsync(ct)
            :new ProviderMarketCatalog(ProviderCatalogState.Unsupported,[],DateTimeOffset.UtcNow,
                "provider market catalog is not implemented");
        if(catalog.State!=ProviderCatalogState.Available)
        {
            AddBlocked(result,provider,requested,
                catalog.State==ProviderCatalogState.Error?CapabilityStatus.Error:CapabilityStatus.Unsupported,
                catalog.Failure??"provider market catalog is unavailable",catalog.CheckedAt);
            return result;
        }

        ExchangePermissionSnapshot permissions;
        try
        {
            permissions=await provider.CheckPermissionsAsync(ct);
        }
        catch(Exception ex)when(ex is not OperationCanceledException)
        {
            AddBlocked(result,provider,requested,CapabilityStatus.Error,
                SensitiveDataRedactor.ForLog(ex.Message,180));
            return result;
        }

        var declaredRead=HasAll(provider.Descriptor.Capabilities,"account","positions","query-order","candles");
        var declaredMutation=HasAll(provider.Descriptor.Capabilities,"place-order","cancel-order","protection-orders");
        var discovered=catalog.Markets.ToDictionary(x=>x.Instrument.CanonicalSymbol,StringComparer.OrdinalIgnoreCase);
        foreach(var canonical in requested)
        {
            if(!discovered.TryGetValue(canonical,out var market))
            {
                result[canonical]=new(provider.Descriptor.Id,provider.ProviderId,canonical,string.Empty,MarketType.Perpetual,
                    CapabilityStatus.Unsupported,permissions.CanRead,false,false,catalog.CheckedAt,
                    "symbol was not returned by provider catalog");
                continue;
            }

            result[canonical]=new(
                market.Instrument.ExchangeId,market.Instrument.ProviderId,canonical,market.Instrument.NativeSymbol,
                market.Instrument.MarketType,market.Status,
                environment.CanRead&&declaredRead&&permissions.CanRead&&market.CanRead,
                environment.CanTrade&&declaredMutation&&permissions.CanTrade&&market.CanTrade,
                environment.TestnetAvailable&&market.TestnetAvailable,market.CheckedAt,
                market.Failure??environment.Failure);
        }
        return result;
    }

    private static bool HasAll(IReadOnlySet<string> capabilities,params string[] required)=>
        required.All(capabilities.Contains);

    private static void AddBlocked(
        IDictionary<string,ExchangeCapability> result,IExchangeProvider provider,IEnumerable<string> symbols,
        CapabilityStatus status,string failure,DateTimeOffset? checkedAt=null)
    {
        foreach(var canonical in symbols)
            result[canonical]=new(provider.Descriptor.Id,provider.ProviderId,canonical,string.Empty,MarketType.Perpetual,
                status,false,false,false,checkedAt??DateTimeOffset.UtcNow,failure);
    }
}
