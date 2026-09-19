using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

public sealed class RuntimeHistoricalCollectionsSnapshotStore
{
    private readonly object _gate = new();
    private readonly RuntimeHistoricalCollectionStateStore _store;
    private RuntimeHistoricalCollectionsSnapshot _current = RuntimeHistoricalCollectionsSnapshot.Unsupported();

    public RuntimeHistoricalCollectionsSnapshotStore(string databasePath)
    {
        _store = new RuntimeHistoricalCollectionStateStore(databasePath);
    }

    public RuntimeHistoricalCollectionsSnapshot Read()
    {
        lock (_gate) return _current;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var request = new HistoricalCollectionRequestV1(HistoricalCollectionPageV1<HistoricalOrderV1>.MaximumPageSize);
        var orders = await _store.ReadOrdersAsync(request, ct);
        var equity = await _store.ReadEquityAsync(request, ct);
        var backtests = await _store.ReadBacktestsAsync(request, ct);
        var skillCalls = await _store.ReadSkillCallsAsync(request, ct);
        var auditEvents = await _store.ReadAuditEventsAsync(request, ct);
        var postTradeReviews = await _store.ReadPostTradeReviewsAsync(request, ct);
        var reconciliations = await _store.ReadReconciliationsAsync(request, ct);
        lock (_gate) _current = new(orders, equity, backtests, skillCalls, auditEvents, postTradeReviews, reconciliations);
    }
}

public sealed record RuntimeHistoricalCollectionsSnapshot(
    HistoricalCollectionPageV1<HistoricalOrderV1> Orders,
    HistoricalCollectionPageV1<HistoricalEquityPointV1> Equity,
    HistoricalCollectionPageV1<HistoricalBacktestV1> Backtests,
    HistoricalCollectionPageV1<HistoricalSkillCallV1> SkillCalls,
    HistoricalCollectionPageV1<HistoricalAuditEventV1> AuditEvents,
    HistoricalCollectionPageV1<HistoricalPostTradeReviewV1> PostTradeReviews,
    HistoricalCollectionPageV1<HistoricalReconciliationV1> Reconciliations)
{
    public static RuntimeHistoricalCollectionsSnapshot Unsupported() => new(
        Page<HistoricalOrderV1>(HistoricalCollectionKindV1.Orders),
        Page<HistoricalEquityPointV1>(HistoricalCollectionKindV1.Equity),
        Page<HistoricalBacktestV1>(HistoricalCollectionKindV1.Backtests),
        Page<HistoricalSkillCallV1>(HistoricalCollectionKindV1.SkillCalls),
        Page<HistoricalAuditEventV1>(HistoricalCollectionKindV1.AuditEvents),
        Page<HistoricalPostTradeReviewV1>(HistoricalCollectionKindV1.PostTradeReviews),
        Page<HistoricalReconciliationV1>(HistoricalCollectionKindV1.Reconciliations));

    private static HistoricalCollectionPageV1<T> Page<T>(HistoricalCollectionKindV1 kind) =>
        new(HistoricalCollectionPageV1<T>.CurrentContractVersion, kind, RuntimeCollectionState.Unsupported, [], null, null, "local-agent-sqlite", "Historical collection has not been refreshed.");
}
