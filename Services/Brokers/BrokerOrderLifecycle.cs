namespace 币安量化机器人.Services.Brokers;

public enum BrokerOrderLifecycleStatus
{
    Accepted,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Unknown
}

public sealed record BrokerOrderLifecycleUpdate(
    string BrokerOrderId,
    BrokerOrderLifecycleStatus Status,
    long Sequence,
    decimal FilledQuantity,
    DateTimeOffset ObservedAtUtc,
    string CorrelationId);

public sealed record BrokerOrderLifecycleSnapshot(
    string BrokerOrderId,
    BrokerOrderLifecycleStatus Status,
    long Sequence,
    decimal FilledQuantity,
    DateTimeOffset ObservedAtUtc,
    string CorrelationId,
    bool CanRequestCancel,
    bool CanRequestReplace);

public sealed record BrokerOrderLifecycleResult(
    bool Applied,
    bool FailClosed,
    string ReasonCode,
    BrokerOrderLifecycleSnapshot? Snapshot);

public sealed class BrokerOrderLifecycle
{
    public const string AppliedReason="BROKER_ORDER_LIFECYCLE_APPLIED";
    public const string UnknownObservedReason="BROKER_ORDER_UNKNOWN_FAIL_CLOSED";
    public const string MissingOrderIdReason="BROKER_ORDER_ID_MISSING";
    public const string UnknownStatusReason="BROKER_ORDER_STATUS_UNKNOWN";
    public const string DuplicateUpdateReason="BROKER_ORDER_UPDATE_DUPLICATE";
    public const string OutOfOrderReason="BROKER_ORDER_UPDATE_OUT_OF_ORDER";
    public const string OrderIdMismatchReason="BROKER_ORDER_ID_MISMATCH";
    public const string InvalidQuantityReason="BROKER_ORDER_FILLED_QUANTITY_INVALID";

    public BrokerOrderLifecycleSnapshot? Snapshot { get; private set; }

    public BrokerOrderLifecycleResult Apply(BrokerOrderLifecycleUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if(string.IsNullOrWhiteSpace(update.BrokerOrderId))
            return Rejected(MissingOrderIdReason);
        if(!Enum.IsDefined(update.Status))
            return Rejected(UnknownStatusReason);
        if(update.Sequence<0||update.FilledQuantity<0)
            return Rejected(InvalidQuantityReason);

        if(Snapshot is not null)
        {
            if(!string.Equals(Snapshot.BrokerOrderId,update.BrokerOrderId,StringComparison.Ordinal))
                return Rejected(OrderIdMismatchReason);
            if(update.Sequence==Snapshot.Sequence)
                return Rejected(DuplicateUpdateReason);
            if(update.Sequence<Snapshot.Sequence||!CanTransition(Snapshot,update))
                return Rejected(OutOfOrderReason);
        }

        var canRequestMutation=update.Status is
            BrokerOrderLifecycleStatus.Accepted or BrokerOrderLifecycleStatus.PartiallyFilled;
        Snapshot=new BrokerOrderLifecycleSnapshot(
            update.BrokerOrderId,
            update.Status,
            update.Sequence,
            update.FilledQuantity,
            update.ObservedAtUtc,
            update.CorrelationId,
            CanRequestCancel:canRequestMutation,
            CanRequestReplace:canRequestMutation);
        if(update.Status==BrokerOrderLifecycleStatus.Unknown)
            return new BrokerOrderLifecycleResult(true,true,UnknownObservedReason,Snapshot);
        return new BrokerOrderLifecycleResult(true,false,AppliedReason,Snapshot);
    }

    private static bool CanTransition(
        BrokerOrderLifecycleSnapshot current,BrokerOrderLifecycleUpdate next)
    {
        if(current.Status is BrokerOrderLifecycleStatus.Filled or
           BrokerOrderLifecycleStatus.Cancelled or
           BrokerOrderLifecycleStatus.Rejected or
           BrokerOrderLifecycleStatus.Unknown)
            return false;

        if(next.FilledQuantity<current.FilledQuantity)
            return false;

        return current.Status switch
        {
            BrokerOrderLifecycleStatus.Accepted => next.Status is
                BrokerOrderLifecycleStatus.PartiallyFilled or
                BrokerOrderLifecycleStatus.Filled or
                BrokerOrderLifecycleStatus.Cancelled or
                BrokerOrderLifecycleStatus.Rejected or
                BrokerOrderLifecycleStatus.Unknown,
            BrokerOrderLifecycleStatus.PartiallyFilled => next.Status is
                BrokerOrderLifecycleStatus.PartiallyFilled or
                BrokerOrderLifecycleStatus.Filled or
                BrokerOrderLifecycleStatus.Cancelled or
                BrokerOrderLifecycleStatus.Rejected or
                BrokerOrderLifecycleStatus.Unknown,
            _ => false
        }&&!(current.Status==next.Status&&current.FilledQuantity==next.FilledQuantity);
    }

    private BrokerOrderLifecycleResult Rejected(string reasonCode) =>
        new(false,true,reasonCode,Snapshot);
}
