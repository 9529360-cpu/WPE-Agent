using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class DeterministicStrategyModuleTests
{
    [Fact]
    public void BuiltInRegistryCoversEveryStrategyFamilyWithVersionedModule()
    {
        var registry = new DeterministicStrategyRegistry();

        Assert.Equal(Enum.GetValues<StrategyFamily>().OrderBy(x => x), registry.Families.OrderBy(x => x));
        foreach (var family in Enum.GetValues<StrategyFamily>())
        {
            var module = registry.Resolve(family);
            Assert.Equal(family, module.Family);
            Assert.False(string.IsNullOrWhiteSpace(module.ImplementationVersion));
        }
    }

    [Fact]
    public void RegistryFailsClosedWhenStrategyFamilyIsMissingOrDuplicated()
    {
        var missing = new IDeterministicStrategyModule[]
        {
            new FakeModule(StrategyFamily.TrendBreakout, "trend-test/1"),
            new FakeModule(StrategyFamily.MeanReversion, "mean-test/1")
        };
        Assert.Throws<InvalidOperationException>(() => new DeterministicStrategyRegistry(missing));

        var duplicate = new IDeterministicStrategyModule[]
        {
            new FakeModule(StrategyFamily.TrendBreakout, "trend-test/1"),
            new FakeModule(StrategyFamily.TrendBreakout, "trend-test/2"),
            new FakeModule(StrategyFamily.MeanReversion, "mean-test/1"),
            new FakeModule(StrategyFamily.NewsMomentum, "news-test/1")
        };
        Assert.Throws<InvalidOperationException>(() => new DeterministicStrategyRegistry(duplicate));
    }

    [Fact]
    public void ResearchEngineRoutesSignalThroughInjectedStrategyModule()
    {
        var registry = new DeterministicStrategyRegistry(
        [
            new FakeModule(StrategyFamily.TrendBreakout, "replacement-trend/42", -1),
            new FakeModule(StrategyFamily.MeanReversion, "mean-test/1"),
            new FakeModule(StrategyFamily.NewsMomentum, "news-test/1")
        ]);
        var engine = new HistoricalResearchEngine(new ResearchRealityModel(new ResearchCostModel(0, 0)), registry);
        var profile = new StrategyProfile
        {
            Id = "replaceable",
            Version = "candidate-v7",
            Symbol = "BTCUSDT",
            Family = StrategyFamily.TrendBreakout,
            Parameters = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0)
        };
        var market = new MarketEvidence("BTCUSDT", 100, 99, 101, 50, 0, 0, 0, new(0, 0, 1, 1, 1, 1, 0), DateTime.UtcNow);

        var signal = engine.Signal(profile, market, []);

        Assert.Equal(-1, signal.Direction);
        Assert.Contains("replacement-trend/42", signal.Reason, StringComparison.Ordinal);
        Assert.Same(registry, engine.Strategies);
    }

    [Fact]
    public void HistoricalResearchEngineContainsNoStrategySpecificFamilyBranches()
    {
        var source = File.ReadAllText(ProductPath("Services", "Agent", "StrategyResearchAgent.cs"));
        var marker = source.IndexOf("internal sealed class HistoricalResearchEngine", StringComparison.Ordinal);
        Assert.True(marker >= 0);
        var engineSource = source[marker..];

        Assert.DoesNotContain("StrategyFamily.TrendBreakout", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StrategyFamily.MeanReversion", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StrategyFamily.NewsMomentum", engineSource, StringComparison.Ordinal);
        Assert.Contains("_strategies.Resolve(profile.Family)", engineSource, StringComparison.Ordinal);
    }

    private sealed class FakeModule(StrategyFamily family, string version, int direction = 0) : IDeterministicStrategyModule
    {
        public StrategyFamily Family => family;
        public string ImplementationVersion => version;

        public StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news) =>
            new(profile.Id, profile.Symbol, direction, direction == 0 ? 0 : .75, $"strategy={ImplementationVersion}; fake");

        public List<(double Return, bool Trade)> Simulate(
            StrategyProfile profile,
            IReadOnlyList<CandleEvidence> candles,
            IReadOnlyList<NewsFeature> news,
            ResearchRealityModel reality) => [];
    }

    private static string ProductPath(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine([root, .. segments]);
    }
}
