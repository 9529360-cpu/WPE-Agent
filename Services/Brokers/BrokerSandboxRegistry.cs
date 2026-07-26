namespace 币安量化机器人.Services.Brokers;

public sealed record BrokerSandboxSelectionRequest(
    string ConfiguredProviderId,
    string AccountId,
    string Venue,
    BrokerOrderType OrderType,
    BrokerMutationCapability Mutation);

public sealed record BrokerSandboxSelectionResult(
    BrokerCapabilityStatus Status,
    IBrokerSandboxProvider? Provider,
    string ReasonCode,
    DateTimeOffset CheckedAtUtc);

public sealed class BrokerSandboxRegistry
{
    private readonly IReadOnlyList<IBrokerSandboxProvider> _providers;

    public BrokerSandboxRegistry(IEnumerable<IBrokerSandboxProvider>? providers=null) =>
        _providers=providers?.ToArray()??[];

    public IReadOnlyList<IBrokerSandboxProvider> Providers=>_providers;
}

public sealed class BrokerSandboxSelectionPolicy
{
    private readonly BrokerSandboxCapabilityProbe _probe;

    public BrokerSandboxSelectionPolicy(BrokerSandboxCapabilityProbe? probe=null) =>
        _probe=probe??new BrokerSandboxCapabilityProbe();

    public async Task<BrokerSandboxSelectionResult> SelectAsync(
        BrokerSandboxRegistry registry,
        BrokerSandboxSelectionRequest request,
        DateTimeOffset now,
        CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(request);

        if(string.IsNullOrWhiteSpace(request.ConfiguredProviderId))
            return Unsupported("BROKER_PROVIDER_NOT_CONFIGURED",now);

        var candidates=registry.Providers.Where(provider=>
            string.Equals(provider.ProviderId,request.ConfiguredProviderId,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(candidates.Length==0)
            return Unsupported("BROKER_PROVIDER_NOT_REGISTERED",now);
        if(candidates.Length!=1)
            return Unsupported("BROKER_PROVIDER_SELECTION_CONFLICT",now);

        var candidate=candidates[0];
        var probe=await _probe.ProbeAsync(
            candidate,
            new BrokerCapabilityProbeRequest(
                request.AccountId,request.Venue,request.OrderType,request.Mutation),
            now,
            ct);
        if(probe.Status!=BrokerCapabilityStatus.Available||!probe.CanMutate)
            return Unsupported(
                string.IsNullOrWhiteSpace(probe.ReasonCode)?"BROKER_PROVIDER_UNSUPPORTED":probe.ReasonCode,
                now);

        return new BrokerSandboxSelectionResult(
            BrokerCapabilityStatus.Available,candidate,string.Empty,now);
    }

    private static BrokerSandboxSelectionResult Unsupported(string reasonCode,DateTimeOffset now) =>
        new(BrokerCapabilityStatus.Unsupported,null,reasonCode,now);
}
