using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.CrossAssetResearch;

public sealed class CrossAssetResearchService(Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public static ResearchEvidenceArtifact CreateEvidenceArtifact(CrossAssetResearchRequest request, DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var observations = request.Observations.OrderBy(x => x.TimestampUtc).ToArray();
        var bounds = ResearchTemporalValidationV1.Bounds(Math.Max(0, observations.Length - 1), TemporalPolicy(request.Policy));
        var startsAt = observations.Length < 2 || bounds.OosCount <= 0 ? DateTimeOffset.MinValue : observations[bounds.OosStart + 1].TimestampUtc;
        var endsAt = observations.Length < 2 || bounds.OosCount <= 0 ? DateTimeOffset.MinValue : observations[bounds.OosEndExclusive].TimestampUtc;
        var artifact = new ResearchEvidenceArtifact(
            Hash(CanonicalDataset(request, observations)),
            Hash(CanonicalParameters(request)),
            request.CodeVersion,
            request.StrategyVersion,
            StableSeed(request.ResearchId),
            startsAt,
            endsAt,
            request.Costs ?? new ResearchCostModel(0, 0),
            request.CorporateActionAdjustmentVersion,
            request.SessionVersion,
            generatedAtUtc.ToUniversalTime(),
            string.Empty);
        return artifact with { ArtifactHash = ComputeCanonicalHash(artifact) };
    }

    public static string ComputeCanonicalHash(ResearchEvidenceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Hash(string.Join("\n",
            "research-evidence-v1",
            artifact.InputDatasetHash,
            artifact.ParameterHash,
            artifact.CodeVersion,
            artifact.StrategyVersion,
            artifact.Seed.ToString(CultureInfo.InvariantCulture),
            artifact.OutOfSampleStartsAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            artifact.OutOfSampleEndsAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            CanonicalCosts(artifact.CostModel),
            artifact.CorporateActionAdjustmentVersion,
            artifact.SessionVersion,
            artifact.GeneratedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
    }

    public static double CorrectedSignificanceThreshold(MultipleHypothesisEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.TrialCount < 1 || evidence.NominalAlpha is <= 0 or >= 1 || evidence.TrialPValues.Count != evidence.TrialCount) return 0;
        return evidence.CorrectionMethod switch
        {
            MultipleTestingCorrectionMethod.Bonferroni => evidence.NominalAlpha / evidence.TrialCount,
            MultipleTestingCorrectionMethod.Conservative => evidence.NominalAlpha / (2 * evidence.TrialCount),
            MultipleTestingCorrectionMethod.BenjaminiHochberg => BenjaminiHochbergCutoff(evidence.TrialPValues, evidence.NominalAlpha),
            _ => 0
        };
    }

    public CrossAssetResearchResult Evaluate(CrossAssetResearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _utcNow();
        var reasons = Validate(request, now);
        if (reasons.Count > 0)
            return Result(request, CrossAssetResearchStatus.Unsupported, CrossAssetStrategyLifecycle.Draft, false, null, reasons, now);

        try
        {
            var observations = request.Observations.OrderBy(x => x.TimestampUtc).ToArray();
            var returns = NetReturns(observations, request.Costs!);
            var bounds = ResearchTemporalValidationV1.Bounds(returns.Length, TemporalPolicy(request.Policy));
            var outOfSample = returns.Skip(bounds.OosStart).Take(bounds.OosCount).ToArray();
            var all = Metrics(returns);
            var oos = Metrics(outOfSample);
            var walkForward = WalkForward(returns, request.Policy.WalkForwardFolds);
            var monteCarlo = MonteCarloLossProbability(returns, request.Policy.MonteCarloRuns, StableSeed(request.ResearchId));
            var trades = observations.Skip(1).Zip(observations, (current, previous) => Math.Abs(current.TargetExposure - previous.TargetExposure) > 1e-9).Count(x => x);
            var metrics = new CrossAssetBacktestMetrics(observations.Length, trades, all.TotalReturn, oos.TotalReturn, all.MaximumDrawdown, all.Sharpe, walkForward, monteCarlo);
            var failed = new List<string>();
            if (request.Policy.RequirePositiveOutOfSampleReturn && oos.TotalReturn <= 0) failed.Add("research.out-of-sample-not-positive");
            if (all.MaximumDrawdown > request.Policy.MaximumDrawdown) failed.Add("research.drawdown-limit");
            if (monteCarlo > request.Policy.MaximumMonteCarloLossProbability) failed.Add("research.monte-carlo-loss-limit");
            if (walkForward < .5) failed.Add("research.walk-forward-unstable");
            var passed = failed.Count == 0;
            return Result(request, CrossAssetResearchStatus.Available, CrossAssetStrategyLifecycle.Draft, passed, metrics, failed, now);
        }
        catch (Exception)
        {
            return Result(request, CrossAssetResearchStatus.Unsupported, CrossAssetStrategyLifecycle.Draft, false, null, ["research.evaluation-failed"], now);
        }
    }

    private static List<string> Validate(CrossAssetResearchRequest request, DateTimeOffset now)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ResearchId) || string.IsNullOrWhiteSpace(request.StrategyId) || string.IsNullOrWhiteSpace(request.InstrumentId)) reasons.Add("research.identity-missing");
        if (request.DataAuthorization is null || !request.DataAuthorization.HistoricalUseAllowed || request.DataAuthorization.ValidUntilUtc < now || string.IsNullOrWhiteSpace(request.DataAuthorization.LicenseReference)) reasons.Add("data.authorization-insufficient");
        if (request.Costs is null || request.Costs.CommissionRate < 0 || request.Costs.SlippageRate < 0 || request.Costs.FixedCostPerTrade < 0 || request.Costs.BorrowRatePerDay < 0) reasons.Add("cost.model-missing-or-invalid");
        if (string.IsNullOrWhiteSpace(request.InstrumentCurrency) || string.IsNullOrWhiteSpace(request.CostCurrency) || !request.InstrumentCurrency.Equals(request.CostCurrency, StringComparison.OrdinalIgnoreCase)) reasons.Add("cost.currency-mismatch");
        if (request.Session is null || string.IsNullOrWhiteSpace(request.Session.TimeZoneId) || (!request.Session.IsContinuous && request.Session.TradingDays.Count == 0)) reasons.Add("session.definition-missing");
        if (request.AssetClass == ResearchAssetClass.Crypto && request.Session is { IsContinuous: false }) reasons.Add("session.crypto-must-be-continuous");
        if (request.Session is not null && request.Observations is { Count: > 0 } && !ObservationsMatchSession(request.Observations, request.Session)) reasons.Add("session.observation-mismatch");
        if (request.Observations is null || request.Observations.Count < request.Policy.MinimumObservations) reasons.Add("data.observations-insufficient");
        if (request.Policy.WalkForwardFolds < 2 || request.Policy.MonteCarloRuns < 10) reasons.Add("validation.policy-invalid");
        if (request.ParameterTrials < 1 || request.ParameterTrials > request.Policy.MaximumParameterTrials || request.Observations is not null && request.Observations.Count < request.ParameterTrials * request.Policy.MinimumObservationsPerParameterTrial) reasons.Add("research.parameter-search-overfit");
        if (request.Observations is { Count: > 0 } && (request.Observations.Any(x => x.Close <= 0 || !double.IsFinite(x.TargetExposure) || Math.Abs(x.TargetExposure) > 1) || request.Observations.Select(x => x.TimestampUtc).Distinct().Count() != request.Observations.Count)) reasons.Add("data.observation-invalid");
        var temporalPoints=request.Observations?.Select(x=>new ResearchTemporalPoint(x.TimestampUtc,x.DataAvailableAtUtc,x.SignalGeneratedAtUtc)).ToArray();
        reasons.AddRange(ResearchTemporalValidationV1.Validate(temporalPoints,Math.Max(0,(request.Observations?.Count??0)-1),TemporalPolicy(request.Policy)));
        var maximumStaleness = request.Policy.MaximumDataStaleness ?? TimeSpan.FromDays(7);
        if (request.DataAsOfUtc is null || request.DataAsOfUtc > now || now - request.DataAsOfUtc > maximumStaleness) reasons.Add("data.stale");
        if (request.AssetClass == ResearchAssetClass.Equity && !request.CorporateActionCoverageConfirmed) reasons.Add("corporate-action.coverage-missing");
        if (request.AssetClass == ResearchAssetClass.Equity && !request.HistoricalUniverseMembershipConfirmed) reasons.Add("research.survivorship-bias");
        if (request.AssetClass == ResearchAssetClass.Equity && request.Observations is { Count: > 0 } && request.CorporateActions.Count > 0 && request.Observations.Any(x => !x.IsAdjustedForCorporateActions)) reasons.Add("corporate-action.adjustment-missing");
        ValidateCrossMarketAlignment(request, reasons);
        ValidateMultipleHypotheses(request, reasons);
        ValidateEvidence(request, now, reasons);
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ValidateEvidence(CrossAssetResearchRequest request, DateTimeOffset now, List<string> reasons)
    {
        var artifact = request.EvidenceArtifact;
        if (artifact is null) { reasons.Add("research.evidence-missing"); return; }
        if (!IsSha256(artifact.InputDatasetHash) || !IsSha256(artifact.ParameterHash) || !IsSha256(artifact.ArtifactHash)
            || string.IsNullOrWhiteSpace(artifact.CodeVersion) || string.IsNullOrWhiteSpace(artifact.StrategyVersion)
            || string.IsNullOrWhiteSpace(artifact.CorporateActionAdjustmentVersion) || string.IsNullOrWhiteSpace(artifact.SessionVersion)
            || artifact.OutOfSampleStartsAtUtc == DateTimeOffset.MinValue || artifact.OutOfSampleEndsAtUtc < artifact.OutOfSampleStartsAtUtc
            || artifact.GeneratedAtUtc > now || artifact.CostModel is null)
        {
            reasons.Add("research.evidence-incomplete");
            return;
        }
        var expected = CreateEvidenceArtifact(request with { EvidenceArtifact = null }, artifact.GeneratedAtUtc);
        if (!FixedEquals(artifact.ArtifactHash, ComputeCanonicalHash(artifact))
            || !FixedEquals(artifact.ArtifactHash, expected.ArtifactHash))
            reasons.Add("research.evidence-tampered");
    }

    private static void ValidateCrossMarketAlignment(CrossAssetResearchRequest request, List<string> reasons)
    {
        if (!request.RequiresCrossMarketAlignment) return;
        var clocks = request.VenueClocks;
        var points = request.AlignmentPoints;
        var policy = request.AlignmentPolicy;
        if (policy is null) { reasons.Add("research.alignment-policy-missing"); return; }
        if (policy.SynchronizationWindow <= TimeSpan.Zero || policy.MaximumTimestampDeviation < TimeSpan.Zero || policy.MaximumTimestampDeviation > policy.SynchronizationWindow)
            reasons.Add("research.alignment-policy-invalid");
        if (policy.AllowForwardFill) reasons.Add("research.forward-fill-forbidden");
        if (clocks is null || clocks.Count < 2 || points is null || points.Count == 0)
        {
            reasons.Add("research.alignment-evidence-missing");
            return;
        }
        var venueIds = clocks.Select(x => x.VenueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (venueIds.Count != clocks.Count || clocks.Any(x => string.IsNullOrWhiteSpace(x.VenueId) || string.IsNullOrWhiteSpace(x.SessionVersion) || !ValidTimeZone(x.TimeZoneId)))
            reasons.Add("research.market-clock-conflict");
        var seenByVenue = venueIds.ToDictionary(x => x, _ => new HashSet<DateTimeOffset>(), StringComparer.OrdinalIgnoreCase);
        foreach (var point in points.OrderBy(x => x.AnchorUtc))
        {
            if (point.VenueObservationTimesUtc is null || !venueIds.SetEquals(point.VenueObservationTimesUtc.Keys))
            {
                reasons.Add("research.alignment-evidence-missing");
                continue;
            }
            var times = point.VenueObservationTimesUtc.Values.Select(x => x.ToUniversalTime()).ToArray();
            if (times.Any(x => (x - point.AnchorUtc.ToUniversalTime()).Duration() > policy.MaximumTimestampDeviation)
                || times.Max() - times.Min() > policy.SynchronizationWindow)
                reasons.Add("research.alignment-window-conflict");
            foreach (var pair in point.VenueObservationTimesUtc)
                if (!seenByVenue[pair.Key].Add(pair.Value.ToUniversalTime())) reasons.Add("research.forward-fill-forbidden");
        }
    }

    private static bool ValidTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static void ValidateMultipleHypotheses(CrossAssetResearchRequest request, List<string> reasons)
    {
        var evidence = request.HypothesisEvidence;
        if (evidence is null) { reasons.Add("research.hypothesis-evidence-missing"); return; }
        if (evidence.TrialCount != request.ParameterTrials || evidence.TrialCount < 1 || !IsSha256(evidence.SearchSpaceHash)
            || !IsSha256(evidence.HoldoutDatasetHash) || string.IsNullOrWhiteSpace(evidence.SelectionRule)
            || !Enum.IsDefined(evidence.CorrectionMethod) || evidence.NominalAlpha is <= 0 or >= 1
            || evidence.TrialPValues.Count != evidence.TrialCount || evidence.TrialPValues.Any(x => !double.IsFinite(x) || x is < 0 or > 1)
            || evidence.SelectedTrialIndex < 0 || evidence.SelectedTrialIndex >= evidence.TrialCount)
        {
            reasons.Add("research.hypothesis-evidence-invalid");
            return;
        }
        if (!evidence.HoldoutUntouched || evidence.HoldoutUsedForSelection || evidence.HoldoutEvaluationCount != 1
            || request.EvidenceArtifact is not null && FixedEquals(evidence.HoldoutDatasetHash, request.EvidenceArtifact.InputDatasetHash))
            reasons.Add("research.holdout-reused");
        var threshold = CorrectedSignificanceThreshold(evidence);
        if (threshold <= 0 || evidence.TrialPValues[evidence.SelectedTrialIndex] > threshold)
            reasons.Add("research.multiple-testing-threshold-not-met");
    }

    private static double BenjaminiHochbergCutoff(IReadOnlyList<double> pValues, double alpha)
    {
        var ordered = pValues.Order().ToArray();
        var cutoff = 0d;
        for (var i = 0; i < ordered.Length; i++) if (ordered[i] <= alpha * (i + 1) / ordered.Length) cutoff = ordered[i];
        return cutoff;
    }

    private static bool ObservationsMatchSession(IReadOnlyList<CrossAssetResearchObservation> observations, TradingSessionDefinition session)
    {
        if (session.IsContinuous) return true;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZoneId);
            return observations.All(observation =>
            {
                var local = TimeZoneInfo.ConvertTime(observation.TimestampUtc, zone);
                var time = TimeOnly.FromDateTime(local.DateTime);
                return session.TradingDays.Contains(local.DayOfWeek) && time >= session.OpensAt && time <= session.ClosesAt;
            });
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static double[] NetReturns(IReadOnlyList<CrossAssetResearchObservation> values, ResearchCostModel costs)
    {
        var result = new double[values.Count - 1];
        for (var i = 1; i < values.Count; i++)
        {
            var previous = values[i - 1];
            var current = values[i];
            var gross = previous.TargetExposure * (double)(current.Close / previous.Close - 1);
            var turnover = Math.Abs(current.TargetExposure - previous.TargetExposure);
            var variableCost = turnover * (double)(costs.CommissionRate + costs.SlippageRate);
            var fixedCost = turnover > 1e-9 ? (double)(costs.FixedCostPerTrade / previous.Close) : 0;
            var borrowDays = previous.TargetExposure < 0 ? Math.Max(0, (current.TimestampUtc - previous.TimestampUtc).TotalDays) : 0;
            result[i - 1] = gross - variableCost - fixedCost - borrowDays * (double)costs.BorrowRatePerDay;
        }
        return result;
    }

    private static (double TotalReturn, double MaximumDrawdown, double Sharpe) Metrics(IReadOnlyList<double> returns)
    {
        if (returns.Count == 0) return (0, 1, 0);
        var equity = 1d; var peak = 1d; var drawdown = 0d;
        foreach (var value in returns) { equity *= Math.Max(.0001, 1 + value); peak = Math.Max(peak, equity); drawdown = Math.Max(drawdown, (peak - equity) / peak); }
        var average = returns.Average();
        var deviation = Math.Sqrt(returns.Select(x => (x - average) * (x - average)).Average());
        return (equity - 1, drawdown, deviation > 0 ? average / deviation * Math.Sqrt(252) : average > 0 ? 9 : 0);
    }

    private static double WalkForward(IReadOnlyList<double> returns, int folds)
    {
        var size = Math.Max(1, returns.Count / folds);
        var positive = 0; var evaluated = 0;
        for (var fold = 0; fold < folds; fold++) { var segment = returns.Skip(fold * size).Take(fold == folds - 1 ? returns.Count : size).ToArray(); if (segment.Length == 0) continue; evaluated++; if (Metrics(segment).TotalReturn > 0) positive++; }
        return evaluated == 0 ? 0 : positive / (double)evaluated;
    }

    private static double MonteCarloLossProbability(IReadOnlyList<double> returns, int runs, int seed)
    {
        var random = new Random(seed); var losses = 0;
        for (var run = 0; run < runs; run++) { var equity = 1d; for (var i = 0; i < returns.Count; i++) equity *= Math.Max(.0001, 1 + returns[random.Next(returns.Count)]); if (equity < 1) losses++; }
        return losses / (double)runs;
    }

    private static int StableSeed(string value) { unchecked { var hash = 17; foreach (var c in value ?? string.Empty) hash = hash * 31 + c; return hash; } }

    private static string CanonicalDataset(CrossAssetResearchRequest request, IEnumerable<CrossAssetResearchObservation> observations)
    {
        var observationPayload = string.Join("\n", observations.Select(x => string.Join("|",
        x.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        x.Close.ToString(CultureInfo.InvariantCulture),
        x.TargetExposure.ToString("R", CultureInfo.InvariantCulture),
        x.IsAdjustedForCorporateActions ? "1" : "0",
        x.DataAvailableAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "missing",
        x.SignalGeneratedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "missing")));
        var clocks = string.Join("\n", (request.VenueClocks ?? []).OrderBy(x => x.VenueId, StringComparer.OrdinalIgnoreCase).Select(x => $"clock|{x.VenueId}|{x.SessionVersion}|{x.TimeZoneId}"));
        var points = string.Join("\n", (request.AlignmentPoints ?? []).OrderBy(x => x.AnchorUtc).Select(point => $"anchor|{point.AnchorUtc.ToUniversalTime():O}|{string.Join(';', point.VenueObservationTimesUtc.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value.ToUniversalTime():O}"))}"));
        return string.Join("\n", observationPayload, clocks, points);
    }

    private static string CanonicalParameters(CrossAssetResearchRequest request) => string.Join("|",
        request.Policy.TrainingFraction.ToString("R", CultureInfo.InvariantCulture), request.Policy.MinimumObservations,
        request.Policy.MinimumOutOfSampleObservations, request.Policy.WalkForwardFolds, request.Policy.MonteCarloRuns,
        request.Policy.MaximumDrawdown.ToString("R", CultureInfo.InvariantCulture),
        request.Policy.MaximumMonteCarloLossProbability.ToString("R", CultureInfo.InvariantCulture),
        request.Policy.RequirePositiveOutOfSampleReturn ? "1" : "0", request.Policy.MaximumDataStaleness?.Ticks ?? -1,
        request.Policy.MaximumParameterTrials, request.Policy.MinimumObservationsPerParameterTrial,
        request.Policy.OosPurgeObservations, request.Policy.OosEmbargoObservations, request.ParameterTrials,
        request.RequiresCrossMarketAlignment ? "1" : "0", request.AlignmentPolicy?.SynchronizationWindow.Ticks ?? -1,
        request.AlignmentPolicy?.MaximumTimestampDeviation.Ticks ?? -1, request.AlignmentPolicy?.AllowForwardFill == true ? "1" : "0",
        request.HypothesisEvidence?.TrialCount ?? -1, request.HypothesisEvidence?.SearchSpaceHash ?? "missing",
        request.HypothesisEvidence?.SelectionRule ?? "missing", request.HypothesisEvidence?.HoldoutDatasetHash ?? "missing",
        request.HypothesisEvidence?.HoldoutUntouched == true ? "1" : "0", request.HypothesisEvidence?.HoldoutUsedForSelection == true ? "1" : "0",
        request.HypothesisEvidence?.HoldoutEvaluationCount ?? -1, request.HypothesisEvidence?.CorrectionMethod.ToString() ?? "missing",
        request.HypothesisEvidence?.NominalAlpha.ToString("R", CultureInfo.InvariantCulture) ?? "missing",
        request.HypothesisEvidence?.SelectedTrialIndex ?? -1,
        string.Join(',', request.HypothesisEvidence?.TrialPValues.Select(x => x.ToString("R", CultureInfo.InvariantCulture)) ?? []));

    private static string CanonicalCosts(ResearchCostModel costs) => string.Join("|",
        costs.CommissionRate.ToString(CultureInfo.InvariantCulture), costs.SlippageRate.ToString(CultureInfo.InvariantCulture),
        costs.FixedCostPerTrade.ToString(CultureInfo.InvariantCulture), costs.BorrowRatePerDay.ToString(CultureInfo.InvariantCulture));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static ResearchTemporalIsolationPolicy TemporalPolicy(CrossAssetValidationPolicy policy)
        =>new(policy.TrainingFraction,policy.MinimumOutOfSampleObservations,policy.OosPurgeObservations,policy.OosEmbargoObservations);

    private static CrossAssetResearchResult Result(CrossAssetResearchRequest request, CrossAssetResearchStatus status, CrossAssetStrategyLifecycle lifecycle, bool passed, CrossAssetBacktestMetrics? metrics, IReadOnlyList<string> reasons, DateTimeOffset now)
        => new(request.ResearchId, request.StrategyId, status, lifecycle, passed, metrics, reasons, request.DataAuthorization is null ? [] : [request.DataAuthorization.ProviderId, request.DataAuthorization.DatasetId, request.DataAuthorization.LicenseReference], now);
}
