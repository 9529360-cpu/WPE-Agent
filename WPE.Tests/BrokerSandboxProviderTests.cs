using System.Text.Json;
using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class BrokerSandboxProviderTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1000);

    [Fact]
    public async Task DefaultProvider_IsUnsupportedForReadsAndMutations()
    {
        var provider=new UnsupportedBrokerSandboxProvider();
        var probe=await Probe(provider);
        var mutation=await provider.SubmitOrderAsync(OrderRequest(),Authorization(),default);

        Assert.Equal(BrokerCapabilityStatus.Unsupported,probe.Status);
        Assert.False(probe.CanMutate);
        Assert.Equal(BrokerCapabilityStatus.Unsupported,mutation.Status);
    }

    [Theory]
    [InlineData(BrokerEnvironment.Paper)]
    [InlineData(BrokerEnvironment.Sandbox)]
    public async Task AllowlistedHttpsPaperOrSandbox_CanReportMutationCapability(BrokerEnvironment environment)
    {
        var result=await Probe(new FakeProvider { EnvironmentValue=environment });

        Assert.Equal(BrokerCapabilityStatus.Available,result.Status);
        Assert.True(result.CanRead);
        Assert.True(result.CanMutate);
    }

    [Theory]
    [InlineData("http://sandbox.invalid")]
    [InlineData("https://other.invalid")]
    [InlineData("https://sandbox.invalid?token=secret")]
    public async Task EndpointOutsideExactHttpsAllowlist_BlocksBeforeCapabilityProbe(string endpoint)
    {
        var provider=new FakeProvider { EndpointValue=new Uri(endpoint) };

        var result=await Probe(provider);

        Assert.False(result.CanMutate);
        Assert.Equal("BROKER_ENDPOINT_UNSUPPORTED",result.ReasonCode);
        Assert.Equal(0,provider.ProbeCalls);
    }

    [Fact]
    public async Task CredentialsArePresenceOnlyAndNeverEchoed()
    {
        var provider=new FakeProvider { HasCredentialsValue=false };
        var result=await Probe(provider);
        var json=JsonSerializer.Serialize(result);
        const string secretMarker="provider-secret-must-not-appear";

        Assert.Equal("BROKER_CREDENTIALS_MISSING",result.ReasonCode);
        Assert.DoesNotContain(secretMarker,json,StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(IBrokerSandboxProvider).GetProperties(),property=>
            property.PropertyType==typeof(string)&&property.Name.Contains("Credential",StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(BrokerCapabilityStatus.Unknown)]
    [InlineData(BrokerCapabilityStatus.Unsupported)]
    [InlineData(BrokerCapabilityStatus.Stale)]
    [InlineData(BrokerCapabilityStatus.Error)]
    public async Task NonAvailableCapabilityCannotMutate(BrokerCapabilityStatus status)
    {
        var provider=new FakeProvider { Snapshot=Capability() with { Status=status } };
        var result=await Probe(provider);

        Assert.Equal(status,result.Status);
        Assert.False(result.CanMutate);
    }

    [Fact]
    public async Task ExpiredCapabilityCannotMutate()
    {
        var provider=new FakeProvider { Snapshot=Capability() with { ExpiresAtUtc=Now } };
        Assert.Equal("BROKER_CAPABILITY_STALE",(await Probe(provider)).ReasonCode);
    }

    [Theory]
    [InlineData("other-account", "XNAS", BrokerOrderType.Limit)]
    [InlineData("paper-account", "OTHER", BrokerOrderType.Limit)]
    [InlineData("paper-account", "XNAS", BrokerOrderType.Stop)]
    public async Task UnsupportedAccountVenueOrOrderTypeCannotMutate(
        string account,string venue,BrokerOrderType orderType)
    {
        var result=await Probe(new FakeProvider(),account,venue,orderType);
        Assert.Equal("BROKER_ORDER_SCOPE_UNSUPPORTED",result.ReasonCode);
        Assert.False(result.CanMutate);
    }

    [Fact]
    public async Task UnsafeClockCannotMutate()
    {
        var provider=new FakeProvider { Snapshot=Capability() with { ClockSynchronized=false } };
        Assert.Equal("BROKER_CLOCK_UNSAFE",(await Probe(provider)).ReasonCode);
    }

    [Fact]
    public async Task ExhaustedRateLimitCannotMutate()
    {
        var provider=new FakeProvider { Snapshot=Capability() with { RateLimitRemaining=0 } };
        Assert.Equal("BROKER_RATE_LIMITED",(await Probe(provider)).ReasonCode);
    }

    [Theory]
    [InlineData(BrokerMutationCapability.Submit)]
    [InlineData(BrokerMutationCapability.Cancel)]
    public async Task MissingRequestedMutationCapabilityCannotMutate(BrokerMutationCapability mutation)
    {
        var snapshot=mutation==BrokerMutationCapability.Submit
            ?Capability() with { CanSubmit=false }
            :Capability() with { CanCancel=false };
        var result=await Probe(new FakeProvider { Snapshot=snapshot },mutation:mutation);

        Assert.Equal(BrokerCapabilityStatus.Unsupported,result.Status);
        Assert.True(result.CanRead);
        Assert.False(result.CanMutate);
    }

    [Fact]
    public async Task InvalidEnvironmentCannotMutateOrProbeProvider()
    {
        var provider=new FakeProvider { EnvironmentValue=(BrokerEnvironment)999 };
        var result=await Probe(provider);

        Assert.Equal("BROKER_ENVIRONMENT_UNSUPPORTED",result.ReasonCode);
        Assert.Equal(0,provider.ProbeCalls);
    }

    private static Task<BrokerCapabilityProbeResult> Probe(
        IBrokerSandboxProvider provider,string account="paper-account",string venue="XNAS",
        BrokerOrderType orderType=BrokerOrderType.Limit,
        BrokerMutationCapability mutation=BrokerMutationCapability.Submit) =>
        new BrokerSandboxConformanceHarness().VerifyAsync(
            provider,new(account,venue,orderType,mutation),Now);

    private static BrokerProviderCapabilitySnapshot Capability() => new(
        BrokerCapabilityStatus.Available,Now.AddSeconds(-1),Now.AddMinutes(1),
        new HashSet<string>(["paper-account"]),new HashSet<string>(["XNAS"]),
        new HashSet<BrokerOrderType>([BrokerOrderType.Limit]),true,10,Now.AddMinutes(1),
        true,true,string.Empty);

    private static BrokerMutationAuthorization Authorization() => new(
        "risk",BrokerRiskGateOutcome.Approved,"executor","trace","idempotency",true);
    private static BrokerOrderRequest OrderRequest() => new(
        "client","paper-account","XNAS:TEST",BrokerOrderSide.Buy,BrokerOrderType.Limit,1m,1m);

    private sealed class FakeProvider:IBrokerSandboxProvider
    {
        public BrokerEnvironment EnvironmentValue { get; init; }=BrokerEnvironment.Sandbox;
        public Uri EndpointValue { get; init; }=new("https://sandbox.invalid");
        public bool HasCredentialsValue { get; init; }=true;
        public BrokerProviderCapabilitySnapshot Snapshot { get; init; }=Capability();
        public int ProbeCalls { get; private set; }
        public string ProviderId=>"fake-provider";
        public BrokerEnvironment Environment=>EnvironmentValue;
        public BrokerCapability Capability=>new(BrokerCapabilityStatus.Available,true,true,true,false,false,Now,"FAKE");
        public Uri Endpoint=>EndpointValue;
        public IReadOnlySet<string> AllowedHttpsEndpoints { get; }=new HashSet<string>(["https://sandbox.invalid"],StringComparer.OrdinalIgnoreCase);
        public bool HasCredentials=>HasCredentialsValue;
        public Task<BrokerProviderCapabilitySnapshot> ProbeCapabilitiesAsync(CancellationToken ct){ProbeCalls++;return Task.FromResult(Snapshot);}
        public Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct)=>throw new NotSupportedException();
        public Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct)=>throw new NotSupportedException();
        public Task<BrokerResult<BrokerOrder>> SubmitOrderAsync(BrokerOrderRequest request,BrokerMutationAuthorization authorization,CancellationToken ct)=>throw new NotSupportedException();
        public Task<BrokerResult<BrokerOrder>> CancelOrderAsync(string orderId,BrokerMutationAuthorization authorization,CancellationToken ct)=>throw new NotSupportedException();
    }
}
