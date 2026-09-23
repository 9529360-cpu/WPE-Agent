using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

internal static class CanonicalMarketEvidenceBuilder
{
    public static async Task<MarketEvidence> BuildAsync(
        string canonicalSymbol,
        Func<string,int,CancellationToken,Task<IReadOnlyList<CandleEvidence>>> getCandles,
        Func<CancellationToken,Task<DerivativesSnapshot>> getDerivatives,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSymbol);
        ArgumentNullException.ThrowIfNull(getCandles);
        ArgumentNullException.ThrowIfNull(getDerivatives);

        var observed=DateTime.UtcNow;
        var candles15Task=getCandles("15m",240,ct);
        var candles1hTask=getCandles("1h",160,ct);
        var candles4hTask=getCandles("4h",120,ct);
        var derivativesTask=getDerivatives(ct);

        await Task.WhenAll(candles15Task,candles1hTask,candles4hTask,derivativesTask);

        var c15=ConfirmedMarketCandlesV1.Select(await candles15Task,"15m",observed);
        var c1=ConfirmedMarketCandlesV1.Select(await candles1hTask,"1h",observed);
        var c4=ConfirmedMarketCandlesV1.Select(await candles4hTask,"4h",observed);
        if(c15.Count<31||c1.Count<31||c4.Count<31)
            throw new InvalidOperationException($"{canonicalSymbol} confirmed market history is incomplete");

        var sourceAt=c15[^1].OpenTime+ConfirmedMarketCandlesV1.Duration("15m");
        var price=c15[^1].Close;
        var age=observed-sourceAt;
        var returns=c15
            .Skip(1)
            .Select((candle,index)=>c15[index].Close>0
                ?Math.Log((double)(candle.Close/c15[index].Close))
                :0)
            .TakeLast(96)
            .ToArray();
        var average=returns.Length==0?0:returns.Average();
        var realized=returns.Length>1
            ?Math.Sqrt(returns.Select(value=>(value-average)*(value-average)).Average())*Math.Sqrt(96)
            :0;
        var recent=c15.TakeLast(8).Select(value=>value.QuoteVolume).DefaultIfEmpty().Average();
        var baseline=c15.TakeLast(96).Select(value=>value.QuoteVolume).DefaultIfEmpty().Average();
        var relative=baseline>0?(double)(recent/baseline):0;
        var anomalies=new List<string>();
        if(age>TimeSpan.FromMinutes(20))anomalies.Add("stale_candles");
        if(relative<=0)anomalies.Add("volume_unavailable");

        var quality=new MarketQualityEvidence
        {
            RealizedVolatility=realized,
            RelativeVolume=relative,
            LiquidityScore=Math.Clamp(relative/2,.25,1),
            SourceCount=3,
            QualityScore=anomalies.Count==0?88:62,
            Anomalies=anomalies
        };

        return new(
            canonicalSymbol,
            price,
            c15.TakeLast(40).Min(value=>value.Low),
            c15.TakeLast(40).Max(value=>value.High),
            Rsi(c15),
            Trend(c15,10),
            Trend(c1,10),
            Trend(c4,10),
            await derivativesTask,
            sourceAt)
        {
            Candles=c15,
            Candles1h=c1,
            Candles4h=c4,
            Quality=quality
        };
    }

    private static double Trend(IReadOnlyList<CandleEvidence> candles,int lookback)
    {
        var referenceIndex=candles.Count-1-lookback;
        return referenceIndex>=0&&candles[referenceIndex].Close>0
            ?(double)(candles[^1].Close/candles[referenceIndex].Close-1)
            :0;
    }

    private static double Rsi(IReadOnlyList<CandleEvidence> candles)
    {
        var values=candles.TakeLast(15).ToArray();
        var changes=values.Zip(values.Skip(1),(a,b)=>(double)(b.Close-a.Close)).ToArray();
        var gain=changes.Select(value=>Math.Max(0,value)).DefaultIfEmpty().Average();
        var loss=changes.Select(value=>Math.Max(0,-value)).DefaultIfEmpty().Average();
        return loss==0?100:100-100/(1+gain/loss);
    }
}
