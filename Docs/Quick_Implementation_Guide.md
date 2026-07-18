# 快速实施指南 - 依赖注入替换

本文档提供逐步实施依赖注入替换 ServiceLocator 的快速参考。

---

## 第一步：准备阶段

### 1.1 安装必要的 NuGet 包

```bash
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Configuration
dotnet add package Microsoft.Extensions.Configuration.Json
dotnet add package Microsoft.Extensions.Hosting
```

### 1.2 在项目根目录创建文件结构

```
Services/
├── ServiceConfiguration.cs          [新建] 
├── ServiceLocator.cs                [保留，逐步迁移]
├── ErrorRecoveryHandler.cs          [新建]
└── ApplicationResourceManager.cs    [新建]

Core/
├── Configuration/
│   └── TradingConfiguration.cs      [新建]
├── Exceptions/
│   └── TradingExceptions.cs         [新建]
└── Logging/
    └── LoggerConfiguration.cs       [新建]
```

---

## 第二步：创建依赖注入服务配置

### 2.1 创建 `Services/ServiceConfiguration.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using 币安量化机器人.Application.Backtesting;
using 币安量化机器人.Application.Services;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Infrastructure.Data;
using 币安量化机器人.Monitoring;

namespace 币安量化机器人.Services;

public static class ServiceConfiguration
{
    /// <summary>
    /// 配置所有交易相关的服务
    /// </summary>
    public static IServiceCollection AddTradingServices(
        this IServiceCollection services,
        Action<TradingServiceOptions>? configure = null)
    {
        var options = new TradingServiceOptions();
        configure?.Invoke(options);

        // === 核心基础服务 ===
        services.AddSingleton<AppSettings>();
        services.AddSingleton<DataCacheService>();
        services.AddSingleton<BinanceApiClient>();
        services.AddSingleton<BinanceStreamClient>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<RiskEngine>();

        // === 风险管理 ===
        services.AddSingleton<IRiskManager, RiskManager>();

        // === 分析与机器学习 ===
        services.AddSingleton<IMultiTimeframeAnalyzer, MultiTimeframeAnalyzer>();
        services.AddSingleton<IMachineLearningSignalGenerator, RandomForestSignalGenerator>();
        services.AddSingleton<IFeatureStore, InMemoryFeatureStore>();

        // === 数据管道 ===
        services.AddSingleton(CreateDataPipeline);
        services.AddSingleton<DataPipelineOrchestrator>();
        services.AddSingleton<IMarketDataService, PipelineMarketDataService>();

        // === 监控与编排 ===
        services.AddSingleton<ITradeMonitoringHub, InMemoryTradeMonitoringHub>();
        services.AddSingleton<StrategyOrchestrator>();
        services.AddSingleton<IBacktestEngine, DefaultBacktestEngine>();
        services.AddSingleton<GridSearchStrategyOptimizer>();
        services.AddSingleton<WalkForwardOptimizer>();

        // === 执行服务 ===
        services.AddSingleton<TradeExecutionService>();
        services.AddSingleton<ErrorRecoveryHandler>();

        // === 日志 ===
        services.AddSingleton<Serilog.ILogger>(sp =>
            Core.Logging.LoggerConfiguration.CreateLogger(options.Environment));

        return services;
    }

    private static RealTimeDataPipeline CreateDataPipeline(IServiceProvider provider)
    {
        var featureStore = provider.GetRequiredService<IFeatureStore>();
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dataDirectory);

        var dbPath = Path.Combine(dataDirectory, "trading.sqlite");
        var importPath = Path.Combine(dataDirectory, "import");
        Directory.CreateDirectory(importPath);

        var sources = new Core.Data.IDataSource[]
        {
            new DatabaseDataSource($"Data Source={dbPath}"),
            new ApiDataSource(
                new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                "https://api.binance.com"),
            new FileDataSource(importPath)
        };

        var qualityRules = new Core.Data.IDataQualityRule[]
        {
            new NullValueQualityRule(),
            new RangeQualityRule("close", 0, double.MaxValue),
            new SpikeDetectionRule("close")
        };

        var engineers = new Core.Data.IFeatureEngineer[]
        {
            new TechnicalIndicatorEngineer(),
            new LagFeatureEngineer()
        };

        return new RealTimeDataPipeline(sources, qualityRules, engineers, featureStore);
    }
}

/// <summary>
/// 交易服务配置选项
/// </summary>
public class TradingServiceOptions
{
    public string Environment { get; set; } = "Development";
    public bool UseTestnet { get; set; } = true;
}
```

### 2.2 创建异常类 `Core/Exceptions/TradingExceptions.cs`

```csharp
using System;

namespace 币安量化机器人.Core.Exceptions;

public abstract class TradingException : Exception
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
    public string? ErrorCode { get; protected set; }

    protected TradingException(string message, Exception? inner = null)
        : base(message, inner) { }
}

public class OrderExecutionException : TradingException
{
    public string? OrderId { get; }
    public int AttemptCount { get; }

    public OrderExecutionException(
        string message,
        string? orderId = null,
        int attemptCount = 1,
        Exception? inner = null)
        : base(message, inner)
    {
        OrderId = orderId;
        AttemptCount = attemptCount;
        ErrorCode = "ORDER_EXECUTION_FAILED";
    }
}

public class RiskLimitExceededException : TradingException
{
    public string RuleName { get; }

    public RiskLimitExceededException(string ruleName, string details)
        : base($"风控规则 '{ruleName}' 已触发: {details}")
    {
        RuleName = ruleName;
        ErrorCode = $"RISK_{ruleName.ToUpperInvariant()}";
    }
}

public class InsufficientBalanceException : TradingException
{
    public decimal RequiredBalance { get; }
    public decimal AvailableBalance { get; }

    public InsufficientBalanceException(decimal required, decimal available)
        : base($"资金不足: 需要 {required}, 可用 {available}")
    {
        RequiredBalance = required;
        AvailableBalance = available;
        ErrorCode = "INSUFFICIENT_BALANCE";
    }
}

public class ApiConnectionException : TradingException
{
    public Uri? Endpoint { get; }
    public int? HttpStatusCode { get; }

    public ApiConnectionException(
        string message,
        Uri? endpoint = null,
        int? statusCode = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Endpoint = endpoint;
        HttpStatusCode = statusCode;
        ErrorCode = $"API_ERROR_{statusCode ?? 0}";
    }
}

public enum ErrorRecoveryStrategy
{
    Retry,
    RetryWithBackoff,
    PauseStrategy,
    HaltTrading,
    Ignore
}

public record ErrorRecoveryConfig
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public double BackoffMultiplier { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
}
```

### 2.3 创建日志配置 `Core/Logging/LoggerConfiguration.cs`

```csharp
using Serilog;
using Serilog.Events;

namespace 币安量化机器人.Core.Logging;

public static class LoggerConfiguration
{
    public static ILogger CreateLogger(
        string environment,
        LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Environment", environment)
            .Enrich.WithProperty("Application", "WPE-Trading")
            .Enrich.WithThreadId();

        if (environment.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        config.WriteTo.File(
            path: Path.Combine("logs", ".txt"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30);

        return config.CreateLogger();
    }
}
```

---

## 第三步：更新应用启动代码

### 3.1 修改 `App.xaml.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Services;

namespace 币安量化机器人;

public partial class App : global::System.Windows.Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static Serilog.ILogger Logger { get; private set; } = null!;

    public App()
    {
        // 加载应用设置
        AppSettingsService.Load();

        // 配置依赖注入容器
        var services = new ServiceCollection();
        services.AddTradingServices(opts =>
        {
            opts.Environment = AppSettingsService.Current.Environment;
            opts.UseTestnet = AppSettingsService.Current.UseFuturesTestnet;
        });

        ServiceProvider = services.BuildServiceProvider();
        Logger = ServiceProvider.GetRequiredService<Serilog.ILogger>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        var main = new MainWindow();
        main.Show();

        // 启动自动化交易代理
        try
        {
            AutoTradingAgent.StartDefault(ServiceProvider);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start AutoTradingAgent");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await AutoTradingAgent.StopAsync();
            
            // 清理资源
            if (ServiceProvider is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            
            if (ServiceProvider is IDisposable disposable)
                disposable.Dispose();
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
```

### 3.2 更新 `AutoTradingAgent.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Strategies;
using Serilog;

namespace 币安量化机器人.Services;

public static class AutoTradingAgent
{
    private static CancellationTokenSource? _cts;
    private static Task? _runTask;
    private static IServiceProvider? _serviceProvider;

    public static bool IsRunning => _runTask is { IsCompleted: false };

    public static void StartDefault(IServiceProvider serviceProvider)
    {
        if (IsRunning) return;

        _serviceProvider = serviceProvider;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var strategy = CreateDefaultStrategy(serviceProvider);
        var cfg = serviceProvider.GetRequiredService<AppSettings>();
        var symbol = string.IsNullOrWhiteSpace(cfg.DefaultSymbol)
            ? "BTCUSDT"
            : cfg.DefaultSymbol.ToUpperInvariant();

        var timeframes = new[] { ParseTimeframe(cfg.DefaultTimeframe) };

        _runTask = Task.Run(async () =>
        {
            try
            {
                var orchestrator = serviceProvider.GetRequiredService<StrategyOrchestrator>();
                await orchestrator.RunAsync(strategy, symbol, timeframes, token);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                var logger = serviceProvider.GetRequiredService<ILogger>();
                logger.Error(ex, "AutoTradingAgent error");
            }
        }, token);
    }

    private static ITradingStrategy CreateDefaultStrategy(IServiceProvider provider)
    {
        var cfg = provider.GetRequiredService<AppSettings>();
        var analyzer = provider.GetRequiredService<IMultiTimeframeAnalyzer>();
        var ml = provider.GetRequiredService<IMachineLearningSignalGenerator>();
        var featureStore = provider.GetRequiredService<IFeatureStore>();

        var parameters = new StrategyParameters(new Dictionary<string, double>
        {
            ["entry_z_score"] = 1.5,
            ["base_quantity"] = (double)cfg.BaseOrderQuantity,
            ["stop_multiplier"] = 2.0
        });

        return new MeanReversionStrategy(analyzer, ml, featureStore, parameters);
    }

    public static async Task StopAsync()
    {
        if (_cts is null) return;

        try
        {
            _cts.Cancel();
            if (_runTask is not null)
                try { await _runTask; } catch { }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _runTask = null;
        }
    }

    private static TimeSpan ParseTimeframe(string tf)
    {
        if (string.IsNullOrWhiteSpace(tf))
            return TimeSpan.FromMinutes(1);

        tf = tf.Trim().ToLowerInvariant();
        if (tf.EndsWith("m") && int.TryParse(tf[..^1], out var mins))
            return TimeSpan.FromMinutes(mins);
        if (tf.EndsWith("h") && int.TryParse(tf[..^1], out var hours))
            return TimeSpan.FromHours(hours);

        return TimeSpan.FromMinutes(1);
    }
}
```

---

## 第四步：更新需要注入的组件

### 4.1 更新 `MainWindow.xaml.cs`

**变更前：**
```csharp
public partial class MainWindow : Window
{
    private readonly BinanceApiClient _api = ServiceLocator.Api;
    private readonly StrategyOrchestrator _orchestrator = ServiceLocator.StrategyOrchestrator;
}
```

**变更后：**
```csharp
public partial class MainWindow : Window
{
    private readonly BinanceApiClient _api;
    private readonly StrategyOrchestrator _orchestrator;

    public MainWindow()
    {
        InitializeComponent();
        
        var serviceProvider = ((App)Application.Current).ServiceProvider;
        _api = serviceProvider.GetRequiredService<BinanceApiClient>();
        _orchestrator = serviceProvider.GetRequiredService<StrategyOrchestrator>();
    }
}
```

### 4.2 更新其他 UserControl 或窗口

对所有使用 `ServiceLocator` 的类应用相同的改变。

---

## 第五步：验证与测试

### 5.1 编译检查

```bash
dotnet clean
dotnet build
```

应该没有编译错误。

### 5.2 运行应用

启动应用，验证：
- ? 应用正常启动
- ? 自动化交易代理启动
- ? 日志正确输出

### 5.3 逐步迁移 ServiceLocator

一旦依赖注入完全运行，可以逐步删除 `ServiceLocator.cs` 中的代码：

1. 在所有代码中替换 `ServiceLocator.Api` → 注入
2. 在所有代码中替换 `ServiceLocator.Cache` → 注入
3. 继续其他服务...
4. 最后删除 `ServiceLocator.cs` 文件

---

## 常见问题 (FAQ)

### Q: 如何在 Unit Test 中使用 DI?

```csharp
[TestInitialize]
public void Setup()
{
    var services = new ServiceCollection();
    services.AddTradingServices();
    _serviceProvider = services.BuildServiceProvider();
}

[TestMethod]
public void MyTest()
{
    var api = _serviceProvider.GetRequiredService<BinanceApiClient>();
    // 测试...
}
```

### Q: 如何在已有的 ServiceLocator 中使用新的 DI?

可以暂时创建一个适配器：

```csharp
public static class ServiceLocator
{
    private static IServiceProvider? _serviceProvider;

    public static void SetServiceProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static BinanceApiClient Api => 
        _serviceProvider?.GetRequiredService<BinanceApiClient>() 
        ?? throw new InvalidOperationException("ServiceProvider not configured");
}
```

### Q: 循环依赖怎么办?

避免循环依赖的方法：
1. 使用接口而不是具体类
2. 考虑是否真的需要双向依赖
3. 引入中间层（如 EventBus 或 Mediator 模式）

### Q: 如何处理按需创建的服务?

使用 `Func<T>` 工厂模式：

```csharp
services.AddSingleton<Func<string, IStrategyFactory>>(
    provider => (symbol) => new StrategyFactory(symbol));
```

---

## 检查清单

- [ ] 创建 ServiceConfiguration.cs
- [ ] 创建异常类文件
- [ ] 创建日志配置文件
- [ ] 更新 App.xaml.cs
- [ ] 更新 AutoTradingAgent.cs
- [ ] 更新 MainWindow.xaml.cs
- [ ] 更新所有窗口和 UserControl
- [ ] 编译通过
- [ ] 运行测试
- [ ] 删除/迁移 ServiceLocator 代码

---

**完成此步骤后，您的项目将具有：**
- ? 清晰的依赖关系
- ? 易于单元测试
- ? 支持不同配置的多环境部署
- ? 更好的代码可维护性

