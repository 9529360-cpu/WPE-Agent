# ?? 开发者快速启动指南

> 从现在到第一周完成 - 快速搭建 DI 框架和基础架构

---

## ? 5 分钟快速理解

### 当前问题
```csharp
// ? ServiceLocator 反模式
var api = ServiceLocator.Api;  // 隐藏的全局依赖
```

### 新方案
```csharp
// ? 显式 DI
public MyService(BinanceApiClient api)  // 显式依赖
{
    _api = api;  // 清晰可见
}
```

### 核心改变
```
ServiceLocator 反模式
    ↓↓↓
Microsoft.Extensions.DependencyInjection
    ↓↓↓
模块化的 6 层架构
    ↓↓↓
完整的闭环自动化交易系统
```

---

## ?? 第 1 周任务清单

### ? Task 1: 安装 NuGet 包 (5 分钟)

```bash
# 在项目目录运行
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Configuration
dotnet add package Serilog
dotnet add package Serilog.Sinks.Console
dotnet add package Serilog.Sinks.File
```

**验证**: 编译成功，无错误 ?

---

### ? Task 2: 创建核心接口 (2 小时)

创建以下文件 (复制后面的代码):

```
Core/
├── Data/
│   └── IDataCollectionService.cs      ← 新建
├── Strategy/
│   ├── IStrategyEvaluationService.cs  ← 新建
│   └── IStrategyFilterService.cs      ← 新建
├── Execution/
│   └── IOrderExecutor.cs              ← 新建
├── Risk/
│   ├── IPositionManager.cs            ← 新建
│   └── ILeverageController.cs         ← 新建
└── Logging/
    └── ITradingRecorder.cs            ← 新建
```

**代码内容**: [见下面的 "核心接口代码" 部分]

**验证**: 所有接口编译成功 ?

---

### ? Task 3: 更新 App.xaml.cs (1 小时)

**文件**: `App.xaml.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace 币安量化机器人;

public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static ILogger Logger { get; private set; } = null!;

    public App()
    {
        // 加载应用设置
        AppSettingsService.Load();

        // 初始化依赖注入容器
        var services = new ServiceCollection();
        
        // 添加交易服务
        // (稍后会创建 ServiceConfiguration)
        
        ServiceProvider = services.BuildServiceProvider();
        Logger = ServiceProvider.GetRequiredService<ILogger>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        var main = new MainWindow();
        main.Show();

        Logger.Information("应用程序启动成功");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
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

**验证**: App 能正常启动 ?

---

### ? Task 4: 创建 ServiceConfiguration (1.5 小时)

**文件**: `Services/ServiceConfiguration.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace 币安量化机器人.Services;

/// <summary>
/// 统一的依赖注入配置
/// </summary>
public static class ServiceConfiguration
{
    public static IServiceCollection AddTradingServices(
        this IServiceCollection services,
        Action<TradingServiceOptions>? configure = null)
    {
        var options = new TradingServiceOptions();
        configure?.Invoke(options);

        // ===== 应用设置 =====
        services.AddSingleton<AppSettings>();

        // ===== 基础设施 =====
        services.AddSingleton<BinanceApiClient>();
        services.AddSingleton<BinanceStreamClient>();

        // ===== 数据层 =====
        // TODO: 实现 BinanceDataCollectionService
        // services.AddSingleton<IDataCollectionService, BinanceDataCollectionService>();

        // ===== 策略层 =====
        // TODO: 实现 StrategyEvaluationService
        // services.AddSingleton<IStrategyEvaluationService, StrategyEvaluationService>();
        // services.AddSingleton<IStrategyFilterService, StrategyFilterService>();

        // ===== 风险层 =====
        // TODO: 实现 PositionManager
        // services.AddSingleton<IPositionManager, PositionManager>();
        // services.AddSingleton<ILeverageController, LeverageController>();

        // ===== 执行层 =====
        // TODO: 实现 RobustOrderExecutor
        // services.AddSingleton<IOrderExecutor, RobustOrderExecutor>();
        // services.AddSingleton<IErrorRecoveryHandler, ErrorRecoveryHandler>();

        // ===== 记录层 =====
        // TODO: 实现 SqliteTradingRecorder
        // services.AddSingleton<ITradingRecorder, SqliteTradingRecorder>();

        // ===== 核心编排 =====
        // TODO: 实现 AutomatedTradingEngine
        // services.AddSingleton<IAutomatedTradingEngine, AutomatedTradingEngine>();

        // ===== 日志 =====
        services.AddSingleton<ILogger>(sp =>
            LoggerConfiguration.CreateLogger(options.Environment));

        return services;
    }
}

public class TradingServiceOptions
{
    public string Environment { get; set; } = "Development";
    public string DataDirectory { get; set; } = "Data";
    public bool UseTestnet { get; set; } = false;
}

/// <summary>
/// Serilog 配置
/// </summary>
public static class LoggerConfiguration
{
    public static ILogger CreateLogger(string environment)
    {
        var config = new global::Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Environment", environment);

        if (environment == "Development")
        {
            config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message}{NewLine}{Exception}");
        }

        config.WriteTo.File(
            path: "logs/trading-.txt",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30);

        return config.CreateLogger();
    }
}
```

**验证**: 编译成功，ServiceConfiguration 可注册 ?

---

### ? Task 5: 更新 App.xaml.cs 引用 ServiceConfiguration (30 分钟)

修改 `App.xaml.cs` 中的初始化代码:

```csharp
public App()
{
    AppSettingsService.Load();

    var services = new ServiceCollection();
    services.AddTradingServices(opts =>
    {
        opts.Environment = "Development";
        opts.UseTestnet = true;
    });

    ServiceProvider = services.BuildServiceProvider();
    Logger = ServiceProvider.GetRequiredService<ILogger>();
    
    Logger.Information("DI 容器初始化成功");
}
```

**验证**: App 启动时日志出现 "DI 容器初始化成功" ?

---

## ?? 核心接口代码 (复制粘贴)

### 1. IDataCollectionService.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Data;

public interface IDataCollectionService
{
    /// <summary>
    /// 实时采集市场数据
    /// </summary>
    IAsyncEnumerable<MarketSnapshot> CollectMarketDataAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 采集账户快照
    /// </summary>
    Task<AccountSnapshot> CollectAccountDataAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取历史K线
    /// </summary>
    Task<IReadOnlyList<Kline>> GetHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default);
}

// 数据模型
public record MarketSnapshot(
    string Symbol,
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public record AccountSnapshot(
    DateTime Timestamp,
    decimal TotalEquity,
    decimal AvailableBalance,
    IReadOnlyList<PositionInfo> Positions);

public record PositionInfo(
    string Symbol,
    decimal Quantity,
    decimal CurrentPrice,
    decimal UnrealizedPnl,
    decimal BreakEvenPrice);

public record Kline(
    DateTime OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    DateTime CloseTime);
```

### 2. IStrategyEvaluationService.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Strategy;

public interface IStrategyEvaluationService
{
    Task<StrategyEvaluationResult> EvaluateAsync(
        ITradingStrategy strategy,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrategyEvaluationResult>> EvaluateMultipleAsync(
        IEnumerable<ITradingStrategy> strategies,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default);
}

public record StrategyEvaluationResult(
    string StrategyName,
    string StrategyId,
    decimal NetProfit,
    decimal ProfitRatio,
    double SharpeRatio,
    RiskLevel RiskLevel,
    int TradeCount,
    DateTime EvaluatedAt);

public enum RiskLevel { Low, Medium, High }
```

### 3. IStrategyFilterService.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Strategy;

public interface IStrategyFilterService
{
    IReadOnlyList<StrategyEvaluationResult> FilterByThreshold(
        IEnumerable<StrategyEvaluationResult> results,
        decimal profitThreshold);

    IReadOnlyList<StrategyEvaluationResult> RankByPriority(
        IEnumerable<StrategyEvaluationResult> results);

    Task DeactivateAsync(
        ITradingStrategy strategy,
        string reason,
        CancellationToken cancellationToken = default);
}
```

### 4. IOrderExecutor.cs

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Execution;

public interface IOrderExecutor
{
    Task<OrderResult> ExecuteMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken cancellationToken = default);

    Task<OrderResult> ExecuteLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        CancellationToken cancellationToken = default);

    Task<OrderResult> ExecuteStopLossAsync(
        string symbol,
        decimal quantity,
        decimal stopPrice,
        CancellationToken cancellationToken = default);

    Task<OrderResult> ClosePositionAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}

public record OrderResult(
    string OrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal ExecutedPrice,
    OrderStatus Status,
    DateTime ExecutedAt);

public enum OrderSide { Buy, Sell }
public enum OrderStatus { Pending, PartiallyFilled, Filled, Cancelled }
```

### 5. IPositionManager.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Risk;

public interface IPositionManager
{
    decimal CalculatePositionSize(
        StrategyEvaluationResult evaluation,
        AccountSnapshot account,
        decimal maxRiskPercentage = 0.02m);

    bool CanOpenPosition(
        AccountSnapshot account,
        decimal requiredMargin);

    decimal GetLeverageRiskFactor(RiskLevel riskLevel);
}
```

### 6. ILeverageController.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Risk;

public interface ILeverageController
{
    Task<decimal> CalculateLeverageCostAsync(
        string symbol,
        decimal borrowAmount,
        decimal daysHeld,
        CancellationToken cancellationToken = default);

    decimal GetRecommendedLeverage(
        StrategyEvaluationResult evaluation);

    bool IsLeverageWithinLimit(
        AccountSnapshot account,
        decimal proposedLeverage);
}
```

### 7. ITradingRecorder.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Core.Persistence;

public interface ITradingRecorder
{
    Task RecordStrategyEvaluationAsync(
        StrategyEvaluationResult evaluation,
        CancellationToken cancellationToken = default);

    Task RecordExecutionAsync(
        OrderResult orderResult,
        CancellationToken cancellationToken = default);

    Task RecordCycleAsync(
        TradingCycleLog log,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TradingCycleLog>> QueryHistoryAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);
}

public record TradingCycleLog(
    int CycleNumber,
    DateTime StartTime,
    DateTime EndTime,
    int StrategiesEvaluated,
    int StrategiesExecuted,
    decimal TotalProfit,
    string? Notes = null);
```

### 8. IAutomatedTradingEngine.cs

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Application;

public interface IAutomatedTradingEngine
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    TradingEngineStatus GetStatus();
}

public enum TradingEngineStatus
{
    Idle,
    Running,
    Paused,
    Stopped
}
```

---

## ? 完成检查清单

### 第 1 周完成标志

- [ ] NuGet 包安装成功
- [ ] 8 个核心接口文件创建完成
- [ ] ServiceConfiguration 类创建完成
- [ ] App.xaml.cs 更新完成
- [ ] 项目编译通过 (0 错误)
- [ ] App 可以启动 (能在日志看到初始化消息)
- [ ] DI 容器可以正常解析服务

### 验证命令

```bash
# 清理编译
dotnet clean

# 构建
dotnet build

# 如果编译成功，您就完成了第 1 周的任务！
```

---

## ?? 第 2 周预览

如果第 1 周成功完成，第 2 周的任务是：

1. 实现 `BinanceDataCollectionService`
2. 实现基础的 `StrategyEvaluationService`
3. 编写单元测试
4. 验证数据采集功能

---

## ?? 常见问题

### Q1: 编译错误 "找不到命名空间"
**A**: 确保 `using` 语句正确，例如：
```csharp
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Strategy;
```

### Q2: App 启动时崩溃
**A**: 检查 `App.xaml.cs` 中是否正确初始化了 ServiceProvider

### Q3: 哪些文件可以先跳过？
**A**: 暂时不需要实现，只需要接口定义：
- `BinanceDataCollectionService` (实现)
- `StrategyEvaluationService` (实现)
- 其他实现类

接口本身必须完成。

---

## ?? 需要帮助？

参考文档：
- **快速参考**: `Docs/Quick_Reference_Architecture.md`
- **详细设计**: `Docs/Closed_Loop_Architecture_Design.md`
- **完整路线图**: `Docs/Closed_Loop_Implementation_Roadmap.md`

---

**下一步**: 
完成本周任务后，开始 [第 2 周的实现](./Closed_Loop_Implementation_Roadmap.md#第-3-4-周-数据层实现)！

---

?? **预计时间**: 5-6 小时  
? **难度**: ?? (中等)  
?? **进度**: Week 1 / 8

