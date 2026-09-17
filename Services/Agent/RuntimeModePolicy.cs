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
    public const string DeterministicRuntimeName = "WPE Deterministic Strategy Runtime";
    public const string ModelOffReason = "Model-off product policy: trading intelligence comes from versioned deterministic strategies, research, risk, execution and recovery; online and local model providers are disabled.";

    public static RuntimeModeResolution Resolve(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // AiMode and Brain slots are retained only for backward-compatible settings reads.
        // They are not trading-runtime authority. A stale or previously configured remote
        // provider must never re-enable model calls in the autonomous trading path.
        return new(
            settings.AiMode,
            AiRuntimeMode.LocalOnly,
            false,
            DeterministicRuntimeName,
            string.Empty,
            ModelOffReason);
    }

    /// <summary>
    /// Legacy configuration lookup for non-authoritative compatibility surfaces.
    /// RuntimeModePolicy.Resolve never grants trading authority to the returned slot.
    /// </summary>
    public static BrainSlot? GetActiveBrain(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Brains.TryGetValue(settings.ActiveBrain, out var slot)
            ? slot
            : settings.Brains.Values.FirstOrDefault();
    }
}
