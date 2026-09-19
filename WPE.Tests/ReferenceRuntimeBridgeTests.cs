using System.Text.Json;
using WpeAgent;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;

namespace WPE.Tests;

public sealed class ReferenceRuntimeBridgeTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 13, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void WebViewUserDataLivesOutsideTheImmutableInstallDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "ReferenceUiWindow.xaml.cs"));
        Assert.Contains("Path.Combine(AppDataPaths.RuntimeDirectory, \"WebView2\")", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataDirectory)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCoreWebView2Async();", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open-settings")]
    [InlineData("open-notification-settings")]
    [InlineData("agent-start")]
    [InlineData("agent-stop")]
    public void HostCommandAllowlist_AcceptsOnlyNamedCommands(string command)
    {
        Assert.True(ReferenceUiWindow.TryGetHostCommand(JsonSerializer.Serialize(new { type = command }), out var parsed));
        Assert.Equal(command, parsed);
        Assert.False(ReferenceUiWindow.TryGetHostCommand("{\"type\":\"place-order\"}", out _));
    }

    [Fact]
    public void HistoricalPageQuery_IsStrictReadOnlyAndSeparateFromControlCommands()
    {
        const string cursor="opaque-host-signed-cursor";
        var json=JsonSerializer.Serialize(new{type="history-page",requestId="request_1",collection="orders",cursor});
        Assert.False(ReferenceUiWindow.TryGetHostCommand(json,out _));
        Assert.True(ReferenceUiWindow.TryGetHistoricalPageRequest(json,out var request));
        Assert.Equal("request_1",request.RequestId);Assert.Equal(HistoricalCollectionKindV1.Orders,request.Kind);Assert.Equal(cursor,request.Cursor);

        foreach(var invalid in new[]{
            JsonSerializer.Serialize(new{type="history-page",requestId="../bad",collection="orders",cursor}),
            JsonSerializer.Serialize(new{type="history-page",requestId="request_1",collection="unknown",cursor}),
            JsonSerializer.Serialize(new{type="history-page",requestId="request_1",collection="orders",cursor="",limit=1}),
            JsonSerializer.Serialize(new{type="history-page",requestId="request_1",collection="orders",cursor,limit=1})
        })Assert.False(ReferenceUiWindow.TryGetHistoricalPageRequest(invalid,out _));
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
