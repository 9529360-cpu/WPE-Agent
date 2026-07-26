using 币安量化机器人.Models;
using 币安量化机器人.Services.Exchange.Binance;

namespace 币安量化机器人.Services.Agent;

internal sealed class MarketStructureSkill
{
    private readonly IBinancePublicMarketTransport _exchange;
    internal MarketStructureSkill(IBinancePublicMarketTransport exchange) => _exchange = exchange;

    public async Task<MarketSkillSnapshot> ObserveAsync(string symbol, string interval, CancellationToken cancellationToken)
    {
        var closes = await _exchange.GetKlineClosesAsync(symbol, interval, 60, cancellationToken);
        if (closes.Count < 30) throw new InvalidOperationException($"{interval} K线不足");
        var price = closes[^1];
        var changes = closes.Zip(closes.Skip(1), (a, b) => a == 0 ? 0d : (double)((b / a - 1) * 100)).ToArray();
        var gains = changes.TakeLast(14).Select(x => Math.Max(x, 0)).Average();
        var losses = changes.TakeLast(14).Select(x => Math.Max(-x, 0)).Average();
        var rsi = losses == 0 ? 100 : 100 - 100 / (1 + gains / losses);
        return new MarketSkillSnapshot(symbol, interval, price, closes.TakeLast(20).Min(), closes.TakeLast(20).Max(), rsi,
            (double)(price / closes[^11] - 1) * 100, (double)(price / closes[^31] - 1) * 100, DateTime.UtcNow);
    }
}

public sealed class RiskDecisionSkill
{
    public bool CanOpen(AgentDecision decision, decimal availableBalance, decimal price, out decimal quantity, out string reason)
    {
        quantity = 0;
        if (decision.Action is not ("LONG" or "SHORT")) { reason = "不是开仓动作"; return false; }
        if (decision.Confidence < 0.60) { reason = "置信度低于60%"; return false; }
        if (price <= 0 || availableBalance <= 0) { reason = "可用余额或价格无效"; return false; }
        var notional = Math.Min(availableBalance * 0.05m * 3m, 100m);
        quantity = Math.Floor(notional / price * 1000m) / 1000m;
        if (quantity < 0.001m) { reason = "按风险限额计算出的数量低于最小下单量"; return false; }
        reason = "通过单次5%保证金、3倍名义敞口和最小数量检查";
        return true;
    }
}

public sealed class DecisionValidationSkill
{
    public AgentDecision Validate(AgentDecision decision, decimal price, decimal quantity)
    {
        decision.Action = decision.Action.ToUpperInvariant();
        if (decision.Action is not ("LONG" or "SHORT" or "CLOSE" or "HOLD"))
            return new AgentDecision { Action = "HOLD", Confidence = 0, Reason = "动作不在允许集合内" };
        if (quantity == 0 && decision.Action == "CLOSE")
            return new AgentDecision { Action = "HOLD", Confidence = 0, Reason = "当前没有仓位可平" };
        if (decision.Support <= 0) decision.Support = price * 0.99m;
        if (decision.Resistance <= 0) decision.Resistance = price * 1.01m;
        return decision;
    }
}
