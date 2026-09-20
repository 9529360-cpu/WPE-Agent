using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

public sealed record ProtectionFillReconciliationResultV1(
    int Examined,
    int Matched,
    int Recorded,
    int RemainingConflicts,
    string Code);

public static class ProtectionFillReconciliationServiceV1
{
    public static async Task<ProtectionFillReconciliationResultV1> ReconcileAsync(
        IExchangeAdapter exchange,
        AgentSqliteStore store,
        IReadOnlyList<ManagedPosition> exchangePositions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(exchangePositions);

        if(exchange is not IProtectionFillEvidenceProvider evidenceProvider)
            return new(0,0,0,CountConflicts(await store.GetExecutionPositionLedgerAsync(ct),exchangePositions),"protection-fill.unsupported");

        var local=await store.GetExecutionPositionLedgerAsync(ct);
        var remaining=Discrepancies(local,exchangePositions);
        if(remaining.Count==0)
            return new(0,0,0,0,"protection-fill.no-conflict");

        var parents=await store.GetProtectedOpeningIntentsAsync(ct);
        var recentBySymbol=new Dictionary<string,IReadOnlyList<ExchangeOrder>>(StringComparer.OrdinalIgnoreCase);
        var examined=0;
        var matched=0;
        var recorded=0;
        var evidenceUnavailable=false;

        foreach(var parent in parents)
        {
            ct.ThrowIfCancellationRequested();
            var key=Key(parent.Intent.Symbol,parent.Intent.Side);
            if(!remaining.TryGetValue(key,out var missing)||missing<=0)continue;

            if(!recentBySymbol.TryGetValue(parent.Intent.Symbol,out var recent))
            {
                try
                {
                    recent=await evidenceProvider.GetRecentOrdersAsync(parent.Intent.Symbol,100,ct);
                    recentBySymbol[parent.Intent.Symbol]=recent;
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch
                {
                    evidenceUnavailable=true;
                    continue;
                }
            }

            foreach(var order in recent.OrderByDescending(x=>x.UpdatedAt))
            {
                examined++;
                if(order.PositionSide!=parent.Intent.Side)continue;
                if(!evidenceProvider.TryMatchProtectionFill(parent.Intent.ClientOrderId,order,out var kind))continue;
                matched++;

                var tolerance=Math.Max(.00000001m,missing*.000001m);
                if(order.ExecutedQuantity<=0||order.ExecutedQuantity>missing+tolerance)continue;
                if(await store.HasExecutionEventAsync(order.ClientOrderId,ct))
                {
                    missing=Math.Max(0,missing-order.ExecutedQuantity);
                    remaining[key]=missing;
                    break;
                }

                var expected=kind==ProtectionFillKind.StopLoss?parent.Intent.StopLoss:parent.Intent.TakeProfit;
                if(expected<=0)continue;
                var closeIntent=new ExecutionIntent(
                    parent.Intent.Symbol,
                    parent.Intent.Side,
                    order.ExecutedQuantity,
                    true,
                    0,
                    0,
                    order.ClientOrderId,
                    kind==ProtectionFillKind.StopLoss?"protection.fill-reconciled.stop-loss":"protection.fill-reconciled.take-profit",
                    parent.Intent.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort,
                    ExecutionOrderType.Market,
                    0,
                    expected);

                await CaptureFeeEvidenceAsync(exchange,store,order,ct);
                await CaptureFundingEvidenceAsync(exchange,store,closeIntent,ct);
                await store.RecordExecutionAsync(
                    parent.CycleId,
                    closeIntent,
                    order with{IsProtection=true},
                    "protection-reconciliation-v1",
                    ct);

                recorded++;
                missing=Math.Max(0,missing-order.ExecutedQuantity);
                remaining[key]=missing;
                break;
            }
        }

        var finalLedger=await store.GetExecutionPositionLedgerAsync(ct);
        var conflicts=CountConflicts(finalLedger,exchangePositions);
        var code=conflicts==0&&recorded>0
            ?"protection-fill.reconciled"
            :evidenceUnavailable
                ?"protection-fill.evidence-unavailable"
                :recorded>0
                    ?"protection-fill.partial"
                    :"protection-fill.no-proof";
        return new(examined,matched,recorded,conflicts,code);
    }

    private static async Task CaptureFeeEvidenceAsync(IExchangeAdapter exchange,AgentSqliteStore store,ExchangeOrder order,CancellationToken ct)
    {
        if(exchange is not IExchangeOrderFeeEvidenceReader reader)return;
        try
        {
            var evidence=await reader.ReadOrderFeeEvidenceAsync(order,ct);
            await store.SaveExchangeOrderFeeEvidenceAsync(evidence,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{/* Observational fee evidence never authorizes a ledger mutation. */}
    }

    private static async Task CaptureFundingEvidenceAsync(IExchangeAdapter exchange,AgentSqliteStore store,ExecutionIntent closeIntent,CancellationToken ct)
    {
        if(exchange is not IExchangeFundingIncomeReader reader)return;
        try
        {
            var start=await store.GetUnambiguousFundingObservationStartAsync(closeIntent.Symbol,closeIntent.Side,closeIntent.Quantity,ct);
            if(start is null)return;
            var evidence=await reader.ReadFundingIncomeAsync(closeIntent.Symbol,start.Value,DateTimeOffset.UtcNow,ct);
            await store.SaveFundingObservationAsync(evidence,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{/* Observational funding evidence never authorizes a ledger mutation. */}
    }

    private static Dictionary<string,decimal> Discrepancies(
        IReadOnlyList<ExecutionPositionLegV1> local,
        IReadOnlyList<ManagedPosition> exchange)
    {
        var actual=exchange
            .Where(x=>x.Quantity>0)
            .GroupBy(x=>Key(x.Symbol,x.Side),StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x=>x.Key,x=>x.Sum(v=>v.Quantity),StringComparer.OrdinalIgnoreCase);
        var result=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
        foreach(var leg in local)
        {
            if(leg.Quantity<=0)continue;
            var key=Key(leg.Symbol,leg.Side);
            var delta=leg.Quantity-actual.GetValueOrDefault(key);
            var tolerance=Math.Max(.00000001m,Math.Max(leg.Quantity,actual.GetValueOrDefault(key))*.000001m);
            if(delta>tolerance)result[key]=delta;
        }
        return result;
    }

    private static int CountConflicts(IReadOnlyList<ExecutionPositionLegV1> local,IReadOnlyList<ManagedPosition> exchange)=>
        Discrepancies(local,exchange).Count;

    private static string Key(string symbol,PositionSide side)=>
        $"{symbol.Trim().ToUpperInvariant()}:{side.ToString().ToLowerInvariant()}";
}
