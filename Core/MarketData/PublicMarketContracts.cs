namespace 币安量化机器人.Core.MarketData;

public enum PublicMarketDataState
{
    Unknown,
    Available,
    Stale
}

public enum PublicMarketHealthState
{
    Stopped,
    Starting,
    Healthy,
    Degraded
}

public sealed record PublicTickerSnapshot(
    string Symbol,
    decimal? Price,
    decimal? ChangePercent,
    decimal? Volume,
    string Source,
    DateTimeOffset? UpdatedAt,
    bool Stale,
    PublicMarketDataState State);

public sealed record PublicKlineSnapshot(
    string Symbol,
    string Interval,
    decimal? Price,
    decimal? ChangePercent,
    decimal? Volume,
    string Source,
    DateTimeOffset? UpdatedAt,
    bool Stale,
    PublicMarketDataState State);

public sealed record PublicMarketHealth(
    PublicMarketHealthState State,
    bool RestAvailable,
    bool StreamAvailable,
    DateTimeOffset? LastSuccessAt,
    string? DiagnosticCode);

public sealed record PublicMarketState(
    IReadOnlyList<PublicTickerSnapshot> Tickers,
    IReadOnlyList<PublicKlineSnapshot> Klines,
    PublicMarketHealth Health);

public interface IPublicMarketRuntime : IAsyncDisposable
{
    bool IsRunning { get; }
    PublicMarketState Read();
    event Action<PublicMarketState>? StateChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

