using System;
using WpeAgent.RuntimeServices;
using WpeAgent.Plugins;
using WpeAgent.Equities;
using 币安量化机器人.Services.Security;
using 币安量化机器人.Services.MarketData;
using 币安量化机器人.Services.Exchange.Binance;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using 币安量化机器人.Services.Agent;
using Serilog;
using 币安量化机器人.Application.Services;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Strategies;
using 币安量化机器人.Monitoring;

namespace 币安量化机器人.Services;

public static class ServiceLocator
{
    static ServiceLocator()
    {
        AppSettingsService.Load();
    }

    private static readonly Lazy<DataCacheService> CacheFactory = new(() =>
    {
        var cache = new DataCacheService();
        cache.InitializeAsync().GetAwaiter().GetResult();
        return cache;
    });

    private static readonly Lazy<Serilog.ILogger> LoggerFactory = new(() =>
    {
        // Simple Serilog-based logger for services to use
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new SensitiveFileLogSink(AppDataPaths.LogFile("services.log")))
            .CreateLogger();
        return logger;
    });
    private static readonly Lazy<AppSettings> SettingsFactory = new(() => AppSettingsService.Current);
    private static readonly Lazy<SystemState> SystemStateFactory = new(() => InitializeSystemState());
    private static readonly Lazy<RuntimeMarketStateStore> RuntimeMarketFactory = new(() => new RuntimeMarketStateStore());
    private static readonly Lazy<RuntimeTradingStateStore> RuntimeTradingFactory = new(() => new RuntimeTradingStateStore());
    private static readonly Lazy<RuntimeBacktestStateStore> RuntimeBacktestFactory = new(() => new RuntimeBacktestStateStore(new AgentSqliteStore()));
    private static readonly Lazy<RuntimeCrossAssetResearchStateStore> RuntimeCrossAssetResearchFactory = new(() => new RuntimeCrossAssetResearchStateStore());
    private static readonly Lazy<RuntimeDistributionStateStore> RuntimeDistributionFactory = new(() => new RuntimeDistributionStateStore());
    private static readonly Lazy<RuntimeEquityStateStore> RuntimeEquityFactory = new(() => new RuntimeEquityStateStore(new AgentSqliteStore()));
    private static readonly Lazy<EquityMarketDataStateStore> RuntimeEquityMarketFactory = new(() => new EquityMarketDataStateStore());
    private static readonly Lazy<RuntimeHistoricalCollectionsSnapshotStore> RuntimeHistoricalCollectionsFactory = new(() => new RuntimeHistoricalCollectionsSnapshotStore(AppDataPaths.File("agent.db")));
    private static readonly Lazy<RuntimeConnectionStateStore> RuntimeConnectionFactory = new(() => new RuntimeConnectionStateStore());
    private static readonly Lazy<RuntimeSkillCallStateStore> RuntimeSkillCallFactory = new(() => new RuntimeSkillCallStateStore(new AgentSqliteStore()));
    private static readonly Lazy<RuntimeMemoryStateStore> RuntimeMemoryFactory = new(() => new RuntimeMemoryStateStore(new AgentSqliteStore()));
    private static readonly Lazy<RuntimeAgentOperationsStateStore> RuntimeAgentOperationsFactory = new(() => new RuntimeAgentOperationsStateStore(new AgentSqliteStore()));
    private static readonly Lazy<RuntimeTeacherStateStore> RuntimeTeacherFactory = new(() => new RuntimeTeacherStateStore(new AgentSqliteStore()));
    private static readonly Lazy<RuntimeAuditStateStore> RuntimeAuditFactory = new(() => new RuntimeAuditStateStore(new AgentSqliteStore()));
    private static readonly Lazy<RuntimeNotificationStateStore> RuntimeNotificationFactory = new(() => new RuntimeNotificationStateStore());
    private static readonly Lazy<RuntimeAuthorizationStateStore> RuntimeAuthorizationFactory = new(() => new RuntimeAuthorizationStateStore(new AgentSettingsStore(), new AgentSqliteStore()));
    private static readonly Lazy<SecurityStorageRuntime> SecurityStorageFactory = new(() => SecurityStorageComposition.Create(AppDataPaths.File("security-storage.db")));
    private static readonly Lazy<PublicMarketRuntime> PublicMarketFactory = new(() => new PublicMarketRuntime(
        ["BTCUSDT", "ETHUSDT"],
        new BinancePublicMarketClient(useTestnet: false),
        new BinanceStreamClient(useTestnet: false),
        Cache));
    private static readonly Lazy<LocalPluginRegistry> PluginRegistryFactory = new(LocalPluginRegistry.CreateDefault);
    private static readonly Lazy<IMultiTimeframeAnalyzer> AnalyzerFactory = new(() => new MultiTimeframeAnalyzer());
    private static readonly Lazy<IMachineLearningSignalGenerator> MlFactory = new(() => new RandomForestSignalGenerator());
    private static readonly Lazy<ITradeMonitoringHub> MonitoringHubFactory = new(() => new InMemoryTradeMonitoringHub());

    public static DataCacheService Cache => CacheFactory.Value;
    public static AppSettings Settings => SettingsFactory.Value;
    public static SystemState SystemState => SystemStateFactory.Value;
    public static RuntimeMarketStateStore RuntimeMarkets => RuntimeMarketFactory.Value;
    public static RuntimeTradingStateStore RuntimeTrading => RuntimeTradingFactory.Value;
    public static RuntimeBacktestStateStore RuntimeBacktests => RuntimeBacktestFactory.Value;
    public static RuntimeCrossAssetResearchStateStore RuntimeCrossAssetResearch => RuntimeCrossAssetResearchFactory.Value;
    public static RuntimeDistributionStateStore RuntimeDistribution => RuntimeDistributionFactory.Value;
    public static RuntimeEquityStateStore RuntimeEquity => RuntimeEquityFactory.Value;
    public static EquityMarketDataStateStore RuntimeEquityMarkets => RuntimeEquityMarketFactory.Value;
    public static RuntimeHistoricalCollectionsSnapshotStore RuntimeHistoricalCollections => RuntimeHistoricalCollectionsFactory.Value;
    public static RuntimeConnectionStateStore RuntimeConnection => RuntimeConnectionFactory.Value;
    public static RuntimeSkillCallStateStore RuntimeSkillCalls => RuntimeSkillCallFactory.Value;
    public static RuntimeMemoryStateStore RuntimeMemory => RuntimeMemoryFactory.Value;
    public static RuntimeAgentOperationsStateStore RuntimeAgentOperations => RuntimeAgentOperationsFactory.Value;
    public static RuntimeTeacherStateStore RuntimeTeacher => RuntimeTeacherFactory.Value;
    public static RuntimeAuditStateStore RuntimeAudit => RuntimeAuditFactory.Value;
    public static RuntimeNotificationStateStore RuntimeNotifications => RuntimeNotificationFactory.Value;
    public static RuntimeAuthorizationStateStore RuntimeAuthorization => RuntimeAuthorizationFactory.Value;
    public static SecurityStorageRuntime SecurityStorage => SecurityStorageFactory.Value;
    public static PublicMarketRuntime PublicMarket => PublicMarketFactory.Value;
    public static LocalPluginRegistry PluginRegistry => PluginRegistryFactory.Value;
    public static IMultiTimeframeAnalyzer Analyzer => AnalyzerFactory.Value;
    public static IMachineLearningSignalGenerator MachineLearning => MlFactory.Value;
    public static ITradeMonitoringHub MonitoringHub => MonitoringHubFactory.Value;
    public static Serilog.ILogger Logger => LoggerFactory.Value;
    private static SystemState InitializeSystemState()
    {
        var mode = Settings.TradingMode?.ToLowerInvariant() switch
        {
            "paper" => TradingMode.Paper,
            "live" => TradingMode.Live,
            _ => TradingMode.Testnet
        };

        return new SystemState
        {
            Mode = mode,
            Status = AgentStatus.Idle,
            Symbol = string.IsNullOrWhiteSpace(Settings.DefaultSymbol) ? "BTCUSDT" : Settings.DefaultSymbol.ToUpperInvariant(),
            Timeframe = string.IsNullOrWhiteSpace(Settings.DefaultTimeframe) ? "1m" : Settings.DefaultTimeframe,
            LastUpdated = DateTime.UtcNow,
            LastMessage = "系统初始化完成"
        };
    }

    public static async ValueTask DisposeAsync()
    {
        if (PublicMarketFactory.IsValueCreated)
            await PublicMarketFactory.Value.DisposeAsync().ConfigureAwait(false);
    }

}
