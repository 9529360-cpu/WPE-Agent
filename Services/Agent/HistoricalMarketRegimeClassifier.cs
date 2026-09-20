namespace 币安量化机器人.Services.Agent;

internal static class HistoricalMarketRegimeClassifier
{
    public static MarketRegime Detect(IReadOnlyList<CandleEvidence> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if(candles.Count<42)return MarketRegime.Unknown;

        var close=candles[^1].Close;
        if(close<=0)return MarketRegime.Unknown;
        var recent=candles.TakeLast(24).ToArray();
        var atrRatio=(double)(recent.Average(value=>value.High-value.Low)/close);
        if(!double.IsFinite(atrRatio))return MarketRegime.Unknown;
        if(atrRatio>=.05)return MarketRegime.Extreme;

        var shortTrend=Return(candles,4);
        var oneHourTrend=Return(candles,10);
        var fourHourTrend=Return(candles,40);
        if(!double.IsFinite(shortTrend)||!double.IsFinite(oneHourTrend)||!double.IsFinite(fourHourTrend))
            return MarketRegime.Unknown;

        var one=Math.Sign(oneHourTrend);
        var four=Math.Sign(fourHourTrend);
        var aligned=one!=0&&one==four&&Math.Abs(oneHourTrend)>=.003&&Math.Abs(fourHourTrend)>=.006;
        if(aligned&&Math.Sign(shortTrend)==one)return MarketRegime.Trending;
        if(aligned)return MarketRegime.Transition;
        if(Math.Abs(oneHourTrend)<.004&&Math.Abs(fourHourTrend)<.008)return MarketRegime.Ranging;
        return MarketRegime.Transition;
    }

    private static double Return(IReadOnlyList<CandleEvidence> candles,int lookback)
    {
        if(candles.Count<=lookback)return double.NaN;
        var previous=candles[candles.Count-1-lookback].Close;
        return previous<=0?double.NaN:(double)(candles[^1].Close/previous-1m);
    }
}
