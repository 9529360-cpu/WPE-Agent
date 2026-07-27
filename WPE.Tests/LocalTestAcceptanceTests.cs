using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class LocalTestAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,4,0,0,TimeSpan.Zero);

    [Fact]
    public void ExactAllPassTrxProducesCandidateBoundEvidence()
    {
        var artifact=LocalTestAcceptanceCanonicalizerV1.Create("market.canonical-contract","3.6.0",Bytes("candidate"),Bytes("tests"),Trx(CanonicalNames));var candidateHash=Hash(Bytes("candidate"));
        Assert.True(LocalTestAcceptanceCanonicalizerV1.IsCanonical(artifact,Now,candidateHash));
        Assert.True(LocalTestAcceptanceCanonicalizerV1.TryParseCanonical(artifact.CanonicalBytes,out var parsed));Assert.Equal(artifact.CanonicalSha256,parsed!.CanonicalSha256);
        var evidence=LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,Now,candidateHash);
        Assert.Equal(AcceptanceEvidenceEnvironmentV1.Local,evidence.Environment);Assert.Equal(artifact.CanonicalSha256,evidence.ArtifactSha256);
    }

    [Fact]
    public void MissingExtraFailedDuplicateMalformedAndWrongCandidateFailClosed()
    {
        Assert.Throws<InvalidOperationException>(()=>Create(CanonicalNames[..^1]));
        Assert.Throws<InvalidOperationException>(()=>Create([..CanonicalNames,"WPE.Tests.Extra.Unapproved"]));
        Assert.Throws<InvalidOperationException>(()=>LocalTestAcceptanceCanonicalizerV1.Create("market.canonical-contract","3.6.0",Bytes("candidate"),Bytes("tests"),Trx(CanonicalNames,"Failed")));
        Assert.Throws<InvalidOperationException>(()=>Create([..CanonicalNames,CanonicalNames[0]]));
        Assert.Throws<InvalidOperationException>(()=>LocalTestAcceptanceCanonicalizerV1.Create("market.canonical-contract","3.6.0",Bytes("candidate"),Bytes("tests"),Bytes("not xml")));
        var artifact=Create(CanonicalNames);Assert.False(LocalTestAcceptanceCanonicalizerV1.IsCanonical(artifact,Now,Hash(Bytes("other"))));
        Assert.False(LocalTestAcceptanceCanonicalizerV1.IsCanonical(artifact with{CanonicalBytes=Bytes("tampered")},Now,artifact.CandidateSha256));
        Assert.False(LocalTestAcceptanceCanonicalizerV1.TryParseCanonical(Bytes("{\"schema\":\"bad\"}"),out _));
    }

    [Fact]
    public void ResearchEvidenceIsBoundToResearchAndExactReplaySet()
    {
        var artifact=LocalTestAcceptanceCanonicalizerV1.Create("research.point-in-time-replay","3.6.0",Bytes("candidate"),Bytes("tests"),Trx(ResearchReplayNames));var candidate=Hash(Bytes("candidate"));Assert.True(LocalTestAcceptanceCanonicalizerV1.IsCanonical(artifact,Now,candidate));var evidence=LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,Now,candidate);Assert.Equal(ModelOffAggregateAgentV1.Research,evidence.Agent);Assert.Equal("research.point-in-time-replay",evidence.RequirementId);
        Assert.Throws<InvalidOperationException>(()=>LocalTestAcceptanceCanonicalizerV1.Create("research.point-in-time-replay","3.6.0",Bytes("candidate"),Bytes("tests"),Trx(ResearchReplayNames[..^1])));
    }

    private static LocalTestAcceptanceArtifactV1 Create(string[] names)=>LocalTestAcceptanceCanonicalizerV1.Create("market.canonical-contract","3.6.0",Bytes("candidate"),Bytes("tests"),Trx(names));
    private static byte[] Trx(string[] names,string outcome="Passed")
    {
        var results=string.Concat(names.Select((x,i)=>$"<UnitTestResult testName=\"{x}\" outcome=\"{outcome}\"/>"));var definitions=string.Concat(names.Select(x=>$"<UnitTest name=\"{x}\"/>"));
        return Bytes($"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Times finish=\"{Now:O}\"/><Results>{results}</Results><TestDefinitions>{definitions}</TestDefinitions><ResultSummary><Counters total=\"{names.Length}\" passed=\"{(outcome=="Passed"?names.Length:0)}\" failed=\"{(outcome=="Passed"?0:names.Length)}\" error=\"0\"/></ResultSummary></TestRun>");
    }
    private static readonly string[] CanonicalNames=
    [
        "WPE.Tests.ConfirmedMarketCandleTests.AnalysisFailsClosedWithoutEnoughConfirmedHistory","WPE.Tests.ConfirmedMarketCandleTests.AnalysisUsesConfirmedCloseTimeAsSourceTime","WPE.Tests.ConfirmedMarketCandleTests.SelectRejectsOpenFutureMalformedAndDuplicateCandles","WPE.Tests.MarketEvidenceProvenanceTests.CanonicalProvenanceBindsProviderEnvironmentSymbolTimeAndFacts","WPE.Tests.MarketEvidenceProvenanceTests.MissingOrTamperedCanonicalBytesFailClosed"
    ];
    private static readonly string[] ResearchReplayNames=
    [
        "WPE.Tests.HistoricalNewsResearchTests.HistoricalQueryReturnsOnlyTheRequestedSymbolAndPointInTimeRange","WPE.Tests.HistoricalNewsResearchTests.NewsMomentumBacktestUsesOnlyNewsPublishedBeforeEachClosedCandle","WPE.Tests.ModelOffLiveCycleInputComposerTests.BacktestValidationSourceRetainsItsOwnValidationTime","WPE.Tests.ModelOffLiveCycleInputComposerTests.ProductionResearchRejectsMacroEvidenceOutsideTheBoundedWindow","WPE.Tests.ModelOffLiveCycleInputComposerTests.ProductionResearchUsesSourceSpecificFreshnessWithoutRelabelingEvidence"
    ];
    private static byte[] Bytes(string value)=>Encoding.UTF8.GetBytes(value);
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
