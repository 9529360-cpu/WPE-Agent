using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public class RiskManager : IRiskManager
{
    private readonly List<IRiskRule> _rules = new();
    private readonly BlacklistManager _blacklist = new();
    private readonly KellyAllocator _allocator = new();
    private readonly ValueAtRiskCalculator _varCalculator = new();
    private RiskProfile _profile = new(0, 0, 0, Array.Empty<string>(), 0, 0);
    private RiskConfiguration _configuration = new(0.2, 0.15, 0.05, 1.5, 3, TimeSpan.FromMinutes(1));
    private DateTime _lastUpdate = DateTime.MinValue;
    private string? _lastSymbol;

    public RiskManager()
    {
        _rules.Add(new MaxPositionRule());
        _rules.Add(new DynamicStopLossRule());
        _rules.Add(new MaxDrawdownRule());
        _rules.Add(new ConsecutiveLossBlacklistRule(_blacklist));
    }

    public event EventHandler<RiskEvent>? RiskTriggered;

    public RiskProfile CurrentProfile => _profile;

    public void Configure(RiskConfiguration configuration)
    {
        _configuration = configuration;
        foreach (var rule in _rules)
        {
            rule.Configure(configuration);
        }
    }

    public ValueTask UpdateAsync(PositionSnapshot position, CancellationToken cancellationToken = default)
    {
        foreach (var rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = rule.Evaluate(position);
            if (!result.Passed)
            {
                RiskTriggered?.Invoke(this, new RiskEvent(position.Symbol, rule.Name, result.Message ?? "", DateTime.UtcNow));
            }
        }

        _lastSymbol = position.Symbol;
        _blacklist.Update(position.Symbol, position.ConsecutiveLosingTrades);
        var kelly = _allocator.Calculate(position);
        var var = _varCalculator.Calculate(position, _configuration);
        _profile = new RiskProfile(position.Quantity * position.CurrentPrice, position.MaxDrawdown, position.DailyPnl < 0 ? Math.Abs(position.DailyPnl) : 0, _blacklist.Symbols, kelly, var);
        _lastUpdate = DateTime.UtcNow;
        return ValueTask.CompletedTask;
    }

    public bool Approve(TradeAction action)
    {
        if (DateTime.UtcNow - _lastUpdate > _configuration.RiskEvaluationInterval)
        {
            return false;
        }

        if (_lastSymbol is not null && _blacklist.IsBlacklisted(_lastSymbol))
        {
            return false;
        }

        return action.ActionType == TradeActionType.Hold || action.Quantity <= _configuration.MaxPositionSize;
    }

    public ValueTask RecordFillAsync(TradeFill fill, CancellationToken cancellationToken = default)
    {
        _blacklist.RecordFill(fill.Symbol, fill.ActionType);
        return ValueTask.CompletedTask;
    }
}
