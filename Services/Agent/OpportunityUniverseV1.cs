using 币安量化机器人.Models;

namespace 币安量化机器人.Services.Agent;

public sealed record OpportunityTickerV1(
    string Symbol,
    decimal LastPrice,
    decimal ApproximateQuoteVolume,
    decimal ChangePercent);

public interface IOpportunityUniverseSourceV1
{
    Task<IReadOnlyList<OpportunityTickerV1>> GetOpportunityTickersAsync(CancellationToken ct);
}

public sealed record OpportunityUniverseV1(
    IReadOnlyList<string> WatchSymbols,
    IReadOnlyList<string> DeepAnalysisSymbols,
    DateTimeOffset ObservedAtUtc,
    string Source);

public static class OpportunityUniverseSelectorV1
{
    public const int DefaultWatchLimit=80;
    public const int DefaultDeepLimit=16;
    public static readonly TimeSpan DefaultRefreshInterval=TimeSpan.FromMinutes(1);

    public static OpportunityUniverseV1 Select(
        IReadOnlyList<OpportunityTickerV1> tickers,
        IEnumerable<string> configuredSymbols,
        DateTimeOffset observedAtUtc,
        int watchLimit=DefaultWatchLimit,
        int deepLimit=DefaultDeepLimit,
        string source="provider-market-wide")
    {
        ArgumentNullException.ThrowIfNull(tickers);
        ArgumentNullException.ThrowIfNull(configuredSymbols);
        if(watchLimit<1||watchLimit>80)throw new ArgumentOutOfRangeException(nameof(watchLimit));
        if(deepLimit<1||deepLimit>16||deepLimit>watchLimit)throw new ArgumentOutOfRangeException(nameof(deepLimit));

        static string Normalize(string value)=>(value??string.Empty).Trim().ToUpperInvariant();
        var configured=configuredSymbols.Select(Normalize)
            .Where(x=>x.EndsWith("USDT",StringComparison.Ordinal)&&x.Length is >=7 and <=20&&x.All(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var available=tickers
            .Where(x=>x is not null)
            .Select(x=>x with{Symbol=Normalize(x.Symbol)})
            .Where(x=>x.Symbol.EndsWith("USDT",StringComparison.Ordinal)&&x.Symbol.Length is >=7 and <=20&&x.Symbol.All(char.IsLetterOrDigit))
            .Where(x=>x.LastPrice>0&&x.ApproximateQuoteVolume>0)
            .GroupBy(x=>x.Symbol,StringComparer.OrdinalIgnoreCase)
            .Select(x=>x.OrderByDescending(v=>v.ApproximateQuoteVolume).First())
            .ToArray();

        var byLiquidity=available
            .OrderByDescending(x=>x.ApproximateQuoteVolume)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal)
            .ToArray();

        var watch=configured
            .Concat(byLiquidity.Select(x=>x.Symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(watchLimit)
            .ToArray();

        var watchSet=watch.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watchTickers=available.Where(x=>watchSet.Contains(x.Symbol)).ToArray();
        var liquid=watchTickers
            .OrderByDescending(x=>x.ApproximateQuoteVolume)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal)
            .Select(x=>x.Symbol);
        var active=watchTickers
            .OrderByDescending(x=>Math.Abs(x.ChangePercent))
            .ThenByDescending(x=>x.ApproximateQuoteVolume)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal)
            .Select(x=>x.Symbol);

        var deep=configured
            .Concat(liquid.Take(Math.Max(1,deepLimit/2)))
            .Concat(active)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(watchSet.Contains)
            .Take(deepLimit)
            .ToArray();

        if(deep.Length==0)
            deep=watch.Take(deepLimit).ToArray();

        return new(watch,deep,observedAtUtc.ToUniversalTime(),source);
    }

    public static OpportunityUniverseV1 ConfiguredOnly(
        IEnumerable<string> configuredSymbols,
        DateTimeOffset observedAtUtc,
        string source="configured-fallback")
    {
        var symbols=configuredSymbols.Select(x=>(x??string.Empty).Trim().ToUpperInvariant())
            .Where(x=>x.Length>0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(DefaultDeepLimit)
            .ToArray();
        return new(symbols,symbols,observedAtUtc.ToUniversalTime(),source);
    }
}
