namespace 币安量化机器人.Services.Brokers;

public enum BrokerEnvironment
{
    Paper,
    Sandbox
}

public enum BrokerCapabilityStatus
{
    Unknown,
    Available,
    Unsupported,
    Stale,
    Error
}

public enum BrokerRiskGateOutcome
{
    Unknown,
    Rejected,
    Approved
}

public enum BrokerOrderSide { Buy, Sell }
public enum BrokerOrderType { Market, Limit, Stop, StopLimit }

public sealed record BrokerCapability(
    BrokerCapabilityStatus Status,
    bool CanReadAccounts,
    bool CanReadPositions,
    bool CanReadOrders,
    bool CanSubmitOrders,
    bool CanCancelOrders,
    DateTimeOffset CheckedAtUtc,
    string ReasonCode);

public sealed record BrokerResult<T>(
    BrokerCapabilityStatus Status,
    T? Value,
    DateTimeOffset ObservedAtUtc,
    string ReasonCode,
    string? CorrelationId=null,
    string? IdempotencyKey=null)
{
    public static BrokerResult<T> Available(T value,DateTimeOffset observedAtUtc) =>
        new(BrokerCapabilityStatus.Available,value,observedAtUtc,string.Empty);

    public static BrokerResult<T> Unsupported(
        string reasonCode,string? correlationId=null,string? idempotencyKey=null) =>
        new(
            BrokerCapabilityStatus.Unsupported,
            default,
            DateTimeOffset.UtcNow,
            reasonCode,
            correlationId,
            idempotencyKey);
}

public sealed record BrokerAccount(
    string AccountId,
    string Currency,
    decimal Cash,
    decimal Equity,
    decimal BuyingPower,
    DateTimeOffset ObservedAtUtc);

public sealed record BrokerPosition(
    string AccountId,
    string InstrumentId,
    decimal Quantity,
    decimal AveragePrice,
    decimal MarketPrice,
    string Currency,
    DateTimeOffset ObservedAtUtc);

public sealed record BrokerOrder(
    string OrderId,
    string ClientOrderId,
    string AccountId,
    string InstrumentId,
    BrokerOrderSide Side,
    BrokerOrderType Type,
    decimal Quantity,
    decimal? LimitPrice,
    string Status,
    DateTimeOffset ObservedAtUtc);

public sealed record BrokerOrderQuery(string? AccountId=null,string? InstrumentId=null,bool OpenOnly=false);

public sealed record BrokerOrderRequest(
    string ClientOrderId,
    string AccountId,
    string InstrumentId,
    BrokerOrderSide Side,
    BrokerOrderType Type,
    decimal Quantity,
    decimal? LimitPrice=null);

public sealed record BrokerMutationAuthorization(
    string RiskGateDecisionId,
    BrokerRiskGateOutcome RiskGateOutcome,
    string ReliableExecutorAttemptId,
    string CorrelationId,
    string IdempotencyKey,
    bool RoutedByReliableOrderExecutor);

public interface IBrokerReadSource
{
    Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct);
    Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct);
    Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct);
}

public interface IBrokerMutationBoundary
{
    Task<BrokerResult<BrokerOrder>> SubmitOrderAsync(
        BrokerOrderRequest request,BrokerMutationAuthorization authorization,CancellationToken ct);
    Task<BrokerResult<BrokerOrder>> CancelOrderAsync(
        string orderId,BrokerMutationAuthorization authorization,CancellationToken ct);
}

public interface IBrokerSandboxAdapter:IBrokerReadSource,IBrokerMutationBoundary
{
    string ProviderId { get; }
    BrokerEnvironment Environment { get; }
    BrokerCapability Capability { get; }
}
