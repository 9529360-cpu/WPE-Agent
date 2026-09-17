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
    public void RegistryFailsClosedWhenStrategyFamilyIsMissingDuplicatedOrUnversioned()
    {
        var missing = new IDeterministicStrategyModule[]
        {
            new FakeModule(StrategyFamily.TrendBreakout, "trend-test-v1"),
            new FakeModule(StrategyFamily.MeanReversion, "mean-test-v1")
        };
        Assert.Throws<InvalidOperationException>(() => new DeterministicStrategyRegistry(missing));

        var duplicate = new IDeterministicStrategyModule[]
        {
            new FakeModule(StrategyFamily.TrendBreakout, "trend-test-v1"),
            new FakeModule(StrategyFamily.TrendBreakout, "trend-test-v2"),
            new FakeModule(StrategyFamily.MeanReversion, "mean-test-v1"),
            new FakeModule(StrategyFamily.NewsMomentum, "news-test-v1")
        };
        Assert.Throws<InvalidOperationException>(() => new DeterministicStrategyRegistry(duplicate));

        var unsafeVersion = CompleteModules("trend/test/v1");
        Assert.Throws<InvalidOperationException>(() => new DeterministicStrategyRegistry(unsafeVersion));
    }

    [Fact]
    public void ResearchEngineRoutesSignalThroughInjectedStrategyModuleWhenProfileIsBound()
    {
        var registry = Registry("replacement-trend-v42", -1);
        var engine = new HistoricalResearchEngine(new ResearchRealityModel(new ResearchCostModel(0, 0)), registry);
        var profile = Profile(registry.BindProfileVersion(StrategyFamily.TrendBreakout, "candidate-v7"));
        var market = new MarketEvidence("BTCUSDT", 100, 99, 101, 50, 0, 0, 0, new(0, 0, 1, 1, 1, 1, 0), DateTime.UtcNow);

        var signal = engine.Signal(profile, market, []);

        Assert.Equal(-1, signal.Direction);
        Assert.Contains("replacement-trend-v42", signal.Reason, StringComparison.Ordinal);
        Assert.Same(registry, engine.Strategies);
    }

    [Fact]
    public void UnboundOrOldImplementationCannotProduceTradingSignal()
    {
        var registry = Registry("replacement-trend-v42", 1);
        var engine = new HistoricalResearchEngine(new ResearchRealityModel(new ResearchCostModel(0, 0)), registry);
        var legacy = Profile("candidate-v7");
        var previousImplementation = Profile("candidate-v7--impl-replacement-trend-v41");
        var market = new MarketEvidence("BTCUSDT", 100, 99, 101, 50, 0, 0, 0, new(0, 0, 1, 1, 1, 1, 0), DateTime.UtcNow);

        var legacySignal = engine.Signal(legacy, market, []);
        var oldSignal = engine.Signal(previousImplementation, market, []);

        Assert.Equal(0, legacySignal.Direction);
        Assert.Equal(0, oldSignal.Direction);
        Assert.Contains("revalidated", legacySignal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revalidated", oldSignal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(registry.IsProfileCompatible(legacy));
        Assert.False(registry.IsProfileCompatible(previousImplementation));
    }

    [Fact]
    public void ImplementationVersionChangesCandidateIdentityAndInvalidatesOldBinding()
    {
        var v1 = Registry("replacement-trend-v1");
        var v2 = Registry("replacement-trend-v2");
        var v1Version = v1.BindProfileVersion(StrategyFamily.TrendBreakout, "candidate-1");
        var v2Version = v2.BindProfileVersion(StrategyFamily.TrendBreakout, "candidate-1");
        var v1Id = v1.CandidateId("btcusdt", StrategyFamily.TrendBreakout, "0");
        var v2Id = v2.CandidateId("btcusdt", StrategyFamily.TrendBreakout, "0");

        Assert.NotEqual(v1Version, v2Version);
        Assert.NotEqual(v1Id, v2Id);
        Assert.True(v1.IsProfileCompatible(Profile(v1Version)));
        Assert.False(v2.IsProfileCompatible(Profile(v1Version)));
        Assert.True(v2.IsProfileCompatible(Profile(v2Version)));
    }

    [Fact]
    public async Task ResearchCycleRetiresLegacyActiveAndCreatesImplementationBoundCandidates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpe-strategy-version-{Guid.NewGuid():N}.db");
        try
        {
            var store = new AgentSqliteStore(path);
            var parameters = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0);
            var hash = LocalStrategyParameters.Hash(parameters);
            var legacy = new StrategyProfile
            {
                Id = "BTCUSDT-TrendBreakout-legacy-active",
                Version = "trendbreakout-legacy",
                Symbol = "BTCUSDT",
                Family = StrategyFamily.TrendBreakout,
                Parameters = parameters,
                ParametersHash = hash,
                LineageHash = StrategyLineage.Hash("BTCUSDT", StrategyFamily.TrendBreakout, null, null, 0, hash),
                Lifecycle = StrategyLifecycle.Active,
                ValidationTrades = StrategyGovernor.MinimumValidationTrades,
                ShadowObservations = StrategyGovernor.MinimumShadowObservations,
                QualityScore = .9,
                Expectancy = .01,
                MaxDrawdown = .05
            };
            await store.UpsertStrategyAsync(legacy, CancellationToken.None);

            var agent = new StrategyResearchAgent(store, utcNow: () => new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc));
            await agent.RunOnceAsync(["BTCUSDT"], new RiskLimits { MinimumBacktestTrades = 10 }, CancellationToken.None);

            var rows = await store.GetStrategiesAsync(CancellationToken.None);
            var retired = rows.Single(x => x.Id == legacy.Id);
            Assert.Equal(StrategyLifecycle.Retired, retired.Lifecycle);
            Assert.Contains("implementation changed", retired.LastReason, StringComparison.OrdinalIgnoreCase);

            var currentTrend = rows.Where(x => x.Family == StrategyFamily.TrendBreakout && x.Id != legacy.Id).ToArray();
            Assert.NotEmpty(currentTrend);
            Assert.All(currentTrend, x => Assert.Contains("trend-breakout-v1", x.Id, StringComparison.Ordinal));
            Assert.All(currentTrend, x => Assert.EndsWith("--impl-trend-breakout-v1", x.Version, StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(path);
            DeleteIfExists(path + "-wal");
            DeleteIfExists(path + "-shm");
        }
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
        Assert.Contains("IsProfileCompatible(profile)", engineSource, StringComparison.Ordinal);
    }

    private static DeterministicStrategyRegistry Registry(string trendVersion, int direction = 0) => new(
    [
        new FakeModule(StrategyFamily.TrendBreakout, trendVersion, direction),
        new FakeModule(StrategyFamily.MeanReversion, "mean-test-v1"),
        new FakeModule(StrategyFamily.NewsMomentum, "news-test-v1")
    ]);

    private static IDeterministicStrategyModule[] CompleteModules(string trendVersion) =>
    [
        new FakeModule(StrategyFamily.TrendBreakout, trendVersion),
        new FakeModule(StrategyFamily.MeanReversion, "mean-test-v1"),
        new FakeModule(StrategyFamily.NewsMomentum, "news-test-v1")
    ];

    private static StrategyProfile Profile(string version) => new()
    {
        Id = "replaceable",
        Version = version,
        Symbol = "BTCUSDT",
        Family = StrategyFamily.TrendBreakout,
        Parameters = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0)
    };

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

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static string ProductPath(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine([root, .. segments]);
    }
}
