using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public interface IMarketDataReader
{
    Task<IReadOnlyList<FundingRateSnapshot>> GetFundingRatesAsync(string? symbol = null, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TickerQuote>> GetMiniTickersAsync(IEnumerable<string>? symbols = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit = 500, CancellationToken cancellationToken = default);
}
