# 闭环架构实施指南 - 分步骤执行计划

> 从现有的 ServiceLocator 架构迁移到完整的模块化闭环系统

**目标**: 在 8 周内实现完整的闭环自动化交易系统

---

## ?? 整体时间规划

```
第 1-2 周: 基础设施和依赖注入改造
第 3-4 周: 数据层和策略层实现
第 5-6 周: 执行层和风险层实现
第 7 周:   编排层（闭环引擎）实现
第 8 周:   测试、优化和上线
```

---

## ?? 第 1-2 周: 基础设施改造

### 任务 1.1: 迁移到 Microsoft.Extensions.DependencyInjection

**文件**: `Services/ServiceConfiguration.cs` (新建)

```csharp
using Microsoft.Extensions.DependencyInjection;
using 币安量化机器人.Application.Services;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Infrastructure.Data;
using 币安量化机器人.Infrastructure.Persistence;

namespace 币安量化机器人.Services;

/// <summary>
/// 完整的依赖注入配置 - 支持闭环自动化交易
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

        // ===== 基础设施服务 =====
        services.AddSingleton<BinanceApiClient>();
        services.AddSingleton<BinanceStreamClient>();

        // ===== 数据层 =====
        services.AddSingleton<IDataCollectionService, BinanceDataCollectionService>();
        services.AddSingleton<DataCacheService>();

        // ===== 策略层 =====
        services.AddSingleton<IStrategyEvaluationService, StrategyEvaluationService>();
        services.AddSingleton<IStrategyFilterService, StrategyFilterService>();
        services.AddSingleton<IBacktestEngine, DefaultBacktestEngine>();
        services.AddSingleton<IStrategyRepository, InMemoryStrategyRepository>();

        // ===== 风险层 =====
        services.AddSingleton<IRiskManager, ThreadSafeRiskManager>();
        services.AddSingleton<IPositionManager, PositionManager>();
        services.AddSingleton<ILeverageController, LeverageController>();

        // ===== 执行层 =====
        services.AddSingleton<IOrderExecutor, RobustOrderExecutor>();
        services.AddSingleton<IErrorRecoveryHandler>(sp =>
            new ErrorRecoveryHandler(
                sp.GetRequiredService<ILogger>(),
                new ErrorRecoveryConfig { MaxRetries = 3 }));

        // ===== 持久化层 =====
        services.AddSingleton<ITradingRecorder>(sp =>
            new SqliteTradingRecorder(
                $"Data Source={options.DataDirectory}/trading.db",
                sp.GetRequiredService<ILogger>()));

        // ===== 核心编排层 (最重要!) =====
        services.AddSingleton<IAutomatedTradingEngine, AutomatedTradingEngine>();

        // ===== 日志系统 =====
        services.AddSingleton<ILogger>(sp =>
            Core.Logging.LoggerConfiguration.CreateLogger(options.Environment));

        return services;
    }
}

public class TradingServiceOptions
{
    public string Environment { get; set; } = "Production";
    public string DataDirectory { get; set; } = "Data";
    public bool UseTestnet { get; set; } = false;
}
```

**验收标准:**
- [ ] 所有服务注册到容器
- [ ] 编译无错误
- [ ] 依赖注入可以正常解析

---

### 任务 1.2: 更新 App.xaml.cs

**文件**: `App.xaml.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Services;

namespace 币安量化机器人;

public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static ILogger Logger { get; private set; } = null!;

    public App()
    {
        // 加载配置
        AppSettingsService.Load();

        // 初始化 DI 容器
        var services = new ServiceCollection();
        services.AddTradingServices(opts =>
        {
            opts.Environment = AppSettingsService.Current.Environment;
            opts.UseTestnet = AppSettingsService.Current.UseFuturesTestnet;
        });

        ServiceProvider = services.BuildServiceProvider();
        Logger = ServiceProvider.GetRequiredService<ILogger>();

        _logger.Information("应用程序已初始化");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var main = new MainWindow();
        main.Show();

        // 启动自动化交易引擎
        try
        {
            var engine = ServiceProvider.GetRequiredService<IAutomatedTradingEngine>();
            // 稍后在主窗口启动按钮中触发
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "初始化自动化交易引擎失败");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var engine = ServiceProvider.GetRequiredService<IAutomatedTradingEngine>();
            await engine.StopAsync();

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

**验收标准:**
- [ ] App 启动无异常
- [ ] DI 容器正常工作
- [ ] 日志系统初始化成功

---

## ?? 第 3-4 周: 数据层实现

### 任务 3.1: 实现数据采集服务

**文件**: `Infrastructure/Data/IDataCollectionService.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Infrastructure.Data;

/// <summary>
/// 数据采集服务 - 统一管理所有数据源
/// </summary>
public interface IDataCollectionService
{
    /// <summary>
    /// 实时推送市场行情
    /// </summary>
    IAsyncEnumerable<MarketSnapshot> CollectMarketDataAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 采集账户快照（余额、仓位）
    /// </summary>
    Task<AccountSnapshot> CollectAccountDataAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取历史K线（用于回测）
    /// </summary>
    Task<IReadOnlyList<Kline>> GetHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default);
}

// === 数据模型 ===
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

**文件**: `Infrastructure/Data/BinanceDataCollectionService.cs`

```csharp
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Infrastructure.Data;

/// <summary>
/// 币安数据采集实现 - 支持 WebSocket 和 REST API
/// </summary>
public class BinanceDataCollectionService : IDataCollectionService
{
    private readonly BinanceApiClient _apiClient;
    private readonly BinanceStreamClient _streamClient;
    private readonly ILogger _logger;

    public BinanceDataCollectionService(
        BinanceApiClient apiClient,
        BinanceStreamClient streamClient,
        ILogger logger)
    {
        _apiClient = apiClient;
        _streamClient = streamClient;
        _logger = logger.ForContext<BinanceDataCollectionService>();
    }

    public async IAsyncEnumerable<MarketSnapshot> CollectMarketDataAsync(
        IEnumerable<string> symbols,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var symbolList = symbols.ToList();
        _logger.Information("开始采集市场数据: {Symbols}", string.Join(", ", symbolList));

        // 使用 WebSocket 实时推送
        await foreach (var marketData in _streamClient.SubscribeKlinesAsync(symbolList, cancellationToken))
        {
            yield return new MarketSnapshot(
                marketData.Symbol,
                marketData.Timestamp,
                marketData.Open,
                marketData.High,
                marketData.Low,
                marketData.Close,
                marketData.Volume);
        }
    }

    public async Task<AccountSnapshot> CollectAccountDataAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.Information("采集账户数据...");

        try
        {
            var balances = await _apiClient.GetAccountBalancesAsync(cancellationToken);
            var positions = await _apiClient.GetPositionsAsync(cancellationToken);

            var totalEquity = CalculateTotalEquity(balances, positions);
            var availableBalance = GetAvailableUsdt(balances);

            return new AccountSnapshot(
                DateTime.UtcNow,
                totalEquity,
                availableBalance,
                positions.Select(p => new PositionInfo(
                    p.Symbol,
                    p.Quantity,
                    p.CurrentPrice,
                    p.UnrealizedProfit,
                    p.BreakEvenPrice)).ToList());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "采集账户数据失败");
            throw;
        }
    }

    public async Task<IReadOnlyList<Kline>> GetHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        _logger.Information(
            "获取历史K线: {Symbol} {Interval} {Start} - {End}",
            symbol, interval, startTime, endTime);

        var klines = new List<Kline>();
        var current = startTime;

        // 分批获取（每次最多 1000 条）
        while (current < endTime)
        {
            var batch = await _apiClient.GetKlinesAsync(
                symbol,
                interval,
                current,
                Math.Min(current.AddDays(1), endTime),
                1000,
                cancellationToken);

            klines.AddRange(batch.Select(k => new Kline(
                k.OpenTime,
                k.Open,
                k.High,
                k.Low,
                k.Close,
                k.Volume,
                k.CloseTime)));

            current = current.AddDays(1);
        }

        _logger.Information("获取 {Count} 条 K 线数据", klines.Count);
        return klines;
    }

    private decimal CalculateTotalEquity(
        IReadOnlyList<AccountBalance> balances,
        IReadOnlyList<PositionData> positions)
    {
        var walletBalance = balances.Sum(b => b.WalletBalance);
        var unrealizedPnl = positions.Sum(p => p.UnrealizedProfit);
        return walletBalance + unrealizedPnl;
    }

    private decimal GetAvailableUsdt(IReadOnlyList<AccountBalance> balances)
    {
        return balances
            .FirstOrDefault(b => b.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase))?
            .AvailableBalance ?? 0;
    }
}
```

**验收标准:**
- [ ] WebSocket 连接正常
- [ ] 能正常获取市场数据
- [ ] 能正常获取账户信息
- [ ] 历史数据获取成功

---

### 任务 3.2: 实现策略评估服务

**文件**: `Application/Services/StrategyEvaluationService.cs`

```csharp
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Application.Services;

public class StrategyEvaluationService : IStrategyEvaluationService
{
    private readonly IBacktestEngine _backtestEngine;
    private readonly IPositionManager _positionManager;
    private readonly ILeverageController _leverageController;
    private readonly ILogger _logger;

    public StrategyEvaluationService(
        IBacktestEngine backtestEngine,
        IPositionManager positionManager,
        ILeverageController leverageController,
        ILogger logger)
    {
        _backtestEngine = backtestEngine;
        _positionManager = positionManager;
        _leverageController = leverageController;
        _logger = logger.ForContext<StrategyEvaluationService>();
    }

    public async Task<StrategyEvaluationResult> EvaluateAsync(
        ITradingStrategy strategy,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("开始评估策略: {StrategyName}", strategy.Name);

        try
        {
            // 回测策略
            var backtestResult = await _backtestEngine.RunAsync(
                new BacktestRequest
                {
                    Strategy = strategy,
                    Symbol = "BTCUSDT",
                    StartTime = marketSnapshots.First().Timestamp,
                    EndTime = marketSnapshots.Last().Timestamp
                },
                cancellationToken);

            // 计算成本
            var transactionCosts = CalculateTransactionCosts(backtestResult);
            var leverageCosts = await CalculateLeverageCostsAsync(
                strategy, backtestResult, cancellationToken);

            var netProfit = backtestResult.NetProfit - transactionCosts - leverageCosts;
            var profitRatio = accountSnapshot.TotalEquity > 0
                ? netProfit / accountSnapshot.TotalEquity
                : 0;

            var result = new StrategyEvaluationResult(
                strategy.Name,
                strategy.Id,
                netProfit,
                profitRatio,
                backtestResult.SharpeRatio,
                GetRiskLevel(strategy.MaxLeverage),
                backtestResult.TradeCount,
                DateTime.UtcNow);

            _logger.Information(
                "策略评估完成 {StrategyName}: 净利={NetProfit}, 收益率={ProfitRatio:P}, Sharpe={Sharpe}",
                strategy.Name, netProfit, profitRatio, backtestResult.SharpeRatio);

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "策略评估失败: {StrategyName}", strategy.Name);
            throw;
        }
    }

    public async Task<IReadOnlyList<StrategyEvaluationResult>> EvaluateMultipleAsync(
        IEnumerable<ITradingStrategy> strategies,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("批量评估 {Count} 个策略", strategies.Count());

        // 并行评估所有策略
        var tasks = strategies.Select(s =>
            EvaluateAsync(s, marketSnapshots, accountSnapshot, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private decimal CalculateTransactionCosts(BacktestResult result)
    {
        // 手续费 + 滑点
        const decimal brokerageFee = 0.0005m;    // 0.05%
        const decimal slippage = 0.001m;         // 0.1%
        var trades = result.Signals.Count(s =>
            s.Action.ActionType != TradeActionType.Hold);

        return result.TotalValue * trades * (brokerageFee + slippage);
    }

    private async Task<decimal> CalculateLeverageCostsAsync(
        ITradingStrategy strategy,
        BacktestResult result,
        CancellationToken cancellationToken)
    {
        if (strategy.MaxLeverage <= 1)
            return 0;

        var dailyRate = 0.0001m; // 0.01% 日利率
        var daysHeld = result.EndTime.Subtract(result.StartTime).TotalDays;

        return result.TotalValue * (strategy.MaxLeverage - 1) * dailyRate * (decimal)daysHeld;
    }

    private RiskLevel GetRiskLevel(decimal leverage)
    {
        return leverage switch
        {
            <= 1 => RiskLevel.Low,
            <= 5 => RiskLevel.Medium,
            _ => RiskLevel.High
        };
    }
}

// === 数据模型 ===
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

**验收标准:**
- [ ] 能正常评估单个策略
- [ ] 能并行评估多个策略
- [ ] 计算结果正确（考虑手续费和滑点）

---

## ?? 第 5-6 周: 执行层和风险层

### 任务 5.1: 实现订单执行服务

**文件**: `Application/Services/RobustOrderExecutor.cs`

```csharp
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Application.Services;

/// <summary>
/// 可靠的订单执行器 - 支持重试和异常恢复
/// </summary>
public class RobustOrderExecutor : IOrderExecutor
{
    private readonly BinanceApiClient _apiClient;
    private readonly IErrorRecoveryHandler _errorHandler;
    private readonly ILogger _logger;

    public RobustOrderExecutor(
        BinanceApiClient apiClient,
        IErrorRecoveryHandler errorHandler,
        ILogger logger)
    {
        _apiClient = apiClient;
        _errorHandler = errorHandler;
        _logger = logger.ForContext<RobustOrderExecutor>();
    }

    public async Task<OrderResult> ExecuteMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        return await _errorHandler.ExecuteWithRetryAsync(
            async ct =>
            {
                _logger.Information("执行市价单: {Symbol} {Side} {Quantity}", symbol, side, quantity);

                var result = await _apiClient.PlaceMarketOrderAsync(
                    symbol, side, quantity, ct);

                return new OrderResult(
                    result.OrderId,
                    symbol,
                    side,
                    quantity,
                    result.ExecutedPrice,
                    OrderStatus.Filled,
                    DateTime.UtcNow);
            },
            $"MarketOrder_{symbol}_{side}",
            cancellationToken);
    }

    public async Task<OrderResult> ExecuteLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        CancellationToken cancellationToken = default)
    {
        return await _errorHandler.ExecuteWithRetryAsync(
            async ct =>
            {
                _logger.Information(
                    "执行限价单: {Symbol} {Side} {Quantity} @ {Price}",
                    symbol, side, quantity, price);

                var result = await _apiClient.PlaceLimitOrderAsync(
                    symbol, side, quantity, price, ct);

                return new OrderResult(
                    result.OrderId,
                    symbol,
                    side,
                    quantity,
                    price,
                    OrderStatus.Pending,
                    DateTime.UtcNow);
            },
            $"LimitOrder_{symbol}_{price}",
            cancellationToken);
    }

    public async Task<OrderResult> ExecuteStopLossAsync(
        string symbol,
        decimal quantity,
        decimal stopPrice,
        CancellationToken cancellationToken = default)
    {
        return await _errorHandler.ExecuteWithRetryAsync(
            async ct =>
            {
                _logger.Information(
                    "执行止损单: {Symbol} {Quantity} @ {StopPrice}",
                    symbol, quantity, stopPrice);

                var result = await _apiClient.PlaceStopLossAsync(
                    symbol, quantity, stopPrice, ct);

                return new OrderResult(
                    result.OrderId,
                    symbol,
                    OrderSide.Sell,
                    quantity,
                    stopPrice,
                    OrderStatus.Pending,
                    DateTime.UtcNow);
            },
            $"StopLoss_{symbol}",
            cancellationToken);
    }

    public async Task<OrderResult> ClosePositionAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var positions = await _apiClient.GetPositionsAsync(cancellationToken);
        var position = positions.FirstOrDefault(p =>
            p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (position?.Quantity == 0)
            return null!;

        var side = position.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
        return await ExecuteMarketOrderAsync(symbol, side, Math.Abs(position.Quantity), cancellationToken);
    }
}

// === 数据模型 ===
public record OrderResult(
    string OrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal ExecutedPrice,
    OrderStatus Status,
    DateTime ExecutedAt);

public enum OrderStatus { Pending, PartiallyFilled, Filled, Cancelled }
public enum OrderSide { Buy, Sell }
```

**验收标准:**
- [ ] 市价单执行成功
- [ ] 限价单执行成功
- [ ] 止损单执行成功
- [ ] 异常重试机制工作

---

## ?? 第 7 周: 闭环编排引擎实现

### 任务 7.1: 实现自动化交易引擎（核心）

**文件**: `Application/ClosedLoopOrchestration/AutomatedTradingEngine.cs`

```csharp
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Application.ClosedLoopOrchestration;

/// <summary>
/// 自动化交易引擎 - 实现完整的闭环交易流程
/// 
/// 流程:
/// 1. 采集数据 → 2. 评估策略 → 3. 筛选排序 → 4. 实盘执行 → 5. 记录日志 → 6. 等待循环
/// </summary>
public class AutomatedTradingEngine : IAutomatedTradingEngine
{
    private readonly IDataCollectionService _dataCollection;
    private readonly IStrategyEvaluationService _strategyEvaluation;
    private readonly IStrategyFilterService _strategyFilter;
    private readonly IOrderExecutor _orderExecutor;
    private readonly ITradingRecorder _recorder;
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _cycleNumber = 0;
    private TradingEngineStatus _status = TradingEngineStatus.Idle;

    public AutomatedTradingEngine(
        IDataCollectionService dataCollection,
        IStrategyEvaluationService strategyEvaluation,
        IStrategyFilterService strategyFilter,
        IOrderExecutor orderExecutor,
        ITradingRecorder recorder,
        ILogger logger,
        AppSettings settings)
    {
        _dataCollection = dataCollection;
        _strategyEvaluation = strategyEvaluation;
        _strategyFilter = strategyFilter;
        _orderExecutor = orderExecutor;
        _recorder = recorder;
        _logger = logger.ForContext<AutomatedTradingEngine>();
        _settings = settings;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_status == TradingEngineStatus.Running)
        {
            _logger.Warning("交易引擎已在运行");
            return;
        }

        _logger.Information("========== 启动自动化交易引擎 ==========");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _status = TradingEngineStatus.Running;

        _loopTask = Task.Run(() => RunTradingLoopAsync(_cts.Token), _cts.Token);
        await _loopTask;
    }

    public async Task StopAsync()
    {
        if (_status != TradingEngineStatus.Running)
            return;

        _logger.Information("========== 停止自动化交易引擎 ==========");
        _cts?.Cancel();

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }

        _status = TradingEngineStatus.Stopped;
    }

    public TradingEngineStatus GetStatus() => _status;

    /// <summary>
    /// 完整的闭环交易循环实现
    /// </summary>
    private async Task RunTradingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _cycleNumber++;
                var cycleStartTime = DateTime.UtcNow;

                _logger.Information("========== 【交易周期 #{CycleNumber}】开始 ==========", _cycleNumber);

                // ========== 步骤 1: 采集数据 ==========
                _logger.Information("【步骤 1】采集市场数据和账户信息...");
                var marketSnapshots = await CollectMarketDataAsync(cancellationToken);
                var accountSnapshot = await _dataCollection.CollectAccountDataAsync(cancellationToken);

                _logger.Information(
                    "账户资金: 总权益={TotalEquity:F2}, 可用={Available:F2}",
                    accountSnapshot.TotalEquity,
                    accountSnapshot.AvailableBalance);

                // ========== 步骤 2: 评估策略 ==========
                _logger.Information("【步骤 2】评估所有策略...");
                var strategies = await LoadActiveStrategiesAsync(cancellationToken);
                var evaluationResults = await _strategyEvaluation.EvaluateMultipleAsync(
                    strategies,
                    marketSnapshots,
                    accountSnapshot,
                    cancellationToken);

                _logger.Information("策略评估完成: 共 {Count} 个策略", evaluationResults.Count);
                foreach (var result in evaluationResults.Take(5))
                {
                    _logger.Information(
                        "  {StrategyName}: 收益率={ProfitRatio:P2}, Sharpe={Sharpe:F2}",
                        result.StrategyName,
                        result.ProfitRatio,
                        result.SharpeRatio);
                }

                // ========== 步骤 3: 筛选和排序 ==========
                _logger.Information("【步骤 3】筛选和排序策略...");
                var profitThreshold = decimal.Parse(_settings.MinProfitThreshold ?? "0.02");
                var filteredResults = _strategyFilter.FilterByThreshold(evaluationResults, profitThreshold);
                var rankedResults = _strategyFilter.RankByPriority(filteredResults);

                _logger.Information(
                    "筛选结果: {Count} 个策略达到收益阈值 {Threshold:P}",
                    rankedResults.Count,
                    profitThreshold);

                // ========== 步骤 4: 实盘执行 ==========
                _logger.Information("【步骤 4】执行交易信号...");
                var executedCount = 0;
                var totalProfit = 0m;
                var maxStrategies = int.Parse(_settings.MaxConcurrentStrategies ?? "3");

                foreach (var result in rankedResults.Take(maxStrategies))
                {
                    try
                    {
                        var executed = await ExecuteStrategyAsync(
                            result,
                            accountSnapshot,
                            cancellationToken);

                        if (executed)
                        {
                            executedCount++;
                            totalProfit += result.NetProfit;

                            _logger.Information("? 策略 {StrategyName} 执行成功", result.StrategyName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "执行策略 {StrategyName} 失败", result.StrategyName);
                        // 继续执行其他策略
                    }
                }

                _logger.Information(
                    "实盘执行完成: 执行 {Count} 个策略，总收益 {Profit:F2}",
                    executedCount,
                    totalProfit);

                // ========== 步骤 5: 记录日志 ==========
                _logger.Information("【步骤 5】记录交易数据...");
                var cycleEndTime = DateTime.UtcNow;

                // 记录所有评估结果
                foreach (var result in evaluationResults)
                {
                    await _recorder.RecordStrategyEvaluationAsync(result, cancellationToken);
                }

                // 记录周期总结
                var cycleLog = new TradingCycleLog(
                    _cycleNumber,
                    cycleStartTime,
                    cycleEndTime,
                    evaluationResults.Count,
                    executedCount,
                    totalProfit,
                    $"评估{evaluationResults.Count}个策略，执行{executedCount}个，利润{totalProfit:F2}");

                await _recorder.RecordCycleAsync(cycleLog, cancellationToken);

                _logger.Information(
                    "========== 【交易周期 #{CycleNumber}】完成（耗时 {Duration}ms） ==========",
                    _cycleNumber,
                    (cycleEndTime - cycleStartTime).TotalMilliseconds);

                // ========== 步骤 6: 等待下一个周期 ==========
                var cycleIntervalSeconds = int.Parse(_settings.CycleIntervalSeconds ?? "300");
                _logger.Information("等待 {Seconds} 秒后进行下一个周期...", cycleIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(cycleIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.Information("交易循环已取消");
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "交易循环发生异常，将在 30 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        _logger.Information("========== 交易引擎已停止 ==========");
    }

    private async Task<MarketSnapshot[]> CollectMarketDataAsync(CancellationToken cancellationToken)
    {
        var symbols = (_settings.TradingSymbols ?? "BTCUSDT").Split(',').Select(s => s.Trim());
        var snapshots = new List<MarketSnapshot>();

        // 采集最新的 100 条数据点（用于评估）
        var count = 0;
        await foreach (var snapshot in _dataCollection.CollectMarketDataAsync(symbols, cancellationToken))
        {
            snapshots.Add(snapshot);
            if (++count >= 100) break;
        }

        return snapshots.ToArray();
    }

    private async Task<IReadOnlyList<ITradingStrategy>> LoadActiveStrategiesAsync(
        CancellationToken cancellationToken)
    {
        // 从策略存储库加载所有活跃策略
        // 这里简化为示例，实际应从数据库加载
        return new List<ITradingStrategy>
        {
            // new MeanReversionStrategy(...),
            // new MomentumStrategy(...),
            // ...
        };
    }

    private async Task<bool> ExecuteStrategyAsync(
        StrategyEvaluationResult evaluation,
        AccountSnapshot account,
        CancellationToken cancellationToken)
    {
        // 根据评估结果执行策略
        // 包括: 开仓、平仓、止损等

        // 这是一个简化示例
        _logger.Information("执行策略: {Strategy}", evaluation.StrategyName);

        // 例: 如果收益率 > 5%，执行买入信号
        if (evaluation.ProfitRatio > 0.05m)
        {
            var orderResult = await _orderExecutor.ExecuteMarketOrderAsync(
                "BTCUSDT",
                OrderSide.Buy,
                0.01m,
                cancellationToken);

            await _recorder.RecordExecutionAsync(orderResult, cancellationToken);
            return true;
        }

        return false;
    }
}

// === 数据模型 ===
public record TradingCycleLog(
    int CycleNumber,
    DateTime StartTime,
    DateTime EndTime,
    int StrategiesEvaluated,
    int StrategiesExecuted,
    decimal TotalProfit,
    string? Notes = null);

public enum TradingEngineStatus
{
    Idle,       // 闲置
    Running,    // 运行中
    Paused,     // 暂停
    Stopped     // 已停止
}
```

**验收标准:**
- [ ] 引擎能启动和停止
- [ ] 完整的 6 步闭环流程执行
- [ ] 日志记录详细和完整
- [ ] 能正常处理异常和恢复

---

## ?? 第 8 周: 测试与优化

### 任务 8.1: 编写单元测试

**文件**: `Tests/ClosedLoopOrchestration/AutomatedTradingEngineTests.cs`

```csharp
using Moq;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Tests.ClosedLoopOrchestration;

public class AutomatedTradingEngineTests
{
    private readonly Mock<IDataCollectionService> _mockDataCollection;
    private readonly Mock<IStrategyEvaluationService> _mockStrategyEvaluation;
    private readonly Mock<IStrategyFilterService> _mockStrategyFilter;
    private readonly Mock<IOrderExecutor> _mockOrderExecutor;
    private readonly Mock<ITradingRecorder> _mockRecorder;
    private readonly Mock<ILogger> _mockLogger;
    private readonly AppSettings _settings;
    private readonly AutomatedTradingEngine _engine;

    public AutomatedTradingEngineTests()
    {
        _mockDataCollection = new Mock<IDataCollectionService>();
        _mockStrategyEvaluation = new Mock<IStrategyEvaluationService>();
        _mockStrategyFilter = new Mock<IStrategyFilterService>();
        _mockOrderExecutor = new Mock<IOrderExecutor>();
        _mockRecorder = new Mock<ITradingRecorder>();
        _mockLogger = new Mock<ILogger>();
        _settings = new AppSettings { CycleIntervalSeconds = "1" };

        _engine = new AutomatedTradingEngine(
            _mockDataCollection.Object,
            _mockStrategyEvaluation.Object,
            _mockStrategyFilter.Object,
            _mockOrderExecutor.Object,
            _mockRecorder.Object,
            _mockLogger.Object,
            _settings);
    }

    [Fact]
    public async Task StartAsync_ShouldChangeStatusToRunning()
    {
        // Act
        var startTask = _engine.StartAsync();
        await Task.Delay(100);

        // Assert
        Assert.Equal(TradingEngineStatus.Running, _engine.GetStatus());

        // Cleanup
        await _engine.StopAsync();
    }

    [Fact]
    public async Task RunningCycle_ShouldExecuteSixSteps()
    {
        // Arrange
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _mockDataCollection
            .Setup(x => x.CollectAccountDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot(
                DateTime.UtcNow,
                1000,
                500,
                new List<PositionInfo>()));

        _mockStrategyEvaluation
            .Setup(x => x.EvaluateMultipleAsync(
                It.IsAny<IEnumerable<ITradingStrategy>>(),
                It.IsAny<MarketSnapshot[]>(),
                It.IsAny<AccountSnapshot>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StrategyEvaluationResult>
            {
                new("TestStrategy", "test1", 100, 0.1m, 2.0, RiskLevel.Low, 10, DateTime.UtcNow)
            });

        // Act
        await _engine.StartAsync(cts.Token);

        // Assert
        _mockDataCollection.Verify(
            x => x.CollectAccountDataAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _mockStrategyEvaluation.Verify(
            x => x.EvaluateMultipleAsync(
                It.IsAny<IEnumerable<ITradingStrategy>>(),
                It.IsAny<MarketSnapshot[]>(),
                It.IsAny<AccountSnapshot>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _mockRecorder.Verify(
            x => x.RecordCycleAsync(It.IsAny<TradingCycleLog>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
```

**验收标准:**
- [ ] 单元测试通过率 > 90%
- [ ] 代码覆盖率 > 70%
- [ ] 集成测试成功

---

## ? 完整的实施检查清单

### 第 1-2 周: 基础设施
- [ ] ServiceConfiguration 创建完成
- [ ] App.xaml.cs 更新完成
- [ ] DI 容器正常工作
- [ ] 编译无错误

### 第 3-4 周: 数据层和策略层
- [ ] IDataCollectionService 实现
- [ ] BinanceDataCollectionService 实现
- [ ] IStrategyEvaluationService 实现
- [ ] IStrategyFilterService 实现
- [ ] 所有接口测试通过

### 第 5-6 周: 执行层和风险层
- [ ] IOrderExecutor 实现
- [ ] RobustOrderExecutor 实现
- [ ] IPositionManager 实现
- [ ] ILeverageController 实现
- [ ] ErrorRecoveryHandler 集成
- [ ] 所有订单类型测试通过

### 第 7 周: 编排层
- [ ] IAutomatedTradingEngine 实现
- [ ] AutomatedTradingEngine 实现
- [ ] 6 步闭环流程完整
- [ ] 日志系统集成

### 第 8 周: 测试优化
- [ ] 单元测试编写完成
- [ ] 集成测试执行
- [ ] 压力测试通过
- [ ] 上线部署

---

## ?? 关键成果指标 (KPI)

| 指标 | 目标 | 验证方式 |
|------|------|---------|
| **闭环执行周期** | 5-10 分钟 | 日志记录 |
| **策略评估准确率** | > 85% | 回测对比 |
| **执行成功率** | > 99% | 订单确认 |
| **平均响应时间** | < 500ms | 性能监控 |
| **系统可用性** | > 99.9% | 24h 测试 |
| **异常恢复时间** | < 30s | 日志分析 |

---

## ?? 总结

这个 8 周实施计划将您的项目从 `ServiceLocator` 架构完整地迁移到**模块化的闭环自动化交易系统**，完全满足您的需求：

? **数据采集** - 实时 WebSocket + REST API  
? **策略评估** - 并行回测和评分  
? **策略筛选** - 自动排序和优先级  
? **实盘执行** - 多种订单类型，异常恢复  
? **风险管理** - 仓位管理和杠杆控制  
? **完整记录** - 数据持久化和回溯  
? **闭环编排** - 自动循环和无人干预  

**立即开始第 1-2 周的实施！** ??

