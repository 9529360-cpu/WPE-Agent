using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Agent;

internal enum ExecutionDriftPhaseV1
{
    IntentAccepted,
    PreflightQuote,
    SubmissionAttempted,
    ProviderObserved,
    TimeoutCancelRequested,
    PostCancelObserved,
    ExecutionRecorded
}

internal enum ExecutionDriftSourceV1 { Local, Exchange }

internal sealed record ExecutionDriftObservationInputV1(
    string CycleId,
    string ClientOrderId,
    ExecutionDriftPhaseV1 Phase,
    ExecutionDriftSourceV1 Source,
    string ProviderId,
    string Environment,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal RequestedQuantity,
    decimal LimitPrice,
    decimal ObservedExecutedQuantity,
    decimal ExpectedPrice,
    decimal ObservedAveragePrice,
    string ProviderStatus,
    double SpreadBps = 0,
    double LiquidityScore = 0,
    double AtrPercent = 0,
    DateTimeOffset? ExchangeUpdatedAtUtc = null);

internal sealed record ExecutionDriftObservationV1(
    string Schema,
    string CycleId,
    string ClientOrderId,
    int Sequence,
    ExecutionDriftPhaseV1 Phase,
    ExecutionDriftSourceV1 Source,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ExchangeUpdatedAtUtc,
    string ProviderId,
    string Environment,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal RequestedQuantity,
    decimal LimitPrice,
    decimal ObservedExecutedQuantity,
    decimal ExpectedPrice,
    decimal ObservedAveragePrice,
    string ProviderStatus,
    double SpreadBps,
    double LiquidityScore,
    double AtrPercent,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal sealed record ExecutionDriftSummaryV1(
    string ClientOrderId,
    string ProviderId,
    string Environment,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal LimitPrice,
    int ObservationCount,
    int ExchangeObservationCount,
    int PartialFillObservationCount,
    decimal RequestedQuantity,
    decimal FinalObservedQuantity,
    decimal FillRatio,
    decimal ExpectedPrice,
    decimal FinalAveragePrice,
    decimal? PreflightPrice,
    double? PreflightSpreadBps,
    double? PreflightLiquidityScore,
    double? PreflightAtrPercent,
    double? SubmitToFirstExchangeMilliseconds,
    double? IntentToFinalMilliseconds,
    double? AdverseSlippageBps,
    string FinalStatus);

internal static class ExecutionDriftCanonicalizerV1
{
    internal const string Schema="wpe.execution-drift-observation/1.1";

    internal static ExecutionDriftObservationV1 Create(
        ExecutionDriftObservationInputV1 input,
        int sequence,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        var observed=observedAtUtc.ToUniversalTime();
        var exchange=input.ExchangeUpdatedAtUtc?.ToUniversalTime();
        if(sequence<0||observedAtUtc.Offset!=TimeSpan.Zero)
            throw new ArgumentException("Execution drift observation must use a non-negative sequence and explicit UTC observation time.");
        if(!Token(input.CycleId,240)||!Token(input.ClientOrderId,120)||!Token(input.ProviderId,80)
           ||!Token(input.Environment,40)||!Symbol(input.Symbol)||!Status(input.ProviderStatus)
           ||!Enum.IsDefined(input.Side)||!Enum.IsDefined(input.OrderType))
            throw new ArgumentException("Execution drift observation identity is invalid.");
        if(input.RequestedQuantity<0||input.LimitPrice<0||input.ObservedExecutedQuantity<0||input.ExpectedPrice<0||input.ObservedAveragePrice<0
           ||!double.IsFinite(input.SpreadBps)||input.SpreadBps<0
           ||!double.IsFinite(input.LiquidityScore)||input.LiquidityScore is<0 or>1
           ||!double.IsFinite(input.AtrPercent)||input.AtrPercent<0)
            throw new ArgumentException("Execution drift numeric facts are invalid.");

        var values=new[]
        {
            Schema,input.CycleId,input.ClientOrderId,sequence.ToString(CultureInfo.InvariantCulture),
            input.Phase.ToString(),input.Source.ToString(),observed.ToString("O",CultureInfo.InvariantCulture),
            exchange?.ToString("O",CultureInfo.InvariantCulture)??"none",
            input.ProviderId,input.Environment,input.Symbol,input.Side.ToString(),input.ReduceOnly?"true":"false",input.OrderType.ToString(),
            input.RequestedQuantity.ToString(CultureInfo.InvariantCulture),input.LimitPrice.ToString(CultureInfo.InvariantCulture),
            input.ObservedExecutedQuantity.ToString(CultureInfo.InvariantCulture),input.ExpectedPrice.ToString(CultureInfo.InvariantCulture),
            input.ObservedAveragePrice.ToString(CultureInfo.InvariantCulture),input.ProviderStatus,
            input.SpreadBps.ToString("R",CultureInfo.InvariantCulture),input.LiquidityScore.ToString("R",CultureInfo.InvariantCulture),
            input.AtrPercent.ToString("R",CultureInfo.InvariantCulture)
        };
        var bytes=Encoding.UTF8.GetBytes(string.Join('|',values));
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,input.CycleId,input.ClientOrderId,sequence,input.Phase,input.Source,observed,exchange,
            input.ProviderId,input.Environment,input.Symbol,input.Side,input.ReduceOnly,input.OrderType,input.RequestedQuantity,input.LimitPrice,
            input.ObservedExecutedQuantity,input.ExpectedPrice,input.ObservedAveragePrice,input.ProviderStatus,
            input.SpreadBps,input.LiquidityScore,input.AtrPercent,hash,bytes);
    }

    internal static bool IsCanonical(ExecutionDriftObservationV1 value)
    {
        if(value is null||value.Schema!=Schema||value.CanonicalBytes is null||value.ObservedAtUtc.Offset!=TimeSpan.Zero
           ||value.ExchangeUpdatedAtUtc is { } exchange&&exchange.Offset!=TimeSpan.Zero)return false;
        try
        {
            var expected=Create(new(value.CycleId,value.ClientOrderId,value.Phase,value.Source,value.ProviderId,value.Environment,
                value.Symbol,value.Side,value.ReduceOnly,value.OrderType,value.RequestedQuantity,value.LimitPrice,
                value.ObservedExecutedQuantity,value.ExpectedPrice,value.ObservedAveragePrice,value.ProviderStatus,
                value.SpreadBps,value.LiquidityScore,value.AtrPercent,value.ExchangeUpdatedAtUtc),value.Sequence,value.ObservedAtUtc);
            return string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal)
                   &&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
        }
        catch(ArgumentException){return false;}
    }

    private static bool Token(string value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum&&value.All(c=>char.IsLetterOrDigit(c)||c is '-' or '_' or '.' or ':');
    private static bool Symbol(string value)=>value.Length is>=5 and<=30&&value.All(c=>c is>='A' and<='Z' or>='0' and<='9');
    private static bool Status(string value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=64&&value.All(c=>char.IsLetterOrDigit(c)||c is '_' or '-' or '.');
}

public sealed partial class AgentSqliteStore
{
    private void EnsureExecutionDriftSchema()
    {
        using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_drift_observations(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                schema TEXT NOT NULL,
                cycle_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                phase TEXT NOT NULL,
                source TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                exchange_updated_at TEXT,
                provider_id TEXT NOT NULL,
                environment TEXT NOT NULL,
                symbol TEXT NOT NULL,
                side TEXT NOT NULL,
                reduce_only INTEGER NOT NULL CHECK(reduce_only IN (0,1)),
                order_type TEXT NOT NULL,
                requested_quantity TEXT NOT NULL,
                limit_price TEXT NOT NULL,
                observed_executed_quantity TEXT NOT NULL,
                expected_price TEXT NOT NULL,
                observed_average_price TEXT NOT NULL,
                provider_status TEXT NOT NULL,
                spread_bps TEXT NOT NULL,
                liquidity_score TEXT NOT NULL,
                atr_percent TEXT NOT NULL,
                canonical_sha256 TEXT NOT NULL UNIQUE,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(client_order_id,sequence));
            CREATE INDEX IF NOT EXISTS ix_execution_drift_client_time ON execution_drift_observations(client_order_id,sequence);
            CREATE INDEX IF NOT EXISTS ix_execution_drift_cycle_time ON execution_drift_observations(cycle_id,observed_at,id);
            CREATE INDEX IF NOT EXISTS ix_execution_drift_calibration ON execution_drift_observations(provider_id,environment,symbol,order_type,phase,observed_at);
            CREATE TRIGGER IF NOT EXISTS execution_drift_observations_no_update BEFORE UPDATE ON execution_drift_observations BEGIN SELECT RAISE(ABORT,'execution drift observations are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_drift_observations_no_delete BEFORE DELETE ON execution_drift_observations BEGIN SELECT RAISE(ABORT,'execution drift observations are append-only'); END;
            """;
        q.ExecuteNonQuery();
        EnsureColumn(c,"execution_drift_observations","schema","TEXT NOT NULL DEFAULT 'wpe.execution-drift-observation/1.0'");
        EnsureColumn(c,"execution_drift_observations","provider_id","TEXT NOT NULL DEFAULT 'legacy-unknown'");
        EnsureColumn(c,"execution_drift_observations","environment","TEXT NOT NULL DEFAULT 'Unknown'");
        EnsureColumn(c,"execution_drift_observations","order_type","TEXT NOT NULL DEFAULT 'Market'");
        EnsureColumn(c,"execution_drift_observations","limit_price","TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(c,"execution_drift_observations","spread_bps","TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(c,"execution_drift_observations","liquidity_score","TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(c,"execution_drift_observations","atr_percent","TEXT NOT NULL DEFAULT '0'");
    }

    internal async Task<bool> AppendExecutionDriftObservationAsync(ExecutionDriftObservationInputV1 input,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var next=c.CreateCommand();next.Transaction=tx;next.CommandText="SELECT COALESCE(MAX(sequence),-1)+1 FROM execution_drift_observations WHERE client_order_id=$id";next.Parameters.AddWithValue("$id",input.ClientOrderId);
        var sequence=Convert.ToInt32(await next.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
        var value=ExecutionDriftCanonicalizerV1.Create(input,sequence,_utcNow().ToUniversalTime());
        await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="""
            INSERT INTO execution_drift_observations(
                schema,cycle_id,client_order_id,sequence,phase,source,observed_at,exchange_updated_at,provider_id,environment,
                symbol,side,reduce_only,order_type,requested_quantity,limit_price,observed_executed_quantity,expected_price,
                observed_average_price,provider_status,spread_bps,liquidity_score,atr_percent,canonical_sha256,canonical_bytes)
            VALUES($schema,$cycle,$client,$sequence,$phase,$source,$observed,$exchange,$provider,$environment,
                $symbol,$side,$reduce,$orderType,$requested,$limit,$executed,$expected,$average,$status,$spread,$liquidity,$atr,$hash,$bytes)
            """;
        q.Parameters.AddWithValue("$schema",value.Schema);q.Parameters.AddWithValue("$cycle",value.CycleId);q.Parameters.AddWithValue("$client",value.ClientOrderId);q.Parameters.AddWithValue("$sequence",value.Sequence);
        q.Parameters.AddWithValue("$phase",value.Phase.ToString());q.Parameters.AddWithValue("$source",value.Source.ToString());q.Parameters.AddWithValue("$observed",value.ObservedAtUtc.ToString("O",CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$exchange",value.ExchangeUpdatedAtUtc is null?DBNull.Value:value.ExchangeUpdatedAtUtc.Value.ToString("O",CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$provider",value.ProviderId);q.Parameters.AddWithValue("$environment",value.Environment);
        q.Parameters.AddWithValue("$symbol",value.Symbol);q.Parameters.AddWithValue("$side",value.Side.ToString());q.Parameters.AddWithValue("$reduce",value.ReduceOnly?1:0);q.Parameters.AddWithValue("$orderType",value.OrderType.ToString());
        q.Parameters.AddWithValue("$requested",value.RequestedQuantity.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$limit",value.LimitPrice.ToString(CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$executed",value.ObservedExecutedQuantity.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$expected",value.ExpectedPrice.ToString(CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$average",value.ObservedAveragePrice.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$status",value.ProviderStatus);
        q.Parameters.AddWithValue("$spread",value.SpreadBps.ToString("R",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$liquidity",value.LiquidityScore.ToString("R",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$atr",value.AtrPercent.ToString("R",CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$hash",value.CanonicalSha256);q.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
        var inserted=await q.ExecuteNonQueryAsync(ct)==1;await tx.CommitAsync(ct);return inserted;
    }

    internal async Task<IReadOnlyList<ExecutionDriftObservationV1>> GetExecutionDriftObservationsAsync(string clientOrderId,int limit,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))return [];
        var list=new List<ExecutionDriftObservationV1>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT schema,cycle_id,client_order_id,sequence,phase,source,observed_at,exchange_updated_at,provider_id,environment,
                   symbol,side,reduce_only,order_type,requested_quantity,limit_price,observed_executed_quantity,expected_price,
                   observed_average_price,provider_status,spread_bps,liquidity_score,atr_percent,canonical_sha256,canonical_bytes
            FROM execution_drift_observations WHERE client_order_id=$id ORDER BY sequence LIMIT $limit
            """;
        q.Parameters.AddWithValue("$id",clientOrderId);q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,1000));
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            if(!string.Equals(r.GetString(0),ExecutionDriftCanonicalizerV1.Schema,StringComparison.Ordinal)
               ||!Enum.TryParse<ExecutionDriftPhaseV1>(r.GetString(4),out var phase)
               ||!Enum.TryParse<ExecutionDriftSourceV1>(r.GetString(5),out var source)
               ||!Enum.TryParse<PositionSide>(r.GetString(11),out var side)
               ||!Enum.TryParse<ExecutionOrderType>(r.GetString(13),out var orderType))continue;
            var value=new ExecutionDriftObservationV1(
                r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt32(3),phase,source,
                DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),
                r.IsDBNull(7)?null:DateTimeOffset.Parse(r.GetString(7),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),
                r.GetString(8),r.GetString(9),r.GetString(10),side,r.GetInt32(12)==1,orderType,
                decimal.Parse(r.GetString(14),CultureInfo.InvariantCulture),decimal.Parse(r.GetString(15),CultureInfo.InvariantCulture),
                decimal.Parse(r.GetString(16),CultureInfo.InvariantCulture),decimal.Parse(r.GetString(17),CultureInfo.InvariantCulture),
                decimal.Parse(r.GetString(18),CultureInfo.InvariantCulture),r.GetString(19),
                double.Parse(r.GetString(20),CultureInfo.InvariantCulture),double.Parse(r.GetString(21),CultureInfo.InvariantCulture),double.Parse(r.GetString(22),CultureInfo.InvariantCulture),
                r.GetString(23),(byte[])r[24]);
            if(ExecutionDriftCanonicalizerV1.IsCanonical(value))list.Add(value);
        }
        return list;
    }

    internal async Task<ExecutionDriftSummaryV1?> GetExecutionDriftSummaryAsync(string clientOrderId,CancellationToken ct)
    {
        var values=await GetExecutionDriftObservationsAsync(clientOrderId,1000,ct);if(values.Count==0)return null;
        var first=values[0];
        if(values.Any(x=>!string.Equals(x.ProviderId,first.ProviderId,StringComparison.Ordinal)
                         ||!string.Equals(x.Environment,first.Environment,StringComparison.Ordinal)
                         ||!string.Equals(x.Symbol,first.Symbol,StringComparison.Ordinal)
                         ||x.Side!=first.Side||x.ReduceOnly!=first.ReduceOnly||x.OrderType!=first.OrderType
                         ||x.RequestedQuantity!=first.RequestedQuantity||x.ExpectedPrice!=first.ExpectedPrice||x.LimitPrice!=first.LimitPrice))
            return null;
        var exchange=values.Where(x=>x.Source==ExecutionDriftSourceV1.Exchange).ToArray();
        var final=values.LastOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.ExecutionRecorded)??values[^1];
        var submit=values.FirstOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.SubmissionAttempted);
        var firstExchange=exchange.FirstOrDefault();
        var preflight=values.LastOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.PreflightQuote);
        var intentAccepted=values.FirstOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.IntentAccepted)??first;
        var fillRatio=final.RequestedQuantity>0?final.ObservedExecutedQuantity/final.RequestedQuantity:0;
        double? submitMs=submit is not null&&firstExchange is not null?(firstExchange.ObservedAtUtc-submit.ObservedAtUtc).TotalMilliseconds:null;
        var endMs=(final.ObservedAtUtc-intentAccepted.ObservedAtUtc).TotalMilliseconds;
        double? adverse=null;
        if(final.ExpectedPrice>0&&final.ObservedAveragePrice>0)
        {
            var direction=final.Side==PositionSide.Long?1m:-1m;
            adverse=(double)(direction*(final.ObservedAveragePrice-final.ExpectedPrice)/final.ExpectedPrice*10000m);
        }
        return new(clientOrderId,first.ProviderId,first.Environment,first.Symbol,first.Side,first.ReduceOnly,first.OrderType,first.LimitPrice,
            values.Count,exchange.Length,exchange.Count(x=>x.ObservedExecutedQuantity>0&&x.ObservedExecutedQuantity<x.RequestedQuantity),
            final.RequestedQuantity,final.ObservedExecutedQuantity,fillRatio,final.ExpectedPrice,final.ObservedAveragePrice,
            preflight?.ObservedAveragePrice,preflight?.SpreadBps,preflight?.LiquidityScore,preflight?.AtrPercent,
            submitMs,endMs,adverse,final.ProviderStatus);
    }
}
