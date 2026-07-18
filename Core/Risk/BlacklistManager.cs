using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public class BlacklistManager
{
    private readonly ConcurrentDictionary<string, int> _lossCounters = new();
    private readonly ConcurrentDictionary<string, DateTime> _blacklist = new();
    private TimeSpan _cooldown = TimeSpan.FromHours(1);
    private int _threshold = 3;

    public IReadOnlyCollection<string> Symbols => _blacklist.Keys.ToList();

    public void Configure(RiskConfiguration configuration)
    {
        _threshold = configuration.BlacklistThreshold;
    }

    public void Update(string symbol, int consecutiveLosses)
    {
        if (consecutiveLosses >= _threshold)
        {
            _blacklist[symbol] = DateTime.UtcNow.Add(_cooldown);
        }
        else
        {
            _lossCounters[symbol] = consecutiveLosses;
        }

        ClearExpired();
    }

    public bool IsBlacklisted(string symbol)
    {
        ClearExpired();
        return _blacklist.ContainsKey(symbol);
    }

    public void RecordFill(string symbol, TradeActionType actionType)
    {
        if (actionType == TradeActionType.EnterLong || actionType == TradeActionType.EnterShort)
        {
            _lossCounters.AddOrUpdate(symbol, 0, (_, __) => 0);
        }
    }

    private void ClearExpired()
    {
        foreach (var item in _blacklist.ToArray())
        {
            if (item.Value <= DateTime.UtcNow)
            {
                _blacklist.TryRemove(item.Key, out _);
            }
        }
    }
}

public sealed class ConsecutiveLossBlacklistRule : IRiskRule
{
    private readonly BlacklistManager _manager;
    private int _threshold = 3;

    public ConsecutiveLossBlacklistRule(BlacklistManager manager)
    {
        _manager = manager;
    }

    public string Name => "Blacklist";

    public void Configure(RiskConfiguration configuration)
    {
        _threshold = configuration.BlacklistThreshold;
        _manager.Configure(configuration);
    }

    public RiskRuleResult Evaluate(in PositionSnapshot snapshot)
    {
        if (snapshot.ConsecutiveLosingTrades >= _threshold)
        {
            return new RiskRuleResult(false, $"{snapshot.Symbol} reached {_threshold} losses");
        }

        return new RiskRuleResult(true);
    }
}
