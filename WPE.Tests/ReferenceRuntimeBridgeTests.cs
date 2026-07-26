using System.Text.Json;
using WpeAgent;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;

namespace WPE.Tests;

public sealed class ReferenceRuntimeBridgeTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 13, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("open-settings")]
    [InlineData("agent-start")]
    [InlineData("agent-stop")]
    public void HostCommandAllowlist_AcceptsOnlyNamedCommands(string command)
    {
        Assert.True(ReferenceUiWindow.TryGetHostCommand(JsonSerializer.Serialize(new { type = command }), out var parsed));
        Assert.Equal(command, parsed);
        Assert.False(ReferenceUiWindow.TryGetHostCommand("{\"type\":\"place-order\"}", out _));
    }

    [Fact]
    public void TrustedSnapshot_RequiresFreshTestnetProviderAndAuthority()
    {
        var json = SnapshotJson(Now.AddSeconds(-2), Now, ready: true, permission: true);
        Assert.True(ReferenceUiWindow.IsTrustedRuntimeJson(json, true, Now));
        Assert.True(ReferenceUiWindow.IsTrustedRuntimeJson(json, false, Now));
    }

    [Theory]
    [InlineData(-20, true, true)]
    [InlineData(2, true, true)]
    [InlineData(-2, false, true)]
    [InlineData(-2, true, false)]
    public void Start_FailsClosedForStaleFutureOrRevokedAuthority(int sourceOffsetSeconds, bool ready, bool permission)
    {
        Assert.False(ReferenceUiWindow.IsTrustedRuntimeJson(SnapshotJson(Now.AddSeconds(sourceOffsetSeconds), Now, ready, permission), true, Now));
    }

    [Fact]
    public void Stop_FailsClosedForRevokedAuthority()
    {
        Assert.False(ReferenceUiWindow.IsTrustedRuntimeJson(SnapshotJson(Now.AddSeconds(-2), Now, ready: false, permission: false), true, Now));
    }

    [Fact]
    public void MalformedOrUnsupportedSnapshot_FailsClosed()
    {
        Assert.False(ReferenceUiWindow.IsTrustedRuntimeJson("{}", true, Now));
        Assert.False(ReferenceUiWindow.IsTrustedRuntimeJson("not-json", false, Now));
        Assert.False(ReferenceUiWindow.IsTrustedRuntimeJson(SnapshotJson(Now.AddSeconds(-2), Now, true, true).Replace("\"1.0\"", "\"2.0\""), true, Now));
    }

    [Fact]
    public void RuntimeSnapshotFactory_FutureSourceIsNotFresh()
    {
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = Now.AddSeconds(1) }, Now);
        Assert.False(snapshot.Freshness.Fresh);
        Assert.Equal("Stale", snapshot.Account.State.ToString());
    }

    private static string SnapshotJson(DateTime source, DateTime generated, bool ready, bool permission) => JsonSerializer.Serialize(new
    {
        contractVersion = "1.0", environment = "Testnet", generatedAtUtc = generated, sourceUpdatedAtUtc = source,
        freshness = new { fresh = generated >= source && (generated - source).TotalSeconds <= 15, ageSeconds = Math.Max(0, (generated - source).TotalSeconds), staleAfterSeconds = 15 },
        connectionStatus = new { state = "Available", value = new { providerId = "binance", environment = "Testnet", ready, exchangeConnected = ready, tradePermission = permission } }
    });
}
