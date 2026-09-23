namespace 币安量化机器人.Services.Agent;
using 币安量化机器人.Core.Models;

public sealed record RuntimeModeResolution(
    AiRuntimeMode RequestedMode,
    AiRuntimeMode EffectiveMode,
    bool AllowRemoteBrain,
    string ProviderName,
    string ModelName,
    string FallbackReason)
{
    public bool IsLocalOnly => EffectiveMode==AiRuntimeMode.LocalOnly;
}

public static class RuntimeModePolicy
{
    public static RuntimeModeResolution Resolve(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            AiRuntimeMode.LocalOnly,
            AiRuntimeMode.LocalOnly,
            false,
            "WPE Local Brain",
            "local-deterministic",
            "Trading brain is local deterministic only.");
    }

    public static BrainSlot GetActiveBrain(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Brains.TryGetValue("WPE Local Brain",out var slot)
            ?slot
            :new BrainSlot();
    }
}
