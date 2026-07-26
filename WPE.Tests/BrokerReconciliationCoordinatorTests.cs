using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class BrokerReconciliationCoordinatorTests
{
    private static readonly DateTimeOffset AsOf=DateTimeOffset.FromUnixTimeSeconds(3000);

    [Fact]
    public void ValidSelectedBatch_AtomicallyUpdatesProjection()
    {
        var coordinator=new BrokerReconciliationCoordinator();
        var result=coordinator.Reconcile(Selected(),Batch());
        Assert.True(result.Applied);
        Assert.Equal(BrokerCapabilityStatus.Available,result.Status);
        Assert.Equal("batch-1",coordinator.Projection!.BatchId);
        Assert.Single(coordinator.Projection.Orders);
        Assert.Single(coordinator.Projection.Fills);
        Assert.Equal(0,Provider.MutationCalls);
    }

    [Fact]
    public void FailedSelection_BlocksWholeBatch()
    {
        var coordinator=new BrokerReconciliationCoordinator();
        var result=coordinator.Reconcile(new(BrokerCapabilityStatus.Unsupported,null,"blocked",AsOf),Batch());
        AssertRejected(result,"BROKER_SELECTION_REQUIRED");
        Assert.Null(coordinator.Projection);
    }

    [Fact]
    public void IdenticalDuplicate_IsIdempotent()
    {
        var coordinator=new BrokerReconciliationCoordinator();
        var batch=Batch();
        var first=coordinator.Reconcile(Selected(),batch);
        var duplicate=coordinator.Reconcile(Selected(),batch);
        Assert.True(first.Applied);
        Assert.False(duplicate.Applied);
        Assert.True(duplicate.Idempotent);
        Assert.Same(first.Projection,duplicate.Projection);
    }

    [Fact]
    public void ConflictingDuplicate_IsAtomicError()
    {
        var coordinator=new BrokerReconciliationCoordinator();
        var original=coordinator.Reconcile(Selected(),Batch()).Projection;
        var result=coordinator.Reconcile(Selected(),Batch() with { Currency="JPY" });
        AssertRejected(result,"BROKER_RECONCILIATION_DUPLICATE_CONFLICT");
        Assert.Same(original,coordinator.Projection);
    }

    [Theory]
    [InlineData(0,3001)]
    [InlineData(2,3000)]
    public void SequenceOrAsOfRegression_IsAtomicError(long sequence,long unixSeconds)
    {
        var coordinator=new BrokerReconciliationCoordinator();
        var original=coordinator.Reconcile(Selected(),Batch()).Projection;
        var next=Batch("batch-2",sequence,DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        var result=coordinator.Reconcile(Selected(),next);
        AssertRejected(result,"BROKER_RECONCILIATION_OUT_OF_ORDER");
        Assert.Same(original,coordinator.Projection);
    }

    [Fact]
    public void UnknownOrderRejectsWholeBatch()
    {
        var coordinator=new BrokerReconciliationCoordinator();
        var batch=Batch() with { Orders=[Batch().Orders[0] with { Status=BrokerOrderLifecycleStatus.Unknown }] };
        var result=coordinator.Reconcile(Selected(),batch);
        Assert.Equal(BrokerCapabilityStatus.Unsupported,result.Status);
        Assert.Null(coordinator.Projection);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("venue")]
    [InlineData("currency")]
    [InlineData("correlation")]
    public void FactMismatchRejectsWholeBatch(string field)
    {
        var batch=Batch();
        batch=field switch
        {
            "provider"=>batch with { ProviderId="other" },
            "account"=>batch with { AccountId="other" },
            "venue"=>batch with { Venue="OTHER" },
            "currency"=>batch with { Currency="JPY" },
            _=>batch with { CorrelationId="other" }
        };
        var result=new BrokerReconciliationCoordinator().Reconcile(Selected(),batch);
        Assert.False(result.Applied);
        Assert.Null(result.Projection);
    }

    [Fact]
    public void InvalidFillRejectsWholeBatchWithoutMutation()
    {
        var batch=Batch() with { Fills=[Batch().Fills[0] with { BrokerOrderId="missing" }] };
        var result=new BrokerReconciliationCoordinator().Reconcile(Selected(),batch);
        AssertRejected(result,"BROKER_RECONCILIATION_FILL_INVALID");
        Assert.Equal(0,Provider.MutationCalls);
    }

    private static BrokerReconciliationBatch Batch(string id="batch-1",long sequence=1,DateTimeOffset? asOf=null)
    {
        var at=asOf??AsOf;
        return new(id,sequence,"fake-provider","paper-account","XNAS","USD",at,"trace-1",
            new("paper-account","USD",100m,100m,100m,at),
            [new("order-1","paper-account","XNAS","USD",BrokerOrderLifecycleStatus.Filled,at,"trace-1")],
            [new("fill-1","order-1","paper-account","XNAS","USD",1m,10m,at,"trace-1")]);
    }
    private static BrokerSandboxSelectionResult Selected()=>new(
        BrokerCapabilityStatus.Available,Provider,string.Empty,AsOf);
    private static readonly ReadOnlyFakeProvider Provider=new();
    private static void AssertRejected(BrokerReconciliationResult result,string reason)
    {
        Assert.False(result.Applied);Assert.False(result.Idempotent);Assert.Equal(reason,result.ReasonCode);
    }

    private sealed class ReadOnlyFakeProvider:IBrokerSandboxProvider
    {
        public int MutationCalls { get; private set; }
        public string ProviderId=>"fake-provider";
        public BrokerEnvironment Environment=>BrokerEnvironment.Sandbox;
        public BrokerCapability Capability=>new(BrokerCapabilityStatus.Available,true,true,true,false,false,AsOf,"FAKE");
        public Uri Endpoint=>new("https://sandbox.invalid");
        public IReadOnlySet<string> AllowedHttpsEndpoints=>new HashSet<string>(["https://sandbox.invalid"]);
        public bool HasCredentials=>true;
        public Task<BrokerProviderCapabilitySnapshot> ProbeCapabilitiesAsync(CancellationToken ct)=>throw new InvalidOperationException();
        public Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct)=>throw new InvalidOperationException();
        public Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct)=>throw new InvalidOperationException();
        public Task<BrokerResult<BrokerOrder>> SubmitOrderAsync(BrokerOrderRequest request,BrokerMutationAuthorization authorization,CancellationToken ct){MutationCalls++;throw new InvalidOperationException();}
        public Task<BrokerResult<BrokerOrder>> CancelOrderAsync(string orderId,BrokerMutationAuthorization authorization,CancellationToken ct){MutationCalls++;throw new InvalidOperationException();}
    }
}
