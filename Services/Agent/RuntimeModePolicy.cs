using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Services.Agent;

public sealed record RuntimeModeResolution(
    AiRuntimeMode RequestedMode,
    AiRuntimeMode EffectiveMode,
    bool AllowRemoteBrain,
    string ProviderName,
    string ModelName,
    string FallbackReason)
{
    public bool IsLocalOnly => EffectiveMode == AiRuntimeMode.LocalOnly;
}

public static class RuntimeModePolicy
{
    public static RuntimeModeResolution Resolve(AgentSettings settings)
    {
        var requested = settings.AiMode;
        var slot = GetActiveBrain(settings);

        if (requested == AiRuntimeMode.LocalOnly)
        {
            return new(
                requested,
                AiRuntimeMode.LocalOnly,
                false,
                "WPE Local Brain",
                "local-deterministic",
                "Local Only mode disables remote Brain calls.");
        }

        if (slot is null)
        {
            return new(
                requested,
                AiRuntimeMode.LocalOnly,
                false,
                "WPE Local Brain",
                "local-deterministic",
                "No active Brain slot is configured; falling back to local deterministic mode.");
        }

        if (slot.IsLocal || slot.Provider.Equals("WPE Local Brain", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                requested,
                AiRuntimeMode.LocalOnly,
                false,
                slot.Provider,
                string.IsNullOrWhiteSpace(slot.Model) ? "local-deterministic" : slot.Model,
                "The selected Brain is local-only, so remote Brain calls remain disabled.");
        }

        var configured =
            !string.IsNullOrWhiteSpace(slot.Endpoint) &&
            !string.IsNullOrWhiteSpace(slot.Model) &&
            !string.IsNullOrWhiteSpace(slot.EncryptedKey);

        if (!configured)
        {
            return new(
                requested,
                AiRuntimeMode.LocalOnly,
                false,
                slot.Provider,
                slot.Model,
                "Remote Brain configuration is incomplete; falling back to local deterministic mode.");
        }

        return new(
            requested,
            requested,
            true,
            slot.Provider,
            slot.Model,
            string.Empty);
    }

    public static BrainSlot? GetActiveBrain(AgentSettings settings)
        => settings.Brains.TryGetValue(settings.ActiveBrain, out var slot)
            ? slot
            : settings.Brains.Values.FirstOrDefault();
}
