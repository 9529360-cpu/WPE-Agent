using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MediumHorizonAgentTests
{
    private static readonly DateTime Observed=new(2026,9,28,0,5,0,DateTimeKind.Utc);

    [Fact]
    public void MissingDailyHistoryCannotCreateRiskIncreasingDecision()
    {
        var market=ActionableMarket();
        market=market with{Candles1d=Array.Empty<CandleEvidence>()};

        var decision=MediumHorizonDecisionSkill.DecideMarket(market);

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Contains("Daily/4h confirmed candle history is incomplete",decision.Reason,StringComparison.Ordinal);
    }

    [Fact]
    public void DailyAndFourHourConflictCannotTrade()
    {
        var market=ActionableMarket();
        market=market with{Candles4h=Trend(new DateTime(2026,9,8,0,0,0,DateTimeKind.Utc),TimeSpan.FromHours(4),120,140m,-.20m,1.5m)};

        var decision=MediumHorizonDecisionSkill.DecideMarket(market);

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Contains("not aligned with the daily regime",decision.Reason,StringComparison.Ordinal);
    }

    [Fact]
    public void ClosedDailyAndFourHourPullbackConfirmationCanOpenWithBoundedRiskBudget()
    {
        var market=ActionableMarket();

        var decision=MediumHorizonDecisionSkill.DecideMarket(market);

        Assert.Equal(DecisionAction.OpenLong,decision.Action);
        Assert.Equal(MediumHorizonDecisionSkill.DecisionContextKind,decision.DecisionContextKind);
        Assert.Equal(MediumHorizonDecisionSkill.Version,decision.StrategyVersion);
        Assert.True(decision.RiskRewardRatio>=MediumHorizonDecisionSkill.MinimumRiskReward);
        Assert.InRange(decision.RiskBudgetFraction,.0035m,.0085m);
        Assert.Contains("direction_authority=daily-plus-4h-only",decision.EvidenceReferences);
        Assert.Contains("minute_data_role=execution-only",decision.EvidenceReferences);
    }

    [Fact]
    public void IntradayCandlesHaveNoDirectionalAuthority()
    {
        var market=ActionableMarket();
        var price=market.Price;
        var calm=market with{Candles=FlatIntraday(price),Candles1h=FlatIntraday(price)};
        var violentOpposite=market with{Candles=DownIntraday(price),Candles1h=DownIntraday(price)};

        var a=MediumHorizonDecisionSkill.DecideMarket(calm);
        var b=MediumHorizonDecisionSkill.DecideMarket(violentOpposite);

        Assert.Equal(DecisionAction.OpenLong,a.Action);
        Assert.Equal(a.Action,b.Action);
        Assert.Equal(a.DecisionContextId,b.DecisionContextId);
        Assert.Equal(a.EntryPrice,b.EntryPrice);
        Assert.Equal(a.StopLossPrice,b.StopLossPrice);
        Assert.Equal(a.TakeProfitPrice,b.TakeProfitPrice);
    }

    [Fact]
    public void OrdinaryThesisExitRequiresAFullPostEntryFourHourBar()
    {
        var market=ActionableMarket();
        var confirmed=ConfirmedMarketCandlesV1.Select(market.Candles4h,"4h",market.CollectedAt);
        var last=confirmed[^1];
        var lastOpen=new DateTimeOffset(last.OpenTime,TimeSpan.Zero);

        Assert.False(MediumHorizonDecisionSkill.HasFullPostEntryFourHourBar(lastOpen.AddHours(1),market));

        var extended=market with
        {
            CollectedAt=market.CollectedAt.AddHours(4),
            Candles4h=market.Candles4h.Concat([
                Candle(last.OpenTime.AddHours(4),last.Close,last.Close+1.5m,last.Close-.5m,last.Close+1m)
            ]).ToArray()
        };
        Assert.True(MediumHorizonDecisionSkill.HasFullPostEntryFourHourBar(lastOpen.AddHours(1),extended));
    }

    private static MarketEvidence ActionableMarket()
    {
        var daily=Trend(new DateTime(2026,6,1,0,0,0,DateTimeKind.Utc),TimeSpan.FromDays(1),118,80m,.45m,2.4m);
        var h4=Trend(new DateTime(2026,9,8,0,0,0,DateTimeKind.Utc),TimeSpan.FromHours(4),115,100m,.16m,1.35m).ToList();
        var anchor=h4[^7].Close;
        h4.RemoveRange(h4.Count-6,6);
        var time=h4[^1].OpenTime.AddHours(4);
        h4.Add(Candle(time,anchor+.2m,anchor+.7m,anchor-1.5m,anchor-.9m));time=time.AddHours(4);
        h4.Add(Candle(time,anchor-.9m,anchor-.3m,anchor-1.8m,anchor-1.2m));time=time.AddHours(4);
        h4.Add(Candle(time,anchor-1.2m,anchor-.2m,anchor-1.6m,anchor-.4m));time=time.AddHours(4);
        h4.Add(Candle(time,anchor-.4m,anchor+.5m,anchor-.8m,anchor+.2m));time=time.AddHours(4);
        h4.Add(Candle(time,anchor+.2m,anchor+.9m,anchor-.2m,anchor+.5m));time=time.AddHours(4);
        h4.Add(Candle(time,anchor+.4m,anchor+2.1m,anchor+.3m,anchor+1.9m));
        var price=h4[^1].Close;
        return new(
            "ZZZUSDT",price,price*.95m,price*1.12m,55,0,0,0,
            new DerivativesSnapshot(0,0,0,0,0,0,0),Observed)
        {
            Candles=FlatIntraday(price),
            Candles1h=FlatIntraday(price),
            Candles4h=h4,
            Candles1d=daily,
            Quality=new MarketQualityEvidence
            {
                QualityScore=95,LiquidityScore=.95,SpreadBps=2,
                BestBid=price-.01m,BestAsk=price+.01m,AtrPercent=.02
            }
        };
    }

    private static List<CandleEvidence> Trend(DateTime start,TimeSpan step,int count,decimal first,decimal delta,decimal range)
    {
        var result=new List<CandleEvidence>();
        for(var i=0;i<count;i++)
        {
            var close=first+i*delta;
            var open=close-delta*.3m;
            result.Add(Candle(start+TimeSpan.FromTicks(step.Ticks*i),open,Math.Max(open,close)+range,Math.Min(open,close)-range,close));
        }
        return result;
    }

    private static IReadOnlyList<CandleEvidence> FlatIntraday(decimal price)=>
        Trend(new DateTime(2026,9,26,0,0,0,DateTimeKind.Utc),TimeSpan.FromMinutes(15),100,price-.2m,.002m,.1m);

    private static IReadOnlyList<CandleEvidence> DownIntraday(decimal price)=>
        Trend(new DateTime(2026,9,26,0,0,0,DateTimeKind.Utc),TimeSpan.FromMinutes(15),100,price+5m,-.04m,.2m);

    private static CandleEvidence Candle(DateTime time,decimal open,decimal high,decimal low,decimal close)=>
        new(time,open,high,low,close,1000m,100000m,100,500m);
}