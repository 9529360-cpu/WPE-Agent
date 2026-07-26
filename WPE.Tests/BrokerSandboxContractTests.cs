using System.Reflection;
using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class BrokerSandboxContractTests
{
    [Theory]
    [InlineData(BrokerEnvironment.Paper)]
    [InlineData(BrokerEnvironment.Sandbox)]
    public void Adapter_IsReadOnlyInNonLiveEnvironments(BrokerEnvironment environment)
    {
        var adapter=new BrokerSandboxAdapter("unconnected-broker",environment,new RecordingReadSource());

        Assert.Equal(environment,adapter.Environment);
        Assert.True(adapter.Capability.CanReadAccounts);
        Assert.True(adapter.Capability.CanReadPositions);
        Assert.True(adapter.Capability.CanReadOrders);
        Assert.False(adapter.Capability.CanSubmitOrders);
        Assert.False(adapter.Capability.CanCancelOrders);
        Assert.Equal("BROKER_READ_ONLY",adapter.Capability.ReasonCode);
    }

    [Fact]
    public async Task Queries_AreDelegatedToReadOnlySource()
    {
        var source=new RecordingReadSource();
        var adapter=new BrokerSandboxAdapter("unconnected-broker",BrokerEnvironment.Sandbox,source);

        Assert.Equal(BrokerCapabilityStatus.Available,(await adapter.GetAccountsAsync(default)).Status);
        Assert.Equal(BrokerCapabilityStatus.Available,(await adapter.GetPositionsAsync("paper-account",default)).Status);
        Assert.Equal(BrokerCapabilityStatus.Available,(await adapter.GetOrdersAsync(new(OpenOnly:true),default)).Status);
        Assert.Equal(1,source.AccountCalls);
        Assert.Equal(1,source.PositionCalls);
        Assert.Equal(1,source.OrderCalls);
    }

    [Fact]
    public async Task Mutations_AreUnsupportedWithoutRealBrokerIntegration()
    {
        var source=new RecordingReadSource();
        var adapter=new BrokerSandboxAdapter("unconnected-broker",BrokerEnvironment.Paper,source);
        var authorization=ApprovedAuthorization();
        var request=new BrokerOrderRequest(
            "client-1","paper-account","US:AAPL",BrokerOrderSide.Buy,BrokerOrderType.Limit,1m,100m);

        var submit=await adapter.SubmitOrderAsync(request,authorization,default);
        var cancel=await adapter.CancelOrderAsync("order-1",authorization,default);

        Assert.Equal(BrokerCapabilityStatus.Unsupported,submit.Status);
        Assert.Equal(BrokerCapabilityStatus.Unsupported,cancel.Status);
        Assert.Equal(BrokerSandboxAdapter.MutationUnsupportedReason,submit.ReasonCode);
        Assert.Equal("trace-1",submit.CorrelationId);
        Assert.Equal("idempotency-1",submit.IdempotencyKey);
        Assert.Equal("trace-1",cancel.CorrelationId);
        Assert.Equal("idempotency-1",cancel.IdempotencyKey);
        Assert.Null(submit.Value);
        Assert.Null(cancel.Value);
        Assert.Equal(0,source.MutationCalls);
    }

    [Theory]
    [InlineData(BrokerRiskGateOutcome.Unknown)]
    [InlineData(BrokerRiskGateOutcome.Rejected)]
    public async Task RiskGateNotApproved_IsRejectedBeforeDefaultUnsupported(
        BrokerRiskGateOutcome outcome)
    {
        var adapter=new BrokerSandboxAdapter(
            "unconnected-broker",BrokerEnvironment.Sandbox,new RecordingReadSource());
        var authorization=ApprovedAuthorization() with { RiskGateOutcome=outcome };

        var result=await adapter.SubmitOrderAsync(OrderRequest(),authorization,default);

        Assert.Equal(BrokerCapabilityStatus.Unsupported,result.Status);
        Assert.Equal(BrokerSandboxAdapter.RiskGateRejectedReason,result.ReasonCode);
        Assert.Equal(authorization.CorrelationId,result.CorrelationId);
        Assert.Equal(authorization.IdempotencyKey,result.IdempotencyKey);
    }

    [Fact]
    public async Task MutationOutsideReliableOrderExecutor_IsRejected()
    {
        var adapter=new BrokerSandboxAdapter(
            "unconnected-broker",BrokerEnvironment.Paper,new RecordingReadSource());
        var authorization=ApprovedAuthorization() with { RoutedByReliableOrderExecutor=false };

        var result=await adapter.CancelOrderAsync("order-1",authorization,default);

        Assert.Equal(BrokerCapabilityStatus.Unsupported,result.Status);
        Assert.Equal(BrokerSandboxAdapter.ReliableExecutorRequiredReason,result.ReasonCode);
    }

    [Theory]
    [InlineData("", "idempotency-1")]
    [InlineData("trace-1", "")]
    public async Task MissingCorrelationOrIdempotency_FailsClosed(
        string correlationId,string idempotencyKey)
    {
        var adapter=new BrokerSandboxAdapter(
            "unconnected-broker",BrokerEnvironment.Sandbox,new RecordingReadSource());
        var authorization=ApprovedAuthorization() with
        {
            CorrelationId=correlationId,
            IdempotencyKey=idempotencyKey
        };

        var result=await adapter.SubmitOrderAsync(OrderRequest(),authorization,default);

        Assert.Equal(BrokerCapabilityStatus.Unsupported,result.Status);
        Assert.Equal(BrokerSandboxAdapter.MutationContextInvalidReason,result.ReasonCode);
    }

    [Fact]
    public void BrokerContract_DoesNotInheritCryptoLeverageOrMarginSurface()
    {
        var methods=typeof(IBrokerSandboxAdapter).GetMethods()
            .Concat(typeof(IBrokerReadSource).GetMethods())
            .Concat(typeof(IBrokerMutationBoundary).GetMethods())
            .Select(method=>method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(methods,name=>name.Contains("Leverage",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods,name=>name.Contains("Margin",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IBrokerSandboxAdapter).GetInterfaces(),type=>
            string.Equals(type.Name,"IBrokerProvider",StringComparison.Ordinal));
    }

    [Fact]
    public void MutationBoundary_RequiresRiskGateAndReliableExecutorReferences()
    {
        var authorization=typeof(IBrokerMutationBoundary).GetMethods()
            .SelectMany(method=>method.GetParameters())
            .Where(parameter=>parameter.ParameterType==typeof(BrokerMutationAuthorization))
            .ToArray();

        Assert.Equal(2,authorization.Length);
        var properties=typeof(BrokerMutationAuthorization).GetProperties(BindingFlags.Public|BindingFlags.Instance)
            .Select(property=>property.Name).ToArray();
        Assert.Contains(nameof(BrokerMutationAuthorization.RiskGateDecisionId),properties);
        Assert.Contains(nameof(BrokerMutationAuthorization.RiskGateOutcome),properties);
        Assert.Contains(nameof(BrokerMutationAuthorization.ReliableExecutorAttemptId),properties);
        Assert.Contains(nameof(BrokerMutationAuthorization.CorrelationId),properties);
        Assert.Contains(nameof(BrokerMutationAuthorization.IdempotencyKey),properties);
        Assert.Contains(nameof(BrokerMutationAuthorization.RoutedByReliableOrderExecutor),properties);
    }

    private static BrokerMutationAuthorization ApprovedAuthorization() => new(
        "risk-1",
        BrokerRiskGateOutcome.Approved,
        "executor-1",
        "trace-1",
        "idempotency-1",
        RoutedByReliableOrderExecutor:true);

    private static BrokerOrderRequest OrderRequest() => new(
        "client-1","paper-account","US:AAPL",BrokerOrderSide.Buy,BrokerOrderType.Limit,1m,100m);

    private sealed class RecordingReadSource:IBrokerReadSource
    {
        public int AccountCalls { get; private set; }
        public int PositionCalls { get; private set; }
        public int OrderCalls { get; private set; }
        public int MutationCalls => 0;

        public Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct)
        {
            AccountCalls++;
            return Task.FromResult(BrokerResult<IReadOnlyList<BrokerAccount>>.Available([],DateTimeOffset.UtcNow));
        }

        public Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct)
        {
            PositionCalls++;
            return Task.FromResult(BrokerResult<IReadOnlyList<BrokerPosition>>.Available([],DateTimeOffset.UtcNow));
        }

        public Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct)
        {
            OrderCalls++;
            return Task.FromResult(BrokerResult<IReadOnlyList<BrokerOrder>>.Available([],DateTimeOffset.UtcNow));
        }
    }
}
