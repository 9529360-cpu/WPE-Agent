using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed partial class OkxOfficialMcpProvider
{
    public async Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(
        string canonicalSymbol,
        int limit,
        CancellationToken ct)
    {
        var native=Symbols.ToNative(canonicalSymbol);
        var bounded=Math.Clamp(limit,1,100);
        var result=new List<ExchangeOrder>();

        var regular=DataArray(await _mcp.CallToolAsync(
            "swap_get_orders",
            new Dictionary<string,object?>
            {
                ["status"]="history",
                ["instType"]="SWAP",
                ["instId"]=native,
                ["limit"]=bounded
            },
            ct));
        foreach(var row in regular.EnumerateArray())
            result.Add(await MapOrderAsync(row,false,ct));

        var algos=DataArray(await _mcp.CallToolAsync(
            "swap_get_algo_orders",
            new Dictionary<string,object?>
            {
                ["status"]="history",
                ["instType"]="SWAP",
                ["instId"]=native,
                ["state"]="effective",
                ["limit"]=bounded
            },
            ct));
        foreach(var row in algos.EnumerateArray())
            result.Add(await MapOrderAsync(row,true,ct));

        return NormalizeOrderEvents(result)
            .OrderByDescending(order=>order.UpdatedAt)
            .Take(bounded)
            .ToArray();
    }

    public bool TryMatchProtectionFill(
        string parentClientOrderId,
        ExchangeOrder order,
        out ProtectionFillKind kind)
    {
        kind=default;
        if(order is null||
           order.Status!="FILLED"||
           order.ExecutedQuantity<=0||
           !order.IsProtection)
            return false;

        return TryProtectionClientId(
            parentClientOrderId,
            order.ClientOrderId,
            out kind);
    }

    public async Task<ExchangeOrderFeeEvidenceV1> ReadOrderFeeEvidenceAsync(
        ExchangeOrder order,
        CancellationToken ct)
    {
        var observed=DateTimeOffset.UtcNow;
        if(Environment!=ExchangeEnvironment.Testnet)
            return FeeEvidence(
                order,observed,0,0,0,string.Empty,
                ExchangeOrderFeeEvidenceStateV1.Unsupported);

        try
        {
            var native=Symbols.ToNative(order.Symbol);
            var rows=DataArray(await _mcp.CallToolAsync(
                "swap_get_fills",
                new Dictionary<string,object?>
                {
                    ["archive"]=false,
                    ["instType"]="SWAP",
                    ["instId"]=native,
                    ["ordId"]=order.OrderId,
                    ["limit"]=100
                },
                ct));

            if(rows.GetArrayLength()==0)
                return FeeEvidence(
                    order,observed,0,0,0,string.Empty,
                    ExchangeOrderFeeEvidenceStateV1.Missing);

            var contractValue=await ContractValueAsync(native,ct);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            decimal quantity=0;
            decimal fee=0;
            string? asset=null;
            var count=0;

            foreach(var fill in rows.EnumerateArray())
            {
                var tradeId=Str(fill,"tradeId");
                if(string.IsNullOrWhiteSpace(tradeId)||!seen.Add(tradeId))
                    return FeeEvidence(
                        order,observed,0,0,0,string.Empty,
                        ExchangeOrderFeeEvidenceStateV1.Invalid);

                var orderId=Str(fill,"ordId");
                if(!string.IsNullOrWhiteSpace(orderId)&&orderId!=order.OrderId)
                    return FeeEvidence(
                        order,observed,0,0,0,string.Empty,
                        ExchangeOrderFeeEvidenceStateV1.Invalid);

                var feeAsset=Str(fill,"feeCcy").ToUpperInvariant();
                if(string.IsNullOrWhiteSpace(feeAsset)||
                   asset is not null&&!asset.Equals(feeAsset,StringComparison.OrdinalIgnoreCase))
                    return FeeEvidence(
                        order,observed,0,0,0,string.Empty,
                        ExchangeOrderFeeEvidenceStateV1.Invalid);

                var rawFee=Dec(fill,"fee");
                if(rawFee>0)
                    return FeeEvidence(
                        order,observed,0,0,0,string.Empty,
                        ExchangeOrderFeeEvidenceStateV1.Unsupported);

                asset=feeAsset;
                quantity+=Dec(fill,"fillSz")*contractValue;
                fee+=-rawFee;
                count++;
            }

            if(count==0||quantity<=0||string.IsNullOrWhiteSpace(asset))
                return FeeEvidence(
                    order,observed,0,0,0,string.Empty,
                    ExchangeOrderFeeEvidenceStateV1.Invalid);

            var tolerance=Math.Max(.00000001m,order.ExecutedQuantity*.000001m);
            if(order.ExecutedQuantity>0&&Math.Abs(quantity-order.ExecutedQuantity)>tolerance)
                return FeeEvidence(
                    order,observed,0,0,0,string.Empty,
                    ExchangeOrderFeeEvidenceStateV1.Invalid);

            return FeeEvidence(
                order,observed,count,quantity,fee,asset,
                ExchangeOrderFeeEvidenceStateV1.Confirmed);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FeeEvidence(
                order,observed,0,0,0,string.Empty,
                ExchangeOrderFeeEvidenceStateV1.Error);
        }
    }

    private ExchangeOrderFeeEvidenceV1 FeeEvidence(
        ExchangeOrder order,
        DateTimeOffset observed,
        int fillCount,
        decimal quantity,
        decimal fee,
        string asset,
        ExchangeOrderFeeEvidenceStateV1 state)=>
        ExchangeOrderFeeEvidenceCanonicalizerV1.Create(
            ProviderId,
            Environment.ToString(),
            order.Symbol,
            order.OrderId,
            order.ClientOrderId,
            fillCount,
            quantity,
            fee,
            asset,
            observed,
            state);
}
