using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ResearchAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,5,0,0,TimeSpan.Zero);
    private static readonly MarketAcceptanceCandidateV1 Candidate=new("3.6.0",Hash("candidate"));

    [Fact]
    public void ExactLiveSourcesAndFiveCanonicalOutputsProduceTargetEvidence()
    {
        var live=ResearchAggregateEvidenceCanonicalizerV1.Create(ResearchAggregateEvidenceCanonicalizerV1.LiveSourceRequirement,"BTCUSDT",Candidate,Now,Sources());Assert.True(ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(live,Now));
        var cycle=ResearchAggregateEvidenceCanonicalizerV1.Create(ResearchAggregateEvidenceCanonicalizerV1.ModelOffCycleRequirement,"BTCUSDT",Candidate,Now,Sources(),Outputs());Assert.True(ResearchAggregateEvidenceCanonicalizerV1.TryParseCanonical(cycle.CanonicalBytes,out var parsed),Encoding.UTF8.GetString(cycle.CanonicalBytes));Assert.Equal(cycle.CanonicalSha256,parsed!.CanonicalSha256);Assert.True(ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(cycle,Now),string.Join(',',cycle.Outputs.Select(x=>$"{x.Capability}:{x.CanonicalSha256==Hash(x.CanonicalBytes)}")));
        var evidence=ResearchAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(cycle,Now);Assert.Equal(ModelOffAggregateAgentV1.Research,evidence.Agent);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Target,evidence.Environment);
    }

    [Fact]
    public void MissingSourceOutputCandidateAndTamperingFailClosed()
    {
        var sources=Sources();var outputs=Outputs();var cycle=ResearchAggregateEvidenceCanonicalizerV1.Create(ResearchAggregateEvidenceCanonicalizerV1.ModelOffCycleRequirement,"BTCUSDT",Candidate,Now,sources,outputs);
        Assert.False(ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(ResearchAggregateEvidenceCanonicalizerV1.Create(ResearchAggregateEvidenceCanonicalizerV1.ModelOffCycleRequirement,"BTCUSDT",Candidate,Now,sources.Where(x=>x.Category!="News").ToArray(),outputs),Now));
        Assert.False(ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(ResearchAggregateEvidenceCanonicalizerV1.Create(ResearchAggregateEvidenceCanonicalizerV1.ModelOffCycleRequirement,"BTCUSDT",Candidate,Now,sources,outputs[..^1]),Now));
        Assert.False(ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(cycle with{CanonicalBytes=Encoding.UTF8.GetBytes("tampered")},Now));Assert.False(ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(cycle with{CandidateSha256="bad"},Now));Assert.False(ResearchAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{}"),out _));
    }

    [Fact]
    public void FreshTechnicalBindingUsesOnlyClosedOneMinuteCandles()
    {
        var observed=Now.UtcDateTime;var candles=Enumerable.Range(0,41).Select(i=>new CandleEvidence(observed.AddMinutes(-40+i),100+i,102+i,99+i,101+i,10,1000,2,4)).ToArray();var baseMarket=new MarketEvidence("BTCUSDT",100,90,110,50,.1,.2,.3,new(0,1,1,1,1,1,0),observed.AddMinutes(-15)){Quality=new(){QualityScore=90}};var result=ResearchAggregateAcceptanceRunnerV1.BindFreshTechnicalMarket(baseMarket,candles,observed);
        Assert.Equal(observed,result.CollectedAt);Assert.Equal(40,result.Candles.Count);Assert.DoesNotContain(result.Candles,x=>x.OpenTime==observed);Assert.True(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(result));Assert.Equal(baseMarket.Trend1h,result.Trend1h);
    }

    private static ResearchAcceptanceSourceV1[] Sources()=>[new("Fundamental","exchange-info","binance-futures",Now,Hash("f")),new("Macro","cpi","bls-public-api-v2",Now.AddDays(-10),Hash("m1")),new("Macro","labor","bls-public-api-v2",Now.AddDays(-10),Hash("m2")),new("Market","BTCUSDT","binance-futures",Now,Hash("market")),new("News","sec","sec",Now.AddMinutes(-2),Hash("news"))];
    private static ResearchAcceptanceOutputV1[] Outputs()=>new[]{"Backtest","Fundamental","Macro","News","Technical"}.Select(x=>{var bytes=Encoding.UTF8.GetBytes(x);return new ResearchAcceptanceOutputV1(x,Hash(bytes),bytes);}).ToArray();
    private static string Hash(string value)=>Hash(Encoding.UTF8.GetBytes(value));private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
