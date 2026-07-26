using WpeAgent.CrossAssetResearch;

namespace WPE.Tests;

public sealed class CrossAssetResearchContractsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
    private readonly CrossAssetResearchService _service = new(() => Now);

    [Fact]
    public void MissingDataOrAuthorizationFailsClosedAsDraft()
    {
        var result = _service.Evaluate(Request([]) with { DataAuthorization = null });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
        Assert.False(result.Passed);
        Assert.Null(result.Metrics);
        Assert.Contains("data.authorization-insufficient", result.ReasonCodes);
        Assert.Contains("data.observations-insufficient", result.ReasonCodes);
    }

    [Fact]
    public void PassingResearchRemainsDraft()
    {
        var result = _service.Evaluate(Request(TrendingObservations()));

        Assert.Equal(CrossAssetResearchStatus.Available, result.Status);
        Assert.True(result.Passed);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
        Assert.Empty(result.ReasonCodes);
        Assert.NotNull(result.Metrics);
        Assert.True(result.Metrics.OutOfSampleReturn > 0);
        Assert.Equal(1, result.Metrics.WalkForwardScore);
        Assert.InRange(result.Metrics.MonteCarloLossProbability, 0, 1);
    }

    [Fact]
    public void EquityCorporateActionsRequireAdjustedHistory()
    {
        var observations = TrendingObservations().Select(x => x with { IsAdjustedForCorporateActions = false }).ToArray();
        var request = Request(observations) with
        {
            AssetClass = ResearchAssetClass.Equity,
            Session = new("America/New_York", new(9, 30), new(16, 0), new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }),
            CorporateActions = [new(CorporateActionKind.Split, Now.AddDays(-30), 2, Reference: "issuer-action-1")],
            HistoricalUniverseMembershipConfirmed = true
        };

        var result = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
        Assert.Contains("corporate-action.adjustment-missing", result.ReasonCodes);
    }

    [Fact]
    public void CompleteResearchRemainsDraftAndAccountsForCosts()
    {
        var observations = TrendingObservations();
        var free = _service.Evaluate(WithEvidence(Request(observations) with { Costs = new(0, 0) }));
        var costly = _service.Evaluate(Request(observations));

        Assert.Equal(CrossAssetStrategyLifecycle.Draft, costly.Lifecycle);
        Assert.True(costly.Passed);
        Assert.True(costly.Metrics!.TotalReturn < free.Metrics!.TotalReturn);
        Assert.Equal(3, costly.InputReferences.Count);
    }

    [Fact]
    public void OosWalkForwardAndMonteCarloAreReproducible()
    {
        var request = Request(TrendingObservations());

        var first = _service.Evaluate(request);
        var second = _service.Evaluate(request);

        Assert.Equal(first.Metrics!.OutOfSampleReturn, second.Metrics!.OutOfSampleReturn);
        Assert.Equal(first.Metrics.WalkForwardScore, second.Metrics.WalkForwardScore);
        Assert.Equal(first.Metrics.MonteCarloLossProbability, second.Metrics.MonteCarloLossProbability);
    }

    [Fact]
    public void EquitySessionMismatchIsUnsupported()
    {
        var observations = TrendingObservations().Select(x => x with { TimestampUtc = x.TimestampUtc.Date.AddHours(2) }).ToArray();
        var result = _service.Evaluate(EquityRequest(observations) with { CorporateActionCoverageConfirmed = true });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("session.observation-mismatch", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
    }

    [Fact]
    public void StaleDataIsUnsupported()
    {
        var result = _service.Evaluate(Request(TrendingObservations()) with { DataAsOfUtc = Now.AddDays(-8) });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("data.stale", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void MissingCorporateActionCoverageIsUnsupported()
    {
        var result = _service.Evaluate(EquityRequest(EquityObservations()));

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("corporate-action.coverage-missing", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
    }

    [Fact]
    public void LookAheadLeakageIsUnsupportedWithAuditReason()
    {
        var observations = TrendingObservations();
        observations[80] = observations[80] with { DataAvailableAtUtc = observations[80].TimestampUtc.AddMinutes(1) };

        var result = _service.Evaluate(Request(observations));

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.look-ahead-leakage", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void MissingHistoricalUniverseMembershipBlocksSurvivorshipBias()
    {
        var request = EquityRequest(EquityObservations()) with { CorporateActionCoverageConfirmed = true };

        var result = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.survivorship-bias", result.ReasonCodes);
    }

    [Fact]
    public void CostCurrencyCannotBeCombinedWithoutConversionEvidence()
    {
        var result = _service.Evaluate(Request(TrendingObservations()) with { CostCurrency = "JPY" });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("cost.currency-mismatch", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
    }

    [Fact]
    public void ParameterSearchWithoutEnoughIndependentSamplesIsUnsupported()
    {
        var result = _service.Evaluate(Request(TrendingObservations()) with { ParameterTrials = 6 });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.parameter-search-overfit", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void TimeZoneConvertedObservationsInsideSessionRemainDeterministicDraft()
    {
        var request = WithEvidence(EquityRequest(EquityObservations()) with
        {
            Session = new("Asia/Tokyo", new(18, 30), new(19, 30), new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>())),
            CorporateActionCoverageConfirmed = true,
            HistoricalUniverseMembershipConfirmed = true
        });

        var first = _service.Evaluate(request);
        var second = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Available, first.Status);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, first.Lifecycle);
        Assert.Equal(first.Metrics, second.Metrics);
    }

    [Fact]
    public void MissingResearchEvidenceIsUnsupportedDraft()
    {
        var result = _service.Evaluate(Request(TrendingObservations()) with { EvidenceArtifact = null });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
        Assert.Contains("research.evidence-missing", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void CanonicalEvidenceHashIsReproducibleAndContainsNoRawDataset()
    {
        var request = Request(TrendingObservations());
        var first = CrossAssetResearchService.CreateEvidenceArtifact(request with { EvidenceArtifact = null }, Now);
        var second = CrossAssetResearchService.CreateEvidenceArtifact(request with { EvidenceArtifact = null }, Now);

        Assert.Equal(first, second);
        Assert.Equal(first.ArtifactHash, CrossAssetResearchService.ComputeCanonicalHash(first));
        Assert.Equal(64, first.InputDatasetHash.Length);
        Assert.DoesNotContain("BTC-USD", first.ArtifactHash, StringComparison.Ordinal);
        Assert.Null(typeof(ResearchEvidenceArtifact).GetProperty("Observations"));
    }

    [Fact]
    public void TamperedEvidenceIsRejectedBeforeMetrics()
    {
        var request = Request(TrendingObservations());
        var tampered = request.EvidenceArtifact! with { StrategyVersion = "altered-version" };

        var result = _service.Evaluate(request with { EvidenceArtifact = tampered });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.evidence-tampered", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void PurgedAndEmbargoedOosWindowIsCapturedInEvidence()
    {
        var observations = TrendingObservations();
        var request = Request(observations);
        var artifact = request.EvidenceArtifact!;

        Assert.Equal(observations[109].TimestampUtc, artifact.OutOfSampleStartsAtUtc);
        Assert.Equal(observations[154].TimestampUtc, artifact.OutOfSampleEndsAtUtc);
        Assert.Equal(CrossAssetResearchStatus.Available, _service.Evaluate(request).Status);
    }

    [Fact]
    public void InsufficientSamplesAfterPurgeAndEmbargoAreUnsupported()
    {
        var request = Request(TrendingObservations());
        request = WithEvidence(request with { Policy = request.Policy with { OosPurgeObservations = 30, OosEmbargoObservations = 10 } });

        var result = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.temporal-isolation-insufficient", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void CrossMarketAlignmentRequiresExplicitPolicyAndEvidence()
    {
        var result = _service.Evaluate(Request(TrendingObservations()) with { RequiresCrossMarketAlignment = true });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.alignment-policy-missing", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
    }

    [Fact]
    public void ExplicitMultiVenueClockAlignmentRemainsDeterministicDraft()
    {
        var request = AlignedCrossMarketRequest();

        var first = _service.Evaluate(request);
        var second = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Available, first.Status);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, first.Lifecycle);
        Assert.Equal(first.Metrics, second.Metrics);
    }

    [Fact]
    public void TimestampDeviationOutsideExplicitWindowIsUnsupported()
    {
        var request = AlignedCrossMarketRequest();
        var points = request.AlignmentPoints!.ToArray();
        points[10] = points[10] with { VenueObservationTimesUtc = new Dictionary<string, DateTimeOffset> { ["crypto-venue"] = points[10].AnchorUtc, ["equity-venue"] = points[10].AnchorUtc.AddMinutes(8) } };
        request = WithEvidence(request with { AlignmentPoints = points });

        var result = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.alignment-window-conflict", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void ReusedVenueTimestampCannotImplicitlyForwardFill()
    {
        var request = AlignedCrossMarketRequest();
        var points = request.AlignmentPoints!.ToArray();
        var previous = points[9].VenueObservationTimesUtc["equity-venue"];
        points[10] = points[10] with { VenueObservationTimesUtc = new Dictionary<string, DateTimeOffset> { ["crypto-venue"] = points[10].AnchorUtc, ["equity-venue"] = previous } };
        request = WithEvidence(request with { AlignmentPoints = points });

        var result = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.forward-fill-forbidden", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
    }

    [Fact]
    public void MissingMultipleHypothesisEvidenceIsUnsupportedDraft()
    {
        var result = _service.Evaluate(Request(TrendingObservations()) with { HypothesisEvidence = null });

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.hypothesis-evidence-missing", result.ReasonCodes);
        Assert.Equal(CrossAssetStrategyLifecycle.Draft, result.Lifecycle);
    }

    [Fact]
    public void HoldoutReuseForSelectionIsUnsupported()
    {
        var request = Request(TrendingObservations());
        request = WithEvidence(request with { HypothesisEvidence = request.HypothesisEvidence! with { HoldoutUsedForSelection = true, HoldoutUntouched = false, HoldoutEvaluationCount = 2 } });

        var result = _service.Evaluate(request);

        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.holdout-reused", result.ReasonCodes);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void BonferroniRejectsSelectedTrialAboveFamilyWiseThreshold()
    {
        var request = Request(TrendingObservations()) with
        {
            ParameterTrials = 3,
            HypothesisEvidence = HypothesisEvidence(3, MultipleTestingCorrectionMethod.Bonferroni, [.001, .02, .20], 1)
        };
        request = WithEvidence(request);

        var result = _service.Evaluate(request);

        Assert.Equal(0.05 / 3, CrossAssetResearchService.CorrectedSignificanceThreshold(request.HypothesisEvidence!), 12);
        Assert.Equal(CrossAssetResearchStatus.Unsupported, result.Status);
        Assert.Contains("research.multiple-testing-threshold-not-met", result.ReasonCodes);
    }

    [Fact]
    public void BenjaminiHochbergThresholdIsDeterministic()
    {
        var evidence = HypothesisEvidence(3, MultipleTestingCorrectionMethod.BenjaminiHochberg, [.20, .001, .02], 2);

        var first = CrossAssetResearchService.CorrectedSignificanceThreshold(evidence);
        var second = CrossAssetResearchService.CorrectedSignificanceThreshold(evidence);

        Assert.Equal(.02, first, 12);
        Assert.Equal(first, second);
    }

    private static CrossAssetResearchRequest Request(IReadOnlyList<CrossAssetResearchObservation> observations)
    {
        var request = new CrossAssetResearchRequest(
            "research-1", "strategy-1", "v1", "BTC-USD", ResearchAssetClass.Crypto, observations,
            new("UTC", new(0, 0), new(23, 59), new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>()), true),
            new(.0002m, .0003m), [], new("licensed-feed", "btc-daily-v1", "license-42", true, Now.AddDays(1)),
            new(MinimumObservations: 120, MinimumOutOfSampleObservations: 30),
            DataAsOfUtc: Now,
            InstrumentCurrency: "USD",
            CostCurrency: "USD",
            CodeVersion: "wpe-cross-asset-v1",
            CorporateActionAdjustmentVersion: "adjustment-v1",
            SessionVersion: "session-v1",
            HypothesisEvidence: HypothesisEvidence(1, MultipleTestingCorrectionMethod.Bonferroni, [.01], 0));
        return WithEvidence(request);
    }

    private static CrossAssetResearchRequest WithEvidence(CrossAssetResearchRequest request)
        => request with { EvidenceArtifact = CrossAssetResearchService.CreateEvidenceArtifact(request with { EvidenceArtifact = null }, Now) };

    private static CrossAssetResearchRequest AlignedCrossMarketRequest()
    {
        var anchors = Enumerable.Range(0, 40).Select(i => Now.AddHours(-40 + i)).ToArray();
        var points = anchors.Select(anchor => new CrossMarketAlignmentPoint(anchor, new Dictionary<string, DateTimeOffset>
        {
            ["crypto-venue"] = anchor.AddSeconds(-30),
            ["equity-venue"] = anchor.AddSeconds(30)
        })).ToArray();
        return WithEvidence(Request(TrendingObservations()) with
        {
            RequiresCrossMarketAlignment = true,
            VenueClocks =
            [
                new("crypto-venue", "crypto-24x7-v1", "UTC"),
                new("equity-venue", "equity-regular-v2", "Asia/Tokyo")
            ],
            AlignmentPoints = points,
            AlignmentPolicy = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1))
        });
    }

    private static MultipleHypothesisEvidence HypothesisEvidence(int trials, MultipleTestingCorrectionMethod method, IReadOnlyList<double> pValues, int selectedTrialIndex)
        => new(trials, new string('a', 64), "minimum-corrected-p-value", new string('b', 64), true, false, 1, method, .05, pValues, selectedTrialIndex);

    private static CrossAssetResearchRequest EquityRequest(IReadOnlyList<CrossAssetResearchObservation> observations) => Request(observations) with
    {
        AssetClass = ResearchAssetClass.Equity,
        InstrumentId = "WPE",
        Session = new("UTC", new(9, 30), new(16, 0), new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>()))
    };

    private static CrossAssetResearchObservation[] EquityObservations() => Enumerable.Range(0, 160)
        .Select(i => Observation(new DateTimeOffset(Now.AddDays(-160 + i).UtcDateTime.Date.AddHours(10), TimeSpan.Zero), 100m + i, i == 0 ? 0 : 1))
        .ToArray();

    private static CrossAssetResearchObservation[] TrendingObservations() => Enumerable.Range(0, 160)
        .Select(i => Observation(Now.AddDays(-160 + i), 100m + i, i == 0 ? 0 : 1))
        .ToArray();

    private static CrossAssetResearchObservation Observation(DateTimeOffset timestamp, decimal close, double exposure)
        => new(timestamp, close, exposure, true, timestamp, timestamp);
}
