using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ProtectionReconciliationStateV1 { Confirmed,Incomplete,Conflicting,Stale,Invalid }
public sealed record ProtectionEvidenceLegV1(
    string Symbol,
    PositionSide Side,
    decimal PositionQuantity,
    bool StopLossConfirmed,
    bool TakeProfitConfirmed,
    bool StopCoverageConfirmed,
    bool TakeProfitCoverageConfirmed,
    string StopCoverageProof,
    string TakeProfitCoverageProof,
    decimal? StopFixedQuantity,
    decimal? TakeProfitFixedQuantity,
    string ProofKind,
    int EvidenceCount);
public sealed record ProtectionReconciliationReportV1(string Schema,string ReportId,DateTimeOffset ObservedAtUtc,DateTimeOffset EvaluatedAtUtc,ProtectionReconciliationStateV1 State,bool AllowsRiskIncrease,IReadOnlyList<ProtectionEvidenceLegV1> Legs,IReadOnlyList<string> ReasonCodes,string CanonicalSha256,byte[] CanonicalBytes);

public static class ProtectionReconciliationServiceV1
{
    public const string Schema="wpe.protection-reconciliation/1.1";
    public static readonly TimeSpan MaximumAge=TimeSpan.FromSeconds(30);

    public static ProtectionReconciliationReportV1 Reconcile(IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExchangeOrder> orders,DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(orders);
        observedAtUtc=observedAtUtc.ToUniversalTime();
        evaluatedAtUtc=evaluatedAtUtc.ToUniversalTime();
        var reasons=new SortedSet<string>(StringComparer.Ordinal);
        var positionRows=new Dictionary<string,ManagedPosition>(StringComparer.Ordinal);
        foreach(var position in positions)
        {
            var key=Key(position.Symbol,position.Side);
            if(key is null||position.Quantity<=0){reasons.Add("protection.invalid-position");continue;}
            if(!positionRows.TryAdd(key,position))reasons.Add("protection.invalid-position-duplicate:"+key);
        }

        var protection=new Dictionary<string,List<ExchangeOrder>>(StringComparer.Ordinal);
        foreach(var order in orders.Where(x=>x.IsProtection))
        {
            if(order.PositionSide is null||string.IsNullOrWhiteSpace(order.OrderId)||string.IsNullOrWhiteSpace(order.Type))
            {
                reasons.Add("protection.invalid-order");
                continue;
            }
            var key=Key(order.Symbol,order.PositionSide.Value);
            if(key is null){reasons.Add("protection.invalid-order");continue;}
            if(!positionRows.ContainsKey(key))reasons.Add("protection.orphan-order:"+key);
            if(!protection.TryGetValue(key,out var list))protection[key]=list=[];
            list.Add(order);
        }

        var legs=new List<ProtectionEvidenceLegV1>();
        foreach(var pair in positionRows.OrderBy(x=>x.Key,StringComparer.Ordinal))
        {
            var key=pair.Key;
            var position=pair.Value;
            var evidence=protection.GetValueOrDefault(key)??[];
            var combinedRows=evidence.Where(IsCombined).ToArray();
            var stopRows=evidence.Where(x=>IsCombined(x)||IsStop(x)).ToArray();
            var takeRows=evidence.Where(x=>IsCombined(x)||IsTakeProfit(x)).ToArray();
            var stop=stopRows.Length>0;
            var take=takeRows.Length>0;
            if(!stop)reasons.Add("protection.missing-stop:"+key);
            if(!take)reasons.Add("protection.missing-take-profit:"+key);

            var stopCoverage=Coverage(stopRows,position.Quantity);
            var takeCoverage=Coverage(takeRows,position.Quantity);
            AddCoverageReason(reasons,key,"stop",stop,stopCoverage);
            AddCoverageReason(reasons,key,"take-profit",take,takeCoverage);

            legs.Add(new(
                position.Symbol.Trim().ToUpperInvariant(),
                position.Side,
                position.Quantity,
                stop,
                take,
                stopCoverage.Confirmed,
                takeCoverage.Confirmed,
                stopCoverage.Proof,
                takeCoverage.Proof,
                stopCoverage.FixedQuantity,
                takeCoverage.FixedQuantity,
                combinedRows.Length>0?"combined":stop&&take?"separate":"incomplete",
                evidence.Count));
        }

        ProtectionReconciliationStateV1 state;
        if(observedAtUtc>evaluatedAtUtc||evaluatedAtUtc-observedAtUtc>MaximumAge)
        {
            reasons.Add("protection.observation-stale");
            state=ProtectionReconciliationStateV1.Stale;
        }
        else if(reasons.Any(x=>x.StartsWith("protection.invalid",StringComparison.Ordinal)))
            state=ProtectionReconciliationStateV1.Invalid;
        else if(reasons.Any(x=>x.StartsWith("protection.orphan",StringComparison.Ordinal)))
            state=ProtectionReconciliationStateV1.Conflicting;
        else
            state=reasons.Count==0?ProtectionReconciliationStateV1.Confirmed:ProtectionReconciliationStateV1.Incomplete;

        var reasonRows=reasons.ToArray();
        var canonical=Canonical(observedAtUtc,evaluatedAtUtc,state,legs,reasonRows);
        var hash=Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        return new(Schema,"protection-reconciliation:"+hash,observedAtUtc,evaluatedAtUtc,state,state==ProtectionReconciliationStateV1.Confirmed,legs,reasonRows,hash,canonical);
    }

    public static bool IsCanonical(ProtectionReconciliationReportV1 report)
    {
        if(report is null||report.Schema!=Schema||report.CanonicalSha256.Length!=64||report.ReportId!="protection-reconciliation:"+report.CanonicalSha256||report.AllowsRiskIncrease!=(report.State==ProtectionReconciliationStateV1.Confirmed))
            return false;
        try
        {
            var expected=Canonical(report.ObservedAtUtc.ToUniversalTime(),report.EvaluatedAtUtc.ToUniversalTime(),report.State,report.Legs,report.ReasonCodes);
            return CryptographicOperations.FixedTimeEquals(expected,report.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected),Convert.FromHexString(report.CanonicalSha256));
        }
        catch{return false;}
    }

    private sealed record CoverageResult(bool Confirmed,string Proof,decimal? FixedQuantity,string? Failure);

    private static CoverageResult Coverage(IReadOnlyList<ExchangeOrder> rows,decimal positionQuantity)
    {
        if(rows.Count==0)return new(false,"missing",null,null);
        if(rows.Any(x=>x.ProtectionCoverage==ProtectionCoverageKind.Unknown))
            return new(false,"unknown",null,"unknown");
        var fixedRows=rows.Where(x=>x.ProtectionCoverage==ProtectionCoverageKind.FixedQuantity).ToArray();
        if(fixedRows.Any(x=>x.ProtectionQuantity is null or <=0))
            return new(false,"invalid",null,"invalid");
        var fixedQuantity=fixedRows.Length==0?null:fixedRows.Sum(x=>x.ProtectionQuantity!.Value);
        if(rows.Any(x=>x.ProtectionCoverage==ProtectionCoverageKind.PositionWide))
            return new(true,fixedRows.Length==0?"position-wide":"position-wide+fixed",fixedQuantity,null);
        return fixedQuantity==positionQuantity
            ?new(true,"fixed-quantity",fixedQuantity,null)
            :new(false,"fixed-quantity",fixedQuantity,"mismatch");
    }

    private static void AddCoverageReason(SortedSet<string> reasons,string key,string leg,bool present,CoverageResult coverage)
    {
        if(!present||coverage.Confirmed)return;
        var prefix=coverage.Failure switch
        {
            "invalid"=>"protection.invalid-coverage",
            "mismatch"=>"protection.coverage-mismatch",
            _=>"protection.coverage-unknown"
        };
        reasons.Add($"{prefix}:{key}:{leg}");
    }

    private static bool IsCombined(ExchangeOrder order)
    {
        var type=order.Type.ToUpperInvariant();
        return type.Contains("OCO",StringComparison.Ordinal)||type.Contains("POSITION_TPSL",StringComparison.Ordinal);
    }
    private static bool IsStop(ExchangeOrder order)
    {
        var type=order.Type.ToUpperInvariant();
        return type.Contains("STOP",StringComparison.Ordinal)||type.Contains("LOSS",StringComparison.Ordinal);
    }
    private static bool IsTakeProfit(ExchangeOrder order)
    {
        var type=order.Type.ToUpperInvariant();
        return type.Contains("TAKE",StringComparison.Ordinal)||type.Contains("PROFIT",StringComparison.Ordinal);
    }
    private static string? Key(string symbol,PositionSide side)
    {
        var normalized=new string((symbol??string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(24).ToArray());
        return normalized.Length<5||!Enum.IsDefined(side)?null:$"{normalized}:{side.ToString().ToLowerInvariant()}";
    }
    private static byte[] Canonical(DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc,ProtectionReconciliationStateV1 state,IReadOnlyList<ProtectionEvidenceLegV1> legs,IReadOnlyList<string> reasons)=>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema=Schema,
            observedAtUtc,
            evaluatedAtUtc,
            state=state.ToString().ToLowerInvariant(),
            legs=legs.Select(x=>new
            {
                symbol=x.Symbol,
                side=x.Side.ToString().ToLowerInvariant(),
                positionQuantity=x.PositionQuantity,
                stop=x.StopLossConfirmed,
                take=x.TakeProfitConfirmed,
                stopCoverage=x.StopCoverageConfirmed,
                takeCoverage=x.TakeProfitCoverageConfirmed,
                stopCoverageProof=x.StopCoverageProof,
                takeCoverageProof=x.TakeProfitCoverageProof,
                stopFixedQuantity=x.StopFixedQuantity,
                takeFixedQuantity=x.TakeProfitFixedQuantity,
                proof=x.ProofKind,
                count=x.EvidenceCount
            }),
            reasons
        });
}
