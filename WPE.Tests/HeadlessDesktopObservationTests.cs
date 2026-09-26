using System.Text.Json;
using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class HeadlessDesktopObservationTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-headless-desktop-observer-"+Guid.NewGuid().ToString("N"));
    private string HealthPath=>Path.Combine(_dir,"headless-health-v1.json");
    private static readonly DateTimeOffset Now=new(2026,9,26,18,30,0,TimeSpan.Zero);

    [Fact]
    public void FreshReadyHeadlessHealthIsAccepted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HealthPath,HealthJson(Now,ready:true));

        var value=new HeadlessRuntimeHealthReader(HealthPath,()=>Now).Read();

        Assert.True(value.Available);
        Assert.True(value.Ready);
        Assert.True(value.AgentRunning);
        Assert.True(value.AccessFresh);
        Assert.True(value.HeartbeatFresh);
        Assert.False(value.LeaseLost);
        Assert.Equal("run-headless",value.RunId);
        Assert.Equal(42,value.EventSequence);
    }

    [Fact]
    public void StaleHeadlessHealthCannotClaimTradingAuthority()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HealthPath,HealthJson(Now.AddSeconds(-16),ready:true));

        var value=new HeadlessRuntimeHealthReader(HealthPath,()=>Now).Read();

        Assert.True(value.Available);
        Assert.False(value.Ready);
        Assert.Equal("STALE",value.RecoveryStatus);
        Assert.False(value.AgentRunning);
    }

    [Fact]
    public void MalformedHeadlessHealthFailsClosed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HealthPath,"{not-json");

        var value=new HeadlessRuntimeHealthReader(HealthPath,()=>Now).Read();

        Assert.False(value.Available);
        Assert.False(value.Ready);
        Assert.Equal("headless.unavailable",value.Code);
    }

    [Fact]
    public void DesktopObserverUsesReadOnlyProviderSurfaceAndNoTradingMutationOwner()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","HeadlessDesktopObservation.cs"));

        Assert.Contains("IProviderReadOnlyAccessClient",source,StringComparison.Ordinal);
        Assert.Contains("ProviderReadOnlyAccessClient",source,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceMarketAsync",source,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceStopMarketAsync",source,StringComparison.Ordinal);
        Assert.DoesNotContain("CancelOrderAsync",source,StringComparison.Ordinal);
        Assert.DoesNotContain("ReliableOrderExecutor",source,StringComparison.Ordinal);
        Assert.DoesNotContain("TradingExecutionGateway",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AutoTradingAgent.StartDefault",source,StringComparison.Ordinal);

        var reference=File.ReadAllText(Path.Combine(root,"ReferenceUiWindow.xaml.cs"));
        var app=File.ReadAllText(Path.Combine(root,"App.xaml.cs"));
        Assert.Contains("command == \"agent-start\" && _agentControlAllowed()",reference,StringComparison.Ordinal);
        Assert.Contains("command == \"agent-stop\" && _agentControlAllowed()",reference,StringComparison.Ordinal);
        Assert.Contains("() => !runtimeHost.HeadlessAuthorityDetected",app,StringComparison.Ordinal);
        Assert.Contains("!runtimeHost.HeadlessAuthorityDetected",app,StringComparison.Ordinal);
    }

    private static string HealthJson(DateTimeOffset observedAt,bool ready)
    {
        var payload=new
        {
            schema="wpe.headless-process-health/1.0",
            processId=123,
            observedAtUtc=observedAt,
            state=ready?"ready":"blocked",
            code=ready?"headless.ready":"headless.blocked",
            runtime=new
            {
                schema="wpe.trading-runtime-health/1.0",
                observedAtUtc=observedAt,
                accessReady=ready,
                accessCheckedAtUtc=observedAt,
                agentRunning=ready,
                agentStatus=ready?"Running":"Stopped",
                runId="run-headless",
                runtimeHeartbeatAtUtc=observedAt,
                recoveryStatus="CLEAN_START",
                eventSequence=42,
                leaseLost=false,
                accessFresh=ready,
                heartbeatFresh=ready,
                ready
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    public void Dispose()
    {
        try{if(Directory.Exists(_dir))Directory.Delete(_dir,true);}catch{}
    }
}
