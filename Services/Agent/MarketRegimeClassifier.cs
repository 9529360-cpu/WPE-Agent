using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public static class MarketRegimeClassifier
{
    public static MarketRegime Detect(MarketEvidence market)
    {
        ArgumentNullException.ThrowIfNull(market);
        if (!double.IsFinite(market.Trend15m) ||
            !double.IsFinite(market.Trend1h) ||
            !double.IsFinite(market.Trend4h) ||
            !double.IsFinite(market.Quality.AtrPercent) ||
            !double.IsFinite(market.Quality.LiquidationIntensity))
            return MarketRegime.Unknown;

        if (market.Quality.AtrPercent >= .05 || market.Quality.LiquidationIntensity >= .80)
            return MarketRegime.Extreme;

        var oneHour = Math.Sign(market.Trend1h);
        var fourHour = Math.Sign(market.Trend4h);
        var aligned = oneHour != 0 &&
                      oneHour == fourHour &&
                      Math.Abs(market.Trend1h) >= .003 &&
                      Math.Abs(market.Trend4h) >= .006;

        if (aligned && Math.Sign(market.Trend15m) == oneHour)
            return MarketRegime.Trending;
        if (aligned)
            return MarketRegime.Transition;
        if (Math.Abs(market.Trend1h) < .004 && Math.Abs(market.Trend4h) < .008)
            return MarketRegime.Ranging;

        return MarketRegime.Transition;
    }
}
