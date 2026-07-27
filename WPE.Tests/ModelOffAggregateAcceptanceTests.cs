using WpeAgent.ModelOff;
using System.Security.Cryptography;
using System.Text;

namespace WPE.Tests;

public sealed class ModelOffAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,12,0,0,TimeSpan.Zero);

    [Fact]
    public void EveryAggregateHasDistinctLocalAndLiveRequirements()
    {
        foreach(var agent in Enum.GetValues<ModelOffAggregateAgentV1>())
        {
            var requirements=ModelOffAggregateAcceptanceGateV1.Requirements(agent);
            Assert.Equal(4,requirements.Count);Assert.Equal(4,requirements.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(requirements,x=>!x.LiveEvidence);Assert.Contains(requirements,x=>x.LiveEvidence);
        }
    }

    [Theory]
    [InlineData(ModelOffAggregateAgentV1.Market)]
    [InlineData(ModelOffAggregateAgentV1.Research)]
    [InlineData(ModelOffAggregateAgentV1.Strategy)]
    [InlineData(ModelOffAggregateAgentV1.Risk)]
    [InlineData(ModelOffAggregateAgentV1.Execution)]
    [InlineData(ModelOffAggregateAgentV1.Recovery)]
    [InlineData(ModelOffAggregateAgentV1.Audit)]
    public void CompleteFreshEvidenceAcceptsExactlyOneAggregate(ModelOffAggregateAgentV1 agent)
    {
        var result=ModelOffAggregateAcceptanceGateV1.Evaluate(agent,Complete(agent),Now);
        Assert.True(result.Accepted);Assert.Empty(result.Errors);Assert.Equal(4,result.SatisfiedRequirements.Count);Assert.Equal(64,result.EvidenceSetSha256.Length);
    }

    [Fact]
    public void MissingLiveEvidenceKeepsAggregatePartial()
    {
        var evidence=Complete(ModelOffAggregateAgentV1.Execution).Where(x=>x.Environment!=AcceptanceEvidenceEnvironmentV1.Testnet).ToArray();
        var result=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Execution,evidence,Now);
        Assert.False(result.Accepted);Assert.Contains(result.Errors,x=>x.StartsWith("acceptance.missing.execution.live-",StringComparison.Ordinal));
    }

    [Fact]
    public void StaleDuplicateCrossAgentAndMalformedEvidenceFailClosed()
    {
        var evidence=Complete(ModelOffAggregateAgentV1.Market).ToList();var first=evidence[0];
        evidence[0]=first with{ArtifactSha256=new string('a',64)};evidence[1]=evidence[1] with{ObservedAtUtc=Now.AddDays(-181)};evidence.Add(evidence[2]);evidence.Add(Complete(ModelOffAggregateAgentV1.Risk)[0]);
        var result=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Market,evidence,Now);
        Assert.False(result.Accepted);Assert.Contains("acceptance.cross-agent-evidence",result.Errors);Assert.Contains("acceptance.duplicate-evidence",result.Errors);Assert.Contains(result.Errors,x=>x.StartsWith("acceptance.hash.",StringComparison.Ordinal));Assert.Contains(result.Errors,x=>x.StartsWith("acceptance.stale.",StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceSetHashIsOrderIndependent()
    {
        var first=Complete(ModelOffAggregateAgentV1.Audit);var second=first.Reverse().ToArray();
        Assert.Equal(ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Audit,first,Now).EvidenceSetSha256,ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Audit,second,Now).EvidenceSetSha256);
    }

    [Fact]
    public void ExcessiveValidityAndArtifactHashMismatchFailClosed()
    {
        var evidence=Complete(ModelOffAggregateAgentV1.Research);evidence[0]=evidence[0] with{ValidUntilUtc=Now.AddDays(365)};evidence[1]=evidence[1] with{CanonicalArtifact=Encoding.UTF8.GetBytes("changed")};
        var result=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Research,evidence,Now);
        Assert.False(result.Accepted);Assert.Contains(result.Errors,x=>x.StartsWith("acceptance.stale.",StringComparison.Ordinal));Assert.Contains(result.Errors,x=>x.StartsWith("acceptance.hash.",StringComparison.Ordinal));
    }

    private static AggregateAcceptanceEvidenceV1[] Complete(ModelOffAggregateAgentV1 agent)=>ModelOffAggregateAcceptanceGateV1.Requirements(agent).Select((x,index)=>
    {
        var artifact=Encoding.UTF8.GetBytes($"{agent}|{x.Id}|{index}");var hash=Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant();
        return new AggregateAcceptanceEvidenceV1(x.Id,agent,x.Environment,Now.AddMinutes(-index-1),Now.AddHours(1),true,hash,artifact,x.LiveEvidence?"binance-futures":null);
    }).ToArray();
}
