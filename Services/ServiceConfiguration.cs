namespace 币安量化机器人.Services;

using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Core.Execution;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Core.Persistence;
using 币安量化机器人.Application.ClosedLoopOrchestration;
using 币安量化机器人.Infrastructure.Data;
using 币安量化机器人.Infrastructure.Persistence;
using 币安量化机器人.Application.Services;

/// <summary>
/// 服务配置类
/// 负责注册所有依赖注入（DI）服务
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// 配置依赖注入容器
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务提供者</returns>
    public static ServiceProvider ConfigureServices(IServiceCollection services)
    {
        // ============ 基础设施服务 ============
        RegisterInfrastructureServices(services);

        // ============ Core Layer - 数据采集 ============
        RegisterDataServices(services);

        // ============ Core Layer - 策略 ============
        RegisterStrategyServices(services);

        // ============ Core Layer - 执行 ============
        RegisterExecutionServices(services);

        // ============ Core Layer - 风险管理 ============
        RegisterRiskServices(services);

        // ============ Core Layer - 持久化 ============
        RegisterPersistenceServices(services);

        // ============ Application Layer - 闭环编排 ============
        RegisterOrchestrationServices(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 注册基础设施服务
    /// </summary>
    private static void RegisterInfrastructureServices(IServiceCollection services)
    {
        // 注册 Binance API 客户端
        services.AddSingleton<BinanceApiClient>();

        // 注册现有的基础设施服务
        if (services.FirstOrDefault(x => x.ServiceType == typeof(BinanceApiClient)) == null)
        {
            services.AddSingleton<BinanceApiClient>();
        }
    }

    /// <summary>
    /// 注册数据采集服务
    /// </summary>
    private static void RegisterDataServices(IServiceCollection services)
    {
        services.AddSingleton<IDataCollectionService, BinanceDataCollectionService>();
    }

    /// <summary>
    /// 注册策略服务
    /// </summary>
    private static void RegisterStrategyServices(IServiceCollection services)
    {
        services.AddScoped<IStrategyEvaluationService, StrategyEvaluationService>();
        services.AddScoped<IStrategyFilterService, StrategyFilterService>();
    }

    /// <summary>
    /// 注册执行服务
    /// </summary>
    private static void RegisterExecutionServices(IServiceCollection services)
    {
        // 注册订单执行和错误恢复的实现 (Week 4)
        services.AddSingleton<IOrderExecutor, RobustOrderExecutor>();
        services.AddSingleton<IErrorRecoveryHandler, ErrorRecoveryHandler>();
    }

    /// <summary>
    /// 注册风险管理服务
    /// </summary>
    private static void RegisterRiskServices(IServiceCollection services)
    {
        // 注册仓位管理和杠杆控制的实现 (Week 4)
        services.AddSingleton<IPositionManager, PositionManager>();
        services.AddSingleton<ILeverageController, LeverageController>();
    }

    /// <summary>
    /// 注册持久化服务
    /// </summary>
    private static void RegisterPersistenceServices(IServiceCollection services)
    {
        // 注册交易记录器的实现 (Week 5)
        services.AddSingleton<ITradingRecorder>(provider =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "trading.db");
            return new SqliteTradingRecorder(dbPath);
        });

        // 注册交易仓储 (Week 5)
        services.AddSingleton<ITradeRepository>(provider =>
        {
            var recorder = provider.GetRequiredService<ITradingRecorder>();
            return new TradeRepository(recorder);
        });
    }

    /// <summary>
    /// 注册闭环编排服务
    /// </summary>
    private static void RegisterOrchestrationServices(IServiceCollection services)
    {
        // 注册自动化交易引擎的实现 (Week 5)
        services.AddSingleton<IAutomatedTradingEngine>(provider =>
        {
            var dataCollection = provider.GetRequiredService<IDataCollectionService>();
            var strategyEvaluation = provider.GetRequiredService<IStrategyEvaluationService>();
            var strategyFilter = provider.GetRequiredService<IStrategyFilterService>();
            var orderExecutor = provider.GetRequiredService<IOrderExecutor>();
            var positionManager = provider.GetRequiredService<IPositionManager>();
            var leverageController = provider.GetRequiredService<ILeverageController>();
            var tradingRecorder = provider.GetRequiredService<ITradingRecorder>();

            return new AutomatedTradingEngine(
                dataCollection,
                strategyEvaluation,
                strategyFilter,
                orderExecutor,
                positionManager,
                leverageController,
                tradingRecorder);
        });
    }
}
