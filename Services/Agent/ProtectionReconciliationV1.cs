using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ProtectionReconciliationStateV1 { Confirmed,Incomplete,Conflicting,Stale,Invalid }

public sealed record ProtectionEvidenceLegV1(
    string Symbol,
    PositionSide Side,
    bool StopLossConfirmed,
    bool TakeProfitConfirmed,
    string ProofKind,
    int EvidenceCount,
    decimal PositionQuantity,
    bool CoverageConfirmed,
    string CoverageProof);

public sealed record ProtectionReconciliationReportV1(
    string Schema,
    string ReportId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    ProtectionReconciliationStateV1 State,
    bool AllowsRiskIncrease,
    IReadOnlyList<ProtectionEvidenceLegV1> Legs,
    IReadOnlyList<string> ReasonCodes,
    string CanonicalSha256,
    byte[] CanonicalBytes);

public static class ProtectionReconciliationServiceV1
{
    public const string Schema="wpe.protection-reconciliation/1.1";
    public static readonly TimeSpan MaximumAge=TimeSpan.FromSeconds(30);

    public static ProtectionReconciliationReportV1 Reconcile(
        IReadOnlyList<ManagedPosition> positions,
        IReadOnlyList<ExchangeOrder> orders,
        DateTimeOffset observedAtUtc,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(orders);
        observedAtUtc=observedAtUtc.ToUniversalTime();
        evaluatedAtUtc=evaluatedAtUtc.ToUniversalTime();

        var reasons=new SortedSet<string>(StringComparer.Ordinal);
        var positionByKey=new Dictionary<string,ManagedPosition>(StringComparer.Ordinal);
        foreach(var position in positions)
        {
            var key=Key(position.Symbol,position.Side);
            if(key is null||position.Quantity<=0)
            {
                reasons.Add("protection.invalid-position");
                continue;
            }
            if(!positionByKey.TryAdd(key,position))
                reasons.Add("protection.invalid-position-duplicate:"+key);
        }

        var protection=new Dictionary<string,List<ExchangeOrder>>(StringComparer.Ordinal);
        foreach(var order in orders.Where(x=>x.IsProtection))
        {
            if(order.PositionSide is null
                ||string.IsNullOrWhiteSpace(order.OrderId)
                ||string.IsNullOrWhiteSpace(order.Type)
                ||!CoverageMetadataValid(order))
            {
                reasons.Add("protection.invalid-order");
                continue;
            }

            var key=Key(order.Symbol,order.PositionSide.Value);
            if(key is null)
            {
                reasons.Add("protection.invalid-order");
                continue;
            }
            if(!positionByKey.ContainsKey(key))
                reasons.Add("protection.orphan-order:"+key);
            if(!protection.TryGetValue(key,out var list))
                protection[key]=list=[];
            list.Add(order);
        }

        var legs=new List<ProtectionEvidenceLegV1>();
        foreach(var pair in positionByKey.OrderBy(x=>x.Key,StringComparer.Ordinal))
        {
            var key=pair.Key;
            var position=pair.Value;
            var evidence=protection.GetValueOrDefault(key)??[];
            var combined=evidence.Where(IsCombined).ToArray();
            var stops=evidence.Where(x=>IsCombined(x)||IsStop(x)).ToArray();
            var takes=evidence.Where(x=>IsCombined(x)||IsTake(x)).ToArray();

            var stop=stops.Length>0;
            var take=takes.Length>0;
            var stopCoverage=stops.Any(x=>Covers(x,position.Quantity));
            var takeCoverage=takes.Any(x=>Covers(x,position.Quantity));
            var coverage=stop&&take&&stopCoverage&&takeCoverage;

            if(!stop)reasons.Add("protection.missing-stop:"+key);
            else if(!stopCoverage)reasons.Add("protection.stop-coverage-unproven:"+key);
            if(!take)reasons.Add("protection.missing-take-profit:"+key);
            else if(!takeCoverage)reasons.Add("protection.take-profit-coverage-unproven:"+key);

            var parts=key.Split(':');
            legs.Add(new(
                parts[0],
                Enum.Parse<PositionSide>(parts[1],true),
                stop,
                take,
                combined.Length>0?"combined":stop&&take?"separate":"incomplete",
                evidence.Count,
                position.Quantity,
                coverage,
                CoverageProof(stops,takes,position.Quantity)));
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
        return new(
            Schema,
            "protection-reconciliation:"+hash,
            observedAtUtc,
            evaluatedAtUtc,
            state,
            state==ProtectionReconciliationStateV1.Confirmed,
            legs,
            reasonRows,
            hash,
            canonical);
    }

    public static bool IsCanonical(ProtectionReconciliationReportV1 report)
    {
        if(report is null
            ||report.Schema!=Schema
            ||report.CanonicalSha256.Length!=64
            ||report.ReportId!="protection-reconciliation:"+report.CanonicalSha256
            ||report.AllowsRiskIncrease!=(report.State==ProtectionReconciliationStateV1.Confirmed))
            return false;
        try
        {
            var expected=Canonical(
                report.ObservedAtUtc.ToUniversalTime(),
                report.EvaluatedAtUtc.ToUniversalTime(),
                report.State,
                report.Legs,
                report.ReasonCodes);
            return CryptographicOperations.FixedTimeEquals(expected,report.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(expected),
                    Convert.FromHexString(report.CanonicalSha256));
        }
        catch
        {
            return false;
        }
    }

    private static bool CoverageMetadataValid(ExchangeOrder order)=>order.ProtectionCoverage switch
    {
        ProtectionCoverageKind.Unknown=>order.ProtectionQuantity==0,
        ProtectionCoverageKind.PositionWide=>order.ProtectionQuantity==0,
        ProtectionCoverageKind.FixedQuantity=>order.ProtectionQuantity>0,
        _=>false
    };

    private static bool Covers(ExchangeOrder order,decimal positionQuantity)=>order.ProtectionCoverage switch
    {
        ProtectionCoverageKind.PositionWide=>true,
        ProtectionCoverageKind.FixedQuantity=>order.ProtectionQuantity>=positionQuantity,
        _=>false
    };

    private static bool IsCombined(ExchangeOrder order)
    {
        var type=order.Type.ToUpperInvariant();
        return type.Contains("OCO",StringComparison.Ordinal)
            ||type.Contains("POSITION_TPSL",StringComparison.Ordinal);
    }

    private static bool IsStop(ExchangeOrder order)
    {
        var type=order.Type.ToUpperInvariant();
        return type.Contains("STOP",StringComparison.Ordinal)
            ||type.Contains("LOSS",StringComparison.Ordinal);
    }

    private static bool IsTake(ExchangeOrder order)
    {
        var type=order.Type.ToUpperInvariant();
        return type.Contains("TAKE",StringComparison.Ordinal)
            ||type.Contains("PROFIT",StringComparison.Ordinal);
    }

    private static string CoverageProof(
        IReadOnlyList<ExchangeOrder> stops,
        IReadOnlyList<ExchangeOrder> takes,
        decimal positionQuantity)
    {
        var covering=stops.Concat(takes)
            .Where(x=>Covers(x,positionQuantity))
            .DistinctBy(x=>string.IsNullOrWhiteSpace(x.OrderId)?x.ClientOrderId:x.OrderId,StringComparer.Ordinal)
            .ToArray();
        if(covering.Length==0)return "unproven";
        var modes=covering.Select(x=>x.ProtectionCoverage).Distinct().ToArray();
        return modes.Length>1?"mixed":modes[0] switch
        {
            ProtectionCoverageKind.PositionWide=>"position-wide",
            ProtectionCoverageKind.FixedQuantity=>"fixed-quantity",
            _=>"unproven"
        };
    }

    private static string? Key(string symbol,PositionSide side)
    {
        var normalized=new string((symbol??string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(24).ToArray());
        return normalized.Length<5||!Enum.IsDefined(side)?null:$"{normalized}:{side.ToString().ToLowerInvariant()}";
    }

    private static byte[] Canonical(
        DateTimeOffset observedAtUtc,
        DateTimeOffset evaluatedAtUtc,
        ProtectionReconciliationStateV1 state,
        IReadOnlyList<ProtectionEvidenceLegV1> legs,
        IReadOnlyList<string> reasons)=>
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
                stop=x.StopLossConfirmed,
                take=x.TakeProfitConfirmed,
                proof=x.ProofKind,
                count=x.EvidenceCount,
                positionQuantity=x.PositionQuantity,
                coverage=x.CoverageConfirmed,
                coverageProof=x.CoverageProof
            }),
            reasons
        });
}
