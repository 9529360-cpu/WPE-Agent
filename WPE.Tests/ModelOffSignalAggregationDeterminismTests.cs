using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffSignalAggregationDeterminismTests
{
    private static readonly DateTimeOffset Now=new(2026,7,23,14,0,0,TimeSpan.Zero);

    [Fact]
    public void InjectedClockPreservesExistingAnalyzeSignature()
    {
        var skill=new SignalAggregationSkill(()=>Now);var result=skill.Analyze(Pack(Market("BTCUSDT",Now.AddMinutes(-1).UtcDateTime)),Policy());
        Assert.Single(result);Assert.True(result[0].Fresh);
    }

    [Fact]
    public void ExplicitEvaluationTimeMustBeUtcAndNonDefault()
    {
        var skill=new SignalAggregationSkill();var evidence=Pack(Market("BTCUSDT",Now.UtcDateTime));
        Assert.Throws<ArgumentException>(()=>skill.Analyze(evidence,Policy(),default(DateTimeOffset)));
        Assert.Throws<ArgumentException>(()=>skill.Analyze(evidence,Policy(),Now.ToOffset(TimeSpan.FromHours(9))));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("non-utc")]
    [InlineData("default-time")]
    [InlineData("non-finite")]
    public void InvalidOrOutOfWindowEvidenceFailsClosed(string fixture)
    {
        var collected=fixture switch
        {
            "future"=>Now.AddSeconds(1).UtcDateTime,
            "stale"=>Now.AddMinutes(-6).UtcDateTime,
            "non-utc"=>DateTime.SpecifyKind(Now.UtcDateTime,DateTimeKind.Unspecified),
            "default-time"=>default,
            _=>Now.AddMinutes(-1).UtcDateTime
        };
        var market=Market("BTCUSDT",collected,fixture=="non-finite"?double.NaN:.01);
        var assessment=new SignalAggregationSkill().Analyze(Pack(market),Policy(),Now).Single();
        Assert.False(assessment.Fresh);Assert.False(assessment.EntryReady);Assert.Equal(DecisionAction.Hold,assessment.RecommendedAction);
    }

    [Fact]
    public void ExactFreshnessBoundaryIsAcceptedButFutureIsNot()
    {
        var skill=new SignalAggregationSkill();
        Assert.True(skill.Analyze(Pack(Market("BTCUSDT",Now.AddMinutes(-5).UtcDateTime)),Policy(),Now).Single().Fresh);
        Assert.False(skill.Analyze(Pack(Market("BTCUSDT",Now.AddTicks(1).UtcDateTime)),Policy(),Now).Single().Fresh);
    }

    [Fact]
    public void NullMarketFixtureFailsClosedInsteadOfThrowing()
    {
        var evidence=new EvidencePack{Completeness=100,Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",null!}}};
        var assessment=new SignalAggregationSkill().Analyze(evidence,Policy(),Now).Single();
        Assert.Equal("UNKNOWN",assessment.Symbol);Assert.False(assessment.EntryReady);Assert.Equal(DecisionAction.Hold,assessment.RecommendedAction);
    }

    [Fact]
    public void MarketMutationAfterCanonicalBindingFailsClosed()
    {
        var market=Market("BTCUSDT",Now.AddMinutes(-1).UtcDateTime);var altered=market with{Trend15m=market.Trend15m+1};var assessment=new SignalAggregationSkill().Analyze(Pack(altered),Policy(),Now).Single();Assert.False(assessment.EntryReady);Assert.Equal(DecisionAction.Hold,assessment.RecommendedAction);Assert.Contains("signal.invalid-market-evidence",assessment.MissingConditions);
    }

    [Fact]
    public void ProductionAggregationCarriesExactActiveStrategyIdentity()
    {
        var signal=new 币安量化机器人.Core.Strategy.StrategySignal("strategy-alpha","BTCUSDT",1,.8,"qualified","candidate-v7");
        var assessment=new SignalAggregationSkill().Analyze(
            Pack(Market("BTCUSDT",Now.AddMinutes(-1).UtcDateTime)),Policy(),Now,
            new Dictionary<string,币安量化机器人.Core.Strategy.StrategySignal>{{"BTCUSDT",signal}}).Single();

        Assert.Equal("strategy-alpha",assessment.StrategyId);
        Assert.Equal("candidate-v7",assessment.StrategyVersion);
        Assert.True(assessment.EntryReady);
    }

    [Fact]
    public void ProductionAggregationFailsClosedWithoutExactActiveStrategySignal()
    {
        var assessment=new SignalAggregationSkill().Analyze(
            Pack(Market("BTCUSDT",Now.AddMinutes(-1).UtcDateTime)),Policy(),Now,
            new Dictionary<string,币安量化机器人.Core.Strategy.StrategySignal>()).Single();

        Assert.False(assessment.EntryReady);
        Assert.Equal(DecisionAction.Hold,assessment.RecommendedAction);
        Assert.Contains("strategy.active-signal-required",assessment.MissingConditions);
        Assert.Empty(assessment.StrategyId);
        Assert.Empty(assessment.StrategyVersion);
    }

    [Fact]
    public void IdenticalInputsAndTimeProduceIdenticalOrderedAssessments()
    {
        var first=Pack(Market("ZZZUSDT",Now.AddMinutes(-1).UtcDateTime),Market("AAAUSDT",Now.AddMinutes(-1).UtcDateTime));
        var second=Pack(Market("AAAUSDT",Now.AddMinutes(-1).UtcDateTime),Market("ZZZUSDT",Now.AddMinutes(-1).UtcDateTime));
        var skill=new SignalAggregationSkill();var left=skill.Analyze(first,Policy(),Now);var right=skill.Analyze(second,Policy(),Now);
        Assert.Equal(new[]{"AAAUSDT","ZZZUSDT"},left.Select(x=>x.Symbol));
        Assert.Equal(JsonSerializer.Serialize(left),JsonSerializer.Serialize(right));
    }

    private static DecisionPolicy Policy()=>new(){MaximumEvidenceAgeMinutes=5,MinimumEvidenceCompleteness=0,MinimumConfidence=0,MinimumDirectionalScore=0,MaximumConflictRatio=1,MinimumMarketQuality=0};
    private static EvidencePack Pack(params MarketEvidence[] markets)=>new(){Completeness=100,Markets=markets.ToDictionary(x=>x.Symbol,StringComparer.Ordinal)};
    private static MarketEvidence Market(string symbol,DateTime collected,double trend=.01)
    {
        var market=new MarketEvidence(symbol,50_000m,49_000m,51_000m,55,trend,trend,trend,new(0,1m,1m,1m,1m,1.1m,0),collected){Quality=new(){QualityScore=100,OrderBookImbalance=.1,RelativeVolume=1.2,AtrPercent=.01,LiquidationIntensity=.1}};
        return double.IsFinite(trend)?market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"test-provider","Testnet")}:market;
    }
}
