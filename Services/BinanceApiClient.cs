using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public class BinanceApiClient : IDisposable
{
    private const string MainnetEndpoint = "https://fapi.binance.com";
    private const string TestnetEndpoint = "https://testnet.binancefuture.com";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000)
    };

    private string? _apiKey;
    private byte[]? _secretBytes;
    private readonly int _receiveWindow;

    public BinanceApiClient(HttpClient? httpClient = null, bool useTestnet = true, string? endpoint = null, int timeoutSeconds = 20, bool useProxy = false, string? proxyUrl = null, int receiveWindow = 5000)
    {
        endpoint=string.IsNullOrWhiteSpace(endpoint)?useTestnet?TestnetEndpoint:MainnetEndpoint:endpoint.TrimEnd('/');var handler=new HttpClientHandler();if(useProxy&&Uri.TryCreate(proxyUrl,UriKind.Absolute,out var proxy)){handler.Proxy=new WebProxy(proxy);handler.UseProxy=true;}_httpClient=httpClient??new HttpClient(handler){BaseAddress=new Uri(endpoint),Timeout=TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds,5,120))};_receiveWindow=Math.Clamp(receiveWindow,1000,60000);
    }

    public void SetApiCredentials(string apiKey, string secretKey)
    {
        _apiKey = apiKey;
        _secretBytes = Encoding.UTF8.GetBytes(secretKey);
    }

    public async Task<IReadOnlyList<FundingRateSnapshot>> GetFundingRatesAsync(string? symbol = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(symbol))
            query["symbol"] = symbol.ToUpperInvariant();

        var fundingRates = await SendPublicAsync<List<FundingRateDto>>(HttpMethod.Get, "/fapi/v1/fundingRate", query, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MarkPriceDto> markPrices;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            markPrices = await SendPublicAsync<List<MarkPriceDto>>(HttpMethod.Get, "/fapi/v1/premiumIndex", null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var markPrice = await SendPublicAsync<MarkPriceDto>(HttpMethod.Get, "/fapi/v1/premiumIndex", new Dictionary<string, string?> { ["symbol"] = symbol.ToUpperInvariant() }, cancellationToken).ConfigureAwait(false);
            markPrices = new[] { markPrice };
        }
        var markMap = markPrices.ToDictionary(m => m.Symbol, m => m);

        return fundingRates
            .GroupBy(r => r.Symbol)
            .Select(g => MapFunding(g.Key, g.OrderByDescending(x => x.FundingTime).Take(limit).ToArray(), markMap))
            .OrderByDescending(f => Math.Abs(f.LastFundingRate))
            .ToArray();
    }

    public async Task<IReadOnlyList<TickerQuote>> GetMiniTickersAsync(IEnumerable<string>? symbols = null, CancellationToken cancellationToken = default)
    {
        var tickers = await SendPublicAsync<List<TickerDto>>(HttpMethod.Get, "/fapi/v1/ticker/24hr", null, cancellationToken).ConfigureAwait(false);
        HashSet<string>? filter = null;
        if (symbols is not null)
            filter = new HashSet<string>(symbols.Select(s => s.ToUpperInvariant()));

        return tickers
            .Where(t => filter is null || filter.Contains(t.Symbol))
            .Select(MapTicker)
            .ToArray();
    }

    public async Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var account = await SendSignedAsync<AccountDto>(HttpMethod.Get, "/fapi/v2/account", null, cancellationToken).ConfigureAwait(false);
        return account.Assets.Select(MapBalance).Where(b => b.WalletBalance != 0 || b.AvailableBalance != 0).ToArray();
    }

    public async Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var account = await SendSignedAsync<AccountDto>(HttpMethod.Get, "/fapi/v2/account", null, cancellationToken).ConfigureAwait(false);
        return account.Positions
            .Where(p => decimal.TryParse(p.PositionAmt, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) && qty != 0)
            .Select(MapPosition)
            .ToArray();
    }

    public async Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var query = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(symbol))
            query["symbol"] = symbol.ToUpperInvariant();

        var orders = await SendSignedAsync<List<OrderDto>>(HttpMethod.Get, "/fapi/v1/openOrders", query, cancellationToken).ConfigureAwait(false);
        return orders.Select(MapOrderResponse).ToArray();
    }

    public async Task<OrderResponse> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var query = BuildOrderPayload(request);
        var order = await SendSignedAsync<OrderDto>(HttpMethod.Post, "/fapi/v1/order", query, cancellationToken).ConfigureAwait(false);
        return MapOrderResponse(order);
    }

    public async Task<IReadOnlyList<OrderResponse>> PlaceBatchOrdersAsync(BatchOrderRequest batch, CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var payload = batch.Orders.Select(BuildOrderPayload).ToArray();
        var query = new Dictionary<string, string?>
        {
            ["batchOrders"] = JsonSerializer.Serialize(payload)
        };

        var orders = await SendSignedAsync<List<OrderDto>>(HttpMethod.Post, "/fapi/v1/batchOrders", query, cancellationToken).ConfigureAwait(false);
        return orders.Select(MapOrderResponse).ToArray();
    }

    public async Task<OrderResponse> CancelOrderAsync(string symbol, long orderId, CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var query = new Dictionary<string, string?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId.ToString(CultureInfo.InvariantCulture)
        };
        var order = await SendSignedAsync<OrderDto>(HttpMethod.Delete, "/fapi/v1/order", query, cancellationToken).ConfigureAwait(false);
        return MapOrderResponse(order);
    }

    public async Task<IReadOnlyList<TradeExecution>> GetRecentTradesAsync(string symbol, int limit = 20, CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var query = new Dictionary<string, string?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };

        var trades = await SendSignedAsync<List<UserTradeDto>>(HttpMethod.Get, "/fapi/v1/userTrades", query, cancellationToken).ConfigureAwait(false);
        return trades.Select(MapTrade).ToArray();
    }

    public async Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit = 500, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["interval"] = interval,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };

        var raw = await SendPublicAsync<string>(HttpMethod.Get, "/fapi/v1/klines", query, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.EnumerateArray()
            .Select(k => decimal.Parse(k[4].GetString()!, CultureInfo.InvariantCulture))
            .ToArray();
    }

    public Task<string> GetPublicRawAsync(string path, IDictionary<string,string?>? query, CancellationToken cancellationToken = default)
        => SendPublicAsync<string>(HttpMethod.Get, path, query, cancellationToken);

    public Task<string> GetSignedRawAsync(string path, IDictionary<string,string?>? query, CancellationToken cancellationToken = default)
        => SendSignedAsync<string>(HttpMethod.Get, path, query, cancellationToken);

    public Task<string> PostSignedRawAsync(string path, IDictionary<string,string?>? query, CancellationToken cancellationToken = default)
        => SendSignedAsync<string>(HttpMethod.Post, path, query, cancellationToken);

    public Task<string> DeleteSignedRawAsync(string path, IDictionary<string,string?>? query, CancellationToken cancellationToken = default)
        => SendSignedAsync<string>(HttpMethod.Delete, path, query, cancellationToken);

    private Dictionary<string, string?> BuildOrderPayload(OrderRequest request)
    {
        var payload = new Dictionary<string, string?>
        {
            ["symbol"] = request.Symbol.ToUpperInvariant(),
            ["side"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["type"] = request.Type switch
            {
                OrderType.Market => "MARKET",
                OrderType.Limit => "LIMIT",
                OrderType.StopLoss => "STOP_MARKET",
                OrderType.StopLossLimit => "STOP",
                OrderType.TakeProfit => "TAKE_PROFIT_MARKET",
                OrderType.TakeProfitLimit => "TAKE_PROFIT",
                _ => "LIMIT"
            },
            ["quantity"] = request.Quantity.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(request.ClientOrderId)) payload["newClientOrderId"] = request.ClientOrderId;
        if (!string.IsNullOrWhiteSpace(request.PositionSide)) payload["positionSide"] = request.PositionSide;
        if (request.ReduceOnly) payload["reduceOnly"] = "true";
        if (request.ClosePosition) { payload.Remove("quantity"); payload["closePosition"] = "true"; }

        if (request.Type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit)
            payload["price"] = request.Price.ToString(CultureInfo.InvariantCulture);

        if (request.Type is OrderType.StopLoss or OrderType.StopLossLimit or OrderType.TakeProfit or OrderType.TakeProfitLimit)
            payload["stopPrice"] = request.StopPrice.ToString(CultureInfo.InvariantCulture);
        if (request.Type is OrderType.StopLoss or OrderType.TakeProfit) payload["workingType"] = request.WorkingType;

        if (request.Type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit)
        {
            payload["timeInForce"] = request.TimeInForce switch
            {
                TimeInForce.Gtc => "GTC",
                TimeInForce.Ioc => "IOC",
                TimeInForce.Fok => "FOK",
                _ => "GTC"
            };
        }

        return payload;
    }

    private async Task<T> SendPublicAsync<T>(HttpMethod method, string path, IDictionary<string, string?>? query, CancellationToken cancellationToken)
        => await SendAsyncInternal<T>(() => new HttpRequestMessage(method, BuildUri(path, query)), cancellationToken).ConfigureAwait(false);

    private async Task<T> SendSignedAsync<T>(HttpMethod method, string path, IDictionary<string, string?>? query, CancellationToken cancellationToken)
    {
        EnsureSigned();
        return await SendAsyncInternal<T>(() =>
        {
            var payload = CloneQuery(query);
            payload["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            payload["recvWindow"] = _receiveWindow.ToString(CultureInfo.InvariantCulture);
            var queryString = BuildQueryString(payload);
            payload["signature"] = ComputeSignature(queryString);
            var request = new HttpRequestMessage(method, BuildUri(path, payload));
            request.Headers.Add("X-MBX-APIKEY", _apiKey);
            return request;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsyncInternal<T>(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            using var request = requestFactory();
            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!ShouldRetry(response.StatusCode, attempt, out var delay))
                {
                    response.EnsureSuccessStatusCode();
                    if (typeof(T) == typeof(string))
                    {
                        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        return (T)(object)text;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var data = await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
                    if (data is null)
                        throw new InvalidOperationException("Failed to deserialize Binance response");
                    return data;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (TryGetRetryDelay(attempt, out var delay) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && TryGetRetryDelay(attempt, out var delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Exceeded retry attempts for Binance request");
    }

    private static bool ShouldRetry(HttpStatusCode statusCode, int attempt, out TimeSpan delay)
    {
        if (!TryGetRetryDelay(attempt, out delay))
            return false;

        if (statusCode == (HttpStatusCode)429 ||
            statusCode == HttpStatusCode.RequestTimeout ||
            statusCode == HttpStatusCode.InternalServerError ||
            statusCode == HttpStatusCode.BadGateway ||
            statusCode == HttpStatusCode.ServiceUnavailable ||
            statusCode == HttpStatusCode.GatewayTimeout)
        {
            return true;
        }

        delay = default;
        return false;
    }

    private static bool TryGetRetryDelay(int attempt, out TimeSpan delay)
    {
        if (attempt >= RetryDelays.Length)
        {
            delay = default;
            return false;
        }

        delay = RetryDelays[attempt];
        return true;
    }

    private static IDictionary<string, string?> CloneQuery(IDictionary<string, string?>? source)
    {
        if (source is null || source.Count == 0)
            return new Dictionary<string, string?>();

        return new Dictionary<string, string?>(source);
    }

    private string ComputeSignature(string queryString)
    {
        if (_secretBytes is null)
            throw new InvalidOperationException("API secret has not been configured");

        using var hmac = new HMACSHA256(_secretBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static Uri BuildUri(string path, IDictionary<string, string?>? query)
    {
        if (query is null || query.Count == 0)
            return new Uri(path, UriKind.Relative);

        var queryString = BuildQueryString(query);
        return new Uri($"{path}?{queryString}", UriKind.Relative);
    }

    private static string BuildQueryString(IDictionary<string, string?> query)
    {
        return string.Join('&', query
            .Where(kvp => kvp.Value is not null)
            .Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value!)}"));
    }

    private void EnsureSigned()
    {
        if (string.IsNullOrEmpty(_apiKey) || _secretBytes is null)
            throw new InvalidOperationException("请先在 API 管理中配置 Binance API Key 与 Secret");
    }

    private static FundingRateSnapshot MapFunding(string symbol, FundingRateDto[] rates, IDictionary<string, MarkPriceDto> markMap)
    {
        var latest = rates.First();
        markMap.TryGetValue(symbol, out var mark);
        var history = rates.OrderBy(r => r.FundingTime).Select(r => new FundingHistoryPoint
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.FundingTime).UtcDateTime,
            FundingRate = r.FundingRate
        }).ToList();

        var avg7 = history.TakeLast(21).Average(h => h.FundingRate);
        var predicted = history.TakeLast(12).Any() ? history.TakeLast(12).Average(h => h.FundingRate) : history.LastOrDefault()?.FundingRate ?? 0d;

        return new FundingRateSnapshot
        {
            Symbol = symbol,
            Pair = symbol,
            ContractType = mark?.ContractType ?? "PERPETUAL",
            BaseAsset = mark?.Symbol?.Replace("USDT", string.Empty) ?? symbol,
            QuoteAsset = "USDT",
            MarkPrice = mark?.MarkPrice ?? 0,
            IndexPrice = mark?.IndexPrice ?? 0,
            LastFundingRate = latest.FundingRate,
            PredictedFundingRate = predicted,
            Avg7dFundingRate = avg7,
            NextFundingTime = DateTimeOffset.FromUnixTimeMilliseconds(latest.FundingTime).UtcDateTime.AddHours(8),
            OpenInterest = mark?.EstimatedSettlePrice ?? 0,
            History = history
        };
    }

    private static TickerQuote MapTicker(TickerDto dto)
    {
        return new TickerQuote(
            dto.Symbol,
            dto.LastPrice,
            dto.IndexPrice,
            dto.PriceChangePercent,
            dto.Volume,
            dto.HighPrice,
            dto.LowPrice);
    }

    private static PositionSnapshot MapPosition(PositionDto dto)
    {
        decimal.TryParse(dto.PositionAmt, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty);
        decimal.TryParse(dto.EntryPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var entry);
        decimal.TryParse(dto.MarkPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var mark);
        decimal.TryParse(dto.UnrealizedProfit, NumberStyles.Number, CultureInfo.InvariantCulture, out var pnl);
        decimal.TryParse(dto.Leverage, NumberStyles.Number, CultureInfo.InvariantCulture, out var leverage);
        decimal.TryParse(dto.MaintMargin, NumberStyles.Number, CultureInfo.InvariantCulture, out var maintMargin);

        return new PositionSnapshot
        {
            Symbol = dto.Symbol,
            PositionAmt = qty,
            EntryPrice = entry,
            MarkPrice = mark,
            UnrealizedProfit = pnl,
            Leverage = leverage,
            MaintenanceMargin = maintMargin,
            IsIsolated = dto.Isolated
            ,PositionSide = dto.PositionSide
            ,LiquidationPrice = decimal.TryParse(dto.LiquidationPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var liq) ? liq : 0
        };
    }

    private static AccountBalance MapBalance(AccountAssetDto dto)
    {
        decimal.TryParse(dto.WalletBalance, NumberStyles.Number, CultureInfo.InvariantCulture, out var wallet);
        decimal.TryParse(dto.AvailableBalance, NumberStyles.Number, CultureInfo.InvariantCulture, out var available);
        decimal.TryParse(dto.UnrealizedProfit, NumberStyles.Number, CultureInfo.InvariantCulture, out var pnl);
        decimal.TryParse(dto.MarginBalance, NumberStyles.Number, CultureInfo.InvariantCulture, out var margin);

        return new AccountBalance
        {
            Asset = dto.Asset,
            WalletBalance = wallet,
            AvailableBalance = available,
            CrossUnrealizedPnl = pnl,
            MarginBalance = margin
        };
    }

    private static OrderResponse MapOrderResponse(OrderDto dto)
    {
        return new OrderResponse
        {
            Symbol = dto.Symbol,
            OrderId = dto.OrderId,
            ClientOrderId = dto.ClientOrderId ?? string.Empty,
            ExecutedQuantity = dto.ExecutedQty,
            CumulativeQuoteQuantity = dto.CumQuote,
            Price = dto.Price,
            AvgPrice = dto.AvgPrice,
            Status = dto.Status ?? string.Empty,
            Type = dto.Type ?? string.Empty,
            PositionSide = dto.PositionSide ?? string.Empty,
            ReduceOnly = dto.ReduceOnly,
            ClosePosition = dto.ClosePosition,
            Time = DateTimeOffset.FromUnixTimeMilliseconds(dto.UpdateTime ?? dto.Time).UtcDateTime
        };
    }

    private static TradeExecution MapTrade(UserTradeDto dto)
    {
        return new TradeExecution
        {
            Symbol = dto.Symbol,
            Side = dto.IsBuyer ? OrderSide.Buy : OrderSide.Sell,
            Quantity = dto.Qty,
            Price = dto.Price,
            Time = DateTimeOffset.FromUnixTimeMilliseconds(dto.Time).UtcDateTime
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private record FundingRateDto
    {
        public string Symbol { get; init; } = string.Empty;
        public double FundingRate { get; init; }
        public long FundingTime { get; init; }
    }

    private record MarkPriceDto
    {
        public string Symbol { get; init; } = string.Empty;
        public string ContractType { get; init; } = "PERPETUAL";
        public double MarkPrice { get; init; }
        public double IndexPrice { get; init; }
        public double EstimatedSettlePrice { get; init; }
    }

    private record TickerDto
    {
        public string Symbol { get; init; } = string.Empty;
        public double LastPrice { get; init; }
        public double PriceChangePercent { get; init; }
        public double Volume { get; init; }
        public double HighPrice { get; init; }
        public double LowPrice { get; init; }
        public double IndexPrice { get; init; }
    }

    private record AccountDto
    {
        public List<PositionDto> Positions { get; init; } = new();
        public List<AccountAssetDto> Assets { get; init; } = new();
    }

    private record PositionDto
    {
        public string Symbol { get; init; } = string.Empty;
        public string PositionAmt { get; init; } = "0";
        public string EntryPrice { get; init; } = "0";
        public string MarkPrice { get; init; } = "0";
        public string UnrealizedProfit { get; init; } = "0";
        public string Leverage { get; init; } = "0";
        public string MaintMargin { get; init; } = "0";
        public bool Isolated { get; init; }
        public string PositionSide { get; init; } = string.Empty;
        public string LiquidationPrice { get; init; } = "0";
    }

    private record AccountAssetDto
    {
        public string Asset { get; init; } = string.Empty;
        public string WalletBalance { get; init; } = "0";
        public string AvailableBalance { get; init; } = "0";
        public string MarginBalance { get; init; } = "0";
        public string UnrealizedProfit { get; init; } = "0";
    }

    private record OrderDto
    {
        public string Symbol { get; init; } = string.Empty;
        public long OrderId { get; init; }
        public string? ClientOrderId { get; init; }
        public decimal Price { get; init; }
        public decimal AvgPrice { get; init; }
        public decimal ExecutedQty { get; init; }
        public decimal CumQuote { get; init; }
        public string? Status { get; init; }
        public string? Type { get; init; }
        public string? PositionSide { get; init; }
        public bool ReduceOnly { get; init; }
        public bool ClosePosition { get; init; }
        public long Time { get; init; }
        public long? UpdateTime { get; init; }
    }

    private record UserTradeDto
    {
        public string Symbol { get; init; } = string.Empty;
        public decimal Qty { get; init; }
        public decimal Price { get; init; }
        public bool IsBuyer { get; init; }
        public long Time { get; init; }
    }

    public async Task<OrderResponse> GetOrderAsync(string symbol, long orderId, CancellationToken cancellationToken = default)
    {
        EnsureSigned();
        var query = new Dictionary<string, string?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId.ToString(CultureInfo.InvariantCulture)
        };

        var order = await SendSignedAsync<OrderDto>(HttpMethod.Get, "/fapi/v1/order", query, cancellationToken).ConfigureAwait(false);
        return MapOrderResponse(order);
    }
}
