namespace 币安量化机器人.Services.Brokers;

public sealed record BrokerReconciliationOrder(
    string BrokerOrderId,string AccountId,string Venue,string Currency,
    BrokerOrderLifecycleStatus Status,DateTimeOffset AsOfUtc,string CorrelationId);
public sealed record BrokerReconciliationFill(
    string FillId,string BrokerOrderId,string AccountId,string Venue,string Currency,
    decimal Quantity,decimal Price,DateTimeOffset AsOfUtc,string CorrelationId);
public sealed record BrokerReconciliationBatch(
    string BatchId,long Sequence,string ProviderId,string AccountId,string Venue,string Currency,
    DateTimeOffset AsOfUtc,string CorrelationId,BrokerAccount Account,
    IReadOnlyList<BrokerReconciliationOrder> Orders,IReadOnlyList<BrokerReconciliationFill> Fills);
public sealed record BrokerReadOnlyProjection(
    string BatchId,long Sequence,string ProviderId,string AccountId,string Venue,string Currency,
    DateTimeOffset AsOfUtc,string CorrelationId,BrokerAccount Account,
    IReadOnlyList<BrokerReconciliationOrder> Orders,IReadOnlyList<BrokerReconciliationFill> Fills);
public sealed record BrokerReconciliationResult(
    BrokerCapabilityStatus Status,bool Applied,bool Idempotent,string ReasonCode,
    BrokerReadOnlyProjection? Projection);

public sealed class BrokerReconciliationCoordinator
{
    public BrokerReadOnlyProjection? Projection { get; private set; }
    private BrokerReconciliationBatch? _lastBatch;

    public BrokerReconciliationResult Reconcile(
        BrokerSandboxSelectionResult selection,BrokerReconciliationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(batch);
        if(selection.Status!=BrokerCapabilityStatus.Available||selection.Provider is null)
            return Rejected(BrokerCapabilityStatus.Unsupported,"BROKER_SELECTION_REQUIRED");
        if(_lastBatch is not null&&batch.BatchId==_lastBatch.BatchId)
            return batch==_lastBatch||Equivalent(batch,_lastBatch)
                ?new(BrokerCapabilityStatus.Available,false,true,"BROKER_RECONCILIATION_IDEMPOTENT",Projection)
                :Rejected(BrokerCapabilityStatus.Error,"BROKER_RECONCILIATION_DUPLICATE_CONFLICT");
        if(Missing(batch)||batch.Sequence<0)
            return Rejected(BrokerCapabilityStatus.Error,"BROKER_RECONCILIATION_MISSING");
        if(Projection is not null&&(batch.Sequence<=Projection.Sequence||batch.AsOfUtc<=Projection.AsOfUtc))
            return Rejected(BrokerCapabilityStatus.Error,"BROKER_RECONCILIATION_OUT_OF_ORDER");
        if(!string.Equals(selection.Provider.ProviderId,batch.ProviderId,StringComparison.OrdinalIgnoreCase))
            return Rejected(BrokerCapabilityStatus.Unsupported,"BROKER_RECONCILIATION_PROVIDER_MISMATCH");
        if(!FactsMatch(batch))
            return Rejected(BrokerCapabilityStatus.Error,"BROKER_RECONCILIATION_FACT_MISMATCH");
        if(batch.Orders.Any(order=>order.Status==BrokerOrderLifecycleStatus.Unknown||!Enum.IsDefined(order.Status)))
            return Rejected(BrokerCapabilityStatus.Unsupported,"BROKER_RECONCILIATION_UNKNOWN");
        if(batch.Fills.Any(fill=>fill.Quantity<=0||fill.Price<=0||
            !batch.Orders.Any(order=>order.BrokerOrderId==fill.BrokerOrderId)))
            return Rejected(BrokerCapabilityStatus.Error,"BROKER_RECONCILIATION_FILL_INVALID");

        var projection=new BrokerReadOnlyProjection(
            batch.BatchId,batch.Sequence,batch.ProviderId,batch.AccountId,batch.Venue,batch.Currency,
            batch.AsOfUtc,batch.CorrelationId,batch.Account,batch.Orders.ToArray(),batch.Fills.ToArray());
        Projection=projection;
        _lastBatch=batch with { Orders=batch.Orders.ToArray(),Fills=batch.Fills.ToArray() };
        return new(BrokerCapabilityStatus.Available,true,false,"BROKER_RECONCILIATION_APPLIED",projection);
    }

    private static bool Missing(BrokerReconciliationBatch b)=>
        string.IsNullOrWhiteSpace(b.BatchId)||string.IsNullOrWhiteSpace(b.ProviderId)||
        string.IsNullOrWhiteSpace(b.AccountId)||string.IsNullOrWhiteSpace(b.Venue)||
        string.IsNullOrWhiteSpace(b.Currency)||string.IsNullOrWhiteSpace(b.CorrelationId);
    private static bool FactsMatch(BrokerReconciliationBatch b)=>
        b.Account.AccountId==b.AccountId&&b.Account.Currency==b.Currency&&b.Account.ObservedAtUtc==b.AsOfUtc&&
        b.Orders.All(x=>x.AccountId==b.AccountId&&x.Venue==b.Venue&&x.Currency==b.Currency&&
            x.AsOfUtc==b.AsOfUtc&&x.CorrelationId==b.CorrelationId&&!string.IsNullOrWhiteSpace(x.BrokerOrderId))&&
        b.Fills.All(x=>x.AccountId==b.AccountId&&x.Venue==b.Venue&&x.Currency==b.Currency&&
            x.AsOfUtc==b.AsOfUtc&&x.CorrelationId==b.CorrelationId&&!string.IsNullOrWhiteSpace(x.FillId));
    private static bool Equivalent(BrokerReconciliationBatch a,BrokerReconciliationBatch b)=>
        a with { Orders=[],Fills=[] }==b with { Orders=[],Fills=[] }&&
        a.Orders.SequenceEqual(b.Orders)&&a.Fills.SequenceEqual(b.Fills);
    private BrokerReconciliationResult Rejected(BrokerCapabilityStatus status,string reason)=>
        new(status,false,false,reason,Projection);
}
