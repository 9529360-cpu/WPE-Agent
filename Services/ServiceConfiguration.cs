namespace 币安量化机器人.Services;

using System.IO;
using Microsoft.Extensions.DependencyInjection;
using 币安量化机器人.Application.Services;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Persistence;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Infrastructure.Data;
using 币安量化机器人.Infrastructure.Persistence;

public static class ServiceConfiguration
{
    public static ServiceProvider ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IStrategyEvaluationService, StrategyEvaluationService>();
        services.AddScoped<IStrategyFilterService, StrategyFilterService>();
        services.AddSingleton<IPositionManager, PositionManager>();
        services.AddSingleton<ILeverageController, LeverageController>();
        services.AddSingleton<ITradingRecorder>(_ =>
            new SqliteTradingRecorder(AppDataPaths.File("trading.db")));
        services.AddSingleton<ITradeRepository>(provider =>
            new TradeRepository(provider.GetRequiredService<ITradingRecorder>()));
        return services.BuildServiceProvider();
    }
}
