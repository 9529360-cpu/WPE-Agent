using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WpeAgent.Plugins;

public sealed class PluginManifestValidator
{
    private static readonly HashSet<string> Phase0Types = new(StringComparer.Ordinal)
    {
        "data-source", "notification", "exchange-adapter"
    };

    private static readonly HashSet<string> ReservedTypes = new(StringComparer.Ordinal)
    {
        "strategy", "risk-rule", "brain-provider"
    };

    private static readonly HashSet<string> Permissions = new(StringComparer.Ordinal)
    {
        "market.read", "account.read", "data.read", "notify.emit", "notify.webhook",
        "exchange.testnet.read", "exchange.testnet.trade"
    };

    private static readonly IReadOnlyDictionary<string, HashSet<string>> TypePermissions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["data-source"] = new(["market.read", "account.read", "data.read"], StringComparer.Ordinal),
            ["notification"] = new(["notify.emit", "notify.webhook"], StringComparer.Ordinal),
            ["exchange-adapter"] = new(["exchange.testnet.read", "exchange.testnet.trade"], StringComparer.Ordinal)
        };

    private static readonly IReadOnlyDictionary<string, string> EntryContracts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data-source"] = "data-source.v1",
            ["notification"] = "notification.v1",
            ["exchange-adapter"] = "exchange-provider-catalog.v1"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public PluginValidationResult ValidateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Invalid("Manifest JSON is empty.");
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifestV1>(json, JsonOptions);
            return manifest is null ? Invalid("Manifest JSON is null.") : Validate(manifest);
        }
        catch (JsonException ex)
        {
            return Invalid($"Manifest schema is invalid: {ex.Message}");
        }
    }

    public PluginValidationResult Validate(PluginManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.SchemaVersion, PluginPhase0.SchemaVersion, StringComparison.Ordinal))
            return Invalid($"Unsupported schemaVersion '{manifest.SchemaVersion}'.");
        if (!ValidId(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            return Invalid("Plugin id or name is invalid.");
        if (!TryVersion(manifest.Version, out _)) return Invalid($"Plugin version '{manifest.Version}' is invalid.");
        if (ReservedTypes.Contains(manifest.Type)) return Invalid($"Plugin type '{manifest.Type}' is reserved after Phase 0.");
        if (!Phase0Types.Contains(manifest.Type)) return Invalid($"Unknown plugin type '{manifest.Type}'.");
        if (manifest.Publisher is null || string.IsNullOrWhiteSpace(manifest.Publisher.Id) ||
            string.IsNullOrWhiteSpace(manifest.Publisher.Name) || string.IsNullOrWhiteSpace(manifest.Publisher.SignatureKeyId))
            return Invalid("Publisher signature metadata is required.");
        if (manifest.Entry is null || !string.Equals(manifest.Entry.Kind, "wpe-contract", StringComparison.Ordinal) ||
            !EntryContracts.TryGetValue(manifest.Type, out var contract) || !string.Equals(manifest.Entry.Contract, contract, StringComparison.Ordinal))
            return Invalid("Unknown or incompatible plugin entry contract.");
        if (manifest.Permissions is null || manifest.Permissions.Any(x => !Permissions.Contains(x)))
            return Invalid("Manifest requests an unknown permission.");
        if (manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Count)
            return Invalid("Manifest contains duplicate permissions.");
        if (manifest.Permissions.Any(x => !TypePermissions[manifest.Type].Contains(x)))
            return Invalid($"Manifest permission is not valid for type '{manifest.Type}'.");
        if (manifest.Lifecycle is null) return Invalid("Plugin lifecycle metadata is required.");
        if (manifest.Type == "exchange-adapter" && (!manifest.Lifecycle.TestnetOnly || manifest.Lifecycle.DefaultEnabled))
            return Invalid("Phase 0 exchange adapters must be Testnet-only and disabled by default.");
        if (manifest.Compatibility is null || !TryVersion(manifest.Compatibility.WpeMinVersion, out var min) ||
            !TryVersion(manifest.Compatibility.WpeMaxVersion, out var max) || min > max)
            return Invalid("Compatibility version range is invalid.");
        if (manifest.Compatibility.Os is null || manifest.Compatibility.Os.Count == 0 ||
            manifest.Compatibility.Os.Any(x => !string.Equals(x, PluginPhase0.HostOs, StringComparison.Ordinal)))
            return Invalid("Unknown or missing host OS metadata.");
        if (manifest.Compatibility.Runtimes is null || manifest.Compatibility.Runtimes.Count == 0 ||
            manifest.Compatibility.Runtimes.Any(x => !string.Equals(x, PluginPhase0.HostRuntime, StringComparison.Ordinal)))
            return Invalid("Unknown or missing host runtime metadata.");
        if (manifest.Signature is null || !string.Equals(manifest.Signature.Algorithm, "ed25519", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Signature.Value) || manifest.Signature.SignedAtUtc == default)
            return Invalid("Required ed25519 signature metadata is missing or unsupported.");

        var host = Version.Parse(PluginPhase0.HostVersion);
        var compatible = host >= min && host <= max;
        return new(true, compatible, compatible ? null : $"Host {host} is outside [{min}, {max}].", manifest);
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static bool TryVersion(string? value, out Version version)
    {
        version = new Version();
        if (value is null || !Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var parsed) || parsed is null) return false;
        version = parsed;
        return true;
    }
    private static PluginValidationResult Invalid(string error) => new(false, false, error, null);
}
