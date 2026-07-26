using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class BrokerSandboxRegistryTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(2000);

    [Fact]
    public async Task DefaultRegistry_IsEmptyAndUnsupported()
    {
        var registry=new BrokerSandboxRegistry();
        var result=await Select(registry);

        Assert.Empty(registry.Providers);
        AssertUnsupported(result,"BROKER_PROVIDER_NOT_REGISTERED");
    }

    [Fact]
    public async Task ProviderMustBeExplicitlyConfigured()
    {
        var provider=new SelectionFakeProvider();
        var result=await Select(new([provider]),configuredProviderId:" ");

        AssertUnsupported(result,"BROKER_PROVIDER_NOT_CONFIGURED");
        Assert.Equal(0,provider.ProbeCalls);
    }

    [Fact]
    public async Task ExactlyOneMatchingAvailableCandidate_IsSelectedWithoutMutation()
    {
        var provider=new SelectionFakeProvider();
        var result=await Select(new([provider]));

        Assert.Equal(BrokerCapabilityStatus.Available,result.Status);
        Assert.Same(provider,result.Provider);
        Assert.Equal(1,provider.ProbeCalls);
        Assert.Equal(0,provider.MutationCalls);
    }

    [Fact]
    public async Task MultipleConfiguredCandidates_AreUnsupportedWithoutProbe()
    {
        var first=new SelectionFakeProvider();
        var second=new SelectionFakeProvider();
        var result=await Select(new([first,second]));

        AssertUnsupported(result,"BROKER_PROVIDER_SELECTION_CONFLICT");
        Assert.Equal(0,first.ProbeCalls+second.ProbeCalls);
    }

    [Theory]
    [InlineData(BrokerCapabilityStatus.Unknown)]
    [InlineData(BrokerCapabilityStatus.Stale)]
    [InlineData(BrokerCapabilityStatus.Unsupported)]
    [InlineData(BrokerCapabilityStatus.Error)]
    public async Task NonAvailableOrExpiredTruth_IsUnsupported(BrokerCapabilityStatus status)
    {
        var provider=new SelectionFakeProvider
        {
            Snapshot=Capability() with { Status=status,ReasonCode="BROKER_NOT_AVAILABLE" }
        };

        AssertUnsupported(await Select(new([provider])),"BROKER_NOT_AVAILABLE");
    }

    [Fact]
    public async Task ExpiredCapability_IsUnsupported()
    {
        var provider=new SelectionFakeProvider
        {
            Snapshot=Capability() with { ExpiresAtUtc=Now }
        };
        AssertUnsupported(await Select(new([provider])),"BROKER_CAPABILITY_STALE");
    }

    [Theory]
    [InlineData("other-account", "XNAS", BrokerOrderType.Limit)]
    [InlineData("paper-account", "OTHER", BrokerOrderType.Limit)]
    [InlineData("paper-account", "XNAS", BrokerOrderType.Stop)]
    public async Task AccountVenueAndOrderTypeMustMatch(
        string account,string venue,BrokerOrderType orderType)
    {
        var result=await Select(new([new SelectionFakeProvider()]),account:account,venue:venue,orderType:orderType);
        AssertUnsupported(result,"BROKER_ORDER_SCOPE_UNSUPPORTED");
    }

    [Fact]
    public async Task UnsafeClock_IsUnsupported()
    {
        var provider=new SelectionFakeProvider
        {
            Snapshot=Capability() with { ClockSynchronized=false }
        };
        AssertUnsupported(await Select(new([provider])),"BROKER_CLOCK_UNSAFE");
    }

    [Fact]
    public async Task NonSandboxEndpoint_IsUnsupported()
    {
        var provider=new SelectionFakeProvider { EndpointValue=new Uri("https://other.invalid") };
        AssertUnsupported(await Select(new([provider])),"BROKER_ENDPOINT_UNSUPPORTED");
    }

    private static Task<BrokerSandboxSelectionResult> Select(
        BrokerSandboxRegistry registry,
        string configuredProviderId="fake-provider",
        string account="paper-account",
        string venue="XNAS",
        BrokerOrderType orderType=BrokerOrderType.Limit) =>
        new BrokerSandboxSelectionPolicy().SelectAsync(
            registry,new(configuredProviderId,account,venue,orderType,BrokerMutationCapability.Submit),Now);

    private static BrokerProviderCapabilitySnapshot Capability() => new(
        BrokerCapabilityStatus.Available,Now.AddSeconds(-1),Now.AddMinutes(1),
        new HashSet<string>(["paper-account"]),new HashSet<string>(["XNAS"]),
        new HashSet<BrokerOrderType>([BrokerOrderType.Limit]),true,5,Now.AddMinutes(1),
        true,true,string.Empty);

    private static void AssertUnsupported(BrokerSandboxSelectionResult result,string reasonCode)
    {
        Assert.Equal(BrokerCapabilityStatus.Unsupported,result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(reasonCode,result.ReasonCode);
    }

    private sealed class SelectionFakeProvider:IBrokerSandboxProvider
    {
        public BrokerProviderCapabilitySnapshot Snapshot { get; init; }=Capability();
        public Uri EndpointValue { get; init; }=new("https://sandbox.invalid");
        public int ProbeCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public string ProviderId=>"fake-provider";
        public BrokerEnvironment Environment=>BrokerEnvironment.Sandbox;
        public BrokerCapability Capability=>new(BrokerCapabilityStatus.Available,true,true,true,false,false,Now,"FAKE");
        public Uri Endpoint=>EndpointValue;
        public IReadOnlySet<string> AllowedHttpsEndpoints { get; }=new HashSet<string>(["https://sandbox.invalid"],StringComparer.OrdinalIgnoreCase);
        public bool HasCredentials=>true;
        public Task<BrokerProviderCapabilitySnapshot> ProbeCapabilitiesAsync(CancellationToken ct){ProbeCalls++;return Task.FromResult(Snapshot);}
        public Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct)=>throw new InvalidOperationException("Selection must not read accounts.");
        public Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct)=>throw new InvalidOperationException("Selection must not read positions.");
        public Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct)=>throw new InvalidOperationException("Selection must not read orders.");
        public Task<BrokerResult<BrokerOrder>> SubmitOrderAsync(BrokerOrderRequest request,BrokerMutationAuthorization authorization,CancellationToken ct){MutationCalls++;throw new InvalidOperationException("Selection must not submit.");}
        public Task<BrokerResult<BrokerOrder>> CancelOrderAsync(string orderId,BrokerMutationAuthorization authorization,CancellationToken ct){MutationCalls++;throw new InvalidOperationException("Selection must not cancel.");}
    }
}
