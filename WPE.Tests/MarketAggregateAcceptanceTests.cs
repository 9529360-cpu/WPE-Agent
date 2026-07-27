using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MarketAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Start=new(2026,7,27,12,0,0,TimeSpan.Zero);
    private static readonly MarketAcceptanceCandidateV1 Candidate=new("3.6.0",Hash("candidate"));

    [Fact]
    public async Task FourFreshCanonicalSamplesProduceEligibleSustainedEvidence()
    {
        var now=Start;await using var source=new FakeSource(()=>Market(now.AddMinutes(-5),50000m+now.Minute));
        var artifact=await MarketAggregateAcceptanceCollectorV1.CollectAsync(source,MarketAggregateEvidenceCanonicalizerV1.SustainedFreshnessRequirement,"BTCUSDT",Candidate,4,TimeSpan.FromMinutes(5),()=>now,(span,_)=>{now+=span;return Task.CompletedTask;});

        Assert.True(MarketAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,now));Assert.Equal(4,artifact.Samples.Count);Assert.Equal(TimeSpan.FromMinutes(15),artifact.CompletedAtUtc-artifact.StartedAtUtc);
        Assert.True(MarketAggregateEvidenceCanonicalizerV1.TryParseCanonical(artifact.CanonicalBytes,out var parsed));Assert.Equal(artifact.CanonicalSha256,parsed!.CanonicalSha256);
        var evidence=MarketAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(artifact,now);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Target,evidence.Environment);Assert.Equal(artifact.CanonicalSha256,evidence.ArtifactSha256);
    }

    [Fact]
    public void ShortWindowStaleSourceDuplicateAndTamperingFailClosed()
    {
        var samples=new[]{Sample(Start,Start.AddMinutes(-5),"a"),Sample(Start.AddMinutes(5),Start,"b"),Sample(Start.AddMinutes(10),Start.AddMinutes(5),"c"),Sample(Start.AddMinutes(14),Start.AddMinutes(9),"d")};
        var shortWindow=MarketAggregateEvidenceCanonicalizerV1.Create(MarketAggregateEvidenceCanonicalizerV1.SustainedFreshnessRequirement,"binance-futures","Testnet","BTCUSDT",Candidate,samples);
        Assert.False(MarketAggregateEvidenceCanonicalizerV1.IsCanonical(shortWindow,Start.AddMinutes(14)));

        var valid=MarketAggregateEvidenceCanonicalizerV1.Create(MarketAggregateEvidenceCanonicalizerV1.SustainedFreshnessRequirement,"binance-futures","Testnet","BTCUSDT",Candidate,samples.Select((x,i)=>x with{ObservedAtUtc=Start.AddMinutes(i*5),SourceAtUtc=Start.AddMinutes(i*5-5)}).ToArray());
        Assert.True(MarketAggregateEvidenceCanonicalizerV1.IsCanonical(valid,Start.AddMinutes(15)));
        Assert.False(MarketAggregateEvidenceCanonicalizerV1.IsCanonical(valid with{Samples=valid.Samples.Select((x,i)=>i==0?x with{SourceAtUtc=x.ObservedAtUtc.AddMinutes(-21)}:x).ToArray()},Start.AddMinutes(15)));
        Assert.False(MarketAggregateEvidenceCanonicalizerV1.IsCanonical(valid with{Samples=valid.Samples.Select(x=>x with{MarketEvidenceSha256=valid.Samples[0].MarketEvidenceSha256}).ToArray()},Start.AddMinutes(15)));
        Assert.False(MarketAggregateEvidenceCanonicalizerV1.IsCanonical(valid with{CanonicalBytes=Encoding.UTF8.GetBytes("tampered")},Start.AddMinutes(15)));
        Assert.False(MarketAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{\"schema\":\"bad\"}"),out _));
    }

    [Fact]
    public async Task TypedMarketArtifactsSatisfyOnlyTheTwoLiveMarketRequirements()
    {
        var now=Start;await using var source=new FakeSource(()=>Market(now.AddMinutes(-5),50000m+now.Minute));
        var read=await MarketAggregateAcceptanceCollectorV1.CollectAsync(source,MarketAggregateEvidenceCanonicalizerV1.LiveReadRequirement,"BTCUSDT",Candidate,1,TimeSpan.Zero,()=>now,(_,_)=>Task.CompletedTask);
        var sustained=await MarketAggregateAcceptanceCollectorV1.CollectAsync(source,MarketAggregateEvidenceCanonicalizerV1.SustainedFreshnessRequirement,"BTCUSDT",Candidate,4,TimeSpan.FromMinutes(5),()=>now,(span,_)=>{now+=span;return Task.CompletedTask;});
        var evidence=new List<AggregateAcceptanceEvidenceV1>{Local("market.canonical-contract"),Local("market.adversarial-fail-closed"),MarketAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(read,now),MarketAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(sustained,now)};

        var result=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Market,evidence,now);
        Assert.True(result.Accepted);Assert.Equal(4,result.SatisfiedRequirements.Count);
    }

    [Fact]
    public void AcceptanceSourceAuthorityIsReadOnly()
    {
        var methods=typeof(IMarketAcceptanceSourceV1).GetMethods().Select(x=>x.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["ReadAsync"],methods);Assert.DoesNotContain(methods,x=>x.Contains("Place",StringComparison.OrdinalIgnoreCase)||x.Contains("Cancel",StringComparison.OrdinalIgnoreCase)||x.Contains("Account",StringComparison.OrdinalIgnoreCase));
    }

    private static MarketAcceptanceSampleV1 Sample(DateTimeOffset observed,DateTimeOffset source,string seed)=>new(observed,source,50000m,90,240,Hash(seed));
    private static AggregateAcceptanceEvidenceV1 Local(string id){var bytes=Encoding.UTF8.GetBytes(id);return new(id,ModelOffAggregateAgentV1.Market,AcceptanceEvidenceEnvironmentV1.Local,Start.AddMinutes(-1),Start.AddDays(1),true,Hash(bytes),bytes,null);}
    private static string Hash(string value)=>Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static MarketEvidence Market(DateTimeOffset sourceAt,decimal price)
    {
        var candles=Enumerable.Range(0,31).Select(i=>new CandleEvidence(sourceAt.AddMinutes(-15*(30-i)).UtcDateTime,price-1,price+1,price-2,price,10,price*10,20,5)).ToArray();
        var market=new MarketEvidence("BTCUSDT",price,price-100,price+100,50,.01,.02,.03,new(0,1000,1,1,1,1,0),sourceAt.UtcDateTime){Candles=candles,Quality=new(){QualityScore=90,SourceCount=5,LiquidityScore=.9}};
        return market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance-futures","Testnet")};
    }

    private sealed class FakeSource(Func<MarketEvidence> read):IMarketAcceptanceSourceV1
    {
        public Task<MarketEvidence> ReadAsync(string symbol,CancellationToken ct)=>Task.FromResult(read());
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
