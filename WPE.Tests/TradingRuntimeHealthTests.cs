using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class TradingRuntimeHealthTests
{
    private static readonly DateTimeOffset Now=new(2026,9,19,10,0,0,TimeSpan.Zero);

    [Fact]
    public void ReadyRequiresFreshAccessFreshHeartbeatRunningAgentAndOwnedLease()
    {
        var ready=Health(Now.AddMinutes(-1),Now.AddSeconds(-5),agentRunning:true,leaseLost:false);
        Assert.True(ready.AccessFresh);
        Assert.True(ready.HeartbeatFresh);
        Assert.True(ready.Ready);

        Assert.False(Health(Now.AddMinutes(-31),Now.AddSeconds(-5),true,false).Ready);
        Assert.False(Health(Now.AddMinutes(-1),Now.AddSeconds(-16),true,false).Ready);
        Assert.False(Health(Now.AddMinutes(-1),Now.AddSeconds(-5),false,false).Ready);
        Assert.False(Health(Now.AddMinutes(-1),Now.AddSeconds(-5),true,true).Ready);
    }

    [Fact]
    public void FutureOrMissingAuthorityTimestampsFailClosed()
    {
        Assert.False(Health(Now.AddSeconds(1),Now.AddSeconds(-5),true,false).AccessFresh);
        Assert.False(Health(Now.AddMinutes(-1),Now.AddSeconds(1),true,false).HeartbeatFresh);

        var missing=new TradingRuntimeHealthV1(
            TradingRuntimeHealthV1.CurrentSchema,Now,true,null,true,"Running","run",null,"NOT_STARTED",0,false);
        Assert.False(missing.Ready);
    }

    [Fact]
    public void RuntimeRefreshesAccessBeforeFreshnessWindowExpires()
    {
        Assert.True(TradingRuntimeHost.AccessRefreshInterval<TradingRuntimeHealthV1.MaximumAccessAge);
        Assert.Equal(TimeSpan.FromMinutes(10),TradingRuntimeHost.AccessRefreshInterval);

        var source=File.ReadAllText(Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..")),
            "Services",
            "TradingRuntimeHost.cs"));
        Assert.Contains("if (now >= _nextAccessRefreshAtUtc)",source,StringComparison.Ordinal);
        Assert.Contains("await RefreshAccessAsync().ConfigureAwait(false)",source,StringComparison.Ordinal);
        Assert.Contains("Periodic access readiness refresh failed.",source,StringComparison.Ordinal);
    }

    private static TradingRuntimeHealthV1 Health(DateTimeOffset access,DateTimeOffset heartbeat,bool agentRunning,bool leaseLost)=>
        new(TradingRuntimeHealthV1.CurrentSchema,Now,true,access,agentRunning,"Running","run",heartbeat,leaseLost?"LEASE_LOST":"CLEAN_START",1,leaseLost);
}
