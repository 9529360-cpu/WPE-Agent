using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;
using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class ReadOnlyFacadeContractTests
{
    private static readonly Type[] UiReaderInterfaces =
        [typeof(IMarketDataReader), typeof(IAccountReader), typeof(IOrderQueryReader)];

    [Fact]
    public void UiReaderInterfaces_DoNotExposeMutationCredentialOrRawMethods()
    {
        var exposed = UiReaderInterfaces.SelectMany(type => type.GetMethods())
            .Where(method => IsForbidden(method.Name))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}");
        Assert.Empty(exposed);
    }

    [Fact]
    public void ProductionAssembly_DoesNotContainLegacyBinanceReadStack()
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "BinanceReadFacade",
            "AiForecastService",
            "BinanceDataCollectionService"
        };

        Assert.DoesNotContain(typeof(ServiceLocator).Assembly.GetTypes(), type => forbidden.Contains(type.Name));
    }

    [Fact]
    public void ServiceLocator_PublicSurfaceDoesNotExposeLegacyBinanceReadStack()
    {
        Assert.DoesNotContain(
            typeof(ServiceLocator).GetMembers(BindingFlags.Public | BindingFlags.Static),
            ExposesLegacyBinanceReadStack);
    }

    [Fact]
    public void ProductionComposition_DoesNotRegisterLegacyBinanceServices()
    {
        using var provider = ServiceConfiguration.ConfigureServices(new ServiceCollection());
        var dataCollectionContract = typeof(ServiceLocator).Assembly.GetTypes()
            .Single(type => type.Name == "IDataCollectionService");

        Assert.Null(provider.GetService<BinanceApiClient>());
        Assert.Null(provider.GetService(dataCollectionContract));
    }

    [Fact]
    public void ProductionAssembly_DoesNotCompileUnmountedLegacyDesktopModules()
    {
        var legacyModuleNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "AccountFundsView",
            "ModelHub",
            "FundingView",
            "RealtimeView",
            "BacktestView",
            "RiskCenterView",
            "PositionsOrdersView",
            "AlertCenterView",
            "NotificationService"
        };

        var compiledLegacyModules = typeof(ServiceLocator).Assembly.GetTypes()
            .Where(type => legacyModuleNames.Contains(type.Name))
            .Select(type => type.FullName);

        Assert.Empty(compiledLegacyModules);
    }

    [Fact]
    public void ProductionAssembly_DoesNotContainDeletedLegacyExecutionTypes()
    {
        var forbiddenTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "AutomatedTradingEngine",
            "RobustOrderExecutor",
            "ExecutionService",
            "StrategySignalExecutor",
            "TradeExecutionService",
            "TradeView"
        };

        var compiledForbiddenTypes = typeof(ServiceLocator).Assembly.GetTypes()
            .Where(type => forbiddenTypes.Contains(type.Name))
            .Select(type => type.FullName);

        Assert.Empty(compiledForbiddenTypes);
    }

    [Fact]
    public async Task BinancePlugin_StillCreatesProviderScopedAdapter()
    {
        var plugin = new BinanceProviderPlugin();
        var profile = new ExchangeConnectionProfile
        {
            ProviderId = plugin.Descriptor.Id,
            IsTestnet = true,
            Endpoint = "https://testnet.binancefuture.com"
        };

        await using var provider = plugin.Create(
            profile,
            new Dictionary<string, string>
            {
                ["apiKey"] = "test-key",
                ["secret"] = "test-secret"
            });

        Assert.IsType<BinanceFuturesAdapter>(provider);
        Assert.Equal(ExchangeEnvironment.Testnet, provider.Environment);
    }

    private static bool IsForbidden(string name) =>
        name.StartsWith("Place", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Raw", StringComparison.OrdinalIgnoreCase);

    private static bool ExposesClient(MemberInfo member) => member switch
    {
        MethodInfo method => ContainsClient(method.ReturnType) || method.GetParameters().Any(parameter => ContainsClient(parameter.ParameterType)),
        PropertyInfo property => ContainsClient(property.PropertyType),
        FieldInfo field => ContainsClient(field.FieldType),
        EventInfo eventInfo => eventInfo.EventHandlerType is not null && ContainsClient(eventInfo.EventHandlerType),
        _ => false
    };

    private static bool ExposesLegacyBinanceReadStack(MemberInfo member) => member switch
    {
        MethodInfo method => IsLegacyBinanceReadType(method.ReturnType) ||
                             method.GetParameters().Any(parameter => IsLegacyBinanceReadType(parameter.ParameterType)),
        PropertyInfo property => IsLegacyBinanceReadType(property.PropertyType),
        FieldInfo field => IsLegacyBinanceReadType(field.FieldType),
        EventInfo eventInfo => eventInfo.EventHandlerType is not null &&
                               IsLegacyBinanceReadType(eventInfo.EventHandlerType),
        _ => false
    };

    private static bool IsLegacyBinanceReadType(Type type) =>
        type == typeof(BinanceApiClient) ||
        type.Name == "BinanceReadFacade" ||
        type == typeof(BinanceStreamClient) ||
        type == typeof(IMarketDataReader) ||
        type == typeof(IAccountReader) ||
        type == typeof(IOrderQueryReader) ||
        type.IsArray && IsLegacyBinanceReadType(type.GetElementType()!) ||
        type.IsGenericType && type.GetGenericArguments().Any(IsLegacyBinanceReadType);

    private static bool ContainsClient(Type type) =>
        type == typeof(BinanceApiClient) ||
        type.IsArray && ContainsClient(type.GetElementType()!) ||
        type.IsGenericType && type.GetGenericArguments().Any(ContainsClient);
}
