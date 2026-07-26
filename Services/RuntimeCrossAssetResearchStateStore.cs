using WpeAgent.CrossAssetResearch;
using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

/// <summary>Single host-owned, read-only projection of cross-asset research contract results.</summary>
public sealed class RuntimeCrossAssetResearchStateStore
{
    private readonly object _gate = new();
    private RuntimeCrossAssetResearchState _current = RuntimeCrossAssetResearchState.Unsupported("Cross-asset research has not been published.");

    public RuntimeCrossAssetResearchState Read()
    {
        lock (_gate) return _current;
    }

    public void Publish(CrossAssetResearchRequest request, CrossAssetResearchResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var item = Project(request, result);
        lock (_gate)
            _current = new(RuntimeCollectionState.Available, [item], result.EvaluatedAtUtc.ToUniversalTime(), null);
    }

    public void MarkUnsupported(string message)
    {
        lock (_gate) _current = RuntimeCrossAssetResearchState.Unsupported(message);
    }

    private static RuntimeCrossAssetResearchV1 Project(CrossAssetResearchRequest request, CrossAssetResearchResult result)
    {
        if (!string.Equals(request.ResearchId, result.ResearchId, StringComparison.Ordinal)
            || !string.Equals(request.StrategyId, result.StrategyId, StringComparison.Ordinal)
            || result.Lifecycle != CrossAssetStrategyLifecycle.Draft
            || result.Status is not (CrossAssetResearchStatus.Available or CrossAssetResearchStatus.Unsupported))
            throw new ArgumentException("Research request and result do not form a supported Draft projection.", nameof(result));

        var evidence = request.EvidenceArtifact;
        var metrics = result.Metrics is null ? null : new RuntimeCrossAssetResearchMetricsV1(
            result.Metrics.Observations, result.Metrics.Trades, result.Metrics.TotalReturn,
            result.Metrics.OutOfSampleReturn, result.Metrics.MaximumDrawdown, result.Metrics.Sharpe,
            result.Metrics.WalkForwardScore, result.Metrics.MonteCarloLossProbability);
        var alignment = new RuntimeResearchAlignmentSummaryV1(
            request.RequiresCrossMarketAlignment,
            request.VenueClocks?.Count ?? 0,
            request.AlignmentPoints?.Count ?? 0,
            request.AlignmentPolicy?.SynchronizationWindow.TotalSeconds,
            request.AlignmentPolicy?.MaximumTimestampDeviation.TotalSeconds,
            request.AlignmentPolicy?.AllowForwardFill ?? false);
        var hypothesis = request.HypothesisEvidence;
        var multipleTesting = hypothesis is null ? null : new RuntimeMultipleTestingSummaryV1(
            hypothesis.TrialCount,
            hypothesis.CorrectionMethod.ToString(),
            hypothesis.NominalAlpha,
            CrossAssetResearchService.CorrectedSignificanceThreshold(hypothesis),
            hypothesis.HoldoutUntouched,
            hypothesis.HoldoutUsedForSelection,
            hypothesis.HoldoutEvaluationCount);

        return new(
            result.ResearchId, result.StrategyId, request.InstrumentId, request.AssetClass.ToString(),
            result.Status.ToString(), result.Lifecycle.ToString(), result.Passed,
            result.ReasonCodes.Distinct(StringComparer.Ordinal).ToArray(), metrics,
            evidence?.ArtifactHash ?? string.Empty, evidence?.InputDatasetHash ?? string.Empty,
            evidence?.ParameterHash ?? string.Empty, evidence?.CodeVersion ?? request.CodeVersion,
            evidence?.StrategyVersion ?? request.StrategyVersion, evidence?.Seed ?? 0,
            EvidenceTime(evidence?.OutOfSampleStartsAtUtc), EvidenceTime(evidence?.OutOfSampleEndsAtUtc),
            request.Policy.OosPurgeObservations, request.Policy.OosEmbargoObservations,
            alignment, multipleTesting, result.EvaluatedAtUtc.ToUniversalTime());
    }

    private static DateTimeOffset? EvidenceTime(DateTimeOffset? value) =>
        value is null || value == DateTimeOffset.MinValue ? null : value.Value.ToUniversalTime();
}

public sealed record RuntimeCrossAssetResearchState(
    RuntimeCollectionState State,
    IReadOnlyList<RuntimeCrossAssetResearchV1> Items,
    DateTimeOffset? UpdatedAt,
    string? Message)
{
    public static RuntimeCrossAssetResearchState Unsupported(string message) =>
        new(RuntimeCollectionState.Unsupported, [], null, message);
}
