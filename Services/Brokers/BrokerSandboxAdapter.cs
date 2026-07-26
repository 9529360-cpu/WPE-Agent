namespace 币安量化机器人.Services.Brokers;

public sealed class BrokerSandboxAdapter:IBrokerSandboxAdapter
{
    public const string MutationUnsupportedReason="BROKER_MUTATION_UNSUPPORTED";
    public const string RiskGateRejectedReason="BROKER_RISK_GATE_REJECTED";
    public const string ReliableExecutorRequiredReason="BROKER_RELIABLE_EXECUTOR_REQUIRED";
    public const string MutationContextInvalidReason="BROKER_MUTATION_CONTEXT_INVALID";
    private readonly IBrokerReadSource _readSource;

    public BrokerSandboxAdapter(
        string providerId,
        BrokerEnvironment environment,
        IBrokerReadSource readSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ProviderId=providerId;
        Environment=environment;
        _readSource=readSource??throw new ArgumentNullException(nameof(readSource));
        Capability=new BrokerCapability(
            BrokerCapabilityStatus.Available,
            CanReadAccounts:true,
            CanReadPositions:true,
            CanReadOrders:true,
            CanSubmitOrders:false,
            CanCancelOrders:false,
            DateTimeOffset.UtcNow,
            "BROKER_READ_ONLY");
    }

    public string ProviderId { get; }
    public BrokerEnvironment Environment { get; }
    public BrokerCapability Capability { get; }

    public Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct) =>
        _readSource.GetAccountsAsync(ct);

    public Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct) =>
        _readSource.GetPositionsAsync(accountId,ct);

    public Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct) =>
        _readSource.GetOrdersAsync(query,ct);

    public Task<BrokerResult<BrokerOrder>> SubmitOrderAsync(
        BrokerOrderRequest request,BrokerMutationAuthorization authorization,CancellationToken ct) =>
        Task.FromResult(UnsupportedMutation(authorization));

    public Task<BrokerResult<BrokerOrder>> CancelOrderAsync(
        string orderId,BrokerMutationAuthorization authorization,CancellationToken ct) =>
        Task.FromResult(UnsupportedMutation(authorization));

    private static BrokerResult<BrokerOrder> UnsupportedMutation(BrokerMutationAuthorization? authorization)
    {
        if(authorization is null||
           authorization.RiskGateOutcome!=BrokerRiskGateOutcome.Approved||
           string.IsNullOrWhiteSpace(authorization.RiskGateDecisionId))
            return Unsupported(RiskGateRejectedReason,authorization);

        if(!authorization.RoutedByReliableOrderExecutor||
           string.IsNullOrWhiteSpace(authorization.ReliableExecutorAttemptId))
            return Unsupported(ReliableExecutorRequiredReason,authorization);

        if(string.IsNullOrWhiteSpace(authorization.CorrelationId)||
           string.IsNullOrWhiteSpace(authorization.IdempotencyKey))
            return Unsupported(MutationContextInvalidReason,authorization);

        return Unsupported(MutationUnsupportedReason,authorization);
    }

    private static BrokerResult<BrokerOrder> Unsupported(
        string reasonCode,BrokerMutationAuthorization? authorization) =>
        BrokerResult<BrokerOrder>.Unsupported(
            reasonCode,
            authorization?.CorrelationId,
            authorization?.IdempotencyKey);
}
