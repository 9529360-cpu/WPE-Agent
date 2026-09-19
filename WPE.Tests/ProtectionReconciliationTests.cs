using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ProtectionReconciliationTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-protection-reconciliation-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=new(2026,7,27,9,0,0,TimeSpan.Zero);

    [Fact]
    public void NoPositionAndNoProtectionIsConfirmed()
    {
        var report=ProtectionReconciliationServiceV1.Reconcile([],[],Now,Now);Assert.True(report.AllowsRiskIncrease);Assert.Equal(ProtectionReconciliationStateV1.Confirmed,report.State);Assert.Empty(report.Legs);
    }

    [Theory]
    [InlineData("OCO")]
    [InlineData("POSITION_TPSL")]
    public void CombinedProtectionConfirmsBothLegs(string type)
    {
        var report=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("p1",type)],Now,Now);var leg=Assert.Single(report.Legs);Assert.True(leg.StopLossConfirmed);Assert.True(leg.TakeProfitConfirmed);Assert.True(leg.StopCoverageConfirmed);Assert.True(leg.TakeProfitCoverageConfirmed);Assert.Equal("position-wide",leg.StopCoverageProof);Assert.Equal("position-wide",leg.TakeProfitCoverageProof);Assert.Equal("combined",leg.ProofKind);Assert.True(report.AllowsRiskIncrease);
    }

    [Fact]
    public void SeparateStopAndTakeAreConfirmedButGenericOrDuplicateTriggersFailClosed()
    {
        var separate=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("sl","STOP_MARKET"),Protection("tp","TAKE_PROFIT_MARKET")],Now,Now);Assert.True(separate.AllowsRiskIncrease);Assert.Equal("separate",Assert.Single(separate.Legs).ProofKind);
        var explicitTriggers=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("sl2","STOP_TRIGGER"),Protection("tp2","TAKE_PROFIT_TRIGGER")],Now,Now);Assert.True(explicitTriggers.AllowsRiskIncrease);
        var generic=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("a","TRIGGER"),Protection("b","TRIGGER")],Now,Now);Assert.Equal(ProtectionReconciliationStateV1.Incomplete,generic.State);Assert.False(generic.AllowsRiskIncrease);
        var duplicateStops=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("s1","STOP_TRIGGER"),Protection("s2","STOP_TRIGGER")],Now,Now);Assert.Equal(ProtectionReconciliationStateV1.Incomplete,duplicateStops.State);Assert.Contains(duplicateStops.ReasonCodes,x=>x.Contains("missing-take-profit",StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownOrMismatchedProtectionCoverageFailsClosed()
    {
        var unknown=ProtectionReconciliationServiceV1.Reconcile([Position()],[UnknownProtection("unknown","OCO")],Now,Now);
        Assert.Equal(ProtectionReconciliationStateV1.Incomplete,unknown.State);
        Assert.Contains("protection.coverage-unknown:BTCUSDT:long:stop",unknown.ReasonCodes);
        Assert.Contains("protection.coverage-unknown:BTCUSDT:long:take-profit",unknown.ReasonCodes);
        Assert.False(unknown.AllowsRiskIncrease);

        var mismatch=ProtectionReconciliationServiceV1.Reconcile([Position()],[FixedProtection("short","OCO",.5m)],Now,Now);
        var leg=Assert.Single(mismatch.Legs);
        Assert.False(leg.StopCoverageConfirmed);
        Assert.False(leg.TakeProfitCoverageConfirmed);
        Assert.Equal(.5m,leg.StopFixedQuantity);
        Assert.Equal(.5m,leg.TakeProfitFixedQuantity);
        Assert.Contains("protection.coverage-mismatch:BTCUSDT:long:stop",mismatch.ReasonCodes);
        Assert.Contains("protection.coverage-mismatch:BTCUSDT:long:take-profit",mismatch.ReasonCodes);
        Assert.False(mismatch.AllowsRiskIncrease);
    }

    [Fact]
    public void ExactFixedQuantityProtectionConfirmsCoverage()
    {
        var report=ProtectionReconciliationServiceV1.Reconcile([Position()],[FixedProtection("okx","OCO",1m)],Now,Now);
        var leg=Assert.Single(report.Legs);
        Assert.Equal("wpe.protection-reconciliation/1.1",report.Schema);
        Assert.True(leg.StopCoverageConfirmed);
        Assert.True(leg.TakeProfitCoverageConfirmed);
        Assert.Equal("fixed-quantity",leg.StopCoverageProof);
        Assert.Equal(1m,leg.StopFixedQuantity);
        Assert.True(report.AllowsRiskIncrease);
    }

    [Fact]
    public void ProviderProtectionTypesAreExplicitOrUnclassified()
    {
        Assert.Equal("TAKE_PROFIT_TRIGGER",GateExchangeProvider.ProtectionType(1,PositionSide.Long));Assert.Equal("STOP_TRIGGER",GateExchangeProvider.ProtectionType(2,PositionSide.Long));Assert.Equal("STOP_TRIGGER",GateExchangeProvider.ProtectionType(1,PositionSide.Short));Assert.Equal("TAKE_PROFIT_TRIGGER",GateExchangeProvider.ProtectionType(2,PositionSide.Short));Assert.Equal("TRIGGER_UNCLASSIFIED",GateExchangeProvider.ProtectionType(0,PositionSide.Long));
        Assert.Equal("STOP_TRIGGER",BitgetExchangeProvider.ProtectionType("loss_plan","external"));Assert.Equal("TAKE_PROFIT_TRIGGER",BitgetExchangeProvider.ProtectionType("profit_plan","external"));Assert.Equal("STOP_TRIGGER",BitgetExchangeProvider.ProtectionType("","wpe-sl"));Assert.Equal("TPSL_UNCLASSIFIED",BitgetExchangeProvider.ProtectionType("","external"));
    }

    [Fact]
    public void MissingLegOrOrphanProtectionFailsClosed()
    {
        var missing=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("sl","STOP_MARKET")],Now,Now);Assert.Equal(ProtectionReconciliationStateV1.Incomplete,missing.State);Assert.Contains("protection.missing-take-profit:BTCUSDT:long",missing.ReasonCodes);Assert.False(missing.AllowsRiskIncrease);
        var orphan=ProtectionReconciliationServiceV1.Reconcile([], [Protection("orphan","OCO")],Now,Now);Assert.Equal(ProtectionReconciliationStateV1.Conflicting,orphan.State);Assert.False(orphan.AllowsRiskIncrease);
    }

    [Fact]
    public void InvalidOrStaleEvidenceFailsClosed()
    {
        var invalid=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("bad","OCO") with{PositionSide=null}],Now,Now);Assert.Equal(ProtectionReconciliationStateV1.Invalid,invalid.State);
        var stale=ProtectionReconciliationServiceV1.Reconcile([],[],Now-ProtectionReconciliationServiceV1.MaximumAge-TimeSpan.FromMilliseconds(1),Now);Assert.Equal(ProtectionReconciliationStateV1.Stale,stale.State);Assert.False(stale.AllowsRiskIncrease);
    }

    [Fact]
    public async Task CanonicalAuditIsRestartSafeAndRejectsFieldOrByteTampering()
    {
        var report=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("p1","OCO")],Now,Now);var store=new AgentSqliteStore(Database);Assert.True(await store.SaveProtectionReconciliationAsync(report,default));Assert.False(await new AgentSqliteStore(Database).SaveProtectionReconciliationAsync(report,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveProtectionReconciliationAsync(report with{CanonicalBytes=[..report.CanonicalBytes,0]},default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveProtectionReconciliationAsync(report with{State=ProtectionReconciliationStateV1.Incomplete,AllowsRiskIncrease=false},default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveProtectionReconciliationAsync(report with{Legs=[report.Legs[0] with{StopCoverageConfirmed=false}]},default));
    }

    [Fact]
    public async Task ProtectionAuditRowsAreDatabaseAppendOnly()
    {
        var store=new AgentSqliteStore(Database);var report=ProtectionReconciliationServiceV1.Reconcile([],[],Now,Now);Assert.True(await store.SaveProtectionReconciliationAsync(report,default));await using var connection=new SqliteConnection($"Data Source={Database}");await connection.OpenAsync();
        foreach(var sql in new[]{"UPDATE protection_reconciliation_audits SET state='Incomplete'","DELETE FROM protection_reconciliation_audits"}){await using var command=connection.CreateCommand();command.CommandText=sql;await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());}
    }

    [Fact]
    public void ProductionGateUsesProofInsteadOfPositionCountHeuristic()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));Assert.Contains("ProtectionReconciliationServiceV1.Reconcile",source,StringComparison.Ordinal);Assert.Contains("&&protectionReconciliation.AllowsRiskIncrease",source,StringComparison.Ordinal);Assert.DoesNotContain("AssessUnverifiedAutomaticMutation(AutomaticMutationPath.ProtectionAudit",source,StringComparison.Ordinal);Assert.Contains("ExecutePositionManagementRecoveryAsync",source,StringComparison.Ordinal);
    }

    private static ManagedPosition Position()=>new("BTCUSDT",PositionSide.Long,1m,100m,101m,1m,2m,true,50m);
    private static ExchangeOrder Protection(string id,string type)=>new("BTCUSDT",id,"client-"+id,"NEW",0,0,type,PositionSide.Long,true,Now.UtcDateTime){ProtectionCoverage=ProtectionCoverageKind.PositionWide};
    private static ExchangeOrder UnknownProtection(string id,string type)=>new("BTCUSDT",id,"client-"+id,"NEW",0,0,type,PositionSide.Long,true,Now.UtcDateTime);
    private static ExchangeOrder FixedProtection(string id,string type,decimal quantity)=>new("BTCUSDT",id,"client-"+id,"NEW",0,0,type,PositionSide.Long,true,Now.UtcDateTime){ProtectionCoverage=ProtectionCoverageKind.FixedQuantity,ProtectionQuantity=quantity};
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
