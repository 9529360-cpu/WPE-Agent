using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public sealed class BinanceReadFacade : IMarketDataReader, IAccountReader, IOrderQueryReader
{
    private readonly BinanceApiClient _client;

    internal BinanceReadFacade(BinanceApiClient client) => _client = client;

    public Task<IReadOnlyList<FundingRateSnapshot>> GetFundingRatesAsync(string? symbol = null, int limit = 50, CancellationToken cancellationToken = default) =>
        _client.GetFundingRatesAsync(symbol, limit, cancellationToken);
    public Task<IReadOnlyList<TickerQuote>> GetMiniTickersAsync(IEnumerable<string>? symbols = null, CancellationToken cancellationToken = default) =>
        _client.GetMiniTickersAsync(symbols, cancellationToken);
    public Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit = 500, CancellationToken cancellationToken = default) =>
        _client.GetKlineClosesAsync(symbol, interval, limit, cancellationToken);
    public Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(CancellationToken cancellationToken = default) =>
        _client.GetAccountBalancesAsync(cancellationToken);
    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
        _client.GetPositionsAsync(cancellationToken);
    public Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
        _client.GetOpenOrdersAsync(symbol, cancellationToken);
    public Task<IReadOnlyList<TradeExecution>> GetRecentTradesAsync(string symbol, int limit = 20, CancellationToken cancellationToken = default) =>
        _client.GetRecentTradesAsync(symbol, limit, cancellationToken);
}
