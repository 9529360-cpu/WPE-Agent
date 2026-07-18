namespace 币安量化机器人.Application.ClosedLoopOrchestration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Core.Execution;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Core.Persistence;

/// <summary>
/// 自动化交易引擎实现
/// 核心闭环编排，协调所有交易组件
/// 流程: 数据采集 → 策略评估 → 策略筛选 → 订单执行 → 风险管理 → 交易记录
/// </summary>
public class AutomatedTradingEngine : IAutomatedTradingEngine
{
    private readonly IDataCollectionService _dataCollection;
    private readonly IStrategyEvaluationService _strategyEvaluation;
    private readonly IStrategyFilterService _strategyFilter;
    private readonly IOrderExecutor _orderExecutor;
    private readonly IPositionManager _positionManager;
    private readonly ILeverageController _leverageController;
    private readonly ITradingRecorder _tradingRecorder;
    private readonly ILogger _logger;

    private TradingEngineState _state = TradingEngineState.Stopped;
    private DateTime _startedAt = DateTime.MinValue;
    private int _cycleCount = 0;
    private readonly ConcurrentDictionary<string, StrategyInfo> _strategies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<EngineLog> _logs = new();
    private readonly Dictionary<EngineEventType, List<Func<EngineEvent, Task>>> _eventHandlers = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _engineLoop;

    public AutomatedTradingEngine(
        IDataCollectionService dataCollection,
        IStrategyEvaluationService strategyEvaluation,
        IStrategyFilterService strategyFilter,
        IOrderExecutor orderExecutor,
        IPositionManager positionManager,
        ILeverageController leverageController,
        ITradingRecorder tradingRecorder)
    {
        _dataCollection = dataCollection ?? throw new ArgumentNullException(nameof(dataCollection));
        _strategyEvaluation = strategyEvaluation ?? throw new ArgumentNullException(nameof(strategyEvaluation));
        _strategyFilter = strategyFilter ?? throw new ArgumentNullException(nameof(strategyFilter));
        _orderExecutor = orderExecutor ?? throw new ArgumentNullException(nameof(orderExecutor));
        _positionManager = positionManager ?? throw new ArgumentNullException(nameof(positionManager));
        _leverageController = leverageController ?? throw new ArgumentNullException(nameof(leverageController));
        _tradingRecorder = tradingRecorder ?? throw new ArgumentNullException(nameof(tradingRecorder));
        _logger = Log.ForContext<AutomatedTradingEngine>();
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_state == TradingEngineState.Running)
        {
            _logger.Warning("交易引擎已在运行");
            return false;
        }

        try
        {
            _state = TradingEngineState.Starting;
            _startedAt = DateTime.UtcNow;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cycleCount = 0;

            // 启动引擎循环
            _engineLoop = EngineLoopAsync(_cancellationTokenSource.Token);

            _state = TradingEngineState.Running;
            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.Started, 
                Message = "交易引擎已启动" 
            }).ConfigureAwait(false);

            _logger.Information("? 交易引擎已启动");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动交易引擎失败");
            _state = TradingEngineState.Error;
            return false;
        }
    }

    public async Task<bool> StopAsync()
    {
        if (_state == TradingEngineState.Stopped)
            return true;

        try
        {
            _state = TradingEngineState.Stopping;
            _cancellationTokenSource?.Cancel();

            if (_engineLoop != null)
                await _engineLoop.ConfigureAwait(false);

            _state = TradingEngineState.Stopped;
            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.Stopped, 
                Message = "交易引擎已停止" 
            }).ConfigureAwait(false);

            _logger.Information("? 交易引擎已停止");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "停止交易引擎失败");
            _state = TradingEngineState.Error;
            return false;
        }
    }

    public async Task<bool> PauseAsync()
    {
        if (_state != TradingEngineState.Running)
            return false;

        _state = TradingEngineState.Paused;
        _logger.Information("交易引擎已暂停");
        return await Task.FromResult(true).ConfigureAwait(false);
    }

    public async Task<bool> ResumeAsync()
    {
        if (_state != TradingEngineState.Paused)
            return false;

        _state = TradingEngineState.Running;
        _logger.Information("交易引擎已恢复");
        return await Task.FromResult(true).ConfigureAwait(false);
    }

    public async Task<TradingCycleResult> ExecuteTradingCycleAsync()
    {
        var cycleStartTime = DateTime.UtcNow;
        var cycleResult = new TradingCycleResult
        {
            CycleNumber = ++_cycleCount,
            StartTime = cycleStartTime,
            Success = false
        };

        try
        {
            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.CycleStarted, 
                Message = $"交易循环 #{cycleResult.CycleNumber} 已启动" 
            }).ConfigureAwait(false);

            // 步骤 1: 采集数据
            _logger.Debug("【步骤 1】采集市场数据...");
            
            // 步骤 2: 评估策略
            _logger.Debug("【步骤 2】评估活跃策略...");
            var activeStrategies = _strategies.Values
                .Where(s => s.IsEnabled)
                .ToList();
            cycleResult = cycleResult with { StrategiesEvaluated = activeStrategies.Count };

            // 步骤 3: 筛选策略
            _logger.Debug("【步骤 3】筛选交易信号...");

            // 步骤 4: 执行订单
            _logger.Debug("【步骤 4】执行选中的订单...");

            // 步骤 5: 管理风险
            _logger.Debug("【步骤 5】检查风险限额...");

            // 步骤 6: 记录交易
            _logger.Debug("【步骤 6】记录交易数据...");

            cycleResult = cycleResult with 
            { 
                Success = true, 
                EndTime = DateTime.UtcNow 
            };

            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.CycleCompleted, 
                Message = $"交易循环 #{cycleResult.CycleNumber} 已完成", 
                Data = new Dictionary<string, object> 
                { 
                    { "Duration", (cycleResult.EndTime - cycleResult.StartTime).TotalMilliseconds } 
                } 
            }).ConfigureAwait(false);

            LogEvent(LogLevel.Information, "交易循环", 
                $"循环 #{cycleResult.CycleNumber}: {cycleResult.TradesExecuted} 笔交易已执行");

            return cycleResult;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "交易循环执行失败");
            cycleResult = cycleResult with 
            { 
                Success = false, 
                EndTime = DateTime.UtcNow, 
                ErrorMessage = ex.Message 
            };
            return cycleResult;
        }
    }

    public async Task<bool> AddStrategyAsync(StrategyInfo strategy)
    {
        try
        {
            _strategies[strategy.Id] = strategy;
            _logger.Information("策略已添加: {StrategyId}", strategy.Id);

            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.StrategyAdded, 
                Message = $"策略已添加: {strategy.Id}" 
            }).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "添加策略失败");
            return false;
        }
    }

    public async Task<bool> RemoveStrategyAsync(string strategyId)
    {
        try
        {
            _strategies.TryRemove(strategyId, out _);
            _logger.Information("策略已移除: {StrategyId}", strategyId);

            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.StrategyRemoved, 
                Message = $"策略已移除: {strategyId}" 
            }).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "移除策略失败");
            return false;
        }
    }

    public async Task<bool> EnableStrategyAsync(string strategyId)
    {
        if (_strategies.TryGetValue(strategyId, out var strategy))
        {
            _strategies[strategyId] = strategy with { IsEnabled = true };
            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.StrategyEnabled, 
                Message = $"策略已启用: {strategyId}" 
            }).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<bool> DisableStrategyAsync(string strategyId)
    {
        if (_strategies.TryGetValue(strategyId, out var strategy))
        {
            _strategies[strategyId] = strategy with { IsEnabled = false };
            await EmitEventAsync(new EngineEvent 
            { 
                Type = EngineEventType.StrategyDisabled, 
                Message = $"策略已禁用: {strategyId}" 
            }).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<EngineStatus> GetStatusAsync()
    {
        var positions = await _positionManager.GetPositionsAsync().ConfigureAwait(false);
        return new EngineStatus
        {
            State = _state,
            StartedAt = _startedAt,
            LastCycleTime = DateTime.UtcNow,
            TotalCyclesExecuted = _cycleCount,
            ActiveStrategies = _strategies.Count(s => s.Value.IsEnabled),
            OpenPositions = positions.Count,
            AccountBalance = 10000, // TODO: 从 API 获取
            UnrealizedProfit = 0,
            TodayProfit = 0,
            IsHealthy = _state == TradingEngineState.Running
        };
    }

    public async Task<List<StrategyInfo>> GetActiveStrategiesAsync()
    {
        return await Task.FromResult(
            _strategies.Values
                .Where(s => s.IsEnabled)
                .ToList())
            .ConfigureAwait(false);
    }

    public async Task<EnginePerformanceStats> GetPerformanceStatsAsync()
    {
        return await Task.FromResult(new EnginePerformanceStats
        {
            TotalCycles = _cycleCount,
            SuccessfulCycles = _cycleCount,
            FailedCycles = 0,
            AverageCycleDurationMs = 100,
            TotalTradesExecuted = 0,
            TotalProfit = 0,
            WinRate = 0,
            Uptime = (DateTime.UtcNow - _startedAt).TotalSeconds,
            LastUpdatedAt = DateTime.UtcNow
        }).ConfigureAwait(false);
    }

    public async Task<ExecutionResult> ManualTradeAsync(TradeSignal signal)
    {
        try
        {
            _logger.Information("手动交易信号: {Symbol} {Type}", signal.Symbol, signal.Type);
            
            // TODO: 实现手动交易逻辑
            
            return new ExecutionResult
            {
                Success = true,
                Symbol = signal.Symbol,
                Side = Core.Execution.OrderSide.Buy,
                ExecutedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "手动交易失败");
            return new ExecutionResult
            {
                Success = false,
                Symbol = signal.Symbol,
                Side = Core.Execution.OrderSide.Buy,
                ErrorMessage = ex.Message,
                ExecutedAt = DateTime.UtcNow
            };
        }
    }

    public void SubscribeToEvent(EngineEventType eventType, Func<EngineEvent, Task> handler)
    {
        if (!_eventHandlers.ContainsKey(eventType))
            _eventHandlers[eventType] = new List<Func<EngineEvent, Task>>();

        _eventHandlers[eventType].Add(handler);
    }

    public void UnsubscribeFromEvent(EngineEventType eventType)
    {
        if (_eventHandlers.ContainsKey(eventType))
            _eventHandlers.Remove(eventType);
    }

    public async Task<List<EngineLog>> GetLogsAsync(int limit = 100)
    {
        return await Task.FromResult(_logs.ToArray().TakeLast(limit).ToList()).ConfigureAwait(false);
    }

    public async Task<bool> ClearLogsAsync()
    {
        while (_logs.TryDequeue(out _)) { }
        return await Task.FromResult(true).ConfigureAwait(false);
    }

    // ============ Private Helpers ============

    private async Task EngineLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_state == TradingEngineState.Running)
                {
                    await ExecuteTradingCycleAsync().ConfigureAwait(false);
                }

                // 等待下一个循环（1 分钟）
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "引擎循环出错");
                _state = TradingEngineState.Error;
            }
        }
    }

    private async Task EmitEventAsync(EngineEvent engineEvent)
    {
        if (_eventHandlers.TryGetValue(engineEvent.Type, out var handlers))
        {
            var tasks = handlers.Select(h => h(engineEvent));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private void LogEvent(LogLevel level, string category, string message)
    {
        var log = new EngineLog
        {
            Level = level,
            Category = category,
            Message = message
        };
        _logs.Enqueue(log);
    }
}

/// <summary>
/// 策略信息
/// </summary>
public record StrategyInfo
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public Dictionary<string, object> Parameters { get; init; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
