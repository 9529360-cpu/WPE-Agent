using System.Text.Json;
using 币安量化机器人.Services.Brokers;

namespace WPE.Tests;

public sealed class BrokerReconciliationAuditTests
{
    private static readonly DateTimeOffset At=DateTimeOffset.FromUnixTimeSeconds(4000);

    [Fact]
    public void AppliedReceipt_ContainsOnlyHashesAndDecisionMetadata()
    {
        var chain=new BrokerReconciliationAuditChain();
        var batch=Batch();
        var projection=Projection(batch);
        var audit=chain.Append(Selection(),batch,new(BrokerCapabilityStatus.Available,true,false,"APPLIED",projection),null);
        var json=JsonSerializer.Serialize(audit.Receipt);

        Assert.True(audit.Recorded);
        Assert.DoesNotContain("12345.67",json,StringComparison.Ordinal);
        Assert.DoesNotContain("order-private",json,StringComparison.Ordinal);
        Assert.DoesNotContain("fill-private",json,StringComparison.Ordinal);
        Assert.DoesNotContain("paper-account",json,StringComparison.Ordinal);
        Assert.True(BrokerReconciliationAuditChain.VerifyChain(chain.Receipts));
    }

    [Fact]
    public void ConsecutiveReceipts_FormHashChain()
    {
        var chain=new BrokerReconciliationAuditChain();
        var firstBatch=Batch();
        var first=AppendApplied(chain,firstBatch);
        var second=AppendApplied(chain,Batch("batch-2",2,At.AddSeconds(1)),Projection(firstBatch));
        Assert.Equal(first.Receipt!.ReceiptHash,second.Receipt!.PreviousReceiptHash);
        Assert.True(BrokerReconciliationAuditChain.VerifyChain(chain.Receipts));
    }

    [Fact]
    public void DuplicateBatch_FailsClosedWithoutAppending()
    {
        var chain=new BrokerReconciliationAuditChain();
        var batch=Batch();AppendApplied(chain,batch);
        var duplicate=AppendApplied(chain,batch);
        Assert.False(duplicate.Recorded);Assert.True(duplicate.FailClosed);
        Assert.Equal("BROKER_AUDIT_DUPLICATE",duplicate.ReasonCode);Assert.Single(chain.Receipts);
    }

    [Fact]
    public void TamperedReceiptFailsVerification()
    {
        var chain=new BrokerReconciliationAuditChain();var receipt=AppendApplied(chain,Batch()).Receipt!;
        Assert.False(BrokerReconciliationAuditChain.VerifyChain([receipt with { ReasonCode="tampered" }]));
    }

    [Fact]
    public void BrokenLinkFailsVerification()
    {
        var chain=new BrokerReconciliationAuditChain();var firstBatch=Batch();
        var first=AppendApplied(chain,firstBatch).Receipt!;
        var second=AppendApplied(chain,Batch("batch-2",2,At.AddSeconds(1)),Projection(firstBatch)).Receipt!;
        Assert.False(BrokerReconciliationAuditChain.VerifyChain([first,second with { PreviousReceiptHash="broken" }]));
    }

    [Fact]
    public void RejectedBatchRecordsReasonWithoutProjectionChange()
    {
        var chain=new BrokerReconciliationAuditChain();var batch=Batch();var previous=Projection(batch);
        var result=new BrokerReconciliationResult(BrokerCapabilityStatus.Error,false,false,"REJECTED_REASON",previous);
        var audit=chain.Append(Selection(),batch,result,previous);
        Assert.True(audit.Recorded);Assert.Equal("REJECTED",audit.Receipt!.Decision);
        Assert.Equal("REJECTED_REASON",audit.Receipt.ReasonCode);
        Assert.Equal(audit.Receipt.PreviousProjectionHash,audit.Receipt.NewProjectionHash);
    }

    [Fact]
    public void RejectedBatchCannotClaimProjectionChange()
    {
        var chain=new BrokerReconciliationAuditChain();var batch=Batch();var previous=Projection(batch);
        var changed=Projection(Batch("changed",2,At.AddSeconds(1)));
        var audit=chain.Append(Selection(),batch,new(BrokerCapabilityStatus.Error,false,false,"REJECTED",changed),previous);
        Assert.False(audit.Recorded);Assert.Equal("BROKER_AUDIT_REJECTED_PROJECTION_CHANGED",audit.ReasonCode);
    }

    [Fact]
    public void ReceiptContractHasNoRawFinancialOrOrderFields()
    {
        var names=typeof(BrokerReconciliationAuditReceipt).GetProperties().Select(x=>x.Name).ToArray();
        Assert.DoesNotContain(names,x=>x.Contains("Balance",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names,x=>x.Contains("Quantity",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names,x=>x.Contains("OrderId",StringComparison.OrdinalIgnoreCase));
    }

    private static BrokerReconciliationAuditResult AppendApplied(
        BrokerReconciliationAuditChain chain,BrokerReconciliationBatch batch,
        BrokerReadOnlyProjection? previous=null)
    {
        return chain.Append(Selection(),batch,new(BrokerCapabilityStatus.Available,true,false,"APPLIED",Projection(batch)),previous);
    }
    private static BrokerReconciliationBatch Batch(string id="batch-1",long sequence=1,DateTimeOffset? at=null)
    {
        var time=at??At;
        return new(id,sequence,"fake-provider","paper-account","XNAS","USD",time,"trace-1",
            new("paper-account","USD",12345.67m,12000m,12000m,time),
            [new("order-private","paper-account","XNAS","USD",BrokerOrderLifecycleStatus.Filled,time,"trace-1")],
            [new("fill-private","order-private","paper-account","XNAS","USD",9m,99m,time,"trace-1")]);
    }
    private static BrokerReadOnlyProjection Projection(BrokerReconciliationBatch b)=>new(
        b.BatchId,b.Sequence,b.ProviderId,b.AccountId,b.Venue,b.Currency,b.AsOfUtc,b.CorrelationId,b.Account,b.Orders,b.Fills);
    private static BrokerSandboxSelectionResult Selection()=>new(
        BrokerCapabilityStatus.Available,null,"selected",At);
}
