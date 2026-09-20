using System.Text.Json;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class RuntimeBrokerStateTests
{
    private static readonly DateTimeOffset Now=new(2026,7,29,12,0,0,TimeSpan.Zero);

    [Fact]
    public void DefaultState_IsUnsupportedAndSnapshotWithholdsValue()
    {
        var store=new RuntimeBrokerStateStore(()=>Now);
        var state=store.Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,brokerState:state);

        Assert.Equal(RuntimeCollectionState.Unsupported,state.State);
        Assert.Null(state.Value);
        Assert.Equal(RuntimeCollectionState.Unsupported,snapshot.EquityBroker.State);
        Assert.Null(snapshot.EquityBroker.Value);
    }

    [Fact]
    public void AvailablePaperOrSandboxCapability_RoundTripsAsReadOnlyRuntimeFact()
    {
        var store=new RuntimeBrokerStateStore(()=>Now);
        store.Publish("paper-provider",BrokerEnvironment.Sandbox,new(
            BrokerCapabilityStatus.Available,true,true,true,false,false,Now.AddSeconds(-1),"BROKER_CAPABILITY_AVAILABLE"));

        var state=store.Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,brokerState:state);
        var value=Assert.IsType<RuntimeBrokerCapabilityV1>(snapshot.EquityBroker.Value);

        Assert.Equal(RuntimeCollectionState.Available,snapshot.EquityBroker.State);
        Assert.Equal("paper-provider",value.ProviderId);
        Assert.Equal("Sandbox",value.Environment);
        Assert.True(value.CanReadAccounts);
        Assert.True(value.CanReadPositions);
        Assert.True(value.CanReadOrders);
        Assert.False(value.CanSubmitOrders);
        Assert.False(value.CanCancelOrders);
    }

    [Fact]
    public void ExpiredCapability_IsStaleAndValueIsWithheld()
    {
        var clock=Now;
        var store=new RuntimeBrokerStateStore(()=>clock);
        store.Publish("paper-provider",BrokerEnvironment.Paper,new(
            BrokerCapabilityStatus.Available,true,true,true,false,false,Now,"BROKER_CAPABILITY_AVAILABLE"));

        clock=Now+RuntimeBrokerStateStore.StaleAfter+TimeSpan.FromTicks(1);
        var state=store.Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=clock.UtcDateTime},clock.UtcDateTime,brokerState:state);

        Assert.Equal(RuntimeCollectionState.Stale,state.State);
        Assert.Null(state.Value);
        Assert.Equal(RuntimeCollectionState.Stale,snapshot.EquityBroker.State);
        Assert.Null(snapshot.EquityBroker.Value);
    }

    [Theory]
    [InlineData(BrokerCapabilityStatus.Unknown)]
    [InlineData(BrokerCapabilityStatus.Unsupported)]
    [InlineData(BrokerCapabilityStatus.Stale)]
    [InlineData(BrokerCapabilityStatus.Error)]
    public void NonAvailableCapability_NeverProjectsValue(BrokerCapabilityStatus status)
    {
        var store=new RuntimeBrokerStateStore(()=>Now);
        store.Publish("paper-provider",BrokerEnvironment.Sandbox,new(
            status,true,true,true,true,true,Now,"BROKER_CAPABILITY_UNAVAILABLE"));

        var state=store.Read();
        Assert.NotEqual(RuntimeCollectionState.Available,state.State);
        Assert.Null(state.Value);
    }

    [Fact]
    public void InvalidMetadataOrMutationWithoutOrderRead_FailsClosedWithoutLeakingReasonText()
    {
        const string secret="sk-broker-secret-token-123456";
        var futureStore=new RuntimeBrokerStateStore(()=>Now);
        futureStore.Publish("provider?token="+secret,BrokerEnvironment.Sandbox,new(
            BrokerCapabilityStatus.Available,true,true,true,false,false,Now.AddSeconds(1),"BROKER_CAPABILITY_AVAILABLE"));
        var inconsistentStore=new RuntimeBrokerStateStore(()=>Now);
        inconsistentStore.Publish("paper-provider",BrokerEnvironment.Sandbox,new(
            BrokerCapabilityStatus.Available,true,true,false,true,false,Now,"Authorization-"+secret));

        foreach(var state in new[]{futureStore.Read(),inconsistentStore.Read()})
        {
            Assert.Equal(RuntimeCollectionState.Error,state.State);
            Assert.Null(state.Value);
            Assert.DoesNotContain(secret,JsonSerializer.Serialize(state),StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SnapshotFactory_RejectsFutureDirectBrokerState()
    {
        var value=new RuntimeBrokerCapabilityV1("paper-provider","Paper",true,true,true,false,false,Now.AddSeconds(1),"BROKER_CAPABILITY_AVAILABLE");
        var state=new RuntimeBrokerState(RuntimeCollectionState.Available,value,Now.AddSeconds(1),null);

        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,brokerState:state);

        Assert.Equal(RuntimeCollectionState.Error,snapshot.EquityBroker.State);
        Assert.Null(snapshot.EquityBroker.Value);
    }
}
