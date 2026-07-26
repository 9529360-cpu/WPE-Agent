using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Services.Agent;

internal sealed record RecoveryReconciliationRequest(
    string ReceiptId,
    string CorrelationId,
    string ProviderId,
    string Environment,
    string AccountId,
    string Symbol,
    string IntentHash,
    DateTimeOffset RequestedAtUtc);

internal sealed record TrustedRecoveryObservation(
    ManagedPosition Position,
    DateTimeOffset ObservedAtUtc,
    ReduceOnlyRecoveryResult Result);

internal interface ITrustedRecoveryObservationSource
{
    Task<TrustedRecoveryObservation> ObserveAsync(RecoveryReconciliationRequest request,CancellationToken ct);
}

internal interface IRecoverySigningKeyStore
{
    RecoverySigningKeyLease OpenCurrentSigningKey();
    IReadOnlyList<RecoverySigningKeyLease> OpenVerificationKeys();
}

internal sealed class RecoverySigningKeyLease : IDisposable
{
    private byte[]? _key;

    internal RecoverySigningKeyLease(ReadOnlySpan<byte> key)
    {
        if(key.Length<32)throw new ArgumentException("Recovery receipt key must be at least 256 bits.",nameof(key));
        _key=key.ToArray();
    }

    internal ReadOnlySpan<byte> Key=>_key??throw new ObjectDisposedException(nameof(RecoverySigningKeyLease));

    public void Dispose()
    {
        if(_key is null)return;
        CryptographicOperations.ZeroMemory(_key);
        _key=null;
    }
}

internal sealed record RecoveryReconciliationServices(
    ITrustedRecoveryReconciler Reconciler,
    IReduceOnlyRecoveryReceiptVerifier Verifier);

internal interface ITrustedRecoveryReconciler
{
    Task<ReduceOnlyRecoveryReceipt> ReconcileAsync(RecoveryReconciliationRequest request,CancellationToken ct);
}

internal static class RecoveryReconciliationComposition
{
    internal static RecoveryReconciliationServices Create(
        IRecoverySigningKeyStore keyStore,
        ITrustedRecoveryObservationSource observations,
        Func<DateTimeOffset>? utcNow=null)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(observations);
        var signer=new TrustedRecoveryReconciler(keyStore,observations,utcNow??(()=>DateTimeOffset.UtcNow));
        return new(signer,new RecoveryReceiptVerifier(keyStore));
    }
}

internal sealed class TrustedRecoveryReconciler(
    IRecoverySigningKeyStore keyStore,
    ITrustedRecoveryObservationSource observations,
    Func<DateTimeOffset> utcNow):ITrustedRecoveryReconciler
{
    public async Task<ReduceOnlyRecoveryReceipt> ReconcileAsync(RecoveryReconciliationRequest request,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var observation=await observations.ObserveAsync(request,ct);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Position);
        var issuedAt=utcNow().ToUniversalTime();
        var unsigned=new ReduceOnlyRecoveryReceipt(
            request.ReceiptId,request.CorrelationId,request.ProviderId,request.Environment,request.AccountId,
            observation.Position.Symbol,observation.Position.Side,observation.Position.Quantity,
            observation.ObservedAtUtc.ToUniversalTime(),request.IntentHash,observation.Result,
            issuedAt,issuedAt.Add(TradingExecutionGateway.MaximumRecoveryObservationAge),string.Empty);
        using var key=keyStore.OpenCurrentSigningKey();
        return unsigned with{Signature=RecoveryReceiptSignature.Sign(unsigned,key.Key)};
    }

    private static void ValidateRequest(RecoveryReconciliationRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.ReceiptId)||string.IsNullOrWhiteSpace(request.CorrelationId)||
           string.IsNullOrWhiteSpace(request.ProviderId)||string.IsNullOrWhiteSpace(request.Environment)||
           string.IsNullOrWhiteSpace(request.AccountId)||string.IsNullOrWhiteSpace(request.Symbol)||
           string.IsNullOrWhiteSpace(request.IntentHash))
            throw new ArgumentException("Recovery reconciliation request is incomplete.",nameof(request));
    }
}

internal sealed class RecoveryReceiptVerifier(IRecoverySigningKeyStore keyStore):IReduceOnlyRecoveryReceiptVerifier
{
    public bool Verify(ReduceOnlyRecoveryReceipt receipt)
    {
        if(receipt is null||string.IsNullOrWhiteSpace(receipt.Signature))return false;
        byte[] provided;
        try{provided=Convert.FromHexString(receipt.Signature);}
        catch(FormatException){return false;}
        var keys=keyStore.OpenVerificationKeys();
        try
        {
            foreach(var key in keys)
            {
                var expected=Convert.FromHexString(RecoveryReceiptSignature.Sign(receipt with{Signature=string.Empty},key.Key));
                if(provided.Length==expected.Length&&CryptographicOperations.FixedTimeEquals(provided,expected))return true;
            }
            return false;
        }
        finally
        {
            foreach(var key in keys)key.Dispose();
        }
    }
}

internal static class RecoveryReceiptSignature
{
    internal static string Sign(ReduceOnlyRecoveryReceipt receipt,ReadOnlySpan<byte> key)
    {
        var payload=string.Join("\u001f",
            receipt.ReceiptId,receipt.CorrelationId,receipt.ProviderId,receipt.Environment,receipt.AccountId,
            receipt.Symbol,receipt.Side.ToString(),receipt.Quantity.ToString("G29",CultureInfo.InvariantCulture),
            receipt.ObservedAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
            receipt.IntentHash,receipt.Result.ToString(),
            receipt.IssuedAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
            receipt.ExpiresAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));
        return Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(payload)));
    }
}
