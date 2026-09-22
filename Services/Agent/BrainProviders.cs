namespace 币安量化机器人.Services.Agent;

public sealed class DeterministicBrainProvider : IAssistantProvider
{
    private readonly ITradingDecisionAgent _decisionAgent;

    public DeterministicBrainProvider():this(new TechnicalDecisionAgent()) {}

    internal DeterministicBrainProvider(ITradingDecisionAgent decisionAgent)
    {
        _decisionAgent=decisionAgent??throw new ArgumentNullException(nameof(decisionAgent));
    }

    public string Name => "WPE Local Brain";
    public bool IsLocal => true;

    public Task<BrainHealth> HealthCheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new BrainHealth(true, "LOCAL_DETERMINISTIC"));
    }

    public Task<BrainDecisionResult> DecideAsync(EvidencePack evidence,AgentContext context,CancellationToken ct)=>
        _decisionAgent.DecideAsync(evidence,context,ct);
}

public sealed record AssistantAdapterDescriptor(string Id,string DisplayName,bool Local,string Protocol);

public static class AssistantAdapterCatalog
{
    public static IReadOnlyList<AssistantAdapterDescriptor> All { get; } =
        [new("local-deterministic","WPE Local Deterministic",true,"native")];
}

/// <summary>Local-only assistant composition boundary.</summary>
public static class AssistantProviderFactory
{
    public static IAssistantProvider CreateLocal()=>new DeterministicBrainProvider();

    public static IAssistantProvider Create(BrainSlot? slot,string? secret,global::币安量化机器人.Core.Models.AiRuntimeMode mode)
    {
        _=slot;_=secret;_=mode;
        return CreateLocal();
    }

    public static IAssistantProvider Create(BrainSlot? slot,string? secret,bool allowRemote)
    {
        _=slot;_=secret;_=allowRemote;
        return CreateLocal();
    }
}
