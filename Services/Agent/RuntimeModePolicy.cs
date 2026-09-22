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
        var requested=settings.AiMode;
        var reason=requested==AiRuntimeMode.LocalOnly
            ?"Local Only mode keeps the trading brain fully local."
            :"Automatic trading is local-only; legacy remote Brain settings are ignored.";

        return new(
            requested,
            AiRuntimeMode.LocalOnly,
            false,
            "WPE Local Brain",
            "local-deterministic",
            reason);
    }

    public static BrainSlot? GetActiveBrain(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Brains.TryGetValue(settings.ActiveBrain,out var slot)
            ?slot
            :settings.Brains.Values.FirstOrDefault();
    }
}
