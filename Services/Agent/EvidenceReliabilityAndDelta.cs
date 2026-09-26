namespace 币安量化机器人.Services.Agent;

public enum EvidenceSourceRunState
{
    Available,
    Fallback,
    Degraded,
    Unavailable,
    Error
}

public sealed record EvidenceSourceRunV1(
    string SourceKey,
    string ProviderId,
    EvidenceSourceRunState State,
    DateTime ObservedAtUtc,
    string Detail,
    string? FallbackFrom = null)
{
    public bool Usable => State is EvidenceSourceRunState.Available or EvidenceSourceRunState.Fallback or EvidenceSourceRunState.Degraded;
}

public enum EvidenceDeltaKind
{
    PriceMove,
    StructureShift,
    SourceDegradation,
    SourceRecovery,
    BreakingNews
}

public enum EvidenceDeltaSeverity
{
    Moderate,
    High,
    Critical
}

public sealed record EvidenceDeltaSignalV1(
    string Key,
    EvidenceDeltaKind Kind,
    EvidenceDeltaSeverity Severity,
    string Subject,
    string Detail);

public sealed record EvidenceDeltaSnapshotV1(
    DateTime CurrentObservedAtUtc,
    DateTime? PreviousObservedAtUtc,
    bool HasBaseline,
    IReadOnlyList<EvidenceDeltaSignalV1> Signals)
{
    public int CriticalCount => Signals.Count(x => x.Severity == EvidenceDeltaSeverity.Critical);
    public int SourceDegradationCount => Signals.Count(x => x.Kind == EvidenceDeltaKind.SourceDegradation);
    public bool HasMaterialChange => Signals.Count > 0;
}

/// <summary>
/// Observation-only comparison between consecutive evidence packs.
/// It cannot authorize, size, route, or execute trades.
/// </summary>
public static class EvidenceDeltaEngineV1
{
    public static EvidenceDeltaSnapshotV1 Compare(
        EvidencePack current,
        EvidencePack? previous,
        decimal priceMoveThresholdPercent = .75m)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (priceMoveThresholdPercent <= 0)
            throw new ArgumentOutOfRangeException(nameof(priceMoveThresholdPercent));

        var currentAt = AsUtc(current.CollectedAt);
        if (previous is null)
            return new(currentAt, null, false, Array.Empty<EvidenceDeltaSignalV1>());

        var signals = new List<EvidenceDeltaSignalV1>();
        CompareMarkets(current, previous, priceMoveThresholdPercent, signals);
        CompareSources(current, previous, signals);
        CompareBreakingNews(current, previous, signals);

        return new(
            currentAt,
            AsUtc(previous.CollectedAt),
            true,
            signals
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.OrderByDescending(signal => signal.Severity).First())
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .ToArray());
    }

    private static void CompareMarkets(
        EvidencePack current,
        EvidencePack previous,
        decimal priceMoveThresholdPercent,
        List<EvidenceDeltaSignalV1> signals)
    {
        foreach (var pair in current.Markets.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!previous.Markets.TryGetValue(pair.Key, out var prior))
                continue;

            var now = pair.Value;
            if (prior.Price > 0 && now.Price > 0)
            {
                var pct = (now.Price - prior.Price) / Math.Abs(prior.Price) * 100m;
                var absolute = Math.Abs(pct);
                if (absolute >= priceMoveThresholdPercent)
                {
                    var severity = absolute >= priceMoveThresholdPercent * 4
                        ? EvidenceDeltaSeverity.Critical
                        : absolute >= priceMoveThresholdPercent * 2
                            ? EvidenceDeltaSeverity.High
                            : EvidenceDeltaSeverity.Moderate;
                    signals.Add(new(
                        $"market:{pair.Key}:price",
                        EvidenceDeltaKind.PriceMove,
                        severity,
                        pair.Key,
                        $"price {prior.Price:F4} -> {now.Price:F4} ({pct:+0.00;-0.00;0.00}%)"));
                }
            }

            var previousStructure = MarketStructureIntelligence.Analyze(prior);
            var currentStructure = MarketStructureIntelligence.Analyze(now);
            if (!previousStructure.Available || !currentStructure.Available)
                continue;

            var biasChanged = previousStructure.HigherTimeframeBias != currentStructure.HigherTimeframeBias;
            var scenarioChanged = previousStructure.Scenario != currentStructure.Scenario;
            var eventChanged = previousStructure.FifteenMinute.Event != currentStructure.FifteenMinute.Event;
            if (!biasChanged && !scenarioChanged && !eventChanged)
                continue;

            var severity = OppositeDirectionalBias(previousStructure.HigherTimeframeBias, currentStructure.HigherTimeframeBias)
                ? EvidenceDeltaSeverity.Critical
                : biasChanged || OppositeDirectionalScenario(previousStructure.Scenario, currentStructure.Scenario)
                    ? EvidenceDeltaSeverity.High
                    : EvidenceDeltaSeverity.Moderate;

            signals.Add(new(
                $"market:{pair.Key}:structure",
                EvidenceDeltaKind.StructureShift,
                severity,
                pair.Key,
                $"bias {previousStructure.HigherTimeframeBias} -> {currentStructure.HigherTimeframeBias}; " +
                $"scenario {previousStructure.Scenario} -> {currentStructure.Scenario}; " +
                $"event {previousStructure.FifteenMinute.Event} -> {currentStructure.FifteenMinute.Event}"));
        }
    }

    private static void CompareSources(
        EvidencePack current,
        EvidencePack previous,
        List<EvidenceDeltaSignalV1> signals)
    {
        var prior = previous.SourceRuns
            .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

        foreach (var now in current.SourceRuns
                     .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                     .Select(x => x.Last())
                     .OrderBy(x => x.SourceKey, StringComparer.Ordinal))
        {
            if (!prior.TryGetValue(now.SourceKey, out var before))
                continue;

            var beforeRank = SourceRank(before.State);
            var nowRank = SourceRank(now.State);
            if (nowRank < beforeRank)
            {
                var severity = IsTradingMarketSource(now.SourceKey)
                    ? EvidenceDeltaSeverity.High
                    : EvidenceDeltaSeverity.Moderate;
                signals.Add(new(
                    $"source:{now.SourceKey}:degraded",
                    EvidenceDeltaKind.SourceDegradation,
                    severity,
                    now.SourceKey,
                    $"{before.State} -> {now.State}; provider={now.ProviderId}; {now.Detail}"));
            }
            else if (nowRank > beforeRank)
            {
                signals.Add(new(
                    $"source:{now.SourceKey}:recovered",
                    EvidenceDeltaKind.SourceRecovery,
                    EvidenceDeltaSeverity.Moderate,
                    now.SourceKey,
                    $"{before.State} -> {now.State}; provider={now.ProviderId}"));
            }
        }
    }

    private static void CompareBreakingNews(
        EvidencePack current,
        EvidencePack previous,
        List<EvidenceDeltaSignalV1> signals)
    {
        var previousIds = previous.News
            .Where(x => x.IsBreaking)
            .Select(NewsIdentity)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in current.News.Where(x => x.IsBreaking))
        {
            var identity = NewsIdentity(item);
            if (identity.Length == 0 || previousIds.Contains(identity))
                continue;

            signals.Add(new(
                $"news:{identity}",
                EvidenceDeltaKind.BreakingNews,
                EvidenceDeltaSeverity.High,
                item.AffectedAssets.FirstOrDefault() ?? "market",
                $"new breaking item from {item.Source}: {item.Title}"));
        }
    }

    private static string NewsIdentity(NewsEvidence item)
    {
        if (!string.IsNullOrWhiteSpace(item.DuplicateGroup))
            return item.DuplicateGroup.Trim();
        if (!string.IsNullOrWhiteSpace(item.Url))
            return item.Url.Trim();
        if (string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Title))
            return string.Empty;
        return $"{item.Source.Trim()}|{item.Title.Trim()}";
    }

    private static int SourceRank(EvidenceSourceRunState state) => state switch
    {
        EvidenceSourceRunState.Available => 4,
        EvidenceSourceRunState.Fallback => 3,
        EvidenceSourceRunState.Degraded => 2,
        EvidenceSourceRunState.Unavailable => 1,
        EvidenceSourceRunState.Error => 0,
        _ => 0
    };

    private static bool IsTradingMarketSource(string sourceKey) =>
        sourceKey.EndsWith(":market", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sourceKey, "realtime", StringComparison.OrdinalIgnoreCase);

    private static bool OppositeDirectionalBias(MarketStructureBias before, MarketStructureBias now) =>
        before == MarketStructureBias.Bullish && now == MarketStructureBias.Bearish ||
        before == MarketStructureBias.Bearish && now == MarketStructureBias.Bullish;

    private static bool OppositeDirectionalScenario(MarketStructureScenario before, MarketStructureScenario now) =>
        IsLong(before) && IsShort(now) || IsShort(before) && IsLong(now);

    private static bool IsLong(MarketStructureScenario value) => value is
        MarketStructureScenario.TrendPullbackLong or
        MarketStructureScenario.RangeReversionLong or
        MarketStructureScenario.BreakoutRetestLong;

    private static bool IsShort(MarketStructureScenario value) => value is
        MarketStructureScenario.TrendPullbackShort or
        MarketStructureScenario.RangeReversionShort or
        MarketStructureScenario.BreakoutRetestShort;

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
