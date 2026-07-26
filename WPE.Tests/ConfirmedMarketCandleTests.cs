using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ConfirmedMarketCandleTests
{
    private static readonly DateTime Now=new(2026,7,27,12,7,0,DateTimeKind.Utc);

    [Fact]
    public void SelectRejectsOpenFutureMalformedAndDuplicateCandles()
    {
        var values=Series(35).ToList();
        values.Add(Candle(Now.AddMinutes(-5),100));
        values.Add(Candle(Now.AddMinutes(15),100));
        values.Add(Candle(Now.AddHours(-20),100) with{High=90});
        values.Add(values[5]);
        var selected=ConfirmedMarketCandlesV1.Select(values,"15m",Now);
        Assert.Equal(34,selected.Count);Assert.All(selected,x=>Assert.True(x.OpenTime.AddMinutes(15)<=Now));Assert.Equal(selected.Count,selected.Select(x=>x.OpenTime).Distinct().Count());
    }

    [Fact]
    public void AnalysisUsesConfirmedCloseTimeAsSourceTime()
    {
        var result=ConfirmedMarketCandlesV1.Analyze("BTCUSDT","15m",Series(40),Now);
        Assert.Equal(Now,result.Timestamp);Assert.Equal(139,result.Price);Assert.Equal(DateTimeKind.Utc,result.Timestamp.Kind);
    }

    [Fact]
    public void AnalysisFailsClosedWithoutEnoughConfirmedHistory()
        =>Assert.Throws<InvalidOperationException>(()=>ConfirmedMarketCandlesV1.Analyze("BTCUSDT","15m",Series(30),Now));

    private static IReadOnlyList<CandleEvidence> Series(int count)=>Enumerable.Range(0,count).Select(i=>Candle(Now.AddMinutes(-15*(count-i)),100+i)).ToArray();
    private static CandleEvidence Candle(DateTime open,decimal close)=>new(open,close-1,close+2,close-2,close,10,1000,10,5);
}
