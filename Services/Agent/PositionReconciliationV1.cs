using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionPositionLegV1(string Symbol,PositionSide Side,decimal Quantity);
public enum PositionReconciliationStateV1 { Confirmed,Conflicting,Stale,Invalid }
public sealed record PositionReconciliationReportV1(string Schema,string ReportId,DateTimeOffset ObservedAtUtc,DateTimeOffset EvaluatedAtUtc,PositionReconciliationStateV1 State,bool AllowsRiskIncrease,IReadOnlyList<ExecutionPositionLegV1> LocalLegs,IReadOnlyList<ExecutionPositionLegV1> ExchangeLegs,IReadOnlyList<string> ReasonCodes,string CanonicalSha256,byte[] CanonicalBytes);

public static class PositionReconciliationServiceV1
{
    public const string Schema="wpe.position-reconciliation/1.0";
    public static readonly TimeSpan MaximumAge=TimeSpan.FromSeconds(30);

    public static PositionReconciliationReportV1 Reconcile(IReadOnlyList<ExecutionPositionLegV1> local,IReadOnlyList<ManagedPosition> exchange,DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(local);ArgumentNullException.ThrowIfNull(exchange);evaluatedAtUtc=evaluatedAtUtc.ToUniversalTime();observedAtUtc=observedAtUtc.ToUniversalTime();var reasons=new SortedSet<string>(StringComparer.Ordinal);var localLegs=NormalizeLocal(local,reasons);var exchangeLegs=NormalizeExchange(exchange,reasons);
        PositionReconciliationStateV1 state;
        if(observedAtUtc>evaluatedAtUtc||evaluatedAtUtc-observedAtUtc>MaximumAge){reasons.Add("position.observation-stale");state=PositionReconciliationStateV1.Stale;}
        else if(reasons.Any(x=>x.StartsWith("position.invalid",StringComparison.Ordinal))){state=PositionReconciliationStateV1.Invalid;}
        else
        {
            foreach(var key in localLegs.Keys.Union(exchangeLegs.Keys).OrderBy(x=>x,StringComparer.Ordinal)){var expected=localLegs.GetValueOrDefault(key);var actual=exchangeLegs.GetValueOrDefault(key);var tolerance=Math.Max(.00000001m,Math.Max(expected,actual)*.000001m);if(Math.Abs(expected-actual)>tolerance)reasons.Add("position.quantity-conflict:"+key);}
            state=reasons.Count==0?PositionReconciliationStateV1.Confirmed:PositionReconciliationStateV1.Conflicting;
        }
        var localRows=Rows(localLegs);var exchangeRows=Rows(exchangeLegs);var reasonRows=reasons.ToArray();var canonical=Canonical(observedAtUtc,evaluatedAtUtc,state,localRows,exchangeRows,reasonRows);var hash=Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();return new(Schema,"position-reconciliation:"+hash,observedAtUtc,evaluatedAtUtc,state,state==PositionReconciliationStateV1.Confirmed,localRows,exchangeRows,reasonRows,hash,canonical);
    }

    public static bool IsCanonical(PositionReconciliationReportV1 report)
    {
        if(report is null||report.Schema!=Schema||report.CanonicalSha256.Length!=64||report.ReportId!="position-reconciliation:"+report.CanonicalSha256||report.AllowsRiskIncrease!=(report.State==PositionReconciliationStateV1.Confirmed))return false;try{var expected=Canonical(report.ObservedAtUtc.ToUniversalTime(),report.EvaluatedAtUtc.ToUniversalTime(),report.State,report.LocalLegs,report.ExchangeLegs,report.ReasonCodes);return CryptographicOperations.FixedTimeEquals(expected,report.CanonicalBytes)&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected),Convert.FromHexString(report.CanonicalSha256));}catch{return false;}
    }

    private static Dictionary<string,decimal> NormalizeLocal(IReadOnlyList<ExecutionPositionLegV1> source,SortedSet<string> reasons)
    {
        var result=new Dictionary<string,decimal>(StringComparer.Ordinal);foreach(var row in source){var key=Key(row.Symbol,row.Side);if(key is null||row.Quantity<0){reasons.Add("position.invalid-local-leg");continue;}if(row.Quantity==0)continue;if(result.ContainsKey(key)){reasons.Add("position.invalid-local-duplicate:"+key);continue;}result[key]=row.Quantity;}return result;
    }
    private static Dictionary<string,decimal> NormalizeExchange(IReadOnlyList<ManagedPosition> source,SortedSet<string> reasons)
    {
        var result=new Dictionary<string,decimal>(StringComparer.Ordinal);foreach(var row in source){var key=Key(row.Symbol,row.Side);if(key is null||row.Quantity<=0||row.EntryPrice<=0||row.MarkPrice<=0||row.Leverage<=0){reasons.Add("position.invalid-exchange-leg");continue;}if(result.ContainsKey(key)){reasons.Add("position.invalid-exchange-duplicate:"+key);continue;}result[key]=row.Quantity;}return result;
    }
    private static string? Key(string symbol,PositionSide side){var normalized=new string((symbol??string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(24).ToArray());return normalized.Length<5||!Enum.IsDefined(side)?null:$"{normalized}:{side.ToString().ToLowerInvariant()}";}
    private static ExecutionPositionLegV1[] Rows(Dictionary<string,decimal> values)=>values.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>{var parts=x.Key.Split(':');return new ExecutionPositionLegV1(parts[0],Enum.Parse<PositionSide>(parts[1],true),x.Value);}).ToArray();
    private static object Row(ExecutionPositionLegV1 value)=>new{symbol=value.Symbol,side=value.Side.ToString().ToLowerInvariant(),quantity=value.Quantity.ToString("G29",CultureInfo.InvariantCulture)};
    private static byte[] Canonical(DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc,PositionReconciliationStateV1 state,IReadOnlyList<ExecutionPositionLegV1> local,IReadOnlyList<ExecutionPositionLegV1> exchange,IReadOnlyList<string> reasons)=>JsonSerializer.SerializeToUtf8Bytes(new{schema=Schema,observedAtUtc,evaluatedAtUtc,state=state.ToString().ToLowerInvariant(),local=local.Select(Row),exchange=exchange.Select(Row),reasons});
}

public enum ExternalPositionIsolationStateV1 { Clear,Isolated,Stale,Invalid }
public sealed record ExternalPositionIsolationReportV1(string Schema,string ReportId,DateTimeOffset ObservedAtUtc,DateTimeOffset EvaluatedAtUtc,ExternalPositionIsolationStateV1 State,bool AllowsRiskIncrease,IReadOnlyList<ExecutionPositionLegV1> ExternalLegs,IReadOnlyList<string> ReasonCodes,string CanonicalSha256,byte[] CanonicalBytes);

public static class ExternalPositionIsolationServiceV1
{
    public const string Schema="wpe.external-position-isolation/1.0";
    public static readonly TimeSpan MaximumAge=PositionReconciliationServiceV1.MaximumAge;
    public static ExternalPositionIsolationReportV1 Evaluate(IReadOnlyList<ExecutionPositionLegV1> local,IReadOnlyList<ManagedPosition> exchange,DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(local);ArgumentNullException.ThrowIfNull(exchange);observedAtUtc=observedAtUtc.ToUniversalTime();evaluatedAtUtc=evaluatedAtUtc.ToUniversalTime();var reasons=new SortedSet<string>(StringComparer.Ordinal);var expected=NormalizeLocal(local,reasons);var actual=NormalizeExchange(exchange,reasons);var external=new List<ExecutionPositionLegV1>();ExternalPositionIsolationStateV1 state;
        if(observedAtUtc>evaluatedAtUtc||evaluatedAtUtc-observedAtUtc>MaximumAge){reasons.Add("external-position.observation-stale");state=ExternalPositionIsolationStateV1.Stale;}else if(reasons.Any(x=>x.StartsWith("external-position.invalid",StringComparison.Ordinal)))state=ExternalPositionIsolationStateV1.Invalid;else{foreach(var pair in actual.OrderBy(x=>x.Key,StringComparer.Ordinal)){var localQuantity=expected.GetValueOrDefault(pair.Key);var tolerance=Math.Max(.00000001m,Math.Max(localQuantity,pair.Value)*.000001m);var excess=pair.Value-localQuantity;if(excess<=tolerance)continue;var parts=pair.Key.Split(':');external.Add(new(parts[0],Enum.Parse<PositionSide>(parts[1],true),excess));reasons.Add("external-position.isolated:"+pair.Key);}state=external.Count==0?ExternalPositionIsolationStateV1.Clear:ExternalPositionIsolationStateV1.Isolated;}
        var rows=external.ToArray();var reasonRows=reasons.ToArray();var canonical=Canonical(observedAtUtc,evaluatedAtUtc,state,rows,reasonRows);var hash=Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();return new(Schema,"external-position-isolation:"+hash,observedAtUtc,evaluatedAtUtc,state,state==ExternalPositionIsolationStateV1.Clear,rows,reasonRows,hash,canonical);
    }
    public static bool IsCanonical(ExternalPositionIsolationReportV1 report)
    {
        if(report is null||report.Schema!=Schema||report.CanonicalSha256.Length!=64||report.ReportId!="external-position-isolation:"+report.CanonicalSha256||report.AllowsRiskIncrease!=(report.State==ExternalPositionIsolationStateV1.Clear))return false;try{var expected=Canonical(report.ObservedAtUtc.ToUniversalTime(),report.EvaluatedAtUtc.ToUniversalTime(),report.State,report.ExternalLegs,report.ReasonCodes);return CryptographicOperations.FixedTimeEquals(expected,report.CanonicalBytes)&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected),Convert.FromHexString(report.CanonicalSha256));}catch{return false;}
    }
    private static Dictionary<string,decimal> NormalizeLocal(IReadOnlyList<ExecutionPositionLegV1> source,SortedSet<string> reasons){var result=new Dictionary<string,decimal>(StringComparer.Ordinal);foreach(var row in source){var key=Key(row.Symbol,row.Side);if(key is null||row.Quantity<0){reasons.Add("external-position.invalid-local-leg");continue;}if(row.Quantity==0)continue;if(!result.TryAdd(key,row.Quantity))reasons.Add("external-position.invalid-local-duplicate:"+key);}return result;}
    private static Dictionary<string,decimal> NormalizeExchange(IReadOnlyList<ManagedPosition> source,SortedSet<string> reasons){var result=new Dictionary<string,decimal>(StringComparer.Ordinal);foreach(var row in source){var key=Key(row.Symbol,row.Side);if(key is null||row.Quantity<=0||row.EntryPrice<=0||row.MarkPrice<=0||row.Leverage<=0){reasons.Add("external-position.invalid-exchange-leg");continue;}if(!result.TryAdd(key,row.Quantity))reasons.Add("external-position.invalid-exchange-duplicate:"+key);}return result;}
    private static string? Key(string symbol,PositionSide side){var normalized=new string((symbol??string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).Take(24).ToArray());return normalized.Length<5||!Enum.IsDefined(side)?null:$"{normalized}:{side.ToString().ToLowerInvariant()}";}
    private static object Row(ExecutionPositionLegV1 value)=>new{symbol=value.Symbol,side=value.Side.ToString().ToLowerInvariant(),quantity=value.Quantity.ToString("G29",CultureInfo.InvariantCulture)};
    private static byte[] Canonical(DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc,ExternalPositionIsolationStateV1 state,IReadOnlyList<ExecutionPositionLegV1> external,IReadOnlyList<string> reasons)=>JsonSerializer.SerializeToUtf8Bytes(new{schema=Schema,observedAtUtc,evaluatedAtUtc,state=state.ToString().ToLowerInvariant(),external=external.Select(Row),reasons});
}
