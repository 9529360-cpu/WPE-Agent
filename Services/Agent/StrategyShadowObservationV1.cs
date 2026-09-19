using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

internal sealed record StrategyShadowObservationV1(
    string Schema,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    string Lifecycle,
    DateTimeOffset ValidationAtUtc,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset MarketCollectedAtUtc,
    decimal MarketPrice,
    int Direction,
    double Confidence,
    string BacktestValidationSha256,
    byte[] BacktestValidationCanonicalBytes,
    string TimelineSha256,
    string MarketProviderId,
    string Environment,
    string MarketProvenanceSha256,
    byte[] MarketProvenanceCanonicalBytes,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class StrategyShadowObservationCanonicalizerV1
{
    internal const string Schema="wpe.strategy-shadow-observation/1.0";
    internal static readonly TimeSpan MaximumMarketAge=TimeSpan.FromHours(1);

    internal static StrategyShadowObservationV1 Create(
        StrategyProfile profile,
        StrategySignal signal,
        MarketEvidence market,
        BacktestValidationFactV1 validation,
        StrategyExposureTimelineArtifactV1 timeline,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(timeline);

        var observed=observedAtUtc.ToUniversalTime();
        if(observedAtUtc.Offset!=TimeSpan.Zero||observed==default)
            throw new ArgumentException("Shadow observation time must be UTC.",nameof(observedAtUtc));
        if(profile.Lifecycle!=StrategyLifecycle.Shadow)
            throw new InvalidOperationException("Only Shadow strategy observations are eligible for qualification evidence.");
        if(!string.Equals(profile.Id,signal.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(profile.Version,signal.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(profile.Symbol,signal.Symbol,StringComparison.Ordinal))
            throw new InvalidOperationException("Strategy signal identity does not match the observed strategy.");
        if(signal.Direction is < -1 or > 1||!double.IsFinite(signal.Confidence)||signal.Confidence<0||signal.Confidence>1)
            throw new InvalidOperationException("Strategy signal is outside the canonical shadow bounds.");
        if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)
           ||market.Provenance is null
           ||!string.Equals(market.Provenance.Environment,"Testnet",StringComparison.Ordinal)
           ||!string.Equals(market.Symbol,profile.Symbol,StringComparison.Ordinal)
           ||market.Price<=0)
            throw new InvalidOperationException("Shadow market provenance is invalid.");
        var marketAt=new DateTimeOffset(market.CollectedAt.ToUniversalTime());
        if(market.CollectedAt.Kind!=DateTimeKind.Utc
           ||marketAt>observed
           ||observed-marketAt>MaximumMarketAge)
            throw new InvalidOperationException("Shadow market evidence is stale or future.");
        if(!BacktestValidationCanonicalizerV1.IsCanonical(validation,observed)
           ||validation.ValidatedAtUtc>marketAt
           ||!validation.Approved
           ||validation.Promoted
           ||!string.Equals(validation.StrategyId,profile.Id,StringComparison.Ordinal)
           ||!string.Equals(validation.StrategyVersion,profile.Version,StringComparison.Ordinal)
           ||!string.Equals(validation.Symbol,profile.Symbol,StringComparison.Ordinal))
            throw new InvalidOperationException("Shadow validation evidence is invalid.");
        if(!StrategyExposureTimelineV1.IsCanonical(timeline)
           ||timeline.LastTradableAtUtc>validation.ValidatedAtUtc
           ||!string.Equals(timeline.StrategyId,profile.Id,StringComparison.Ordinal)
           ||!string.Equals(timeline.StrategyVersion,profile.Version,StringComparison.Ordinal)
           ||!string.Equals(timeline.Symbol,profile.Symbol,StringComparison.Ordinal))
            throw new InvalidOperationException("Shadow timeline evidence is invalid.");

        var draft=new StrategyShadowObservationV1(
            Schema,
            profile.Id,
            profile.Version,
            profile.Symbol,
            profile.Lifecycle.ToString(),
            validation.ValidatedAtUtc,
            observed,
            marketAt,
            market.Price,
            signal.Direction,
            signal.Confidence,
            validation.CanonicalSha256,
            validation.CanonicalBytes,
            timeline.CanonicalSha256,
            market.Provenance.ProviderId,
            market.Provenance.Environment,
            market.Provenance.CanonicalSha256,
            market.Provenance.CanonicalBytes,
            [],
            string.Empty);
        var bytes=Serialize(draft);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with{CanonicalBytes=bytes,CanonicalSha256=hash};
    }

    internal static bool IsCanonical(StrategyShadowObservationV1? value)
    {
        if(value is null
           ||value.Schema!=Schema
           ||value.Lifecycle!="Shadow"
           ||value.ValidationAtUtc.Offset!=TimeSpan.Zero
           ||value.ObservedAtUtc.Offset!=TimeSpan.Zero
           ||value.MarketCollectedAtUtc.Offset!=TimeSpan.Zero
           ||value.ValidationAtUtc>value.MarketCollectedAtUtc
           ||value.MarketCollectedAtUtc>value.ObservedAtUtc
           ||value.ObservedAtUtc-value.MarketCollectedAtUtc>MaximumMarketAge
           ||value.MarketPrice<=0
           ||value.Direction is < -1 or > 1
           ||!double.IsFinite(value.Confidence)
           ||value.Confidence<0||value.Confidence>1
           ||!Sha(value.BacktestValidationSha256)
           ||value.BacktestValidationCanonicalBytes.Length==0
           ||!Sha(value.TimelineSha256)
           ||!Sha(value.MarketProvenanceSha256)
           ||value.MarketProvenanceCanonicalBytes.Length==0
           ||!Sha(value.CanonicalSha256)
           ||value.CanonicalBytes.Length==0
           ||string.IsNullOrWhiteSpace(value.StrategyId)
           ||string.IsNullOrWhiteSpace(value.StrategyVersion)
           ||string.IsNullOrWhiteSpace(value.Symbol)
           ||string.IsNullOrWhiteSpace(value.MarketProviderId)
           ||value.Environment!="Testnet")
            return false;
        try
        {
            if(!CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(value.BacktestValidationCanonicalBytes),
                   Convert.FromHexString(value.BacktestValidationSha256))
               ||!CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(value.MarketProvenanceCanonicalBytes),
                   Convert.FromHexString(value.MarketProvenanceSha256))
               ||!ValidationSourceMatches(value)
               ||!MarketSourceMatches(value))
                return false;
            var bytes=Serialize(value);
            return CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),Convert.FromHexString(value.CanonicalSha256));
        }
        catch{return false;}
    }

    private static bool ValidationSourceMatches(StrategyShadowObservationV1 value)
    {
        var fields=Encoding.UTF8.GetString(value.BacktestValidationCanonicalBytes).Split('|');
        if(fields.Length!=20)return false;
        var fact=BacktestValidationCanonicalizerV1.Create(
            fields[1],fields[2],fields[3],
            DateTimeOffset.Parse(fields[4],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            int.Parse(fields[5],CultureInfo.InvariantCulture),
            int.Parse(fields[6],CultureInfo.InvariantCulture),
            int.Parse(fields[7],CultureInfo.InvariantCulture),
            int.Parse(fields[8],CultureInfo.InvariantCulture),
            double.Parse(fields[9],CultureInfo.InvariantCulture),
            double.Parse(fields[10],CultureInfo.InvariantCulture),
            double.Parse(fields[11],CultureInfo.InvariantCulture),
            double.Parse(fields[12],CultureInfo.InvariantCulture),
            double.Parse(fields[13],CultureInfo.InvariantCulture),
            double.Parse(fields[14],CultureInfo.InvariantCulture),
            double.Parse(fields[15],CultureInfo.InvariantCulture),
            double.Parse(fields[16],CultureInfo.InvariantCulture),
            double.Parse(fields[17],CultureInfo.InvariantCulture),
            bool.Parse(fields[18]),
            bool.Parse(fields[19]));
        return fields[0]==BacktestValidationCanonicalizerV1.Schema
            &&BacktestValidationCanonicalizerV1.IsCanonical(fact,value.ObservedAtUtc)
            &&fact.Approved&&!fact.Promoted
            &&fact.StrategyId==value.StrategyId
            &&fact.StrategyVersion==value.StrategyVersion
            &&fact.Symbol==value.Symbol
            &&fact.ValidatedAtUtc==value.ValidationAtUtc
            &&fact.CanonicalSha256==value.BacktestValidationSha256
            &&CryptographicOperations.FixedTimeEquals(
                fact.CanonicalBytes,value.BacktestValidationCanonicalBytes);
    }

    private static bool MarketSourceMatches(StrategyShadowObservationV1 value)
    {
        using var document=JsonDocument.Parse(value.MarketProvenanceCanonicalBytes);
        var root=document.RootElement;
        var schema=root.GetProperty("schema").GetString()??string.Empty;
        var provider=root.GetProperty("provider_id").GetString()??string.Empty;
        var environment=root.GetProperty("environment").GetString()??string.Empty;
        var symbol=root.GetProperty("symbol").GetString()??string.Empty;
        var collected=root.GetProperty("collected_at_utc").GetDateTimeOffset().UtcDateTime;
        var candles=root.GetProperty("candles").EnumerateArray().Select(row=>
        {
            var cells=row.EnumerateArray().ToArray();
            if(cells.Length!=9)throw new InvalidDataException("Market provenance candle shape is invalid.");
            return new CandleEvidence(
                cells[0].GetDateTimeOffset().UtcDateTime,
                cells[1].GetDecimal(),cells[2].GetDecimal(),cells[3].GetDecimal(),
                cells[4].GetDecimal(),cells[5].GetDecimal(),cells[6].GetDecimal(),
                cells[7].GetInt64(),cells[8].GetDecimal());
        }).ToArray();
        var market=new MarketEvidence(
            symbol,
            root.GetProperty("price").GetDecimal(),
            root.GetProperty("support").GetDecimal(),
            root.GetProperty("resistance").GetDecimal(),
            root.GetProperty("rsi").GetDouble(),
            root.GetProperty("trend_15m").GetDouble(),
            root.GetProperty("trend_1h").GetDouble(),
            root.GetProperty("trend_4h").GetDouble(),
            new DerivativesSnapshot(0,0,0,0,0,0,0),
            collected)
        {
            Candles=candles
        };
        var expected=MarketEvidenceProvenanceCanonicalizerV1.Create(
            market,provider,environment);
        return schema==MarketEvidenceProvenanceCanonicalizerV1.Schema
            &&provider==value.MarketProviderId
            &&environment==value.Environment
            &&symbol==value.Symbol
            &&new DateTimeOffset(collected)==value.MarketCollectedAtUtc
            &&market.Price==value.MarketPrice
            &&expected.CanonicalSha256==value.MarketProvenanceSha256
            &&CryptographicOperations.FixedTimeEquals(
                expected.CanonicalBytes,value.MarketProvenanceCanonicalBytes);
    }

    private static byte[] Serialize(StrategyShadowObservationV1 value)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256",value.BacktestValidationSha256);
            writer.WriteNumber("confidence",value.Confidence);
            writer.WriteNumber("direction",value.Direction);
            writer.WriteString("environment",value.Environment);
            writer.WriteString("lifecycle",value.Lifecycle);
            writer.WriteString("market_collected_at_utc",value.MarketCollectedAtUtc.ToUniversalTime());
            writer.WriteNumber("market_price",value.MarketPrice);
            writer.WriteString("market_provenance_sha256",value.MarketProvenanceSha256);
            writer.WriteString("market_provider_id",value.MarketProviderId);
            writer.WriteString("observed_at_utc",value.ObservedAtUtc.ToUniversalTime());
            writer.WriteString("schema",value.Schema);
            writer.WriteString("strategy_id",value.StrategyId);
            writer.WriteString("strategy_version",value.StrategyVersion);
            writer.WriteString("symbol",value.Symbol);
            writer.WriteString("timeline_sha256",value.TimelineSha256);
            writer.WriteString("validation_at_utc",value.ValidationAtUtc.ToUniversalTime());
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool Sha(string value)=>value.Length==64&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
}
