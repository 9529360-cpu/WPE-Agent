using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class OpportunityBatchDecisionTests
{
    [Fact]
    public void RankedCandidatesPreferBetterOpportunityInsteadOfAlphabeticFirstMatch()
    {
        var observed=DateTime.UtcNow;
        var low=Market("AAAUSDT",100m,70,observed);
        var high=Market("ZZZUSDT",200m,98,observed);
        var evidence=Evidence(low,high);
        var tool=new FixedStructureTool(new Dictionary<string,MarketStructureRead>
        {
            [low.Symbol]=ConfirmedLong(low.Price),
            [high.Symbol]=ConfirmedLong(high.Price)
        });

        var candidates=DirectMarketStructureDecisionSkill.DecideCandidates(evidence,false,2,tool);

        Assert.Equal(2,candidates.Count);
        Assert.Equal("ZZZUSDT",candidates[0].Instrument);
        Assert.Equal("AAAUSDT",candidates[1].Instrument);
        Assert.Equal(2,candidates.Select(x=>x.Instrument).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(candidates,x=>Assert.Contains("candidate_selection=ranked-v1",x.EvidenceReferences));
    }

    [Fact]
    public void FreshBreakoutCloseCreatesSetupButDoesNotChaseTheBreakoutCandle()
    {
        var observed=DateTime.UtcNow;
        var market=Market("BTCUSDT",84_945m,98,observed);
        var tool=new FixedStructureTool(new Dictionary<string,MarketStructureRead>
        {
            [market.Symbol]=BreakoutLong(market.Price)
        });

        var decision=DirectMarketStructureDecisionSkill.Decide(Evidence(market),false,tool);

        Assert.Equal(DecisionAction.Hold,decision.Action);
        Assert.Contains("confirmation is still waiting",decision.Reason,StringComparison.Ordinal);
    }

    private static EvidencePack Evidence(params MarketEvidence[] markets)=>new()
    {
        CollectedAt=markets[0].CollectedAt,
        Completeness=100,
        Account=new AccountSnapshot(10_000m,10_000m,10_000m,markets[0].CollectedAt),
        Markets=markets.ToDictionary(x=>x.Symbol,StringComparer.OrdinalIgnoreCase)
    };

    private static MarketEvidence Market(string symbol,decimal price,int quality,DateTime observed)=>new(
        symbol,price,price-5m,price+5m,60,.4,.6,.8,new(0,0,0,0,0,0,0),observed)
    {
        Quality=new MarketQualityEvidence
        {
            QualityScore=quality,
            LiquidityScore=1,
            RelativeVolume=1.5,
            OrderFlowAvailable=true,
            OrderFlowImbalance=.8,
            SpreadBps=.5,
            BestBid=price-.1m,
            BestAsk=price,
            AtrPercent=.01
        }
    };

    private static MarketStructureRead ConfirmedLong(decimal price)
    {
        var frame=Frame(MarketStructureEvent.BullishConfirmation,price,PriceStructureState.Bullish);
        return new(
            true,MarketStructureBias.Bullish,MarketStructurePhase.BullishPullback,
            MarketStructureScenario.TrendPullbackLong,true,true,
            price-3m,price+6m,frame,frame,frame,
            "confirmed bullish continuation",[])
        {
            ConfirmationClose=price,
            ConfirmationSource="15m-closed"
        };
    }

    private static MarketStructureRead BreakoutLong(decimal price)
    {
        var frame=Frame(MarketStructureEvent.BullishBreak,price,PriceStructureState.Bullish);
        return new(
            true,MarketStructureBias.Bullish,MarketStructurePhase.BullishImpulse,
            MarketStructureScenario.BreakoutRetestLong,true,true,
            price-500m,price,frame,frame,frame,
            "fresh bullish breakout",[])
        {
            ConfirmationClose=price,
            ConfirmationSource="15m-closed"
        };
    }

    private static TimeframeStructureRead Frame(MarketStructureEvent evt,decimal price,PriceStructureState state)=>new(
        "15m",state,evt,price,price-3m,price+3m,price+1m,price,price-1m,price-2m,
        2m,true,true,false,"test");

    private sealed class FixedStructureTool(IReadOnlyDictionary<string,MarketStructureRead> values):IMarketStructureAnalysisTool
    {
        public string Name=>"test.fixed-structure";
        public MarketStructureRead Analyze(MarketEvidence market)=>values[market.Symbol];
    }
}
