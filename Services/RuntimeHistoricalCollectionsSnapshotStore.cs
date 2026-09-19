using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

public sealed class RuntimeHistoricalCollectionsSnapshotStore
{
    public static readonly TimeSpan FullRevalidationInterval = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1,1);
    private readonly RuntimeHistoricalCollectionStateStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private RuntimeHistoricalCollectionsSnapshot _current = RuntimeHistoricalCollectionsSnapshot.Unsupported();
    private HistoricalCollectionChangeVector? _lastChangeVector;
    private DateTimeOffset? _lastFullRefreshAtUtc;

    public RuntimeHistoricalCollectionsSnapshotStore(string databasePath):this(databasePath,null){}

    internal RuntimeHistoricalCollectionsSnapshotStore(string databasePath,Func<DateTimeOffset>? utcNow)
    {
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
        _store = new RuntimeHistoricalCollectionStateStore(databasePath,_utcNow);
    }

    public RuntimeHistoricalCollectionsSnapshot Read()
    {
        lock (_gate) return _current;
    }

    public async Task<object> ReadPageAsync(HistoricalCollectionKindV1 kind,string? cursor,CancellationToken ct=default)
    {
        var request=new HistoricalCollectionRequestV1(HistoricalCollectionPageV1<HistoricalOrderV1>.MaximumPageSize,cursor);
        return kind switch
        {
            HistoricalCollectionKindV1.Orders=>await _store.ReadOrdersAsync(request,ct),
            HistoricalCollectionKindV1.Equity=>await _store.ReadEquityAsync(request,ct),
            HistoricalCollectionKindV1.Backtests=>await _store.ReadBacktestsAsync(request,ct),
            HistoricalCollectionKindV1.SkillCalls=>await _store.ReadSkillCallsAsync(request,ct),
            HistoricalCollectionKindV1.AuditEvents=>await _store.ReadAuditEventsAsync(request,ct),
            HistoricalCollectionKindV1.PostTradeReviews=>await _store.ReadPostTradeReviewsAsync(request,ct),
            HistoricalCollectionKindV1.Reconciliations=>await _store.ReadReconciliationsAsync(request,ct),
            _=>throw new ArgumentOutOfRangeException(nameof(kind),kind,"Unsupported historical collection kind.")
        };
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var now=_utcNow().ToUniversalTime();
            var changes=await _store.ReadChangeVectorAsync(ct);
            RuntimeHistoricalCollectionsSnapshot current;
            HistoricalCollectionChangeVector? previous;
            DateTimeOffset? lastFull;
            lock(_gate){current=_current;previous=_lastChangeVector;lastFull=_lastFullRefreshAtUtc;}

            var full=previous is null||lastFull is null||now<lastFull.Value||now-lastFull.Value>=FullRevalidationInterval;
            var request=new HistoricalCollectionRequestV1(HistoricalCollectionPageV1<HistoricalOrderV1>.MaximumPageSize);
            var orders=full||current.Orders.State==RuntimeCollectionState.Error||previous!.Orders!=changes.Orders?await _store.ReadOrdersAsync(request,ct):current.Orders;
            var equity=full||current.Equity.State==RuntimeCollectionState.Error||previous!.Equity!=changes.Equity?await _store.ReadEquityAsync(request,ct):current.Equity;
            var backtests=full||current.Backtests.State==RuntimeCollectionState.Error||previous!.Backtests!=changes.Backtests?await _store.ReadBacktestsAsync(request,ct):current.Backtests;
            var skillCalls=full||current.SkillCalls.State==RuntimeCollectionState.Error||previous!.SkillCalls!=changes.SkillCalls?await _store.ReadSkillCallsAsync(request,ct):current.SkillCalls;
            var auditEvents=full||current.AuditEvents.State==RuntimeCollectionState.Error||previous!.AuditEvents!=changes.AuditEvents?await _store.ReadAuditEventsAsync(request,ct):current.AuditEvents;
            var postTradeReviews=full||current.PostTradeReviews.State==RuntimeCollectionState.Error||previous!.PostTradeReviews!=changes.PostTradeReviews?await _store.ReadPostTradeReviewsAsync(request,ct):current.PostTradeReviews;
            var reconciliations=full||current.Reconciliations.State==RuntimeCollectionState.Error||previous!.Reconciliations!=changes.Reconciliations?await _store.ReadReconciliationsAsync(request,ct):current.Reconciliations;
            var snapshot=new RuntimeHistoricalCollectionsSnapshot(orders,equity,backtests,skillCalls,auditEvents,postTradeReviews,reconciliations);

            lock(_gate)
            {
                _current=snapshot;
                _lastChangeVector=changes;
                if(full)_lastFullRefreshAtUtc=now;
            }
        }
        finally{_refreshGate.Release();}
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
