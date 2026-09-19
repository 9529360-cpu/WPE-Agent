using WpeAgent.RuntimeContracts;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class ProductionAutomaticCapabilityGateTests
{
    private static readonly DateTimeOffset Now=new(2026,9,20,0,0,0,TimeSpan.Zero);

    [Fact]
    public async Task StaleCapabilityRefreshesOnceAndThenAllows()
    {
        var artifact=Artifact();
        var capabilities=new Dictionary<string,ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"]=Capability(Now.AddSeconds(-20))
        };
        var calls=0;

        var available=await ProductionAutomaticCapabilityGate.EnsureAvailableAsync(
            artifact,capabilities,_=>
            {
                calls++;
                capabilities["BTCUSDT"]=Capability(Now);
                return Task.CompletedTask;
            },()=>Now,CancellationToken.None);

        Assert.True(available);
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task FreshCapabilityDoesNotProbeAgain()
    {
        var capabilities=new Dictionary<string,ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"]=Capability(Now.AddSeconds(-5))
        };
        var calls=0;

        var available=await ProductionAutomaticCapabilityGate.EnsureAvailableAsync(
            Artifact(),capabilities,_=>{calls++;return Task.CompletedTask;},()=>Now,CancellationToken.None);

        Assert.True(available);
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task FailedRefreshRemainsFailClosed()
    {
        var capabilities=new Dictionary<string,ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"]=Capability(Now.AddSeconds(-30))
        };

        var available=await ProductionAutomaticCapabilityGate.EnsureAvailableAsync(
            Artifact(),capabilities,_=>throw new IOException("probe failed"),()=>Now,CancellationToken.None);

        Assert.False(available);
    }

    [Fact]
    public void FutureCapabilityBeyondSkewWindowIsRejected()
    {
        var capabilities=new Dictionary<string,ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"]=Capability(Now.AddSeconds(20))
        };

        Assert.False(ProductionAutomaticCapabilityGate.IsAvailable(Artifact(),capabilities,Now));
    }

    private static DurableExecutionArtifactV2 Artifact()=>new(
        DurableExecutionArtifactV2.Version,"cycle-capability",
        [new(0,"BTCUSDT","Long",.001m,false,49_000m,51_000m,"WPE-AUTO-CAP","strategy.entry","OpenLong","Market",0,50_000m)],
        5,true,"binance-futures","Testnet","strategy","v1",Now.AddSeconds(-5),"market-v1",Now.AddSeconds(-2),Now.AddMinutes(2));

    private static ExchangeCapability Capability(DateTimeOffset checkedAt)=>new(
        "binance-futures","binance-futures","BTCUSDT","BTCUSDT",MarketType.Perpetual,
        CapabilityStatus.Available,true,true,true,checkedAt);
}
