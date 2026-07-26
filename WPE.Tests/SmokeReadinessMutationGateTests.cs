using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;
using WpeAgent.RuntimeContracts;

namespace WPE.Tests;

public sealed class SmokeReadinessMutationGateTests
{
    private static readonly DateTimeOffset Now=new(2026,7,22,7,0,0,TimeSpan.Zero);

    [Theory]
    [InlineData("clock","Missing")]
    [InlineData("clock","Stale")]
    [InlineData("capability","Unknown")]
    [InlineData("permission","Unsupported")]
    [InlineData("endpoint","Missing")]
    [InlineData("account","Unknown")]
    [InlineData("rule","Unsupported")]
    [InlineData("market","Missing")]
    [InlineData("evidence","Stale")]
    public async Task BlockedReadiness_NeverEntersOpenStepOrMutatesProvider(string category,string stateName)
    {
        var state=Enum.Parse<SmokeReadinessState>(stateName);
        var provider=new RecordingProvider();var readiness=Ready() with
        {
            Observations=Ready().Observations.Select(x=>x.Name==category?x with{State=state}:x).ToArray()
        };

        var result=await Execute(readiness,provider);

        Assert.False(result.Executed);Assert.Equal(0,provider.OpenStepCount);Assert.Equal(0,provider.MutationCount);
    }

    [Fact]
    public async Task AmbiguousClockSkewMissingSource_FailsClosedBeforeOpen()
    {
        var provider=new RecordingProvider();var readiness=Ready() with{MissingSources=["BTCUSDT:clock_skew"]};
        var result=await Execute(readiness,provider);
        Assert.False(result.Executed);Assert.Equal("smoke.readiness.evidence-source-missing",result.Code);
        Assert.Equal(0,provider.OpenStepCount);Assert.Equal(0,provider.MutationCount);
    }

    [Fact]
    public async Task SourceMismatchAndMainnet_AreDeniedWithoutMutation()
    {
        var sourceMismatch=Ready() with{Observations=Ready().Observations.Select(x=>x.Name=="account"?x with{SourceId="other"}:x).ToArray()};
        var provider=new RecordingProvider();Assert.False((await Execute(sourceMismatch,provider)).Executed);
        Assert.False((await Execute(Ready() with{IsTestnet=false},provider)).Executed);
        Assert.Equal(0,provider.OpenStepCount);Assert.Equal(0,provider.MutationCount);
    }

    [Fact]
    public async Task AllowedReadiness_EntersOpenStepOnce()
    {
        var provider=new RecordingProvider();var result=await Execute(Ready(),provider);
        Assert.True(result.Executed);Assert.Equal(1,provider.OpenStepCount);Assert.Equal(1,provider.MutationCount);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("rule")]
    public async Task ProductionComposition_PreservesStaleAcquisitionTimeAndNeverEntersOpen(string staleCategory)
    {
        var provider=new RecordingProvider();var stale=DateTimeOffset.UtcNow-TimeSpan.FromMinutes(3);var fresh=DateTimeOffset.UtcNow;
        var readiness=SmokeTestRunner.CreateMutationReadiness(
            new(){IsTestnet=true,Endpoint="https://testnet.example"},"provider:account",
            new(true,true,false,"account",[]),staleCategory=="permission"?stale:fresh,
            new(true,1,0,"ok",fresh.UtcDateTime),new(1000,1000,1000,fresh.UtcDateTime),
            new("BTCUSDT",.001m,.1m,.001m,5,10),staleCategory=="rule"?stale:fresh,
            Market(fresh),Evidence(fresh),Capability(fresh));

        var result=await Execute(readiness,provider);

        Assert.False(result.Executed);Assert.Equal($"smoke.readiness.{staleCategory}-stale",result.Code);
        Assert.Equal(0,provider.OpenStepCount);Assert.Equal(0,provider.MutationCount);
    }

    private static Task<TradingExecutionGatewayResult> Execute(SmokeMutationReadiness readiness,RecordingProvider provider)=>
        SmokeTestRunner.ExecuteRiskIncreaseAsync(readiness,provider.OpenAsync,CancellationToken.None);
    private static SmokeMutationReadiness Ready()=>new(true,"provider:account",
    [
        Observation("clock"),Observation("capability"),Observation("permission"),Observation("endpoint"),
        Observation("account"),Observation("rule"),Observation("market"),Observation("evidence")
    ],[]);
    private static SmokeReadinessObservation Observation(string name)=>new(name,SmokeReadinessState.Available,"provider:account",DateTimeOffset.UtcNow);
    private static MarketEvidence Market(DateTimeOffset observedAt)=>new("BTCUSDT",100,90,110,50,0,0,0,new(0,0,1,1,1,1,0),observedAt.UtcDateTime);
    private static EvidencePack Evidence(DateTimeOffset observedAt)=>new(){CollectedAt=observedAt.UtcDateTime,Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",Market(observedAt)}}};
    private static ExchangeCapability Capability(DateTimeOffset observedAt)=>new("exchange","provider","BTCUSDT","BTCUSDT",MarketType.Perpetual,CapabilityStatus.Available,true,true,true,observedAt);

    private sealed class RecordingProvider
    {
        public int OpenStepCount { get; private set; }
        public int MutationCount { get; private set; }
        public Task<TradingExecutionGatewayResult> OpenAsync()
        {
            OpenStepCount++;MutationCount++;
            return Task.FromResult(new TradingExecutionGatewayResult(true,"opened"));
        }
    }
}
