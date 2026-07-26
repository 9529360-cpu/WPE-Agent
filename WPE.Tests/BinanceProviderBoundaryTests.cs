using System.Reflection;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange.Binance;

namespace WPE.Tests;

public sealed class BinanceProviderBoundaryTests
{
    [Fact]
    public void RawTransport_IsNonPublicAndDoesNotLeakThroughPublicSignatures()
    {
        var assembly = typeof(BinancePublicMarketClient).Assembly;
        var raw = assembly.GetTypes().Single(type => type.Name == "BinanceApiClient");

        Assert.False(raw.IsPublic || raw.IsNestedPublic);
        Assert.Empty(raw.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(assembly.GetExportedTypes().SelectMany(PublicSignatures), type => Contains(type, raw));
    }

    [Fact]
    public void PublicMarketClient_HasExactCredentialFreeReadOnlySurface()
    {
        var declared = typeof(BinancePublicMarketClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(["Dispose", "GetKlineClosesAsync", "GetMiniTickersAsync"], declared);
        Assert.DoesNotContain(typeof(BinancePublicMarketClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.Name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                     field.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(BinancePublicMarketClient).GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.Name?.Contains("path", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void ProviderPrivateSkill_AcceptsOnlyNarrowPublicMarketTransport()
    {
        var skill = typeof(BinanceFuturesAdapter).Assembly.GetTypes().Single(type => type.Name == "MarketStructureSkill");
        Assert.False(skill.IsPublic);
        var constructor = Assert.Single(skill.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal("IBinancePublicMarketTransport", Assert.Single(constructor.GetParameters()).ParameterType.Name);
    }

    [Fact]
    public void ProductionSource_ConstructsRawTransportOnlyInBinanceAdapter()
    {
        var root = SourceRoot();
        var constructions = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrTestPath(path))
            .Select(path => (Path: Path.GetRelativePath(root, path), Source: File.ReadAllText(path)))
            .Where(item => item.Source.Contains("new BinanceApiClient(", StringComparison.Ordinal))
            .ToArray();

        var construction = Assert.Single(constructions);
        Assert.Equal(Path.Combine("Services", "Agent", "BinanceFuturesAdapter.cs"), construction.Path);
        Assert.DoesNotContain("Activator.CreateInstance", construction.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ExcludesExactLegacyBinanceSources()
    {
        var project = File.ReadAllText(Path.Combine(SourceRoot(), "币安量化机器人.csproj"));
        Assert.Equal(1, Count(project, "Compile Remove=\"Services\\BinanceReadFacade.cs\""));
        Assert.Equal(1, Count(project, "Compile Remove=\"Services\\AiForecastService.cs\""));
        Assert.Equal(1, Count(project, "Compile Remove=\"Infrastructure\\Data\\BinanceDataCollectionService.cs\""));
    }

    private static IEnumerable<Type> PublicSignatures(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).SelectMany(member => member switch
        {
            MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
            ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            EventInfo eventInfo when eventInfo.EventHandlerType is not null => [eventInfo.EventHandlerType],
            _ => []
        });

    private static bool Contains(Type candidate, Type raw) =>
        candidate == raw || candidate.IsArray && Contains(candidate.GetElementType()!, raw) ||
        candidate.IsGenericType && candidate.GetGenericArguments().Any(argument => Contains(argument, raw));

    private static string SourceRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static bool IsGeneratedOrTestPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}WPE.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;
}
