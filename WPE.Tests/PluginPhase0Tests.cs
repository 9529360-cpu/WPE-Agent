using System.Text.Json;
using System.Text.Json.Serialization;
using WpeAgent.Plugins;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;

namespace WPE.Tests;

public sealed class PluginPhase0Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wpe-plugin-tests-{Guid.NewGuid():N}");
    private readonly PluginManifestValidator _validator = new();

    public PluginPhase0Tests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("notification", "notification.v1", "notify.emit")]
    [InlineData("data-source", "data-source.v1", "data.read")]
    public void Validator_AcceptsPhase0ReadAndNotificationContracts(string type, string contract, string permission)
    {
        var result = _validator.Validate(Manifest(type, contract, [permission]));
        Assert.True(result.IsValid, result.Error);
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void Registry_DiscoversTradingPluginDisabledByDefault()
    {
        Write("exchange", Manifest("exchange-adapter", "exchange-provider-catalog.v1",
            ["exchange.testnet.read", "exchange.testnet.trade"], defaultEnabled: false, testnetOnly: true));
        var registry = new LocalPluginRegistry();
        var snapshot = registry.Discover(_directory);
        var plugin = Assert.Single(snapshot.Items);
        Assert.False(plugin.Enabled);
        Assert.True(plugin.Manifest.Lifecycle.TestnetOnly);
        Assert.Equal(PluginRiskLevel.High, plugin.RiskLevel);
    }

    [Fact]
    public void Validator_RejectsUnknownPermission()
    {
        var result = _validator.Validate(Manifest("notification", "notification.v1", ["filesystem.write"]));
        Assert.False(result.IsValid);
        Assert.Contains("unknown permission", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_RejectsEnableForIncompatiblePlugin()
    {
        Write("future", Manifest("data-source", "data-source.v1", ["data.read"], min: "9.0.0", max: "9.9.9"));
        var registry = new LocalPluginRegistry();
        var snapshot = registry.Discover(_directory);
        Assert.Equal(PluginCompatibilityStatus.Incompatible, Assert.Single(snapshot.Items).CompatibilityStatus);
        Assert.False(registry.Enable("wpe.test.data-source", out var error));
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_RejectsManifestWithoutSignatureMetadata()
    {
        var json = JsonSerializer.Serialize(Manifest("notification", "notification.v1", ["notify.emit"]), JsonOptions());
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.EnumerateObject().Where(x => x.Name != "signature")
            .ToDictionary(x => x.Name, x => x.Value.Clone());
        File.WriteAllText(Path.Combine(_directory, "unsigned.plugin.json"), JsonSerializer.Serialize(fields));
        var registry = new LocalPluginRegistry();
        var snapshot = registry.Discover(_directory);
        Assert.Equal(RuntimeCollectionState.Error, snapshot.State);
        Assert.Empty(snapshot.Items);
        Assert.Contains("signature", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema", "2.0")]
    [InlineData("version", "1.2")]
    [InlineData("type", "arbitrary-code")]
    [InlineData("entry", "native-dll")]
    [InlineData("os", "linux")]
    [InlineData("runtime", "net9.0")]
    [InlineData("signature", "rsa")]
    public void Validator_FailsClosedForUnknownMetadata(string field, string value)
    {
        var source = Manifest("data-source", "data-source.v1", ["data.read"]);
        var changed = field switch
        {
            "schema" => source with { SchemaVersion = value },
            "version" => source with { Version = value },
            "type" => source with { Type = value },
            "entry" => source with { Entry = source.Entry with { Kind = value } },
            "os" => source with { Compatibility = source.Compatibility with { Os = [value] } },
            "runtime" => source with { Compatibility = source.Compatibility with { Runtimes = [value] } },
            "signature" => source with { Signature = source.Signature with { Algorithm = value } },
            _ => source
        };
        Assert.False(_validator.Validate(changed).IsValid);
    }

    [Fact]
    public void Snapshot_RoundTripsPluginCatalogAndPermissions()
    {
        Write("notify", Manifest("notification", "notification.v1", ["notify.emit"], defaultEnabled: true));
        var registry = new LocalPluginRegistry();
        registry.Discover(_directory);
        var now = DateTime.UtcNow;
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, pluginRegistry: registry.List());
        Assert.Equal(RuntimeCollectionState.Available, snapshot.Plugins.State);
        var plugin = Assert.Single(snapshot.Plugins.Items);
        Assert.True(plugin.Enabled);
        Assert.Equal("metadataPresent", plugin.SignatureStatus);
        Assert.Contains("notify.emit", plugin.Permissions);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("notification", doc.RootElement.GetProperty("plugins").GetProperty("items")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Snapshot_UsesFourStateEnvelopeWithoutServingNonAvailableItems()
    {
        Write("notify", Manifest("notification", "notification.v1", ["notify.emit"]));
        var registry = new LocalPluginRegistry();
        registry.Discover(_directory);
        registry.MarkStale("Plugin manifest directory must be refreshed.");
        var now = DateTime.UtcNow;

        var stale = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, pluginRegistry: registry.List());
        var unsupported = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now);

        Assert.Equal(RuntimeCollectionState.Stale, stale.Plugins.State);
        Assert.Empty(stale.Plugins.Items);
        Assert.Equal(RuntimeCollectionState.Unsupported, unsupported.Plugins.State);
        Assert.Empty(unsupported.Plugins.Items);
    }

    [Fact]
    public void Registry_RefreshRemovesDeletedLocalManifest()
    {
        Write("notify", Manifest("notification", "notification.v1", ["notify.emit"]));
        var registry = new LocalPluginRegistry();
        Assert.Single(registry.Discover(_directory).Items);
        File.Delete(Path.Combine(_directory, "notify.plugin.json"));
        Assert.Empty(registry.Discover(_directory).Items);
    }

    [Fact]
    public async Task Registry_EnableDisableAndListAreThreadSafe()
    {
        Write("notify", Manifest("notification", "notification.v1", ["notify.emit"]));
        var registry = new LocalPluginRegistry();
        registry.Discover(_directory);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) registry.Enable("wpe.test.notification", out _); else registry.Disable("wpe.test.notification");
            _ = registry.List();
        })));
        Assert.Single(registry.List().Items);
    }

    private void Write(string name, PluginManifestV1 manifest) => File.WriteAllText(
        Path.Combine(_directory, $"{name}.plugin.json"), JsonSerializer.Serialize(manifest, JsonOptions()));

    private static PluginManifestV1 Manifest(string type, string contract, IReadOnlyList<string> permissions,
        bool defaultEnabled = false, bool testnetOnly = false, string min = "3.0.0", string max = "3.9.9") => new(
        PluginPhase0.SchemaVersion,
        $"wpe.test.{type}",
        $"Test {type}",
        type,
        "1.2.3",
        new("wpe.tests", "WPE Tests", "test-key"),
        new(min, max, [PluginPhase0.HostOs], [PluginPhase0.HostRuntime]),
        new("wpe-contract", contract),
        permissions,
        new(defaultEnabled, testnetOnly),
        new("ed25519", "test-signature-metadata", DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
