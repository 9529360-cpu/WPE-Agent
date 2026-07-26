using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

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
        var report=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("p1",type)],Now,Now);var leg=Assert.Single(report.Legs);Assert.True(leg.StopLossConfirmed);Assert.True(leg.TakeProfitConfirmed);Assert.Equal("combined",leg.ProofKind);Assert.True(report.AllowsRiskIncrease);
    }

    [Fact]
    public void SeparateStopAndTakeOrTwoGenericTriggersAreConfirmed()
    {
        var separate=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("sl","STOP_MARKET"),Protection("tp","TAKE_PROFIT_MARKET")],Now,Now);Assert.True(separate.AllowsRiskIncrease);Assert.Equal("separate",Assert.Single(separate.Legs).ProofKind);
        var generic=ProtectionReconciliationServiceV1.Reconcile([Position()],[Protection("a","TRIGGER"),Protection("b","TRIGGER")],Now,Now);Assert.True(generic.AllowsRiskIncrease);
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
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveProtectionReconciliationAsync(report with{CanonicalBytes=[..report.CanonicalBytes,0]},default));await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveProtectionReconciliationAsync(report with{State=ProtectionReconciliationStateV1.Incomplete,AllowsRiskIncrease=false},default));
    }

    [Fact]
    public void ProductionGateUsesProofInsteadOfPositionCountHeuristic()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));Assert.Contains("ProtectionReconciliationServiceV1.Reconcile",source,StringComparison.Ordinal);Assert.Contains("&&protectionReconciliation.AllowsRiskIncrease",source,StringComparison.Ordinal);Assert.DoesNotContain("AssessUnverifiedAutomaticMutation(AutomaticMutationPath.ProtectionAudit",source,StringComparison.Ordinal);Assert.Contains("ExecutePositionManagementRecoveryAsync",source,StringComparison.Ordinal);
    }

    private static ManagedPosition Position()=>new("BTCUSDT",PositionSide.Long,1m,100m,101m,1m,2m,true,50m);
    private static ExchangeOrder Protection(string id,string type)=>new("BTCUSDT",id,"client-"+id,"NEW",0,0,type,PositionSide.Long,true,Now.UtcDateTime);
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
