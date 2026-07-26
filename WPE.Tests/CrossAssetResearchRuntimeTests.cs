using System.Text.Json;
using WpeAgent.CrossAssetResearch;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;

namespace WPE.Tests;

public sealed class CrossAssetResearchRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StoreStartsUnsupportedAndPublishesOnlyDraftSummary()
    {
        var store = new RuntimeCrossAssetResearchStateStore();
        Assert.Equal(RuntimeCollectionState.Unsupported, store.Read().State);

        var request = Request();
        var result = new CrossAssetResearchResult(
            request.ResearchId, request.StrategyId, CrossAssetResearchStatus.Available,
            CrossAssetStrategyLifecycle.Draft, true,
            new(240, 12, .12, .03, .08, 1.1, .72, .18),
            ["research.summary-only"], ["canonical-payload-secret", "credential-secret"], Now);

        store.Publish(request, result);
        var item = Assert.Single(store.Read().Items);
        Assert.Equal("Draft", item.Lifecycle);
        Assert.Equal("Available", item.Status);
        Assert.Equal(3, item.MultipleTesting!.TrialCount);
        Assert.Equal(2, item.Alignment.VenueCount);
        Assert.Equal(5, item.OosPurgeObservations);
        Assert.Equal(request.EvidenceArtifact!.ArtifactHash, item.ArtifactHash);

        var json = JsonSerializer.Serialize(store.Read());
        foreach (var forbidden in new[] { "TimestampUtc", "Close", "TargetExposure", "DataAvailableAtUtc", "TrialPValues", "SelectedTrialIndex", "InputReferences", "canonical-payload-secret", "credential-secret", "CostModel" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedContractFailureRemainsUnsupportedDraftItem()
    {
        var store = new RuntimeCrossAssetResearchStateStore();
        var request = Request();
        store.Publish(request, new(request.ResearchId, request.StrategyId, CrossAssetResearchStatus.Unsupported,
            CrossAssetStrategyLifecycle.Draft, false, null, ["research.evaluation-failed"], [], Now));

        var item = Assert.Single(store.Read().Items);
        Assert.Equal("Unsupported", item.Status);
        Assert.False(item.Passed);
        Assert.Null(item.Metrics);
    }

    [Fact]
    public void StoreRejectsMismatchedContractIdentity()
    {
        var request = Request();
        var result = new CrossAssetResearchResult("other", request.StrategyId, CrossAssetResearchStatus.Unsupported,
            CrossAssetStrategyLifecycle.Draft, false, null, ["research.identity-missing"], [], Now);
        Assert.Throws<ArgumentException>(() => new RuntimeCrossAssetResearchStateStore().Publish(request, result));
    }

    private static CrossAssetResearchRequest Request()
    {
        var policy = new CrossAssetValidationPolicy(OosPurgeObservations: 5, OosEmbargoObservations: 7);
        var evidence = new ResearchEvidenceArtifact(
            new string('a', 64), new string('b', 64), "code-v1", "strategy-v2", 42,
            Now.AddMonths(-2), Now.AddMonths(-1), new(.001m, .002m), "actions-v1", "session-v1", Now,
            new string('c', 64));
        var hypotheses = new MultipleHypothesisEvidence(
            3, new string('d', 64), "lowest corrected p", new string('e', 64), true, false, 1,
            MultipleTestingCorrectionMethod.Bonferroni, .05, [.01, .02, .03], 0);
        return new(
            "research-1", "strategy-1", "strategy-v2", "BTC-USD", ResearchAssetClass.Crypto,
            [new(Now.AddDays(-3), 12345.67m, .25, DataAvailableAtUtc: Now.AddDays(-3), SignalGeneratedAtUtc: Now.AddDays(-3))],
            null, new(.001m, .002m), [], null, policy, Now, InstrumentCurrency: "USD", CostCurrency: "USD",
            ParameterTrials: 3, CodeVersion: "code-v1", EvidenceArtifact: evidence,
            RequiresCrossMarketAlignment: true,
            VenueClocks: [new("venue-a", "v1", "UTC"), new("venue-b", "v1", "UTC")],
            AlignmentPoints: [new(Now.AddDays(-3), new Dictionary<string, DateTimeOffset> { ["venue-a"] = Now.AddDays(-3), ["venue-b"] = Now.AddDays(-3) })],
            AlignmentPolicy: new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
            HypothesisEvidence: hypotheses);
    }
}
