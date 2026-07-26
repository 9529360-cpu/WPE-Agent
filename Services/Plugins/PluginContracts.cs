using System.Text.Json.Serialization;
using WpeAgent.RuntimeContracts;

namespace WpeAgent.Plugins;

public static class PluginPhase0
{
    public const string SchemaVersion = "1.0";
    public static string HostVersion => typeof(PluginPhase0).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("Product assembly version is unavailable.");
    public const string HostOs = "windows";
    public const string HostRuntime = "net8.0-windows";
}

public sealed record PluginPublisherV1(string Id, string Name, string SignatureKeyId);
public sealed record PluginCompatibilityV1(string WpeMinVersion, string WpeMaxVersion, IReadOnlyList<string> Os, IReadOnlyList<string> Runtimes);
public sealed record PluginEntryV1(string Kind, string Contract);
public sealed record PluginLifecycleV1(bool DefaultEnabled, bool TestnetOnly);
public sealed record PluginSignatureV1(string Algorithm, string Value, DateTimeOffset SignedAtUtc);

public sealed record PluginManifestV1(
    string SchemaVersion,
    string Id,
    string Name,
    string Type,
    string Version,
    PluginPublisherV1 Publisher,
    PluginCompatibilityV1 Compatibility,
    PluginEntryV1 Entry,
    IReadOnlyList<string> Permissions,
    PluginLifecycleV1 Lifecycle,
    PluginSignatureV1 Signature);

public enum PluginCompatibilityStatus { Compatible, Incompatible }
public enum PluginSignatureStatus { MetadataPresent }
public enum PluginRiskLevel { Low, Medium, High }

public sealed record PluginValidationResult(
    bool IsValid,
    bool IsCompatible,
    string? Error,
    PluginManifestV1? Manifest);

public sealed record RegisteredPlugin(
    PluginManifestV1 Manifest,
    bool Enabled,
    PluginCompatibilityStatus CompatibilityStatus,
    PluginSignatureStatus SignatureStatus,
    PluginRiskLevel RiskLevel,
    string? StatusMessage);

public sealed record PluginRegistrySnapshot(
    RuntimeCollectionState State,
    IReadOnlyList<RegisteredPlugin> Items,
    DateTimeOffset? UpdatedAt,
    string? Message)
{
    public static PluginRegistrySnapshot Unsupported(string message) => new(RuntimeCollectionState.Unsupported, [], null, message);
    public static PluginRegistrySnapshot Error(string message) => new(RuntimeCollectionState.Error, [], DateTimeOffset.UtcNow, message);
}
