using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Brokers;

public sealed record BrokerReconciliationAuditReceipt(
    long Sequence,string ProviderAccountHash,string BatchHash,string SelectionCapabilityHash,
    DateTimeOffset AsOfUtc,string CorrelationId,string PreviousProjectionHash,string NewProjectionHash,
    string Decision,string ReasonCode,string PreviousReceiptHash,string ReceiptHash);

public sealed record BrokerReconciliationAuditResult(
    bool Recorded,bool FailClosed,string ReasonCode,BrokerReconciliationAuditReceipt? Receipt);

public sealed class BrokerReconciliationAuditChain
{
    public const string GenesisHash="GENESIS";
    private readonly List<BrokerReconciliationAuditReceipt> _receipts=[];
    public IReadOnlyList<BrokerReconciliationAuditReceipt> Receipts=>_receipts;

    public BrokerReconciliationAuditResult Append(
        BrokerSandboxSelectionResult selection,
        BrokerReconciliationBatch batch,
        BrokerReconciliationResult result,
        BrokerReadOnlyProjection? previousProjection)
    {
        if(!VerifyChain(_receipts))return Failed("BROKER_AUDIT_CHAIN_INVALID");
        var batchHash=Hash(batch);
        if(_receipts.Any(x=>x.BatchHash==batchHash))return Failed("BROKER_AUDIT_DUPLICATE");

        var previousProjectionHash=HashProjection(previousProjection);
        var newProjectionHash=HashProjection(result.Projection);
        if(_receipts.Count>0&&_receipts[^1].NewProjectionHash!=previousProjectionHash)
            return Failed("BROKER_AUDIT_PROJECTION_LINK_INVALID");
        if(!result.Applied&&previousProjectionHash!=newProjectionHash)
            return Failed("BROKER_AUDIT_REJECTED_PROJECTION_CHANGED");

        var previousReceiptHash=_receipts.Count==0?GenesisHash:_receipts[^1].ReceiptHash;
        var unsigned=new BrokerReconciliationAuditReceipt(
            _receipts.Count+1,
            Hash(new { batch.ProviderId,batch.AccountId }),
            batchHash,
            Hash(new { selection.Status,ProviderId=selection.Provider?.ProviderId,selection.ReasonCode,selection.CheckedAtUtc }),
            batch.AsOfUtc,
            batch.CorrelationId,
            previousProjectionHash,
            newProjectionHash,
            result.Applied?"APPLIED":result.Idempotent?"IDEMPOTENT":"REJECTED",
            result.ReasonCode,
            previousReceiptHash,
            string.Empty);
        var receipt=unsigned with { ReceiptHash=Hash(unsigned) };
        _receipts.Add(receipt);
        return new(true,false,string.Empty,receipt);
    }

    public static bool VerifyChain(IEnumerable<BrokerReconciliationAuditReceipt> receipts)
    {
        var previous=GenesisHash;
        long sequence=0;
        foreach(var receipt in receipts)
        {
            sequence++;
            if(receipt.Sequence!=sequence||receipt.PreviousReceiptHash!=previous)return false;
            var expected=Hash(receipt with { ReceiptHash=string.Empty });
            if(!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(receipt.ReceiptHash)))return false;
            previous=receipt.ReceiptHash;
        }
        return true;
    }

    private static string HashProjection(BrokerReadOnlyProjection? projection)=>
        projection is null?Hash("NO_PROJECTION"):Hash(projection);
    private static string Hash(object value)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static BrokerReconciliationAuditResult Failed(string reason)=>new(false,true,reason,null);
}
