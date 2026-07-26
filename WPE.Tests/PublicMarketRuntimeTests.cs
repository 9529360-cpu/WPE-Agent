using 币安量化机器人.Core.MarketData;
using 币安量化机器人.Services;
using 币安量化机器人.Services.MarketData;

namespace WPE.Tests;

public sealed class PublicMarketRuntimeTests
{
    [Fact]
    public async Task BinanceSource_DisposesOwnedApiClientAndIsIdempotent()
    {
        var handler = new TrackingHandler();
        var api = Api(handler);
        await using var stream = new BinanceStreamClient();
        var source = new BinancePublicMarketSource(api, stream, ownsApiClient: true);

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task BinanceSource_DoesNotDisposeExplicitlySharedApiClient()
    {
        var handler = new TrackingHandler();
        using var api = Api(handler);
        await using var stream = new BinanceStreamClient();
        var source = new BinancePublicMarketSource(api, stream, ownsApiClient: false);

        await source.DisposeAsync();

        Assert.Equal(0, handler.DisposeCount);
    }

    [Fact]
    public async Task StartsWithoutCredentialsSetupOrAccessAndUsesOnlyPublicSource()
    {
        var source = new FakeSource
        {
            Tickers = [new("BTCUSDT", 62000m, 1.25m, 1234m)],
            Closes = [60000m, 62000m]
        };
        var cache = new FakeCache();
        var clock = new FakeClock(new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(source, cache, clock);

        await runtime.StartAsync();

        var state = runtime.Read();
        Assert.True(runtime.IsRunning);
        Assert.Equal(1, source.StreamStarts);
        Assert.Equal(1, source.TickerReads);
        Assert.Equal(1, source.KlineReads);
        Assert.Equal(PublicMarketHealthState.Degraded, state.Health.State);
        Assert.True(state.Health.RestAvailable);
        Assert.False(state.Health.StreamAvailable);
        Assert.Equal(62000m, Assert.Single(state.Tickers).Price);
        var kline = Assert.Single(state.Klines);
        Assert.Equal(62000m, kline.Price);
        Assert.Equal(3.3333333333333333333333333300m, kline.ChangePercent);
        Assert.Null(kline.Volume);
        Assert.Equal(1, cache.Writes);
    }

    [Fact]
    public async Task UnknownAndStaleAreExplicitAndNeverInventValues()
    {
        var source = new FakeSource { FailReads = true };
        var clock = new FakeClock(new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(source, new FakeCache(), clock);
        await runtime.StartAsync();

        var unknown = runtime.Read();
        Assert.Equal(PublicMarketDataState.Unknown, Assert.Single(unknown.Tickers).State);
        Assert.Null(unknown.Tickers[0].Price);
        Assert.Equal("public-market.rest-unavailable", unknown.Health.DiagnosticCode);
        Assert.Equal(PublicMarketHealthState.Degraded, unknown.Health.State);

        source.FailReads = false;
        source.Tickers = [new("BTCUSDT", 10m, -2m, 50m)];
        source.Closes = [9m, 10m];
        await runtime.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));

        var stale = runtime.Read();
        Assert.Equal(PublicMarketDataState.Stale, stale.Tickers[0].State);
        Assert.True(stale.Tickers[0].Stale);
        Assert.Equal(PublicMarketHealthState.Degraded, stale.Health.State);
    }

    [Fact]
    public async Task RecoversAfterFailureAndAcceptsPublicTickerStreamUpdates()
    {
        var source = new FakeSource { FailReads = true };
        var clock = new FakeClock(new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(source, new FakeCache(), clock);
        await runtime.StartAsync();

        source.FailReads = false;
        source.Tickers = [new("BTCUSDT", 20m, 1m, 100m)];
        source.Closes = [19m, 20m];
        await runtime.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        source.Emit(new("BTCUSDT", 21m, 2m, 110m));

        var state = runtime.Read();
        Assert.Equal(PublicMarketHealthState.Healthy, state.Health.State);
        Assert.True(state.Health.StreamAvailable);
        Assert.Equal(21m, state.Tickers[0].Price);
        Assert.Equal("binance-futures-public-websocket", state.Tickers[0].Source);
    }

    [Fact]
    public async Task StopCancelsPollingAndStopsOnlyPublicTickerSource()
    {
        var source = new FakeSource { Tickers = [new("BTCUSDT", 1m, 0m, 0m)], Closes = [1m] };
        var delay = new BlockingDelay();
        var runtime = new PublicMarketRuntime(["BTCUSDT"], source, new FakeCache(),
            () => DateTimeOffset.UtcNow, delay.WaitAsync, refreshInterval: TimeSpan.FromSeconds(1));
        await runtime.StartAsync();

        await runtime.StopAsync();

        Assert.False(runtime.IsRunning);
        Assert.Equal(1, source.Stops);
        Assert.True(delay.Cancelled);
        Assert.Equal(PublicMarketHealthState.Stopped, runtime.Read().Health.State);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task StartCancellationStopsRuntimeWithoutPrivateStreamWork()
    {
        var source = new FakeSource { Tickers = [new("BTCUSDT", 1m, 0m, 0m)], Closes = [1m] };
        await using var runtime = Create(source, new FakeCache(), new(new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)));
        using var cts = new CancellationTokenSource();
        await runtime.StartAsync(cts.Token);

        cts.Cancel();
        await WaitUntilAsync(() => !runtime.IsRunning);

        Assert.False(runtime.IsRunning);
        Assert.Equal(1, source.StreamStarts);
        Assert.Equal(1, source.Stops);
    }

    [Fact]
    public async Task InjectedSourceRemainsCallerOwned()
    {
        var source = new FakeSource { Tickers = [new("BTCUSDT", 1m, 0m, 0m)], Closes = [1m] };
        var runtime = Create(source, new FakeCache(), new(new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)));

        await runtime.DisposeAsync();

        Assert.Equal(0, source.DisposeCount);
        await source.DisposeAsync();
        Assert.Equal(1, source.DisposeCount);
    }

    private static PublicMarketRuntime Create(FakeSource source, FakeCache cache, FakeClock clock) =>
        new(["BTCUSDT"], source, cache, clock.Get, (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            refreshInterval: TimeSpan.FromHours(1), staleAfter: TimeSpan.FromMinutes(2));

    private static BinanceApiClient Api(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://public-market.invalid") });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    private sealed class FakeSource : IPublicMarketSource, IAsyncDisposable
    {
        public event Action<PublicTickerValue>? TickerReceived;
        public IReadOnlyList<PublicTickerValue> Tickers { get; set; } = [];
        public IReadOnlyList<decimal> Closes { get; set; } = [];
        public bool FailReads { get; set; }
        public int TickerReads { get; private set; }
        public int KlineReads { get; private set; }
        public int StreamStarts { get; private set; }
        public int Stops { get; private set; }
        public int DisposeCount { get; private set; }
        public Task<IReadOnlyList<PublicTickerValue>> GetTickersAsync(IReadOnlyList<string> symbols, CancellationToken ct)
        {
            TickerReads++;
            return FailReads ? Task.FromException<IReadOnlyList<PublicTickerValue>>(new HttpRequestException("offline")) : Task.FromResult(Tickers);
        }
        public Task<IReadOnlyList<decimal>> GetKlineClosesAsync(string symbol, string interval, int limit, CancellationToken ct)
        {
            KlineReads++;
            return FailReads ? Task.FromException<IReadOnlyList<decimal>>(new HttpRequestException("offline")) : Task.FromResult(Closes);
        }
        public Task StartTickerStreamAsync(IReadOnlyList<string> symbols, CancellationToken ct) { StreamStarts++; return Task.CompletedTask; }
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
        public void Emit(PublicTickerValue ticker) => TickerReceived?.Invoke(ticker);
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeCache : IPublicMarketCache
    {
        public int Writes { get; private set; }
        public Task SaveKlineClosesAsync(string symbol, IReadOnlyList<decimal> closes) { Writes++; return Task.CompletedTask; }
    }

    private sealed class FakeClock(DateTimeOffset now)
    {
        public DateTimeOffset Get() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class BlockingDelay
    {
        public bool Cancelled { get; private set; }
        public async Task WaitAsync(TimeSpan _, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network access is forbidden in this test.");
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
