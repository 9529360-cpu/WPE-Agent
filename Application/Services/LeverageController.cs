namespace 币安量化机器人.Application.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using 币安量化机器人.Core.Risk;

/// <summary>
/// 杠杆控制器实现
/// 负责管理保证金和杠杆倍数
/// </summary>
public class LeverageController : ILeverageController
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, int> _symbolLeverage = 
        new(StringComparer.OrdinalIgnoreCase);

    public LeverageController()
    {
        _logger = Log.ForContext<LeverageController>();
    }

    public Task<bool> SetLeverageAsync(string symbol, int leverage)
    {
        var maxLeverage = GetMaxLeverageAsync(symbol).GetAwaiter().GetResult();
        _symbolLeverage[symbol] = Math.Clamp(leverage, 1, maxLeverage);
        _logger.Information("杠杆已设置: {Symbol} => {Leverage}", symbol, _symbolLeverage[symbol]);
        return Task.FromResult(true);
    }

    public Task<int> GetCurrentLeverageAsync(string symbol)
    {
        _symbolLeverage.TryGetValue(symbol, out var lev);
        return Task.FromResult(lev == 0 ? 1 : lev);
    }

    public Task<decimal> CalculateRequiredMarginAsync(
        string symbol, 
        decimal quantity, 
        decimal price, 
        int leverage)
    {
        if (leverage <= 0) 
            leverage = 1;
        
        var notional = quantity * price;
        var required = notional / leverage;
        return Task.FromResult(required);
    }

    public Task<decimal> GetTotalMarginAsync()
    {
        return Task.FromResult(0m);
    }

    public Task<decimal> GetAvailableMarginAsync()
    {
        return Task.FromResult(0m);
    }

    public Task<decimal> CalculateMarginRatioAsync()
    {
        return Task.FromResult(0m);
    }

    public Task<bool> IsLiquidationRiskAsync(decimal warningThreshold = 0.2m)
    {
        return Task.FromResult(false);
    }

    public Task<bool> AutoAdjustLeverageToTargetRatioAsync(decimal targetMarginRatio)
    {
        foreach (var key in _symbolLeverage.Keys.ToList())
        {
            var cur = _symbolLeverage[key];
            if (cur > 1)
                _symbolLeverage[key] = Math.Max(1, (int)(cur * (double)targetMarginRatio));
        }
        return Task.FromResult(true);
    }

    public Task<bool> AddMarginAsync(decimal amount)
    {
        _logger.Information("增加保证金请求: {Amount}", amount);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveMarginAsync(decimal amount)
    {
        _logger.Information("减少保证金请求: {Amount}", amount);
        return Task.FromResult(true);
    }

    public Task<bool> CanOpenWithLeverageAsync(string symbol, decimal quantity, decimal price, int leverage)
    {
        var max = GetMaxLeverageAsync(symbol).GetAwaiter().GetResult();
        return Task.FromResult(leverage <= max);
    }

    public Task<LeverageInfo> GetLeverageInfoAsync()
    {
        var info = new LeverageInfo
        {
            TotalMargin = 0,
            AvailableMargin = 0,
            UsedMargin = 0,
            MarginRatio = 0,
            LiquidationRisk = false,
            SymbolLeverages = _symbolLeverage.ToDictionary(
                kv => kv.Key, 
                kv => new SymbolLeverageInfo 
                { 
                    Symbol = kv.Key, 
                    CurrentLeverage = kv.Value, 
                    MaxLeverage = 125, 
                    UsedMargin = 0, 
                    MaintenanceMargin = 0 
                }),
            UpdatedAt = DateTime.UtcNow
        };
        return Task.FromResult(info);
    }

    public Task<int> GetMaxLeverageAsync(string symbol)
    {
        return Task.FromResult(125);
    }

    public Task<List<LeverageAdjustmentRecord>> GetLeverageHistoryAsync(int limit = 100)
    {
        return Task.FromResult(new List<LeverageAdjustmentRecord>());
    }
}
