using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

internal sealed record ProductionRecoveryServices(
    TradingExecutionGateway Gateway,
    ProductionRecoveryService Recovery);

internal static class ProductionRecoveryComposition
{
    internal static async Task<ProductionRecoveryServices> CreateAsync(
        IExchangeProvider provider,
        ITradingMutationExecutor executor,
        AgentSqliteStore store,
        string keyPath,
        Func<DateTimeOffset>? utcNow=null,
        CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(store);
        if(provider.Environment!=ExchangeEnvironment.Testnet||!executor.IsTestnet)
            throw new InvalidOperationException("Production recovery is Testnet-only.");
        if(!string.Equals(provider.ProviderId,executor.ProviderId,StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery provider identity does not match the mutation executor.");

        var permissions=await provider.CheckPermissionsAsync(ct);
        if(!permissions.CanRead||!permissions.CanTrade||permissions.CanWithdraw||string.IsNullOrWhiteSpace(permissions.AccountId))
            throw new InvalidOperationException("Recovery provider account identity or permissions are unsafe.");
        var trustedExecutor=new TrustedRecoveryMutationExecutor(executor,permissions.AccountId);

        var keys=new DpapiRecoverySigningKeyStore(keyPath,utcNow);
        var observations=new AuthenticatedProviderRecoveryObservationSource(provider,permissions.AccountId,utcNow);
        var authority=RecoveryReconciliationComposition.Create(keys,observations,utcNow);
        var gateway=new TradingExecutionGateway(trustedExecutor,store,utcNow,authority.Verifier);
        return new(gateway,new ProductionRecoveryService(
            authority.Reconciler,gateway,provider.ProviderId,permissions.AccountId,
            executor as IPendingExecutionRecovery));
    }

    internal static string DefaultKeyPath()=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WPE Agent","security","recovery-signing-key.json");
}

internal sealed class TrustedRecoveryMutationExecutor(ITradingMutationExecutor inner,string accountId):ITradingMutationExecutor
{
    public bool IsTestnet=>inner.IsTestnet;
    public string ProviderId=>inner.ProviderId;
    public string AccountId{get;}=accountId;
    public Task<string> ExecutePlanAsync(string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)=>
        inner.ExecutePlanAsync(correlationId,intents,leverage,isolated,ct);
    public Task<string> ExecuteReduceOnlyRecoveryAsync(string correlationId,ExecutionIntent intent,CancellationToken ct)=>
        inner.ExecuteReduceOnlyRecoveryAsync(correlationId,intent,ct);
    public Task<string> ReplaceProtectionAsync(string correlationId,ProtectionAdjustment adjustment,CancellationToken ct)=>
        inner.ReplaceProtectionAsync(correlationId,adjustment,ct);
}

internal sealed class ProductionRecoveryService(
    ITrustedRecoveryReconciler reconciler,
    TradingExecutionGateway gateway,
    string providerId,
    string accountId,
    IPendingExecutionRecovery? pendingRecovery=null)
{
    internal Task<RecoveryResult> RecoverPendingAsync(CancellationToken ct)=>
        pendingRecovery?.RecoverPendingAsync(ct)
        ?? Task.FromResult(new RecoveryResult(false,["recovery.pending-recovery-unavailable"]));

    internal async Task<TradingExecutionGatewayResult> ExecuteAsync(
        string correlationId,ExecutionIntent intent,int leverage,bool isolated,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(correlationId)||intent is null||!intent.ReduceOnly||
           intent.OrderType!=ExecutionOrderType.Market||intent.StopLoss!=0||intent.TakeProfit!=0)
            return new(false,"recovery.command-invalid","recovery.command-invalid");

        var intentHash=TradingExecutionGateway.ComputeIntentHash([intent],leverage,isolated);
        var request=new RecoveryReconciliationRequest(
            "recovery-receipt-"+Guid.NewGuid().ToString("N"),correlationId,providerId,"Testnet",accountId,
            intent.Symbol,intentHash,DateTimeOffset.UtcNow,intent.Side);
        var receipt=await reconciler.ReconcileAsync(request,ct);
        return await gateway.ExecuteReduceOnlyRecoveryAsync(
            new(correlationId,receipt,new(receipt.Symbol,receipt.Side,receipt.Quantity,0,0,0,
                leverage,isolated,0),intent,leverage,isolated),ct);
    }

    internal async Task<TradingExecutionGatewayResult> ReplaceProtectionAsync(
        string correlationId,ProtectionAdjustment adjustment,ManagedPosition observedPosition,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(correlationId)||adjustment is null||observedPosition is null||
           string.IsNullOrWhiteSpace(adjustment.AdjustmentId)||
           !string.Equals(adjustment.Symbol,observedPosition.Symbol,StringComparison.Ordinal)||
           adjustment.Side!=observedPosition.Side||observedPosition.Quantity<=0)
            return new(false,"recovery.protection-command-invalid","recovery.protection-command-invalid");

        var adjustmentHash=TradingExecutionGateway.ComputeProtectionAdjustmentHash(adjustment);
        var request=new RecoveryReconciliationRequest(
            "recovery-protection-"+Guid.NewGuid().ToString("N"),correlationId,providerId,"Testnet",accountId,
            adjustment.Symbol,adjustmentHash,DateTimeOffset.UtcNow,adjustment.Side);
        var receipt=await reconciler.ReconcileAsync(request,ct);
        return await gateway.ExecuteProtectionRecoveryAsync(
            new(correlationId,receipt,observedPosition,adjustment),ct);
    }
}

internal sealed class AuthenticatedProviderRecoveryObservationSource(
    IExchangeProvider provider,
    string expectedAccountId,
    Func<DateTimeOffset>? utcNow=null):ITrustedRecoveryObservationSource
{
    private readonly Func<DateTimeOffset> _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);

    public async Task<TrustedRecoveryObservation> ObserveAsync(RecoveryReconciliationRequest request,CancellationToken ct)
    {
        if(provider.Environment!=ExchangeEnvironment.Testnet||
           !string.Equals(request.Environment,"Testnet",StringComparison.Ordinal)||
           !string.Equals(request.ProviderId,provider.ProviderId,StringComparison.Ordinal)||
           !string.Equals(request.AccountId,expectedAccountId,StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery observation identity is not trusted.");

        var permissions=await provider.CheckPermissionsAsync(ct);
        if(!permissions.CanRead||!permissions.CanTrade||permissions.CanWithdraw||
           !string.Equals(permissions.AccountId,expectedAccountId,StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery provider permissions are unsafe or unavailable.");
        var positions=await provider.GetPositionsAsync(ct);
        var matches=positions.Where(x=>string.Equals(x.Symbol,request.Symbol,StringComparison.Ordinal)&&
            (request.Side is null||x.Side==request.Side.Value)).ToArray();
        if(matches.Length!=1||matches[0].Quantity<=0)
            throw new InvalidOperationException("Recovery position observation is missing or conflicting.");
        return new(matches[0],_utcNow().ToUniversalTime(),ReduceOnlyRecoveryResult.Verified);
    }
}

internal sealed class DpapiRecoverySigningKeyStore:IRecoverySigningKeyStore
{
    private static readonly byte[] Entropy="WPE-Agent-Recovery-Receipt-v1"u8.ToArray();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate=new();

    internal DpapiRecoverySigningKeyStore(string path,Func<DateTimeOffset>? utcNow=null)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Recovery signing keys require Windows DPAPI.");
        _path=string.IsNullOrWhiteSpace(path)?throw new ArgumentException("Recovery key path is required.",nameof(path)):Path.GetFullPath(path);
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    public RecoverySigningKeyLease OpenCurrentSigningKey()=>OpenKeys()[0];
    public IReadOnlyList<RecoverySigningKeyLease> OpenVerificationKeys()=>OpenKeys();

    private List<RecoverySigningKeyLease> OpenKeys()
    {
        lock(_gate)
        {
            var envelope=LoadOrCreate();
            var result=new List<RecoverySigningKeyLease>();
            try
            {
                foreach(var protectedKey in envelope.ProtectedKeys)
                {
                    var key=ProtectedData.Unprotect(Convert.FromBase64String(protectedKey),Entropy,DataProtectionScope.CurrentUser);
                    try{result.Add(new(key));}finally{CryptographicOperations.ZeroMemory(key);}
                }
                return result;
            }
            catch
            {
                foreach(var lease in result)lease.Dispose();
                throw;
            }
        }
    }

    private RecoveryKeyEnvelope LoadOrCreate()
    {
        if(File.Exists(_path))
        {
            var loaded=JsonSerializer.Deserialize<RecoveryKeyEnvelope>(File.ReadAllText(_path));
            if(loaded is null||loaded.Version!=1||loaded.ProtectedKeys.Count==0)
                throw new InvalidDataException("Recovery signing key envelope is invalid.");
            return loaded;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedKey=ProtectedData.Protect(key,Entropy,DataProtectionScope.CurrentUser);
            var envelope=new RecoveryKeyEnvelope(1,_utcNow().ToUniversalTime(),[Convert.ToBase64String(protectedKey)]);
            var temporary=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(envelope));
            File.Move(temporary,_path,false);
            return envelope;
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }

    private sealed record RecoveryKeyEnvelope(int Version,DateTimeOffset CreatedAtUtc,IReadOnlyList<string> ProtectedKeys);
}
