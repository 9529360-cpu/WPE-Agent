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
    public async Task ExecutionLedgerPreservesDecimalQuantityWithoutFloatingPointAggregation()
    {
        var store=new AgentSqliteStore(Database);var first=Intent("open-a",false,.123456789123456789m);var second=Intent("open-b",false,.000000000000000001m);await store.RecordExecutionAsync("a",first,Order(first,"FILLED",first.Quantity),"v1",default);await store.RecordExecutionAsync("b",second,Order(second,"FILLED",second.Quantity),"v1",default);Assert.Equal(.123456789123456790m,Assert.Single(await store.GetExecutionPositionLedgerAsync(default)).Quantity);
    }

    [Fact]
    public void ProductionRiskIncreaseGateConsumesPositionReconciliation()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));Assert.Contains("PositionReconciliationServiceV1.Reconcile",source,StringComparison.Ordinal);Assert.Contains("&&positionReconciliation.AllowsRiskIncrease",source,StringComparison.Ordinal);Assert.Contains("ExecutePositionManagementRecoveryAsync",source,StringComparison.Ordinal);
    }

    private static ManagedPosition Position(string symbol,PositionSide side,decimal quantity)=>new(symbol,side,quantity,100m,101m,1m,2m,true,50m);
    private static ExecutionIntent Intent(string id,bool reduceOnly,decimal quantity)=>new("BTCUSDT",PositionSide.Long,quantity,reduceOnly,90m,120m,id,"test",reduceOnly?DecisionAction.ReduceLong:DecisionAction.OpenLong,ExpectedPrice:100m);
    private static ExchangeOrder Order(ExecutionIntent intent,string status,decimal quantity)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,status,quantity,100m,"MARKET",intent.Side,false,DateTime.UtcNow);
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
