using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services.Exchange.Binance;

public sealed class BinancePublicMarketClient : IBinancePublicMarketTransport, IDisposable
{
    private const string MainnetEndpoint = "https://fapi.binance.com";
    private const string TestnetEndpoint = "https://testnet.binancefuture.com";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public BinancePublicMarketClient(HttpClient? httpClient = null, bool useTestnet = false, int timeoutSeconds = 20, bool useProxy = false, string? proxyUrl = null)
    {
        var handler = new HttpClientHandler();
        if (useProxy && Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(handler)
        {
            BaseAddress = new Uri(useTestnet ? TestnetEndpoint : MainnetEndpoint),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120))
        };
    }

    public async Task<IReadOnlyList<TickerQuote>> GetMiniTickersAsync(IEnumerable<string>? symbols = null, CancellationToken cancellationToken = default)
    {
        var tickers = await GetAsync<List<TickerDto>>("/fapi/v1/ticker/24hr", cancellationToken).ConfigureAwait(false);
        var filter = symbols is null ? null : new HashSet<string>(symbols.Select(symbol => symbol.ToUpperInvariant()), StringComparer.Ordinal);
        return tickers.Where(ticker => filter is null || filter.Contains(ticker.Symbol)).Select(ticker => new TickerQuote(
            ticker.Symbol,
            ParseDouble(ticker.LastPrice),
            0,
            ParseDouble(ticker.PriceChangePercent),
            ParseDouble(ticker.Volume),
            ParseDouble(ticker.HighPrice),
            ParseDouble(ticker.LowPrice))).ToArray();
    }

    public async Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit = 500, CancellationToken cancellationToken = default)
    {
        var path = $"/fapi/v1/klines?symbol={Uri.EscapeDataString(symbol.ToUpperInvariant())}" +
                   $"&interval={Uri.EscapeDataString(interval)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        var raw = await GetAsync<string>(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.EnumerateArray().Select(kline => decimal.Parse(kline[4].GetString()!, CultureInfo.InvariantCulture)).ToArray();
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (typeof(T) == typeof(string))
            return (T)(object)await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to deserialize Binance public market response.");
    }

    private static double ParseDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private sealed class TickerDto
    {
        public string Symbol { get; init; } = string.Empty;
        public string LastPrice { get; init; } = "0";
        public string PriceChangePercent { get; init; } = "0";
        public string Volume { get; init; } = "0";
        public string HighPrice { get; init; } = "0";
        public string LowPrice { get; init; } = "0";
    }
}
