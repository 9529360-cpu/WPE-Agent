namespace 币安量化机器人.Services.Agent;

using 币安量化机器人.Core.Strategy;

internal static class NewsMomentumScoring
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(48);
    private const int MaximumItems = 5;

    internal static double Live(string symbol,IReadOnlyList<NewsEvidence> news,DateTime asOfUtc)
    {
        var asOf=Utc(asOfUtc);
        return Score(symbol,news.Select(value=>new NewsPoint(
            value.AffectedAssets,
            Utc(value.PublishedAt??value.CollectedAt),
            value.Sentiment,
            value.Confidence)),asOf);
    }

    internal static double Historical(string symbol,IReadOnlyList<NewsFeature> news,DateTime asOfUtc)
    {
        var asOf=Utc(asOfUtc);
        return Score(symbol,news.Select(value=>new NewsPoint(
            [value.Asset],
            Utc(value.PublishedAtUtc),
            value.Sentiment,
            value.Confidence)),asOf);
    }

    private static double Score(string symbol,IEnumerable<NewsPoint> values,DateTimeOffset asOf)
    {
        if(string.IsNullOrWhiteSpace(symbol))return 0;
        var relevant=values
            .Where(value=>value.At<=asOf&&asOf-value.At<=MaximumAge)
            .Where(value=>value.Assets.Any(asset=>Matches(symbol,asset)))
            .Where(value=>double.IsFinite(value.Sentiment)&&double.IsFinite(value.Confidence))
            .OrderByDescending(value=>value.At)
            .Take(MaximumItems)
            .Select(value=>
            {
                var age=(asOf-value.At).TotalSeconds;
                var freshness=Math.Clamp(1-age/MaximumAge.TotalSeconds,0,1);
                return Math.Clamp(value.Sentiment,-1,1)*Math.Clamp(value.Confidence,0,1)*freshness;
            })
            .ToArray();
        return relevant.Length==0?0:Math.Clamp(relevant.Average(),-1,1);
    }

    private static bool Matches(string symbol,string? asset)
    {
        if(string.IsNullOrWhiteSpace(asset))return false;
        var normalized=asset.Trim();
        return symbol.Equals(normalized,StringComparison.OrdinalIgnoreCase)
               ||symbol.StartsWith(normalized,StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset Utc(DateTime value)
    {
        var utc=value.Kind switch
        {
            DateTimeKind.Utc=>value,
            DateTimeKind.Local=>value.ToUniversalTime(),
            _=>DateTime.SpecifyKind(value,DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    private sealed record NewsPoint(IReadOnlyList<string> Assets,DateTimeOffset At,double Sentiment,double Confidence);
}
