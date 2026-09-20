using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ExecutionMutationBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-mutation-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProtectionReplacementFailureCarriesStableSafetyExitReasonCode()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","EvidenceAndExecution.cs"));

        Assert.Contains("ReasonCode:PositionExitReasonCodes.ProtectionReplaceFailed",source,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CapabilityStatus.Unsupported)]
    [InlineData(CapabilityStatus.Stale)]
    [InlineData(CapabilityStatus.Error)]
    public async Task ReplaceProtection_UnavailableCapabilityRejectsBeforeProviderMutation(CapabilityStatus status)
    {
        var exchange = new RecordingExchange();
        var executor = CreateExecutor(exchange, out _, status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ReplaceProtectionAsync(
            "cycle", new ProtectionAdjustment("SOLUSDT", PositionSide.Long, 140m, 160m, "test"), CancellationToken.None));

        Assert.Contains("CapabilityBlocked", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, exchange.MutationCount);
    }

    [Theory]
    [InlineData(CapabilityStatus.Unsupported)]
    [InlineData(CapabilityStatus.Stale)]
    [InlineData(CapabilityStatus.Error)]
    public async Task Execute_UnavailableCapabilityRejectsBeforeProviderMutation(CapabilityStatus status)
    {
        var exchange=new RecordingExchange();
        var executor=CreateExecutor(exchange,out _,status);
        var intent=OpeningIntent() with { ReduceOnly=true,Action=DecisionAction.CloseLong };

        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>executor.ExecuteAsync("cycle",intent,5,true,CancellationToken.None));

        Assert.Contains("CapabilityBlocked",error.Message,StringComparison.Ordinal);
        Assert.Equal(0,exchange.MutationCount);
    }

    [Fact]
    public async Task ReplaceProtection_ExpiredAvailableCapabilityRejectsBeforeProviderMutation()
    {
        var exchange = new RecordingExchange();
        var executor = CreateExecutor(exchange,out _,CapabilityStatus.Available,DateTimeOffset.UtcNow-ProviderCapabilityPrecondition.MaximumAge-TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ReplaceProtectionAsync(
            "cycle",new ProtectionAdjustment("SOLUSDT",PositionSide.Long,140m,160m,"test"),CancellationToken.None));

        Assert.Contains("stale",error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0,exchange.MutationCount);
    }

    [Fact]
    public async Task ReplaceProtection_InPlaceProviderSkipsCancelAndUpdatesDirectly()
    {
        var protection=new ExchangeOrder(
            "SOLUSDT","position-protection:SOLUSDT:long:position_tpsl","position-protection",
            "NEW",0,0,"POSITION_TPSL",PositionSide.Long,true,DateTime.UtcNow);
        var exchange=new RecordingInPlaceProtectionExchange
        {
            OpenOrders=[protection],
            FailCancel=true
        };
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        var adjustment=new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,150.075m,160m,"breakeven","WPE-PM-BE-inplace");

        await executor.ReplaceProtectionAsync("cycle",adjustment,CancellationToken.None);

        Assert.Equal(0,exchange.CancelAttempts);
        Assert.Equal(1,exchange.ProtectionSubmissions);
        Assert.Equal("COMPLETED",await store.GetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceProtection_CancellationDuringCancelPersistsCanceledWithoutEmergencyClose()
    {
        using var cts=new CancellationTokenSource();
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);
        var protection=new ExchangeOrder(
            "SOLUSDT","protection-stop","protection-client","NEW",0,0,"STOP_MARKET",
            PositionSide.Long,true,DateTime.UtcNow);
        var exchange=new RecordingExchange
        {
            OpenOrders=[protection],
            Positions=[position],
            CancelCancellationSource=cts
        };
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        var adjustment=new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,150.075m,160m,"breakeven","WPE-PM-BE-cancelled-cancel");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            executor.ReplaceProtectionAsync("cycle",adjustment,cts.Token));

        Assert.Equal(0,exchange.MarketOrderSubmissions);
        Assert.Equal("CANCELED",await store.GetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceProtection_CancellationDuringInPlaceUpdatePersistsCanceledWithoutEmergencyClose()
    {
        using var cts=new CancellationTokenSource();
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);
        var exchange=new RecordingInPlaceProtectionExchange
        {
            Positions=[position],
            ProtectionCancellationSource=cts
        };
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        var adjustment=new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,150.075m,160m,"breakeven","WPE-PM-BE-cancelled-place");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            executor.ReplaceProtectionAsync("cycle",adjustment,cts.Token));

        Assert.Equal(0,exchange.MarketOrderSubmissions);
        Assert.Equal("CANCELED",await store.GetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceProtection_CancelFailureEmergencyClosesManagedPosition()
    {
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);
        var protection=new ExchangeOrder(
            "SOLUSDT","protection-stop","protection-client","NEW",0,0,"STOP_MARKET",
            PositionSide.Long,true,DateTime.UtcNow);
        var exchange=new RecordingExchange
        {
            OpenOrders=[protection],
            Positions=[position],
            FailCancel=true
        };
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        var adjustment=new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,150.075m,160m,"breakeven","WPE-PM-BE-cancelfail");

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            executor.ReplaceProtectionAsync("cycle",adjustment,CancellationToken.None));

        Assert.Equal(1,exchange.MarketOrderSubmissions);
        Assert.True(exchange.LastReduceOnly);
        Assert.Equal("FAILED",await store.GetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceProtection_ExistingDurableStateRejectsBeforeProviderMutation()
    {
        var exchange=new RecordingExchange();
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        var adjustment=new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,150.075m,160m,"breakeven","WPE-PM-BE-existing");
        await store.SetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId!),
            "PENDING",
            CancellationToken.None);

        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>
            executor.ReplaceProtectionAsync("cycle",adjustment,CancellationToken.None));

        Assert.Contains("durable state",error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0,exchange.MutationCount);
    }

    [Fact]
    public async Task RecoverPending_UnsupportedCapabilityDoesNotReplaceProtection()
    {
        var exchange = new RecordingExchange { FoundOrder = FilledOrder() };
        var executor = CreateExecutor(exchange, out var store);
        await store.SaveIntentAsync("cycle", OpeningIntent(), "FILLED", "order-1", CancellationToken.None);

        var result = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Equal(0, exchange.MutationCount);
    }

    [Fact]
    public async Task ProtectionAudit_UnsupportedCapabilityDoesNotRepairProtection()
    {
        var exchange = new RecordingExchange();
        var executor = CreateExecutor(exchange, out var store);
        await store.SaveIntentAsync("cycle", OpeningIntent(), "PROTECTED", "order-1", CancellationToken.None);
        var position = new ManagedPosition("SOLUSDT", PositionSide.Long, 1m, 150m, 151m, 1m, 5m, true, 120m);

        var result = await executor.AuditAndRepairProtectionAsync([position], [], CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Equal(0, exchange.MutationCount);
    }

    [Fact]
    public async Task ProtectionAudit_AvailableCapabilityDoesNotRepairUnownedPosition()
    {
        var exchange=new RecordingExchange();
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        await store.SaveIntentAsync("cycle-old",OpeningIntent(),"PROTECTED","old-order",CancellationToken.None);
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);

        var result=await executor.AuditAndRepairProtectionAsync([position],[],CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Contains(result.Messages,x=>x.StartsWith("recovery.position-ownership-conflict:",StringComparison.Ordinal));
        Assert.Equal(0,exchange.MutationCount);
    }

    [Fact]
    public async Task ProtectionAudit_UncertainOpeningRejectsWithoutMutation()
    {
        var exchange=new RecordingExchange();
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        await SeedManagedOpeningAsync(store);
        await store.SetStateAsync(
            PositionManagementDurableState.OwnershipMissingCandidateKey(OpeningIntent().ClientOrderId),
            DateTimeOffset.UtcNow.ToString("O"),
            CancellationToken.None);
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);

        var result=await executor.AuditAndRepairProtectionAsync([position],[],CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Contains(result.Messages,x=>x.StartsWith("recovery.position-ownership-uncertain:",StringComparison.Ordinal));
        Assert.Equal(0,exchange.MutationCount);
    }

    [Fact]
    public async Task ProtectionAudit_RevokedOpeningRejectsWithoutMutation()
    {
        var exchange=new RecordingExchange();
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        await SeedManagedOpeningAsync(store);
        await store.SetStateAsync(
            PositionManagementDurableState.OwnershipRevocationKey(OpeningIntent().ClientOrderId),
            "position.missing-on-exchange",
            CancellationToken.None);
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);

        var result=await executor.AuditAndRepairProtectionAsync([position],[],CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Contains(result.Messages,x=>x.StartsWith("recovery.position-ownership-revoked:",StringComparison.Ordinal));
        Assert.Equal(0,exchange.MutationCount);
    }

    [Theory]
    [InlineData(ReduceOnlyRecoveryResult.Unknown,"recovery.reconciliation-unknown")]
    [InlineData(ReduceOnlyRecoveryResult.Stale,"recovery.reconciliation-stale")]
    [InlineData(ReduceOnlyRecoveryResult.Conflicting,"recovery.reconciliation-conflicting")]
    public async Task ReduceOnlyRecovery_UnverifiedReconciliationRejectsWithoutMutation(
        ReduceOnlyRecoveryResult reconciliation,string expectedCode)
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(
            RecoveryCommand(authority,reconciliation),CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(expectedCode,result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_MainnetRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(false);
        var gateway=CreateGateway(executor,out _,out var authority);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(RecoveryCommand(authority),CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.testnet-required",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_StaleObservationRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var command=RecoveryCommand(authority,observedAtUtc:DateTimeOffset.UtcNow-TradingExecutionGateway.MaximumRecoveryObservationAge-TimeSpan.FromSeconds(1));

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.observation-stale",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Theory]
    [InlineData(10,true)]
    [InlineData(5,false)]
    public async Task ReduceOnlyRecovery_AccountSettingChangeRejectsWithoutMutation(int leverage,bool isolated)
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var command=RecoveryCommand(authority,leverage:leverage,isolated:isolated);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.account-settings-conflict",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Theory]
    [InlineData(false,1,DecisionAction.CloseLong)]
    [InlineData(true,2,DecisionAction.CloseLong)]
    [InlineData(true,1,DecisionAction.CloseShort)]
    public async Task ReduceOnlyRecovery_RiskIncreasingOrConflictingIntentRejectsWithoutMutation(
        bool reduceOnly,int quantity,DecisionAction action)
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var intent=RecoveryIntent() with { ReduceOnly=reduceOnly,Quantity=quantity,Action=action };
        var command=RecoveryCommand(authority,intent:intent);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.reduction-invalid",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_VerifiedTestnetReductionExecutesWithoutApproval()
    {
        var exchange=new RecordingExchange();
        var executor=CreateExecutor(exchange,out var store,CapabilityStatus.Available);
        var authority=CreateAuthority();
        var gateway=new TradingExecutionGateway(executor,store,null,authority.Verifier);
        await SeedManagedOpeningAsync(store);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(RecoveryCommand(authority),CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal("recovery.reduce-only-submitted",result.Code);
        Assert.Equal(1,exchange.MarketOrderSubmissions);
        Assert.True(exchange.LastReduceOnly);
        Assert.Equal(1,exchange.MutationCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_UncertainOpeningRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await SeedManagedOpeningAsync(store);
        await store.SetStateAsync(
            PositionManagementDurableState.OwnershipMissingCandidateKey(OpeningIntent().ClientOrderId),
            DateTimeOffset.UtcNow.ToString("O"),
            CancellationToken.None);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(RecoveryCommand(authority),CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.position-ownership-conflict",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_RevokedOpeningRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await SeedManagedOpeningAsync(store);
        await store.SetStateAsync(
            PositionManagementDurableState.OwnershipRevocationKey(OpeningIntent().ClientOrderId),
            "position.missing-on-exchange",
            CancellationToken.None);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(RecoveryCommand(authority),CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.position-ownership-conflict",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_UnownedExchangePositionRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(RecoveryCommand(authority),CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.position-ownership-conflict",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_ExistingUnknownStateNeverResubmits()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await store.SaveIntentAsync("prior-cycle",RecoveryIntent(),"UNKNOWN",null,CancellationToken.None);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(RecoveryCommand(authority),CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.reconcile-existing",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_ForgedReceiptRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var command=RecoveryCommand(authority);
        command=command with{Receipt=command.Receipt! with{Signature=new string('0',64)}};

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.receipt-invalid",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_CorrelationMismatchRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var command=RecoveryCommand(authority);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(
            command with{CorrelationId="different-cycle"},CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.receipt-mismatch",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_UntrustedIssuerCannotCreateAcceptedReceipt()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var trustedAuthority);
        var untrustedAuthority=CreateAuthority(64);
        var command=RecoveryCommand(untrustedAuthority);

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.receipt-invalid",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("symbol")]
    [InlineData("quantity")]
    [InlineData("intent")]
    public async Task ReduceOnlyRecovery_MismatchedReceiptRejectsWithoutMutation(string mismatch)
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var command=RecoveryCommand(authority);
        var receipt=command.Receipt!;
        receipt=mismatch switch
        {
            "provider"=>authority.Issue("receipt","other","Testnet","local",command.ObservedPosition,receipt.ObservedAtUtc,receipt.IntentHash,receipt.Result,receipt.IssuedAtUtc,receipt.ExpiresAtUtc),
            "account"=>authority.Issue("receipt","local","Testnet","other",command.ObservedPosition,receipt.ObservedAtUtc,receipt.IntentHash,receipt.Result,receipt.IssuedAtUtc,receipt.ExpiresAtUtc),
            "symbol"=>authority.Issue("receipt","local","Testnet","local",command.ObservedPosition with{Symbol="BTCUSDT"},receipt.ObservedAtUtc,receipt.IntentHash,receipt.Result,receipt.IssuedAtUtc,receipt.ExpiresAtUtc),
            "quantity"=>authority.Issue("receipt","local","Testnet","local",command.ObservedPosition with{Quantity=2m},receipt.ObservedAtUtc,receipt.IntentHash,receipt.Result,receipt.IssuedAtUtc,receipt.ExpiresAtUtc),
            _=>authority.Issue("receipt","local","Testnet","local",command.ObservedPosition,receipt.ObservedAtUtc,new string('A',64),receipt.Result,receipt.IssuedAtUtc,receipt.ExpiresAtUtc)
        };

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command with{Receipt=receipt},CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.receipt-mismatch",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ReduceOnlyRecovery_ExpiredReceiptRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out _,out var authority);
        var now=DateTimeOffset.UtcNow;
        var command=RecoveryCommand(authority,observedAtUtc:now,issuedAtUtc:now-TimeSpan.FromSeconds(20),expiresAtUtc:now-TimeSpan.FromSeconds(1));

        var result=await gateway.ExecuteReduceOnlyRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.receipt-expired",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ProtectionRecovery_VerifiedBreakevenAdjustmentExecutes()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await SeedManagedOpeningAsync(store);
        var command=ProtectionCommand(authority);

        var result=await gateway.ExecuteProtectionRecoveryAsync(command,CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal("recovery.protection-adjusted",result.Code);
        Assert.Equal(1,executor.CallCount);
    }

    [Fact]
    public async Task ProtectionRecovery_UnownedExchangePositionRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await store.SaveIntentAsync("opening",OpeningIntent(),"PROTECTED","order-open",CancellationToken.None);
        var command=ProtectionCommand(authority);

        var result=await gateway.ExecuteProtectionRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.position-ownership-conflict",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ProtectionRecovery_RiskIncreasingAdjustmentRejectsWithoutMutation()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await store.SaveIntentAsync("opening",OpeningIntent(),"PROTECTED","order-open",CancellationToken.None);
        var command=ProtectionCommand(authority,new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,145m,160m,"looser stop","WPE-PM-BE-riskincrease"));

        var result=await gateway.ExecuteProtectionRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.protection-risk-increase",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public async Task ProtectionRecovery_ExistingDurableStateNeverReplays()
    {
        var executor=new RecordingMutationExecutor(true);
        var gateway=CreateGateway(executor,out var store,out var authority);
        await store.SaveIntentAsync("opening",OpeningIntent(),"PROTECTED","order-open",CancellationToken.None);
        var command=ProtectionCommand(authority);
        await store.SetStateAsync(
            PositionManagementDurableState.ProtectionAdjustmentKey(command.Adjustment.AdjustmentId!),
            "PENDING",
            CancellationToken.None);

        var result=await gateway.ExecuteProtectionRecoveryAsync(command,CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal("recovery.protection-existing",result.Code);
        Assert.Equal(0,executor.CallCount);
    }

    [Fact]
    public void LegacyConstructor_RejectsRealProviderWithoutCapabilityContext()
    {
        Directory.CreateDirectory(_directory);
        var store=new AgentSqliteStore(Path.Combine(_directory,"legacy.db"));

        var error=Assert.Throws<InvalidOperationException>(()=>new ReliableOrderExecutor(new RecordingProvider(),store));

        Assert.Contains("capability-aware",error.Message,StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }

    private ReliableOrderExecutor CreateExecutor(RecordingExchange exchange,out AgentSqliteStore store,CapabilityStatus status=CapabilityStatus.Unsupported,DateTimeOffset? checkedAt=null)
    {
        Directory.CreateDirectory(_directory);
        store = new AgentSqliteStore(Path.Combine(_directory,Guid.NewGuid().ToString("N")+".db"));
        var capability = new ExchangeCapability("binance","binance-futures","SOLUSDT","SOLUSDT",MarketType.Perpetual,
            status,true,status==CapabilityStatus.Available,true,checkedAt??DateTimeOffset.UtcNow,"unavailable in test");
        return new ReliableOrderExecutor(exchange,store,new RiskLimits(),SystemOrderPollScheduler.Instance,
            new Dictionary<string,ExchangeCapability>(StringComparer.OrdinalIgnoreCase){{"SOLUSDT",capability}},true);
    }

    private TradingExecutionGateway CreateGateway(ITradingMutationExecutor executor,out AgentSqliteStore store,out TestRecoveryAuthority authority)
    {
        Directory.CreateDirectory(_directory);
        store=new AgentSqliteStore(Path.Combine(_directory,Guid.NewGuid().ToString("N")+".db"));
        authority=CreateAuthority();
        return new TradingExecutionGateway(executor,store,null,authority.Verifier);
    }

    private static TestRecoveryAuthority CreateAuthority(int seed=0)=>new(Enumerable.Range(1,32).Select(x=>(byte)(x+seed)).ToArray());

    private static async Task SeedManagedOpeningAsync(AgentSqliteStore store)
    {
        var opening=OpeningIntent();
        await store.RecordExecutionAsync("opening",opening,FilledOrder(),"test",CancellationToken.None);
        await store.SaveIntentAsync("opening",opening,"PROTECTED","order-1",CancellationToken.None);
    }

    private static ReduceOnlyRecoveryCommand RecoveryCommand(
        TestRecoveryAuthority authority,
        ReduceOnlyRecoveryResult result=ReduceOnlyRecoveryResult.Verified,
        DateTimeOffset? observedAtUtc=null,DateTimeOffset? issuedAtUtc=null,DateTimeOffset? expiresAtUtc=null,
        ExecutionIntent? intent=null,int leverage=5,bool isolated=true)
    {
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);
        intent??=RecoveryIntent();
        var issued=issuedAtUtc??DateTimeOffset.UtcNow;
        var observed=observedAtUtc??issued;
        var expires=expiresAtUtc??issued.AddSeconds(20);
        var hash=TradingExecutionGateway.ComputeIntentHash([intent],leverage,isolated);
        var receipt=authority.Issue("receipt","local","Testnet","local",position,observed,hash,result,issued,expires);
        return new("recovery-cycle",receipt,position,intent,leverage,isolated);
    }

    private static ProtectionRecoveryCommand ProtectionCommand(
        TestRecoveryAuthority authority,ProtectionAdjustment? adjustment=null,
        ReduceOnlyRecoveryResult result=ReduceOnlyRecoveryResult.Verified)
    {
        var position=new ManagedPosition("SOLUSDT",PositionSide.Long,1m,150m,151m,1m,5m,true,120m);
        adjustment??=new ProtectionAdjustment(
            "SOLUSDT",PositionSide.Long,150.075m,160m,"breakeven","WPE-PM-BE-gatewaytest");
        var issued=DateTimeOffset.UtcNow;
        var hash=TradingExecutionGateway.ComputeProtectionAdjustmentHash(adjustment);
        var receipt=authority.Issue(
            "protection-receipt","local","Testnet","local",position,issued,hash,result,issued,issued.AddSeconds(20));
        return new("recovery-cycle",receipt,position,adjustment);
    }

    private sealed class TestRecoveryAuthority
    {
        private readonly TestSigningKeyStore _keys;

        internal TestRecoveryAuthority(byte[] key)
        {
            _keys=new TestSigningKeyStore(key);
            Verifier=RecoveryReconciliationComposition.Create(_keys,new TestObservationSource()).Verifier;
        }

        internal IReduceOnlyRecoveryReceiptVerifier Verifier{get;}

        internal ReduceOnlyRecoveryReceipt Issue(
            string receiptId,string providerId,string environment,string accountId,ManagedPosition position,
            DateTimeOffset observedAtUtc,string intentHash,ReduceOnlyRecoveryResult result,
            DateTimeOffset issuedAtUtc,DateTimeOffset expiresAtUtc,string correlationId="recovery-cycle")
        {
            var unsigned=new ReduceOnlyRecoveryReceipt(receiptId,correlationId,providerId,environment,accountId,
                position.Symbol,position.Side,position.Quantity,observedAtUtc,intentHash,result,issuedAtUtc,expiresAtUtc,string.Empty);
            using var key=_keys.OpenCurrentSigningKey();
            return unsigned with{Signature=RecoveryReceiptSignature.Sign(unsigned,key.Key)};
        }
    }

    private sealed class TestSigningKeyStore(byte[] key):IRecoverySigningKeyStore
    {
        public RecoverySigningKeyLease OpenCurrentSigningKey()=>new(key);
        public IReadOnlyList<RecoverySigningKeyLease> OpenVerificationKeys()=>[new(key)];
    }

    private sealed class TestObservationSource:ITrustedRecoveryObservationSource
    {
        public Task<TrustedRecoveryObservation> ObserveAsync(RecoveryReconciliationRequest request,CancellationToken ct)=>
            throw new NotSupportedException();
    }

    private static ExecutionIntent RecoveryIntent()=>new("SOLUSDT",PositionSide.Long,1m,true,0m,0m,
        "recover-sol","recovery",DecisionAction.CloseLong,ExecutionOrderType.Market,ExpectedPrice:150m);

    private static ExecutionIntent OpeningIntent() => new("SOLUSDT",PositionSide.Long,1m,false,140m,160m,
        "open-sol","test",DecisionAction.OpenLong,ExpectedPrice:150m);

    private static ExchangeOrder FilledOrder() => new("SOLUSDT","order-1","open-sol","FILLED",1m,150m,
        "MARKET",PositionSide.Long,false,DateTime.UtcNow);

    private class RecordingExchange : IExchangeAdapter
    {
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;
        public int MutationCount { get; private set; }
        public int MarketOrderSubmissions { get; private set; }
        public int ProtectionSubmissions { get; private set; }
        public int CancelAttempts { get; private set; }
        public bool LastReduceOnly { get; private set; }
        public bool FailCancel { get; init; }
        public CancellationTokenSource? CancelCancellationSource { get; init; }
        public CancellationTokenSource? ProtectionCancellationSource { get; init; }
        public ExchangeOrder? FoundOrder { get; init; }
        public IReadOnlyList<ExchangeOrder> OpenOrders { get; init; }=[];
        public IReadOnlyList<ManagedPosition> Positions { get; init; }=[];
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct) => Task.FromResult(FoundOrder);
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct) => Task.FromResult(OpenOrders);
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => Task.FromResult(Positions);
        public Task SetHedgeModeAsync(bool enabled,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){MutationCount++;return Task.CompletedTask;}
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct){MutationCount++;MarketOrderSubmissions++;LastReduceOnly=reduceOnly;return Task.FromResult(FilledOrder() with{ClientOrderId=clientOrderId,ExecutedQuantity=quantity,PositionSide=side});}
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct){MutationCount++;return Task.FromResult(FilledOrder());}
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
        {
            MutationCount++;ProtectionSubmissions++;
            if(ProtectionCancellationSource is not null)
            {
                ProtectionCancellationSource.Cancel();
                return Task.FromCanceled<ExchangeOrder>(ProtectionCancellationSource.Token);
            }
            return Task.FromResult(FilledOrder());
        }
        public Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)
        {
            MutationCount++;CancelAttempts++;
            if(CancelCancellationSource is not null)
            {
                CancelCancellationSource.Cancel();
                return Task.FromCanceled(CancelCancellationSource.Token);
            }
            return FailCancel?Task.FromException(new InvalidOperationException("cancel failed")):Task.CompletedTask;
        }
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingInPlaceProtectionExchange:RecordingExchange,IInPlaceProtectionUpdateAdapter
    {
    }

    private sealed class RecordingMutationExecutor(bool isTestnet):ITradingMutationExecutor
    {
        public bool IsTestnet{get;}=isTestnet;
        public string ProviderId=>"local";
        public string AccountId=>"local";
        public int CallCount{get;private set;}
        public Task<string> ExecutePlanAsync(string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult("submitted");
        }
        public Task<string> ExecuteReduceOnlyRecoveryAsync(string correlationId,ExecutionIntent intent,CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult("submitted");
        }
        public Task<string> ReplaceProtectionAsync(string correlationId,ProtectionAdjustment adjustment,CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult("protection-adjusted");
        }
    }

    private sealed class RecordingProvider : RecordingExchange,IExchangeProvider
    {
        public string ConnectionId=>"test-connection";
        public string ProviderId=>"test-provider";
        public ExchangeProviderDescriptor Descriptor=>new("test-provider","Test Provider",ExchangeAssetClass.CryptoCex,true,false,[],new HashSet<string>());
        public ISymbolMapper Symbols=>new ConventionSymbolMapper(ProviderId);
        public IMarketDataProvider MarketData=>throw new NotSupportedException();
        public IBrokerProvider Broker=>throw new NotSupportedException();
        public Task<bool> PingAsync(CancellationToken ct)=>Task.FromResult(true);
        public Task<DateTime> GetServerTimeAsync(CancellationToken ct)=>Task.FromResult(DateTime.UtcNow);
        public Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)=>Task.FromResult(new ExchangePermissionSnapshot(true,true,false,"test",[]));
        public Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)=>Task.FromResult(new ExchangeHealthSnapshot(true,1,0,"ok",DateTime.UtcNow));
        public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> canonicalSymbols,AgentSqliteStore database)=>null;
    }
}
