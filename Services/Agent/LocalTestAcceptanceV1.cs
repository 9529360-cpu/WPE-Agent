using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record LocalTestAcceptanceArtifactV1(
    string Schema,string RequirementId,string CandidateVersion,string CandidateSha256,string TestAssemblySha256,
    string TrxSha256,DateTimeOffset ObservedAtUtc,IReadOnlyList<string> Tests,string CanonicalSha256,byte[] CanonicalBytes);

public static class LocalTestAcceptanceCanonicalizerV1
{
    public const string Schema="wpe.local-test-acceptance/1.0";
    private static readonly XNamespace TrxNamespace="http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly IReadOnlyDictionary<string,string[]> Allowlist=new Dictionary<string,string[]>(StringComparer.Ordinal)
    {
        ["market.canonical-contract"]=
        [
            "WPE.Tests.ConfirmedMarketCandleTests.AnalysisFailsClosedWithoutEnoughConfirmedHistory",
            "WPE.Tests.ConfirmedMarketCandleTests.AnalysisUsesConfirmedCloseTimeAsSourceTime",
            "WPE.Tests.ConfirmedMarketCandleTests.SelectRejectsOpenFutureMalformedAndDuplicateCandles",
            "WPE.Tests.MarketEvidenceProvenanceTests.CanonicalProvenanceBindsProviderEnvironmentSymbolTimeAndFacts",
            "WPE.Tests.MarketEvidenceProvenanceTests.MissingOrTamperedCanonicalBytesFailClosed"
        ],
        ["market.adversarial-fail-closed"]=
        [
            "WPE.Tests.MarketAggregateAcceptanceTests.AcceptanceSourceAuthorityIsReadOnly",
            "WPE.Tests.MarketAggregateAcceptanceTests.FourFreshCanonicalSamplesProduceEligibleSustainedEvidence",
            "WPE.Tests.MarketAggregateAcceptanceTests.ShortWindowStaleSourceDuplicateAndTamperingFailClosed",
            "WPE.Tests.MarketAggregateAcceptanceTests.TypedMarketArtifactsSatisfyOnlyTheTwoLiveMarketRequirements",
            "WPE.Tests.RealtimeMarketIntegrityTests.OnlyCompleteFreshConnectedSnapshotIsEligibleForEnrichment",
            "WPE.Tests.RealtimeMarketIntegrityTests.UnknownAndMalformedKnownEventsDoNotRefreshMarketState",
            "WPE.Tests.RealtimeMarketIntegrityTests.ValidKnownEventsAloneAdvanceMessageCount"
        ],
        ["research.canonical-contract"]=
        [
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.CanonicalNewsEvidenceEntersResearchByHashWithoutBodyText",
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.ResearchBindsTechnicalAssessmentToCanonicalMarketEvidence",
            "WPE.Tests.ModelOffResearchCapabilityTests.AdjacentInvariantsPreserveExplicitFactsAndCanonicalSourceOrdering",
            "WPE.Tests.ModelOffResearchCapabilityTests.BacktestFactsAreCanonicalRepeatableAndBoundToExactStrategySource",
            "WPE.Tests.ModelOffResearchCapabilityTests.CapabilitySpecificEntryPointsProduceTheSameCanonicalContract",
            "WPE.Tests.ModelOffResearchCapabilityTests.FundamentalCapabilityRecordsOnlyValidatedObservedFacts",
            "WPE.Tests.ModelOffResearchCapabilityTests.MacroFactsProduceCanonicalRepeatableOutputWithoutModelInference"
        ],
        ["research.point-in-time-replay"]=
        [
            "WPE.Tests.HistoricalNewsResearchTests.HistoricalQueryReturnsOnlyTheRequestedSymbolAndPointInTimeRange",
            "WPE.Tests.HistoricalNewsResearchTests.NewsMomentumBacktestUsesOnlyNewsPublishedBeforeEachClosedCandle",
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.BacktestValidationSourceRetainsItsOwnValidationTime",
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.ProductionResearchRejectsMacroEvidenceOutsideTheBoundedWindow",
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.ProductionResearchUsesSourceSpecificFreshnessWithoutRelabelingEvidence"
        ],
        ["strategy.lifecycle-contract"]=
        [
            "WPE.Tests.StrategyFactoryLifecycleTests.DegradedStrategyIsArchivedAndCannotRemainSelectable",
            "WPE.Tests.StrategyFactoryLifecycleTests.QualifiedParentProducesBoundedHashedDraftChildren",
            "WPE.Tests.StrategyFactoryLifecycleTests.RetiredBuiltInStrategyCannotBeSelectedAsActiveFallback",
            "WPE.Tests.StrategyFactoryLifecycleTests.TamperedLineageHashIsRejectedBeforePersistence",
            "WPE.Tests.StrategyFactoryLifecycleTests.TamperedParameterHashIsRejectedBeforePersistence",
            "WPE.Tests.StrategyFactoryLifecycleTests.UnqualifiedActiveProfileCannotBecomeAParent",
            "WPE.Tests.StrategyFactoryLifecycleTests.UnvalidatedSeedsNeverStartActiveAndFailedFamiliesReceiveBoundedReplacements"
        ],
        ["strategy.anti-overfit"]=
        [
            "WPE.Tests.StrategyRegimeValidationTests.LegacyPassedFlagCannotBypassRegimePromotionGate",
            "WPE.Tests.StrategyRegimeValidationTests.PerformanceConcentratedInEarlyHistoryFailsOverfitGate",
            "WPE.Tests.StrategyRegimeValidationTests.RobustnessEvaluationIsDeterministic",
            "WPE.Tests.StrategyRegimeValidationTests.StablePerformanceAcrossChronologicalRegimesPasses"
        ],
        ["strategy.no-risk-bypass"]=
        [
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.LiveLoopInvokesShadowAfterRiskReviewAndBeforeOrderAuthorization",
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.ProductionShadowPersistsSevenOutputsWithoutChangingExecution",
            "WPE.Tests.ModelOffLiveCycleInputComposerTests.RiskIncreaseGateRequiresCompletePersistedSevenAgentCycle"
        ]
    };

    public static LocalTestAcceptanceArtifactV1 Create(string requirementId,string candidateVersion,byte[] candidateBytes,byte[] testAssemblyBytes,byte[] trxBytes)
    {
        if(!Allowlist.TryGetValue(requirementId,out var expected))throw new InvalidOperationException("Unsupported local acceptance requirement.");
        if(candidateBytes.Length==0||testAssemblyBytes.Length==0||trxBytes.Length==0)throw new InvalidOperationException("Acceptance inputs must not be empty.");
        var (observed,tests)=ParseTrx(trxBytes,expected);
        var candidateHash=Hash(candidateBytes);var testHash=Hash(testAssemblyBytes);var trxHash=Hash(trxBytes);
        var canonical=Serialize(requirementId,candidateVersion,candidateHash,testHash,trxHash,observed,tests);
        return new(Schema,requirementId,candidateVersion,candidateHash,testHash,trxHash,observed,tests,Hash(canonical),canonical);
    }

    public static bool IsCanonical(LocalTestAcceptanceArtifactV1? value,DateTimeOffset evaluatedAtUtc,string expectedCandidateSha256)
    {
        if(value is null||value.Schema!=Schema||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.ObservedAtUtc>TimeSpan.FromDays(180)||!Sha(expectedCandidateSha256)||value.CandidateSha256!=expectedCandidateSha256||!Sha(value.TestAssemblySha256)||!Sha(value.TrxSha256))return false;
        if(!Allowlist.TryGetValue(value.RequirementId,out var expected)||!value.Tests.SequenceEqual(expected.Order(StringComparer.Ordinal)))return false;
        var recreated=Serialize(value.RequirementId,value.CandidateVersion,value.CandidateSha256,value.TestAssemblySha256,value.TrxSha256,value.ObservedAtUtc,value.Tests);
        return value.CanonicalSha256==Hash(recreated)&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(recreated,value.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(LocalTestAcceptanceArtifactV1 value,DateTimeOffset evaluatedAtUtc,string expectedCandidateSha256)
    {
        if(!IsCanonical(value,evaluatedAtUtc,expectedCandidateSha256))throw new InvalidOperationException("Local test evidence is not canonical or eligible.");
        return new(value.RequirementId,Agent(value.RequirementId),AcceptanceEvidenceEnvironmentV1.Local,value.ObservedAtUtc,value.ObservedAtUtc.AddDays(180),true,value.CanonicalSha256,value.CanonicalBytes,null);
    }

    public static bool TryParseCanonical(byte[]? bytes,out LocalTestAcceptanceArtifactV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;
        try
        {
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=8)return false;
            var requirement=root.GetProperty("requirement_id").GetString()??string.Empty;var version=root.GetProperty("candidate_version").GetString()??string.Empty;var candidate=root.GetProperty("candidate_sha256").GetString()??string.Empty;var test=root.GetProperty("test_assembly_sha256").GetString()??string.Empty;var trx=root.GetProperty("trx_sha256").GetString()??string.Empty;var observed=root.GetProperty("observed_at_utc").GetDateTimeOffset();var tests=root.GetProperty("tests").EnumerateArray().Select(x=>x.GetString()??string.Empty).ToArray();
            if(root.GetProperty("schema").GetString()!=Schema||!Sha(candidate)||!Sha(test)||!Sha(trx)||observed.Offset!=TimeSpan.Zero||!Allowlist.TryGetValue(requirement,out var expected)||!tests.SequenceEqual(expected.Order(StringComparer.Ordinal)))return false;
            var canonical=Serialize(requirement,version,candidate,test,trx,observed,tests);if(!CryptographicOperations.FixedTimeEquals(canonical,bytes))return false;value=new(Schema,requirement,version,candidate,test,trx,observed,tests,Hash(canonical),canonical);return true;
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static (DateTimeOffset Observed,string[] Tests) ParseTrx(byte[] bytes,string[] expected)
    {
        XDocument document;
        try{using var stream=new MemoryStream(bytes,false);document=XDocument.Load(stream,LoadOptions.None);}
        catch(Exception ex)when(ex is System.Xml.XmlException or InvalidOperationException){throw new InvalidOperationException("Malformed TRX evidence.",ex);}
        var root=document.Root;if(root?.Name!=TrxNamespace+"TestRun")throw new InvalidOperationException("Unexpected TRX root.");
        var times=root.Element(TrxNamespace+"Times");
        if(!DateTimeOffset.TryParse(times?.Attribute("finish")?.Value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var finish))throw new InvalidOperationException("TRX finish time is missing.");
        var results=root.Element(TrxNamespace+"Results")?.Elements(TrxNamespace+"UnitTestResult").ToArray()??[];
        var names=results.Select(x=>x.Attribute("testName")?.Value??string.Empty).ToArray();
        if(names.Any(string.IsNullOrWhiteSpace)||names.Distinct(StringComparer.Ordinal).Count()!=names.Length||results.Any(x=>x.Attribute("outcome")?.Value!="Passed"))throw new InvalidOperationException("TRX contains missing, duplicate, or non-passing results.");
        var ordered=names.Order(StringComparer.Ordinal).ToArray();var required=expected.Order(StringComparer.Ordinal).ToArray();
        if(!ordered.SequenceEqual(required,StringComparer.Ordinal))throw new InvalidOperationException("TRX test set does not exactly match the requirement allowlist.");
        var definitions=root.Element(TrxNamespace+"TestDefinitions")?.Elements(TrxNamespace+"UnitTest").Select(x=>x.Attribute("name")?.Value??string.Empty).Order(StringComparer.Ordinal).ToArray()??[];
        if(!definitions.SequenceEqual(required,StringComparer.Ordinal))throw new InvalidOperationException("TRX definitions do not match results.");
        var counters=root.Element(TrxNamespace+"ResultSummary")?.Element(TrxNamespace+"Counters");
        if(counters is null||counters.Attribute("total")?.Value!=required.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)||counters.Attribute("passed")?.Value!=required.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)||counters.Attribute("failed")?.Value!="0"||counters.Attribute("error")?.Value!="0")throw new InvalidOperationException("TRX counters are not an exact all-pass set.");
        return(finish.ToUniversalTime(),required);
    }

    private static byte[] Serialize(string requirementId,string version,string candidateHash,string testHash,string trxHash,DateTimeOffset observed,IReadOnlyList<string> tests)
    {
        using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream)){writer.WriteStartObject();writer.WriteString("candidate_sha256",candidateHash);writer.WriteString("candidate_version",version);writer.WriteString("observed_at_utc",observed);writer.WriteString("requirement_id",requirementId);writer.WriteString("schema",Schema);writer.WriteString("test_assembly_sha256",testHash);writer.WritePropertyName("tests");writer.WriteStartArray();foreach(var test in tests)writer.WriteStringValue(test);writer.WriteEndArray();writer.WriteString("trx_sha256",trxHash);writer.WriteEndObject();}return stream.ToArray();
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static ModelOffAggregateAgentV1 Agent(string requirementId)=>requirementId.StartsWith("market.",StringComparison.Ordinal)?ModelOffAggregateAgentV1.Market:requirementId.StartsWith("research.",StringComparison.Ordinal)?ModelOffAggregateAgentV1.Research:requirementId.StartsWith("strategy.",StringComparison.Ordinal)?ModelOffAggregateAgentV1.Strategy:throw new InvalidOperationException("Unsupported local acceptance Agent.");
}
