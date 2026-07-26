namespace 币安量化机器人.Services.Agent;
/// <summary>Local-only evidence memory. It has no execution, provider, risk, or credential dependencies.</summary>
public sealed class DeterministicMemoryService(AgentSqliteStore database)
{
    public static WpeAgent.ModelOff.ModelOffAgentOutputV1 ProduceNewsModelOff(ModelOffResearchInputV1 input)
        => input.Capability == ModelOffResearchCapabilityV1.News
            ? DeterministicResearchCapabilityProducerV1.Produce(input)
            : throw new ArgumentException("News producer requires the news capability.", nameof(input));

    public static WpeAgent.ModelOff.ModelOffAgentOutputV1 ProduceUnsupportedModelOff(ModelOffResearchInputV1 input)
        => input.Capability is ModelOffResearchCapabilityV1.Macro or ModelOffResearchCapabilityV1.Fundamental
            ? DeterministicResearchCapabilityProducerV1.Produce(input)
            : throw new ArgumentException("Unsupported producer is limited to macro and fundamental capabilities.", nameof(input));

    public Task<bool> RememberAsync(MemoryEvidence evidence,CancellationToken ct)=>database.SaveMemoryAsync(evidence,ct);
    public Task<IReadOnlyList<PersistedMemory>> RetrieveAsync(MemoryQuery query,CancellationToken ct)=>database.SearchMemoriesAsync(query,ct);
}
