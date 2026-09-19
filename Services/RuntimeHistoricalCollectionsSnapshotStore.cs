using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

public sealed class RuntimeHistoricalCollectionsSnapshotStore
{
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1,1);
    private readonly RuntimeHistoricalCollectionStateStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private RuntimeHistoricalCollectionsSnapshot _current = RuntimeHistoricalCollectionsSnapshot.Unsupported();
    private DateTimeOffset? _lastRefreshAtUtc;

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

    public async Task RefreshAsync(CancellationToken ct)
    {
        var now=_utcNow().ToUniversalTime();
        lock(_gate)if(ShouldSkip(now))return;
        await _refreshGate.WaitAsync(ct);
        try
        {
            now=_utcNow().ToUniversalTime();
            lock(_gate)if(ShouldSkip(now))return;
            var request = new HistoricalCollectionRequestV1(HistoricalCollectionPageV1<HistoricalOrderV1>.MaximumPageSize);
            var orders = await _store.ReadOrdersAsync(request, ct);
            var equity = await _store.ReadEquityAsync(request, ct);
            var backtests = await _store.ReadBacktestsAsync(request, ct);
            var skillCalls = await _store.ReadSkillCallsAsync(request, ct);
            var auditEvents = await _store.ReadAuditEventsAsync(request, ct);
            var postTradeReviews = await _store.ReadPostTradeReviewsAsync(request, ct);
            var reconciliations = await _store.ReadReconciliationsAsync(request, ct);
            var snapshot=new RuntimeHistoricalCollectionsSnapshot(orders, equity, backtests, skillCalls, auditEvents, postTradeReviews, reconciliations);
            var hasError=new[]{orders.State,equity.State,backtests.State,skillCalls.State,auditEvents.State,postTradeReviews.State,reconciliations.State}.Any(state=>state==RuntimeCollectionState.Error);
            lock (_gate)
            {
                _current = snapshot;
                _lastRefreshAtUtc=hasError?null:_utcNow().ToUniversalTime();
            }
        }
        finally{_refreshGate.Release();}
    }

    private bool ShouldSkip(DateTimeOffset now)=>_lastRefreshAtUtc is { } last&&now>=last&&now-last<MinimumRefreshInterval;
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
