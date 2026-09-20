using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PositionReconciliationTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-position-reconciliation-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=new(2026,7,27,8,0,0,TimeSpan.Zero);

    [Fact]
    public void ExactFreshLedgerIsConfirmedAndCanonical()
    {
        var local=new[]{new ExecutionPositionLegV1("BTCUSDT",PositionSide.Long,1.25m)};var exchange=new[]{Position("BTCUSDT",PositionSide.Long,1.25m)};
        var first=PositionReconciliationServiceV1.Reconcile(local,exchange,Now.AddSeconds(-2),Now);var second=PositionReconciliationServiceV1.Reconcile(local,exchange,Now.AddSeconds(-2),Now);
        Assert.True(first.AllowsRiskIncrease);Assert.Equal(PositionReconciliationStateV1.Confirmed,first.State);Assert.Empty(first.ReasonCodes);Assert.Equal(first.CanonicalSha256,second.CanonicalSha256);Assert.Equal(first.CanonicalBytes,second.CanonicalBytes);
    }

    [Fact]
    public void ExternalOrMissingPositionConflictsAndStaleObservationNeverAllowsRiskIncrease()
    {
        var external=PositionReconciliationServiceV1.Reconcile([], [Position("ETHUSDT",PositionSide.Short,2m)],Now,Now);Assert.Equal(PositionReconciliationStateV1.Conflicting,external.State);Assert.Contains("position.quantity-conflict:ETHUSDT:short",external.ReasonCodes);Assert.False(external.AllowsRiskIncrease);
        var missing=PositionReconciliationServiceV1.Reconcile([new("BTCUSDT",PositionSide.Long,1m)],[],Now,Now);Assert.Equal(PositionReconciliationStateV1.Conflicting,missing.State);Assert.False(missing.AllowsRiskIncrease);
        var stale=PositionReconciliationServiceV1.Reconcile([],[],Now-PositionReconciliationServiceV1.MaximumAge-TimeSpan.FromMilliseconds(1),Now);Assert.Equal(PositionReconciliationStateV1.Stale,stale.State);Assert.False(stale.AllowsRiskIncrease);
    }

    [Fact]
    public void InvalidDuplicateOrNegativeLegsFailClosed()
    {
        var duplicate=PositionReconciliationServiceV1.Reconcile([], [Position("BTCUSDT",PositionSide.Long,1m),Position("BTCUSDT",PositionSide.Long,1m)],Now,Now);Assert.Equal(PositionReconciliationStateV1.Invalid,duplicate.State);
        var negative=PositionReconciliationServiceV1.Reconcile([new("BTCUSDT",PositionSide.Long,-1m)],[],Now,Now);Assert.Equal(PositionReconciliationStateV1.Invalid,negative.State);
    }

    [Fact]
    public async Task ExecutionLedgerAndAuditSurviveRestartWithoutDuplicateReports()
    {
        var store=new AgentSqliteStore(Database);var open=Intent("open",false,1m);var close=Intent("close",true,.25m);await store.RecordExecutionAsync("cycle-open",open,Order(open,"FILLED",1m),"v1",default);await store.RecordExecutionAsync("cycle-close",close,Order(close,"FILLED",.25m),"v1",default);
        var leg=Assert.Single(await new AgentSqliteStore(Database).GetExecutionPositionLedgerAsync(default));Assert.Equal(.75m,leg.Quantity);
        var report=PositionReconciliationServiceV1.Reconcile([leg],[Position("BTCUSDT",PositionSide.Long,.75m)],Now,Now);Assert.True(await store.SavePositionReconciliationAsync(report,default));Assert.False(await new AgentSqliteStore(Database).SavePositionReconciliationAsync(report,default));
        var tampered=report with{CanonicalBytes=[..report.CanonicalBytes,0]};await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SavePositionReconciliationAsync(tampered,default));
        var fieldTampered=report with{State=PositionReconciliationStateV1.Conflicting,AllowsRiskIncrease=false};await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SavePositionReconciliationAsync(fieldTampered,default));
    }

    [Fact]
    public async Task PositionAndIsolationAuditRowsAreDatabaseAppendOnly()
    {
        var store=new AgentSqliteStore(Database);var position=PositionReconciliationServiceV1.Reconcile([],[],Now,Now);var isolation=ExternalPositionIsolationServiceV1.Evaluate([],[],Now,Now);Assert.True(await store.SavePositionReconciliationAsync(position,default));Assert.True(await store.SaveExternalPositionIsolationAsync(isolation,default));
        await using var connection=new SqliteConnection($"Data Source={Database}");await connection.OpenAsync();
        foreach(var sql in new[]{"UPDATE position_reconciliation_audits SET state='Conflicting'","DELETE FROM position_reconciliation_audits","UPDATE external_position_isolation_audits SET state='Isolated'","DELETE FROM external_position_isolation_audits"}){await using var command=connection.CreateCommand();command.CommandText=sql;await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());}
    }

    [Fact]
    public async Task ExecutionLedgerPreservesDecimalQuantityWithoutFloatingPointAggregation()
    {
        var store=new AgentSqliteStore(Database);var first=Intent("open-a",false,.123456789123456789m);var second=Intent("open-b",false,.000000000000000001m);await store.RecordExecutionAsync("a",first,Order(first,"FILLED",first.Quantity),"v1",default);await store.RecordExecutionAsync("b",second,Order(second,"FILLED",second.Quantity),"v1",default);Assert.Equal(.123456789123456790m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
    }

    [Fact]
    public async Task MissingManagedLegRequiresRepeatedFreshEvidenceBeforeRevocation()
    {
        var clock=Now;
        var store=new AgentSqliteStore(Database,()=>clock);
        var opening=Intent("open-ownership-a",false,1m);
        await store.RecordExecutionAsync("cycle-open",opening,Order(opening,"FILLED",1m),"v1",default);
        await store.SaveIntentAsync("cycle-open",opening,"PROTECTED","order-open",default);
        var local=new[]{new ExecutionPositionLegV1("BTCUSDT",PositionSide.Long,1m)};
        var candidateKey=PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId);
        var revocationKey=PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId);

        var first=await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,local,[],Now,Now,default);

        Assert.Equal(0,first);
        Assert.NotNull(await store.GetStateAsync(candidateKey,default));
        Assert.Null(await store.GetStateAsync(revocationKey,default));

        var secondAt=Now.AddSeconds(6);
        var second=await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,local,[],secondAt,secondAt,default);

        Assert.Equal(1,second);
        Assert.NotNull(await store.GetStateAsync(revocationKey,default));
        Assert.Empty(await store.GetExecutionPositionLedgerAsync(default));
        Assert.True(await 币安量化机器人.Services.AutoTradingAgent.HasUntrustedManagedPositionOwnershipAsync(
            store,[Position("BTCUSDT",PositionSide.Long,1m)],default));
        Assert.Equal(0,await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,local,[],secondAt.AddSeconds(6),secondAt.AddSeconds(6),default));
    }

    [Fact]
    public async Task TransientMissingPositionQuarantinesOwnershipWithoutImmediatePermanentRevocation()
    {
        var clock=Now;
        var store=new AgentSqliteStore(Database,()=>clock);
        var opening=Intent("open-ownership-transient",false,1m);
        await store.RecordExecutionAsync("cycle-open",opening,Order(opening,"FILLED",1m),"v1",default);
        await store.SaveIntentAsync("cycle-open",opening,"PROTECTED","order-open",default);
        var local=new[]{new ExecutionPositionLegV1("BTCUSDT",PositionSide.Long,1m)};
        var candidateKey=PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId);
        var revocationKey=PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId);

        Assert.Equal(0,await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,local,[],Now,Now,default));
        var candidate=await store.GetStateAsync(candidateKey,default);
        Assert.NotNull(candidate);
        Assert.Null(await store.GetStateAsync(revocationKey,default));

        var recoveredAt=Now.AddSeconds(2);
        Assert.Equal(0,await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,local,[Position("BTCUSDT",PositionSide.Long,1m)],recoveredAt,recoveredAt,default));
        Assert.Equal(candidate,await store.GetStateAsync(candidateKey,default));
        Assert.Null(await store.GetStateAsync(revocationKey,default));
        Assert.True(await 币安量化机器人.Services.AutoTradingAgent.HasUntrustedManagedPositionOwnershipAsync(
            store,[Position("BTCUSDT",PositionSide.Long,1m)],default));

        var missingAgainAt=Now.AddSeconds(10);
        Assert.Equal(1,await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,local,[],missingAgainAt,missingAgainAt,default));
        Assert.NotNull(await store.GetStateAsync(revocationKey,default));
    }

    [Fact]
    public async Task StaleMissingPositionObservationCannotRevokeOwnership()
    {
        var clock=Now;
        var store=new AgentSqliteStore(Database,()=>clock);
        var opening=Intent("open-ownership-stale",false,1m);
        await store.RecordExecutionAsync("cycle-open",opening,Order(opening,"FILLED",1m),"v1",default);
        await store.SaveIntentAsync("cycle-open",opening,"PROTECTED","order-open",default);

        var revoked=await 币安量化机器人.Services.AutoTradingAgent.RevokeMissingManagedPositionOwnershipAsync(
            store,[new("BTCUSDT",PositionSide.Long,1m)],[],
            Now-PositionReconciliationServiceV1.MaximumAge-TimeSpan.FromMilliseconds(1),Now,default);

        Assert.Equal(0,revoked);
        Assert.Null(await store.GetStateAsync(
            PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId),default));
        Assert.Null(await store.GetStateAsync(
            PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId),default));
    }

    [Fact]
    public async Task RetiredOwnershipLedgerPreservesHistoryAndStartsFreshGeneration()
    {
        var clock=Now;
        var store=new AgentSqliteStore(Database,()=>clock);
        var oldOpening=Intent("open-retired-old",false,1m);
        await store.RecordExecutionAsync("old",oldOpening,Order(oldOpening,"FILLED",1m),"v1",default);
        Assert.Equal(1m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);

        Assert.True(await store.RetireExecutionPositionLedgerAsync("BTCUSDT",PositionSide.Long,Now,default));
        Assert.Empty(await store.GetExecutionPositionLedgerAsync(default));

        clock=Now.AddSeconds(10);
        var freshOpening=Intent("open-retired-fresh",false,.4m);
        await store.RecordExecutionAsync("fresh",freshOpening,Order(freshOpening,"FILLED",.4m),"v1",default);

        Assert.Equal(.4m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var count=connection.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM execution_events";
        Assert.Equal(2,Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task OwnershipLedgerAsOfObservationDoesNotRetireConcurrentLaterFill()
    {
        var clock=Now;
        var store=new AgentSqliteStore(Database,()=>clock);
        var oldOpening=Intent("open-observed-old",false,1m);
        await store.RecordExecutionAsync("old",oldOpening,Order(oldOpening,"FILLED",1m),"v1",default);
        var observedAt=Now.AddSeconds(1);

        clock=Now.AddSeconds(2);
        var concurrentOpening=Intent("open-observed-new",false,.5m);
        await store.RecordExecutionAsync("new",concurrentOpening,Order(concurrentOpening,"FILLED",.5m),"v1",default);

        Assert.Equal(1m,Assert.Single(await store.GetExecutionPositionLedgerAsync(observedAt,default)).Quantity);
        Assert.True(await store.RetireExecutionPositionLedgerAsync("BTCUSDT",PositionSide.Long,observedAt,default));
        Assert.Equal(.5m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
    }

    [Fact]
    public void ProductionRiskIncreaseGateConsumesPositionReconciliation()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));Assert.Contains("PositionReconciliationServiceV1.Reconcile",source,StringComparison.Ordinal);Assert.Contains("&&positionReconciliation.AllowsRiskIncrease",source,StringComparison.Ordinal);Assert.Contains("ExecutePositionManagementRecoveryAsync",source,StringComparison.Ordinal);Assert.Contains("ApplyPositionMutationInvalidation",source,StringComparison.Ordinal);Assert.Contains("position.reconciliation-invalidated-by-recovery",source,StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulRecoveryMutationInvalidatesCurrentCyclePositionAuthority()
    {
        var unchanged=币安量化机器人.Services.AutoTradingAgent.ApplyPositionMutationInvalidation(true,"position.reconciliation-confirmed",false);Assert.True(unchanged.SafeToIncreaseRisk);Assert.Equal("position.reconciliation-confirmed",unchanged.SafetyMessage);
        var changed=币安量化机器人.Services.AutoTradingAgent.ApplyPositionMutationInvalidation(true,"position.reconciliation-confirmed",true);Assert.False(changed.SafeToIncreaseRisk);Assert.Equal("position.reconciliation-invalidated-by-recovery",changed.SafetyMessage);
    }

    [Fact]
    public void AutomaticMaintenanceObservationRefreshesAndClearsOnlyItsOwnUiMessage()
    {
        const string conflict="authorization.no-mutation-required；protection.reconciliation-confirmed；position.quantity-conflict:BTCUSDT:long；external-position.isolation-clear";
        var blocked=币安量化机器人.Services.AutoTradingAgent.ResolveAutomaticMaintenanceMessage(false,"running",conflict,null,"running");
        Assert.Equal(conflict,blocked.LastMessage);Assert.Equal(conflict,blocked.OwnedMessage);

        var recovered=币安量化机器人.Services.AutoTradingAgent.ResolveAutomaticMaintenanceMessage(true,conflict,"all-confirmed",blocked.OwnedMessage,"running");
        Assert.Equal("running",recovered.LastMessage);Assert.Null(recovered.OwnedMessage);

        var unrelated=币安量化机器人.Services.AutoTradingAgent.ResolveAutomaticMaintenanceMessage(true,"historical-data-degraded","all-confirmed",blocked.OwnedMessage,"running");
        Assert.Equal("historical-data-degraded",unrelated.LastMessage);Assert.Null(unrelated.OwnedMessage);

        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));
        Assert.Contains("await Db.SetStateAsync(\"authorization.automatic-maintenance\",safetyMessage,ct);",source,StringComparison.Ordinal);
        Assert.DoesNotContain("if(!safeToIncreaseRisk)await Db.SetStateAsync(\"authorization.automatic-maintenance\"",source,StringComparison.Ordinal);
    }

    [Fact]
    public void ExactLedgerHasNoExternalPositionAndIsCanonical()
    {
        var local=new[]{new ExecutionPositionLegV1("BTCUSDT",PositionSide.Long,1m)};var exchange=new[]{Position("BTCUSDT",PositionSide.Long,1m)};var first=ExternalPositionIsolationServiceV1.Evaluate(local,exchange,Now,Now);var second=ExternalPositionIsolationServiceV1.Evaluate(local,exchange,Now,Now);Assert.Equal(ExternalPositionIsolationStateV1.Clear,first.State);Assert.True(first.AllowsRiskIncrease);Assert.Empty(first.ExternalLegs);Assert.Equal(first.CanonicalSha256,second.CanonicalSha256);Assert.Equal(first.CanonicalBytes,second.CanonicalBytes);
    }

    [Fact]
    public void ExternalOnlyAndExcessQuantitiesAreIsolatedWithoutClaimingThem()
    {
        var report=ExternalPositionIsolationServiceV1.Evaluate([new("BTCUSDT",PositionSide.Long,1m)],[Position("BTCUSDT",PositionSide.Long,1.25m),Position("ETHUSDT",PositionSide.Short,2m)],Now,Now);Assert.Equal(ExternalPositionIsolationStateV1.Isolated,report.State);Assert.False(report.AllowsRiskIncrease);Assert.Equal(2,report.ExternalLegs.Count);Assert.Equal(.25m,Assert.Single(report.ExternalLegs,x=>x.Symbol=="BTCUSDT").Quantity);Assert.Equal(2m,Assert.Single(report.ExternalLegs,x=>x.Symbol=="ETHUSDT").Quantity);Assert.Contains("external-position.isolated:BTCUSDT:long",report.ReasonCodes);Assert.Contains("external-position.isolated:ETHUSDT:short",report.ReasonCodes);
    }

    [Fact]
    public void MissingExchangeQuantityIsNotMisclassifiedAsExternal()
    {
        var report=ExternalPositionIsolationServiceV1.Evaluate([new("BTCUSDT",PositionSide.Long,2m)],[Position("BTCUSDT",PositionSide.Long,1m)],Now,Now);Assert.Equal(ExternalPositionIsolationStateV1.Clear,report.State);Assert.Empty(report.ExternalLegs);Assert.False(PositionReconciliationServiceV1.Reconcile([new("BTCUSDT",PositionSide.Long,2m)],[Position("BTCUSDT",PositionSide.Long,1m)],Now,Now).AllowsRiskIncrease);
    }

    [Fact]
    public async Task ExternalPositionIsolationFailsClosedAndPersistsCanonicalAudit()
    {
        var invalid=ExternalPositionIsolationServiceV1.Evaluate([], [Position("BTCUSDT",PositionSide.Long,1m),Position("BTCUSDT",PositionSide.Long,1m)],Now,Now);Assert.Equal(ExternalPositionIsolationStateV1.Invalid,invalid.State);Assert.False(invalid.AllowsRiskIncrease);var stale=ExternalPositionIsolationServiceV1.Evaluate([],[],Now-ExternalPositionIsolationServiceV1.MaximumAge-TimeSpan.FromMilliseconds(1),Now);Assert.Equal(ExternalPositionIsolationStateV1.Stale,stale.State);Assert.False(stale.AllowsRiskIncrease);
        var store=new AgentSqliteStore(Database);var report=ExternalPositionIsolationServiceV1.Evaluate([], [Position("ETHUSDT",PositionSide.Short,2m)],Now,Now);Assert.True(await store.SaveExternalPositionIsolationAsync(report,default));Assert.False(await new AgentSqliteStore(Database).SaveExternalPositionIsolationAsync(report,default));await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveExternalPositionIsolationAsync(report with{CanonicalBytes=[..report.CanonicalBytes,0]},default));await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveExternalPositionIsolationAsync(report with{State=ExternalPositionIsolationStateV1.Clear,AllowsRiskIncrease=true},default));
    }

    [Fact]
    public void ProductionGateConsumesExternalIsolationWithoutAddingMutationPath()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));Assert.Contains("ExternalPositionIsolationServiceV1.Evaluate",source,StringComparison.Ordinal);Assert.Contains("&&externalPositionIsolation.AllowsRiskIncrease",source,StringComparison.Ordinal);var contract=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","PositionReconciliationV1.cs"));var isolation=contract[contract.IndexOf("public static class ExternalPositionIsolationServiceV1",StringComparison.Ordinal)..];foreach(var forbidden in new[]{"PlaceMarketAsync","PlaceLimitAsync","PlaceProtectionAsync","CancelOrderAsync","SetLeverageAsync","SetMarginModeAsync"})Assert.DoesNotContain(forbidden,isolation,StringComparison.Ordinal);
    }

    private static ManagedPosition Position(string symbol,PositionSide side,decimal quantity)=>new(symbol,side,quantity,100m,101m,1m,2m,true,50m);
    private static ExecutionIntent Intent(string id,bool reduceOnly,decimal quantity)=>new("BTCUSDT",PositionSide.Long,quantity,reduceOnly,90m,120m,id,"test",reduceOnly?DecisionAction.ReduceLong:DecisionAction.OpenLong,ExpectedPrice:100m);
    private static ExchangeOrder Order(ExecutionIntent intent,string status,decimal quantity)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,status,quantity,100m,"MARKET",intent.Side,false,DateTime.UtcNow);
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
