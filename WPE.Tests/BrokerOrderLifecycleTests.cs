using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class BrokerOrderLifecycleTests
{
    [Fact]
    public void ValidLifecycle_AppliesDeterministically()
    {
        var lifecycle=new BrokerOrderLifecycle();

        AssertApplied(lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Accepted,1,0m)));
        AssertApplied(lifecycle.Apply(Update(BrokerOrderLifecycleStatus.PartiallyFilled,2,2m)));
        var filled=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Filled,3,5m));

        AssertApplied(filled);
        Assert.Equal(BrokerOrderLifecycleStatus.Filled,filled.Snapshot!.Status);
        Assert.False(filled.Snapshot.CanRequestCancel);
        Assert.False(filled.Snapshot.CanRequestReplace);
    }

    [Theory]
    [InlineData(BrokerOrderLifecycleStatus.Accepted)]
    [InlineData(BrokerOrderLifecycleStatus.PartiallyFilled)]
    [InlineData(BrokerOrderLifecycleStatus.Filled)]
    [InlineData(BrokerOrderLifecycleStatus.Cancelled)]
    [InlineData(BrokerOrderLifecycleStatus.Rejected)]
    public void ExplicitAdapterStatuses_AreAcceptedAsInitialObservation(
        BrokerOrderLifecycleStatus status)
    {
        var result=new BrokerOrderLifecycle().Apply(Update(status,1,status==BrokerOrderLifecycleStatus.PartiallyFilled?1m:0m));

        AssertApplied(result);
        Assert.Equal(status,result.Snapshot!.Status);
    }

    [Fact]
    public void UnknownStatus_FailsClosedAndCannotRequestMutation()
    {
        var lifecycle=new BrokerOrderLifecycle();
        var unknown=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Unknown,2,0m));

        Assert.True(unknown.Applied);
        Assert.True(unknown.FailClosed);
        Assert.Equal(BrokerOrderLifecycle.UnknownObservedReason,unknown.ReasonCode);
        Assert.False(unknown.Snapshot!.CanRequestCancel);
        Assert.False(unknown.Snapshot.CanRequestReplace);

        var later=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Accepted,3,0m));
        AssertRejected(later,BrokerOrderLifecycle.OutOfOrderReason);
        Assert.Equal(BrokerOrderLifecycleStatus.Unknown,later.Snapshot!.Status);
    }

    [Fact]
    public void UnrecognizedEnumValue_FailsClosedWithoutState()
    {
        var result=new BrokerOrderLifecycle().Apply(Update((BrokerOrderLifecycleStatus)999,1,0m));

        AssertRejected(result,BrokerOrderLifecycle.UnknownStatusReason);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void MissingBrokerOrderId_FailsClosedWithoutState()
    {
        var result=new BrokerOrderLifecycle().Apply(Update(BrokerOrderLifecycleStatus.Accepted,1,0m) with
        {
            BrokerOrderId=" "
        });

        AssertRejected(result,BrokerOrderLifecycle.MissingOrderIdReason);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void DuplicateUpdate_FailsClosedWithoutChangingState()
    {
        var lifecycle=new BrokerOrderLifecycle();
        var accepted=Update(BrokerOrderLifecycleStatus.Accepted,1,0m);
        lifecycle.Apply(accepted);

        var duplicate=lifecycle.Apply(accepted);

        AssertRejected(duplicate,BrokerOrderLifecycle.DuplicateUpdateReason);
        Assert.Equal(1,duplicate.Snapshot!.Sequence);
    }

    [Fact]
    public void OutOfOrderSequence_FailsClosedWithoutChangingState()
    {
        var lifecycle=new BrokerOrderLifecycle();
        lifecycle.Apply(Update(BrokerOrderLifecycleStatus.PartiallyFilled,4,2m));

        var result=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Filled,3,5m));

        AssertRejected(result,BrokerOrderLifecycle.OutOfOrderReason);
        Assert.Equal(BrokerOrderLifecycleStatus.PartiallyFilled,result.Snapshot!.Status);
    }

    [Theory]
    [InlineData(BrokerOrderLifecycleStatus.Filled)]
    [InlineData(BrokerOrderLifecycleStatus.Cancelled)]
    [InlineData(BrokerOrderLifecycleStatus.Rejected)]
    public void TerminalState_RejectsLaterUpdates(BrokerOrderLifecycleStatus terminal)
    {
        var lifecycle=new BrokerOrderLifecycle();
        lifecycle.Apply(Update(terminal,1,terminal==BrokerOrderLifecycleStatus.Filled?5m:0m));

        var result=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Accepted,2,0m));

        AssertRejected(result,BrokerOrderLifecycle.OutOfOrderReason);
        Assert.Equal(terminal,result.Snapshot!.Status);
    }

    [Fact]
    public void BrokerOrderIdCannotChangeWithinLifecycle()
    {
        var lifecycle=new BrokerOrderLifecycle();
        lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Accepted,1,0m));

        var result=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.Filled,2,5m) with
        {
            BrokerOrderId="broker-order-2"
        });

        AssertRejected(result,BrokerOrderLifecycle.OrderIdMismatchReason);
    }

    [Fact]
    public void FilledQuantityCannotMoveBackward()
    {
        var lifecycle=new BrokerOrderLifecycle();
        lifecycle.Apply(Update(BrokerOrderLifecycleStatus.PartiallyFilled,1,3m));

        var result=lifecycle.Apply(Update(BrokerOrderLifecycleStatus.PartiallyFilled,2,2m));

        AssertRejected(result,BrokerOrderLifecycle.OutOfOrderReason);
        Assert.Equal(3m,result.Snapshot!.FilledQuantity);
    }

    private static BrokerOrderLifecycleUpdate Update(
        BrokerOrderLifecycleStatus status,long sequence,decimal filledQuantity) => new(
            "broker-order-1",status,sequence,filledQuantity,DateTimeOffset.UnixEpoch,"trace-1");

    private static void AssertApplied(BrokerOrderLifecycleResult result)
    {
        Assert.True(result.Applied);
        Assert.False(result.FailClosed);
        Assert.Equal(BrokerOrderLifecycle.AppliedReason,result.ReasonCode);
    }

    private static void AssertRejected(BrokerOrderLifecycleResult result,string reasonCode)
    {
        Assert.False(result.Applied);
        Assert.True(result.FailClosed);
        Assert.Equal(reasonCode,result.ReasonCode);
    }
}
