namespace 币安量化机器人.Application.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using 币安量化机器人.Core.Risk;

/// <summary>
/// 仓位管理器实现
/// 负责跟踪和管理持仓的大小和杠杆比例
/// </summary>
public class PositionManager : IPositionManager
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Position> _positions = 
        new(StringComparer.OrdinalIgnoreCase);

    public PositionManager()
    {
        _logger = Log.ForContext<PositionManager>();
    }

    public Task<bool> OpenPositionAsync(Position position)
    {
        if (position is null) 
            throw new ArgumentNullException(nameof(position));

        _positions.AddOrUpdate(position.Symbol, position, (k, v) => position);
        _logger.Information(
            "仓位已开启: {Symbol} {Qty} @ {Price}", 
            position.Symbol, 
            position.Quantity, 
            position.EntryPrice);
        return Task.FromResult(true);
    }

    public Task<bool> ClosePositionAsync(string symbol, decimal quantity = 0)
    {
        if (!_positions.TryGetValue(symbol, out var pos))
            return Task.FromResult(false);

        if (quantity <= 0 || quantity >= pos.Quantity)
        {
            _positions.TryRemove(symbol, out _);
            _logger.Information("仓位已平: {Symbol} 全部平仓", symbol);
            return Task.FromResult(true);
        }

        var updated = pos with 
        { 
            Quantity = pos.Quantity - quantity, 
            LastUpdateTime = DateTime.UtcNow 
        };
        _positions[symbol] = updated;
        _logger.Information("仓位已部分平: {Symbol} 新数量={Qty}", symbol, updated.Quantity);
        return Task.FromResult(true);
    }

    public Task<bool> PartialCloseAsync(string symbol, decimal quantity)
    {
        return ClosePositionAsync(symbol, quantity);
    }

    public Task<List<Position>> GetPositionsAsync(string? symbol = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Task.FromResult(_positions.Values.ToList());

        return Task.FromResult(
            _positions.Values
                .Where(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .ToList());
    }

    public Task<Position?> GetPositionAsync(string symbol)
    {
        _positions.TryGetValue(symbol, out var pos);
        return Task.FromResult(pos);
    }

    public Task<decimal> CalculateUnrealizedProfitAsync(string symbol, decimal currentPrice)
    {
        if (!_positions.TryGetValue(symbol, out var pos))
            return Task.FromResult(0m);

        var unreal = pos.Direction == PositionDirection.Long 
            ? (currentPrice - pos.EntryPrice) * pos.Quantity - pos.Commission 
            : (pos.EntryPrice - currentPrice) * pos.Quantity - pos.Commission;
        
        return Task.FromResult(unreal);
    }

    public Task<bool> UpdatePositionMarkPriceAsync(string symbol, decimal markPrice)
    {
        if (!_positions.TryGetValue(symbol, out var pos))
            return Task.FromResult(false);

        _positions[symbol] = pos with 
        { 
            CurrentPrice = markPrice, 
            LastUpdateTime = DateTime.UtcNow 
        };
        return Task.FromResult(true);
    }

    public Task<decimal> GetTotalPositionValueAsync()
    {
        var total = _positions.Values.Sum(p => p.Quantity * p.CurrentPrice);
        return Task.FromResult(total);
    }

    public Task<int> GetOpenPositionCountAsync()
    {
        return Task.FromResult(_positions.Count);
    }

    public Task<bool> CanOpenPositionAsync(string symbol, decimal quantity, decimal maxPositionSize)
    {
        var current = _positions.TryGetValue(symbol, out var pos) ? pos.Quantity : 0m;
        return Task.FromResult(current + quantity <= maxPositionSize);
    }

    public Task<bool> UpdateStopLossAsync(string symbol, decimal newStopLossPrice)
    {
        if (!_positions.TryGetValue(symbol, out var pos))
            return Task.FromResult(false);

        _positions[symbol] = pos with 
        { 
            StopLossPrice = newStopLossPrice, 
            LastUpdateTime = DateTime.UtcNow 
        };
        return Task.FromResult(true);
    }

    public Task<bool> UpdateTakeProfitAsync(string symbol, decimal newTakeProfitPrice)
    {
        if (!_positions.TryGetValue(symbol, out var pos))
            return Task.FromResult(false);

        _positions[symbol] = pos with 
        { 
            TakeProfitPrice = newTakeProfitPrice, 
            LastUpdateTime = DateTime.UtcNow 
        };
        return Task.FromResult(true);
    }

    public Task<List<PositionHistory>> GetPositionHistoryAsync(string? symbol = null, int limit = 100)
    {
        return Task.FromResult(new List<PositionHistory>());
    }

    public Task<bool> CloseAllPositionsAsync()
    {
        _positions.Clear();
        _logger.Information("所有仓位已平");
        return Task.FromResult(true);
    }
}
