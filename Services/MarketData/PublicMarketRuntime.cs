using 币安量化机器人.Core.MarketData;
using 币安量化机器人.Models;
using 币安量化机器人.Services.Exchange.Binance;

namespace 币安量化机器人.Services.MarketData;

internal sealed record PublicTickerValue(string Symbol, decimal Price, decimal ChangePercent, decimal Volume);

internal interface IPublicMarketSource
{
    event Action<PublicTickerValue>? TickerReceived;
    Task<IReadOnlyList<PublicTickerValue>> GetTickersAsync(IReadOnlyList<string> symbols, CancellationToken ct);
    Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit, CancellationToken ct);
    Task StartTickerStreamAsync(IReadOnlyList<string> symbols, CancellationToken ct);
    Task StopAsync();
}

internal interface IPublicMarketCache
{
    Task SaveKlineClosesAsync(string symbol, IReadOnlyList<decimal> closes);
}

internal sealed class BinancePublicMarketSource : IPublicMarketSource, IAsyncDisposable
{
    private readonly IBinancePublicMarketTransport _api;
    private readonly BinanceStreamClient _stream;
    private readonly bool _ownsApiClient;
    private int _disposed;

    internal BinancePublicMarketSource(IBinancePublicMarketTransport api, BinanceStreamClient stream, bool ownsApiClient)
    {
        _api = api;
        _stream = stream;
        _ownsApiClient = ownsApiClient;
        _stream.MiniTickerReceived += OnTicker;
    }

    public event Action<PublicTickerValue>? TickerReceived;

    public async Task<IReadOnlyList<PublicTickerValue>> GetTickersAsync(IReadOnlyList<string> symbols, CancellationToken ct) =>
        (await _api.GetMiniTickersAsync(symbols, ct).ConfigureAwait(false))
        .Select(Map)
        .ToArray();

    public Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit, CancellationToken ct) =>
        _api.GetKlineClosesAsync(symbol, interval, limit, ct);

    public Task StartTickerStreamAsync(IReadOnlyList<string> symbols, CancellationToken ct) =>
        _stream.ConnectMiniTickerAsync(symbols, ct);

    public Task StopAsync() => _stream.StopAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stream.MiniTickerReceived -= OnTicker;
        try
        {
            await _stream.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_ownsApiClient && _api is IDisposable disposable) disposable.Dispose();
        }
    }

    private void OnTicker(MiniTickerUpdate update) => TickerReceived?.Invoke(new(
        update.Symbol.ToUpperInvariant(),
        Convert.ToDecimal(update.LastPrice),
        Convert.ToDecimal(update.ChangePercent),
        Convert.ToDecimal(update.Volume)));

    private static PublicTickerValue Map(TickerQuote quote) => new(
        quote.Symbol.ToUpperInvariant(),
        Convert.ToDecimal(quote.LastPrice),
        Convert.ToDecimal(quote.ChangePercent),
        Convert.ToDecimal(quote.Volume));
}

internal sealed class BinancePublicMarketCache(DataCacheService cache) : IPublicMarketCache
{
    public Task SaveKlineClosesAsync(string symbol, IReadOnlyList<decimal> closes) => cache.SavePricesAsync(symbol, closes);
}

public sealed class PublicMarketRuntime : IPublicMarketRuntime
{
    public static WpeAgent.FinancialEvidence.AuthorizedFixtureProjectionV1<PublicTickerSnapshot> CollectAuthorizedLocalFixtures(
        IReadOnlyList<WpeAgent.FinancialEvidence.FinancialEvidenceRecordV1>? fixtures,
        WpeAgent.FinancialEvidence.FinancialEvidenceRetrievalRequestV1 request)
    {
        var corpus = new WpeAgent.FinancialEvidence.AuthorizedLocalCorpusV1().Collect(fixtures, request);
        if (!corpus.Accepted) return new(false, [], corpus.ReasonCodes);
        try
        {
            var items = corpus.Records.Select(record =>
            {
                using var payload = System.Text.Json.JsonDocument.Parse(record.Draft.Payload);
                var root = payload.RootElement;
                var symbol = root.GetProperty("symbol").GetString();
                var price = root.GetProperty("price").GetDecimal();
                var change = root.GetProperty("changePercent").GetDecimal();
                var volume = root.GetProperty("volume").GetDecimal();
                if (string.IsNullOrWhiteSpace(symbol) || price <= 0 || volume < 0) throw new System.Text.Json.JsonException();
                return new PublicTickerSnapshot(symbol.Trim().ToUpperInvariant(), price, change, volume, record.Draft.SourceProvider, record.ObservedAt, false, PublicMarketDataState.Available);
            }).OrderBy(x => x.Symbol, StringComparer.Ordinal).ToArray();
            return new(true, items, []);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            return new(false, [], ["evidence.payload-malformed"]);
        }
    }

    private const string RestSource = "binance-futures-public-rest";
    private const string StreamSource = "binance-futures-public-websocket";
    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _symbols;
    private readonly string _interval;
    private readonly IPublicMarketSource _source;
    private readonly bool _ownsSource;
    private readonly IPublicMarketCache _cache;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _staleAfter;
    private readonly Dictionary<string, PublicTickerSnapshot> _tickers;
    private readonly Dictionary<string, PublicKlineSnapshot> _klines;
    private CancellationTokenSource? _runCts;
    private CancellationTokenRegistration _cancellationRegistration;
    private Task? _pollTask;
    private bool _restAvailable;
    private bool _streamAvailable;
    private DateTimeOffset? _lastSuccessAt;
    private string? _diagnosticCode;

    public PublicMarketRuntime(
        IEnumerable<string> symbols,
        BinancePublicMarketClient api,
        BinanceStreamClient stream,
        DataCacheService cache,
        string interval = "1m",
        TimeSpan? refreshInterval = null,
        TimeSpan? staleAfter = null,
        bool ownsApiClient = true)
        : this(symbols, new BinancePublicMarketSource(api, stream, ownsApiClient), new BinancePublicMarketCache(cache),
            () => DateTimeOffset.UtcNow, Task.Delay, interval, refreshInterval, staleAfter, ownsSource: true)
    {
    }

    internal PublicMarketRuntime(
        IEnumerable<string> symbols,
        IPublicMarketSource source,
        IPublicMarketCache cache,
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        string interval = "1m",
        TimeSpan? refreshInterval = null,
        TimeSpan? staleAfter = null,
        bool ownsSource = false)
    {
        _symbols = symbols.Select(NormalizeSymbol).Distinct(StringComparer.Ordinal).ToArray();
        if (_symbols.Count == 0) throw new ArgumentException("At least one public market symbol is required.", nameof(symbols));
        if (string.IsNullOrWhiteSpace(interval)) throw new ArgumentException("Kline interval is required.", nameof(interval));
        _source = source;
        _ownsSource = ownsSource;
        _cache = cache;
        _clock = clock;
        _delay = delay;
        _interval = interval;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(1);
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(2);
        _tickers = _symbols.ToDictionary(x => x, UnknownTicker, StringComparer.Ordinal);
        _klines = _symbols.ToDictionary(x => x, x => UnknownKline(x, _interval), StringComparer.Ordinal);
        _source.TickerReceived += OnTickerReceived;
    }

    public bool IsRunning { get { lock (_gate) return _runCts is not null; } }
    public event Action<PublicMarketState>? StateChanged;

    public PublicMarketState Read()
    {
        lock (_gate) return SnapshotLocked();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource runCts;
        lock (_gate)
        {
            if (_runCts is not null) return;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCts = _runCts;
            _cancellationRegistration = runCts.Token.Register(() => _ = StopAsync());
            _diagnosticCode = null;
        }
        Publish();

        await RefreshAsync(runCts.Token).ConfigureAwait(false);
        try
        {
            await _source.StartTickerStreamAsync(_symbols, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            await StopAsync().ConfigureAwait(false);
            return;
        }
        catch
        {
            lock (_gate) { _streamAvailable = false; _diagnosticCode = "public-market.stream-unavailable"; }
            Publish();
        }

        lock (_gate) _pollTask = PollAsync(runCts.Token);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        var restSucceeded = false;
        try
        {
            var tickers = await _source.GetTickersAsync(_symbols, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var ticker in tickers.Where(IsValidTicker))
                    _tickers[ticker.Symbol] = new(ticker.Symbol, ticker.Price, ticker.ChangePercent, ticker.Volume, RestSource, now, false, PublicMarketDataState.Available);
            }

            foreach (var symbol in _symbols)
            {
                var closes = await _source.GetKlineClosesAsync(symbol, _interval, 120, cancellationToken).ConfigureAwait(false);
                if (closes.Count == 0 || closes.Any(x => x <= 0)) continue;
                decimal? change = closes.Count < 2 ? null : (closes[^1] - closes[0]) / closes[0] * 100m;
                lock (_gate)
                    _klines[symbol] = new(symbol, _interval, closes[^1], change, null, RestSource, now, false, PublicMarketDataState.Available);
                try { await _cache.SaveKlineClosesAsync(symbol, closes).ConfigureAwait(false); }
                catch { lock (_gate) _diagnosticCode = "public-market.cache-write-failed"; }
            }

            restSucceeded = true;
            lock (_gate) { _restAvailable = true; _lastSuccessAt = now; if (_diagnosticCode == "public-market.rest-unavailable") _diagnosticCode = null; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            lock (_gate) { _restAvailable = false; _diagnosticCode = "public-market.rest-unavailable"; }
        }
        finally
        {
            lock (_gate) MarkStaleLocked(now);
            Publish();
        }

        if (!restSucceeded) return;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? poll;
        lock (_gate) { cts = _runCts; poll = _pollTask; _runCts = null; _pollTask = null; }
        if (cts is null) return;
        cts.Cancel();
        try { await _source.StopAsync().ConfigureAwait(false); } catch { }
        if (poll is not null)
        {
            try { await poll.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cts.Dispose();
        _cancellationRegistration.Dispose();
        lock (_gate) { _streamAvailable = false; _restAvailable = false; _diagnosticCode = null; }
        Publish();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _source.TickerReceived -= OnTickerReceived;
        if (_ownsSource && _source is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _delay(_refreshInterval, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
    }

    private void OnTickerReceived(PublicTickerValue ticker)
    {
        if (!IsValidTicker(ticker) || !_symbols.Contains(ticker.Symbol, StringComparer.Ordinal)) return;
        var now = _clock();
        lock (_gate)
        {
            _tickers[ticker.Symbol] = new(ticker.Symbol, ticker.Price, ticker.ChangePercent, ticker.Volume, StreamSource, now, false, PublicMarketDataState.Available);
            _streamAvailable = true;
            _lastSuccessAt = now;
            if (_diagnosticCode == "public-market.stream-unavailable") _diagnosticCode = null;
        }
        Publish();
    }

    private PublicMarketState SnapshotLocked()
    {
        var now = _clock();
        MarkStaleLocked(now);
        var running = _runCts is not null;
        var values = _tickers.Values.Cast<object>().Concat(_klines.Values).ToArray();
        var available = values.Count(x => x switch
        {
            PublicTickerSnapshot t => t.State == PublicMarketDataState.Available,
            PublicKlineSnapshot k => k.State == PublicMarketDataState.Available,
            _ => false
        });
        var healthState = !running ? PublicMarketHealthState.Stopped
            : available == values.Length && _restAvailable && _streamAvailable ? PublicMarketHealthState.Healthy
            : available == 0 && _lastSuccessAt is null && _diagnosticCode is null ? PublicMarketHealthState.Starting
            : PublicMarketHealthState.Degraded;
        return new(
            _tickers.Values.OrderBy(x => x.Symbol).ToArray(),
            _klines.Values.OrderBy(x => x.Symbol).ToArray(),
            new(healthState, _restAvailable, _streamAvailable, _lastSuccessAt, _diagnosticCode));
    }

    private void MarkStaleLocked(DateTimeOffset now)
    {
        foreach (var symbol in _symbols)
        {
            var ticker = _tickers[symbol];
            if (ticker.UpdatedAt is not null && now - ticker.UpdatedAt > _staleAfter)
                _tickers[symbol] = ticker with { Stale = true, State = PublicMarketDataState.Stale };
            var kline = _klines[symbol];
            if (kline.UpdatedAt is not null && now - kline.UpdatedAt > _staleAfter)
                _klines[symbol] = kline with { Stale = true, State = PublicMarketDataState.Stale };
        }
    }

    private void Publish()
    {
        var handler = StateChanged;
        if (handler is not null) handler(Read());
    }

    private static bool IsValidTicker(PublicTickerValue ticker) =>
        !string.IsNullOrWhiteSpace(ticker.Symbol) && ticker.Price > 0 && ticker.Volume >= 0;
    private static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();
    private static PublicTickerSnapshot UnknownTicker(string symbol) => new(symbol, null, null, null, "unknown", null, false, PublicMarketDataState.Unknown);
    private static PublicKlineSnapshot UnknownKline(string symbol, string interval) => new(symbol, interval, null, null, null, "unknown", null, false, PublicMarketDataState.Unknown);
}
