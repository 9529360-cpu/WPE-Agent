namespace 币安量化机器人.Services.Brokers;

public enum BrokerMutationCapability { Submit, Cancel }

public sealed record BrokerProviderCapabilitySnapshot(
    BrokerCapabilityStatus Status,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlySet<string> AccountIds,
    IReadOnlySet<string> Venues,
    IReadOnlySet<BrokerOrderType> OrderTypes,
    bool ClockSynchronized,
    int RateLimitRemaining,
    DateTimeOffset RateLimitResetsAtUtc,
    bool CanSubmit,
    bool CanCancel,
    string ReasonCode);

public sealed record BrokerCapabilityProbeRequest(
    string AccountId,
    string Venue,
    BrokerOrderType OrderType,
    BrokerMutationCapability Mutation);

public sealed record BrokerCapabilityProbeResult(
    BrokerCapabilityStatus Status,
    bool CanRead,
    bool CanMutate,
    string ReasonCode,
    DateTimeOffset CheckedAtUtc);

public interface IBrokerSandboxProvider:IBrokerSandboxAdapter
{
    Uri Endpoint { get; }
    IReadOnlySet<string> AllowedHttpsEndpoints { get; }
    bool HasCredentials { get; }
    Task<BrokerProviderCapabilitySnapshot> ProbeCapabilitiesAsync(CancellationToken ct);
}

public sealed class UnsupportedBrokerSandboxProvider:IBrokerSandboxProvider
{
    private static readonly BrokerCapability UnsupportedCapability=new(
        BrokerCapabilityStatus.Unsupported,false,false,false,false,false,
        DateTimeOffset.UnixEpoch,"BROKER_PROVIDER_UNSUPPORTED");

    public string ProviderId=>"unsupported";
    public BrokerEnvironment Environment=>BrokerEnvironment.Sandbox;
    public BrokerCapability Capability=>UnsupportedCapability;
    public Uri Endpoint { get; }=new("https://unsupported.invalid");
    public IReadOnlySet<string> AllowedHttpsEndpoints { get; }=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool HasCredentials=>false;

    public Task<BrokerProviderCapabilitySnapshot> ProbeCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new BrokerProviderCapabilitySnapshot(
            BrokerCapabilityStatus.Unsupported,DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch,
            new HashSet<string>(),new HashSet<string>(),new HashSet<BrokerOrderType>(),false,0,
            DateTimeOffset.UnixEpoch,false,false,"BROKER_PROVIDER_UNSUPPORTED"));

    public Task<BrokerResult<IReadOnlyList<BrokerAccount>>> GetAccountsAsync(CancellationToken ct) =>
        Task.FromResult(BrokerResult<IReadOnlyList<BrokerAccount>>.Unsupported("BROKER_PROVIDER_UNSUPPORTED"));
    public Task<BrokerResult<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(string? accountId,CancellationToken ct) =>
        Task.FromResult(BrokerResult<IReadOnlyList<BrokerPosition>>.Unsupported("BROKER_PROVIDER_UNSUPPORTED"));
    public Task<BrokerResult<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(BrokerOrderQuery query,CancellationToken ct) =>
        Task.FromResult(BrokerResult<IReadOnlyList<BrokerOrder>>.Unsupported("BROKER_PROVIDER_UNSUPPORTED"));
    public Task<BrokerResult<BrokerOrder>> SubmitOrderAsync(BrokerOrderRequest request,BrokerMutationAuthorization authorization,CancellationToken ct) =>
        Task.FromResult(BrokerResult<BrokerOrder>.Unsupported(BrokerSandboxAdapter.MutationUnsupportedReason,authorization.CorrelationId,authorization.IdempotencyKey));
    public Task<BrokerResult<BrokerOrder>> CancelOrderAsync(string orderId,BrokerMutationAuthorization authorization,CancellationToken ct) =>
        Task.FromResult(BrokerResult<BrokerOrder>.Unsupported(BrokerSandboxAdapter.MutationUnsupportedReason,authorization.CorrelationId,authorization.IdempotencyKey));
}

public sealed class BrokerSandboxCapabilityProbe
{
    public async Task<BrokerCapabilityProbeResult> ProbeAsync(
        IBrokerSandboxProvider provider,
        BrokerCapabilityProbeRequest request,
        DateTimeOffset now,
        CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        if(!Enum.IsDefined(provider.Environment))
            return Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_ENVIRONMENT_UNSUPPORTED",now);
        if(!IsAllowedEndpoint(provider.Endpoint,provider.AllowedHttpsEndpoints))
            return Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_ENDPOINT_UNSUPPORTED",now);
        if(!provider.HasCredentials)
            return Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_CREDENTIALS_MISSING",now);

        BrokerProviderCapabilitySnapshot capability;
        try
        {
            capability=await provider.ProbeCapabilitiesAsync(ct);
        }
        catch
        {
            return Blocked(BrokerCapabilityStatus.Error,"BROKER_CAPABILITY_ERROR",now);
        }

        if(capability.Status!=BrokerCapabilityStatus.Available)
            return Blocked(capability.Status,capability.ReasonCode,now);
        if(capability.ExpiresAtUtc<=now||capability.ObservedAtUtc>now)
            return Blocked(BrokerCapabilityStatus.Stale,"BROKER_CAPABILITY_STALE",now);
        if(!capability.AccountIds.Contains(request.AccountId)||
           !capability.Venues.Contains(request.Venue)||
           !capability.OrderTypes.Contains(request.OrderType))
            return Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_ORDER_SCOPE_UNSUPPORTED",now);
        if(!capability.ClockSynchronized)
            return Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_CLOCK_UNSAFE",now);
        if(capability.RateLimitRemaining<=0)
            return Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_RATE_LIMITED",now);

        var mutationSupported=request.Mutation switch
        {
            BrokerMutationCapability.Submit=>capability.CanSubmit,
            BrokerMutationCapability.Cancel=>capability.CanCancel,
            _=>false
        };
        return mutationSupported
            ?new BrokerCapabilityProbeResult(BrokerCapabilityStatus.Available,true,true,string.Empty,now)
            :Blocked(BrokerCapabilityStatus.Unsupported,"BROKER_MUTATION_UNSUPPORTED",now,canRead:true);
    }

    private static bool IsAllowedEndpoint(Uri endpoint,IReadOnlySet<string> allowedEndpoints)
    {
        if(endpoint.Scheme!=Uri.UriSchemeHttps||!string.IsNullOrEmpty(endpoint.UserInfo)||
           !string.IsNullOrEmpty(endpoint.Query)||!string.IsNullOrEmpty(endpoint.Fragment))
            return false;
        return allowedEndpoints.Contains(endpoint.AbsoluteUri.TrimEnd('/'));
    }

    private static BrokerCapabilityProbeResult Blocked(
        BrokerCapabilityStatus status,string reason,DateTimeOffset now,bool canRead=false) =>
        new(status,canRead,false,reason,now);
}

public sealed class BrokerSandboxConformanceHarness
{
    private readonly BrokerSandboxCapabilityProbe _probe=new();

    public Task<BrokerCapabilityProbeResult> VerifyAsync(
        IBrokerSandboxProvider provider,BrokerCapabilityProbeRequest request,
        DateTimeOffset now,CancellationToken ct=default) =>
        _probe.ProbeAsync(provider,request,now,ct);
}
