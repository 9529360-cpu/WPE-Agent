using System.Reflection;
using System.IO;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WpeAgent.Plugins;

/// <summary>
/// Thread-safe local manifest registry. It records metadata only: no entry is loaded or executed.
/// </summary>
public sealed class LocalPluginRegistry
{
    private readonly object _gate = new();
    private readonly PluginManifestValidator _validator;
    private readonly SkillExecutionGuard _skillGuard;
    private Dictionary<string, RegisteredPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _builtInIds = new(StringComparer.OrdinalIgnoreCase);
    private RuntimeCollectionState _state = RuntimeCollectionState.Unsupported;
    private DateTimeOffset? _updatedAt;
    private string? _message = "Plugin registry has not been discovered.";

    public LocalPluginRegistry(PluginManifestValidator? validator = null, SkillExecutionGuard? skillGuard = null)
    {
        _validator = validator ?? new PluginManifestValidator();
        _skillGuard = skillGuard ?? new SkillExecutionGuard();
    }

    public PluginRegistrySnapshot Discover(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A local plugin directory is required.", nameof(directory));
        Dictionary<string, RegisteredPlugin> discovered;
        lock (_gate) discovered = _plugins.Where(x => _builtInIds.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.plugin.json", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                PluginValidationResult result;
                try { result = _validator.ValidateJson(File.ReadAllText(file)); }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); continue; }
                if (!result.IsValid || result.Manifest is null) { errors.Add($"{Path.GetFileName(file)}: {result.Error}"); continue; }
                if (discovered.ContainsKey(result.Manifest.Id)) { errors.Add($"{Path.GetFileName(file)}: duplicate plugin id '{result.Manifest.Id}'."); continue; }
                discovered[result.Manifest.Id] = Register(result.Manifest, result.IsCompatible, result.Error);
            }
        }

        lock (_gate)
        {
            _plugins = discovered;
            _updatedAt = DateTimeOffset.UtcNow;
            _state = errors.Count > 0 ? RuntimeCollectionState.Error : RuntimeCollectionState.Available;
            _message = errors.Count > 0 ? string.Join(" | ", errors) : discovered.Count == 0 ? "No local plugin manifests discovered." : null;
            return SnapshotUnsafe();
        }
    }

    public PluginRegistrySnapshot PublishBuiltInExchangeCatalog(IEnumerable<ExchangeProviderDescriptor> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var builtIns = providers.Select(CreateBuiltInExchangeManifest).Select(x => Register(x, true, null))
            .ToDictionary(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _plugins = builtIns;
            _builtInIds = builtIns.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _updatedAt = DateTimeOffset.UtcNow;
            _state = RuntimeCollectionState.Available;
            _message = builtIns.Count == 0 ? "No built-in exchange adapters are available." : null;
            return SnapshotUnsafe();
        }
    }

    public PluginRegistrySnapshot List() { lock (_gate) return SnapshotUnsafe(); }

    public bool Enable(string id, out string? error)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(id, out var plugin)) { error = $"Plugin '{id}' is not registered."; return false; }
            if (plugin.CompatibilityStatus != PluginCompatibilityStatus.Compatible) { error = plugin.StatusMessage ?? "Plugin is incompatible."; return false; }
            try { _skillGuard.AuthorizePluginPermissions(plugin.Manifest.Permissions, TradingMode.Testnet); }
            catch (UnauthorizedAccessException ex) { error = ex.Message; return false; }
            _plugins[id] = plugin with { Enabled = true, StatusMessage = null };
            _updatedAt = DateTimeOffset.UtcNow;
            error = null;
            return true;
        }
    }

    public bool Disable(string id)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(id, out var plugin)) return false;
            _plugins[id] = plugin with { Enabled = false };
            _updatedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void MarkStale(string message)
    {
        lock (_gate) { _state = RuntimeCollectionState.Stale; _message = message; }
    }

    private RegisteredPlugin Register(PluginManifestV1 manifest, bool compatible, string? message)
    {
        _skillGuard.AuthorizePluginPermissions(manifest.Permissions, TradingMode.Testnet);
        var risk = manifest.Type == "exchange-adapter" || manifest.Permissions.Contains("exchange.testnet.trade", StringComparer.Ordinal)
            ? PluginRiskLevel.High
            : manifest.Permissions.Any(x => x is "notify.webhook" or "account.read") ? PluginRiskLevel.Medium : PluginRiskLevel.Low;
        return new(manifest, compatible && manifest.Lifecycle.DefaultEnabled,
            compatible ? PluginCompatibilityStatus.Compatible : PluginCompatibilityStatus.Incompatible,
            PluginSignatureStatus.MetadataPresent, risk, message);
    }

    private PluginRegistrySnapshot SnapshotUnsafe() => new(_state,
        _plugins.Values.OrderBy(x => x.Manifest.Type, StringComparer.Ordinal).ThenBy(x => x.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        _updatedAt, _message);

    public static LocalPluginRegistry CreateDefault()
    {
        var registry = new LocalPluginRegistry();
        // Restrict discovery to the host assembly. The plugin registry never scans or loads external assemblies.
        var catalog = new ExchangeProviderCatalog([typeof(BinanceProviderPlugin).Assembly]);
        registry.PublishBuiltInExchangeCatalog(catalog.Installed);
        registry.Discover(Path.Combine(AppContext.BaseDirectory, "Plugins"));
        return registry;
    }

    private static PluginManifestV1 CreateBuiltInExchangeManifest(ExchangeProviderDescriptor provider) => new(
        PluginPhase0.SchemaVersion,
        $"wpe.exchange.{provider.Id}",
        provider.DisplayName,
        "exchange-adapter",
        PluginPhase0.HostVersion,
        new("wpe.core", "WPE Core", "wpe-core-release-key"),
        new(PluginPhase0.HostVersion, "3.99.99", [PluginPhase0.HostOs], [PluginPhase0.HostRuntime]),
        new("wpe-contract", "exchange-provider-catalog.v1"),
        ["exchange.testnet.read", "exchange.testnet.trade"],
        new(false, true),
        new("ed25519", $"builtin-metadata:{provider.Id}", DateTimeOffset.UnixEpoch));
}
