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
