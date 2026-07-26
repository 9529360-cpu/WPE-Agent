using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpeAgent.TradingAuthorization;

public enum TradingAuthorizationMode
{
    Research,
    Signal,
    Review,
    Auto
}

public sealed class TradingAuthorizationModeJsonConverter : JsonConverter<TradingAuthorizationMode>
{
    public override TradingAuthorizationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<TradingAuthorizationMode>(reader.GetString(), true, out var named) &&
            Enum.IsDefined(named))
            return named;

        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(TradingAuthorizationMode), numeric))
            return (TradingAuthorizationMode)numeric;

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();

        return TradingAuthorizationMode.Review;
    }

    public override void Write(Utf8JsonWriter writer, TradingAuthorizationMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : TradingAuthorizationMode.Review.ToString());
}

public sealed record DeterministicRiskReceipt(
    string ReceiptId,
    string CorrelationId,
    string IntentHash,
    bool Approved,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc = null,
    string? ArtifactHash = null);

public sealed record TradingApprovalReceipt(
    string ReceiptId,
    string CorrelationId,
    string IntentHash,
    string UserId,
    string DeviceId,
    string SessionId,
    bool Approved,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc = null,
    DateTimeOffset? ConsumedAtUtc = null,
    string? ArtifactHash = null);

public sealed record TradingApprovalRequest(
    string RequestId,
    TradingAuthorizationMode Mode,
    string CorrelationId,
    string IntentHash,
    string UserId,
    string DeviceId,
    string SessionId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc = null,
    DateTimeOffset? ConsumedAtUtc = null,
    string? ArtifactHash = null);

public sealed record TradingApprovalConsumption(
    string RequestId,
    string ReceiptId,
    string CorrelationId,
    string IntentHash,
    string UserId,
    string DeviceId,
    string SessionId,
    string? ArtifactHash = null);

public sealed record TradingApprovalPersistenceResult(bool Succeeded, string Code);

public sealed record TradingApprovalConsumptionResult(bool Consumed, string Code);

public sealed record PersistedTradingApprovalReceipt(string RequestId, TradingApprovalReceipt Receipt);

public sealed record TradingAuthorizationRequest(
    TradingAuthorizationMode Mode,
    bool IsTestnet,
    string CorrelationId,
    string IntentHash,
    string UserId,
    string DeviceId,
    string SessionId,
    DeterministicRiskReceipt? RiskReceipt,
    TradingApprovalReceipt? ApprovalReceipt);

public sealed record TradingAuthorizationDecision(bool Allowed, string Code);

public sealed record TradingAuthorizationModeChangeAudit(
    string ChangeId,
    TradingAuthorizationMode OldMode,
    TradingAuthorizationMode NewMode,
    string UserId,
    string DeviceId,
    DateTimeOffset ChangedAtUtc,
    string Reason);

public sealed record TradingAuthorizationModeChangeResult(
    bool Changed,
    string Code,
    TradingAuthorizationMode OldMode,
    TradingAuthorizationMode NewMode);

public enum TradingReviewQueueStatus
{
    Pending,
    Approved,
    Rejected,
    Revoked,
    Expired,
    Claimed,
    Executing,
    Reconciling,
    Succeeded,
    ArtifactInvalid,
    StrategyInvalid,
    MarketStale,
    PolicyBlocked,
    FailedTerminal
}

/// <summary>
/// Dependency-neutral, execution-complete snapshot. Sequence is part of the signed intent order.
/// ReasonCode is intentionally a bounded diagnostic token, never free-form user or model payload.
/// </summary>
public sealed record DurableExecutionIntentSnapshotV1(
    int Sequence,
    string Symbol,
    string Side,
    decimal Quantity,
    bool ReduceOnly,
    decimal StopLoss,
    decimal TakeProfit,
    string ClientOrderId,
    string ReasonCode,
    string Action,
    string OrderType,
    decimal LimitPrice,
    decimal ExpectedPrice);

public sealed record DurableReviewExecutionArtifactV1(
    int ContractVersion,
    IReadOnlyList<DurableExecutionIntentSnapshotV1> Intents,
    int Leverage,
    bool Isolated,
    string ProviderId,
    string Environment,
    string StrategyId,
    string StrategyVersion,
    DateTimeOffset MarketCollectedAtUtc,
    string MarketDataVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public const int Version = 1;
}

public sealed record DurableReviewArtifactHashes(string ArtifactHash, string IntentHash);

public static class DurableReviewArtifactCanonicalizer
{
    private const int MaximumIntents = 16;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize(DurableReviewExecutionArtifactV1 artifact)
    {
        var validation = Validate(artifact);
        if (!validation.Valid) throw new ArgumentException(validation.Code, nameof(artifact));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteArtifact(writer, artifact);
        return stream.ToArray();
    }

    public static DurableReviewArtifactHashes ComputeHashes(DurableReviewExecutionArtifactV1 artifact)
        => new(Sha256Hex(Serialize(artifact)), Sha256Hex(SerializeIntentPayload(artifact)));

    public static string Sha256Hex(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public static bool TryDeserializeCanonical(
        ReadOnlySpan<byte> value,
        out DurableReviewExecutionArtifactV1? artifact,
        out string code)
    {
        artifact = null;
        code = "review.artifact-invalid";
        if (value.IsEmpty || value.Length > 128 * 1024) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<DurableReviewExecutionArtifactV1>(value, ReadOptions);
            if (parsed is null || !Validate(parsed).Valid) return false;
            var canonical = Serialize(parsed);
            if (!value.SequenceEqual(canonical)) return false;
            artifact = parsed;
            code = "review.artifact-valid";
            return true;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static (bool Valid, string Code) Validate(DurableReviewExecutionArtifactV1? artifact)
    {
        if (artifact is null || artifact.ContractVersion != DurableReviewExecutionArtifactV1.Version)
            return (false, "review.artifact-version-invalid");
        if (artifact.Intents is null || artifact.Intents.Count is < 1 or > MaximumIntents)
            return (false, "review.artifact-intents-invalid");
        if (artifact.Leverage is < 1 or > 125 || !artifact.Isolated)
            return (false, "review.artifact-margin-invalid");
        if (!Token(artifact.ProviderId, 64) || !string.Equals(artifact.Environment, "Testnet", StringComparison.Ordinal) ||
            !Token(artifact.StrategyId, 96) || !Token(artifact.StrategyVersion, 96) || !Token(artifact.MarketDataVersion, 96))
            return (false, "review.artifact-context-invalid");
        var created = artifact.CreatedAtUtc.ToUniversalTime();
        var expires = artifact.ExpiresAtUtc.ToUniversalTime();
        var market = artifact.MarketCollectedAtUtc.ToUniversalTime();
        if (artifact.CreatedAtUtc.Offset != TimeSpan.Zero || artifact.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            artifact.MarketCollectedAtUtc.Offset != TimeSpan.Zero || market > created || expires <= created || expires - created > TimeSpan.FromMinutes(30))
            return (false, "review.artifact-time-invalid");
        for (var index = 0; index < artifact.Intents.Count; index++)
        {
            var intent = artifact.Intents[index];
            if (intent is null || intent.Sequence != index || !Symbol(intent.Symbol) || !Token(intent.ClientOrderId, 64) ||
                !Token(intent.ReasonCode, 120) || intent.Quantity <= 0 || intent.StopLoss < 0 || intent.TakeProfit < 0 ||
                intent.LimitPrice < 0 || intent.ExpectedPrice <= 0 || intent.Side is not ("Long" or "Short") ||
                intent.Action is not ("OpenLong" or "OpenShort" or "AddLong" or "AddShort" or "ReduceLong" or "ReduceShort" or "CloseLong" or "CloseShort" or "ReverseToLong" or "ReverseToShort" or "Lock" or "Unlock") ||
                intent.OrderType is not ("Market" or "Limit") || intent.OrderType == "Limit" && intent.LimitPrice <= 0)
                return (false, "review.artifact-intent-invalid");
        }
        return (true, "review.artifact-valid");
    }

    private static byte[] SerializeIntentPayload(DurableReviewExecutionArtifactV1 artifact)
    {
        var validation = Validate(artifact);
        if (!validation.Valid) throw new ArgumentException(validation.Code, nameof(artifact));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", artifact.ContractVersion);
            writer.WriteNumber("leverage", artifact.Leverage);
            writer.WriteBoolean("isolated", artifact.Isolated);
            writer.WritePropertyName("intents");
            writer.WriteStartArray();
            foreach (var intent in artifact.Intents) WriteIntent(writer, intent);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteArtifact(Utf8JsonWriter writer, DurableReviewExecutionArtifactV1 artifact)
    {
        writer.WriteStartObject();
        writer.WriteNumber("contractVersion", artifact.ContractVersion);
        writer.WritePropertyName("intents");
        writer.WriteStartArray();
        foreach (var intent in artifact.Intents) WriteIntent(writer, intent);
        writer.WriteEndArray();
        writer.WriteNumber("leverage", artifact.Leverage);
        writer.WriteBoolean("isolated", artifact.Isolated);
        writer.WriteString("providerId", artifact.ProviderId);
        writer.WriteString("environment", artifact.Environment);
        writer.WriteString("strategyId", artifact.StrategyId);
        writer.WriteString("strategyVersion", artifact.StrategyVersion);
        writer.WriteString("marketCollectedAtUtc", Utc(artifact.MarketCollectedAtUtc));
        writer.WriteString("marketDataVersion", artifact.MarketDataVersion);
        writer.WriteString("createdAtUtc", Utc(artifact.CreatedAtUtc));
        writer.WriteString("expiresAtUtc", Utc(artifact.ExpiresAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteIntent(Utf8JsonWriter writer, DurableExecutionIntentSnapshotV1 intent)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", intent.Sequence);
        writer.WriteString("symbol", intent.Symbol);
        writer.WriteString("side", intent.Side);
        writer.WriteNumber("quantity", intent.Quantity);
        writer.WriteBoolean("reduceOnly", intent.ReduceOnly);
        writer.WriteNumber("stopLoss", intent.StopLoss);
        writer.WriteNumber("takeProfit", intent.TakeProfit);
        writer.WriteString("clientOrderId", intent.ClientOrderId);
        writer.WriteString("reasonCode", intent.ReasonCode);
        writer.WriteString("action", intent.Action);
        writer.WriteString("orderType", intent.OrderType);
        writer.WriteNumber("limitPrice", intent.LimitPrice);
        writer.WriteNumber("expectedPrice", intent.ExpectedPrice);
        writer.WriteEndObject();
    }

    private static bool Token(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '#');
    private static bool Symbol(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length is >= 2 and <= 24 && value.All(character => char.IsAsciiLetterOrDigit(character)) && value == value.ToUpperInvariant();
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}

public enum AutomaticExecutionQueueStatus
{
    Proposed,
    RiskApproved,
    RiskBlocked,
    Claimed,
    Executing,
    Reconciling,
    Succeeded,
    CapabilityUnavailable,
    MarketStale,
    StrategyInvalid,
    ArtifactInvalid,
    PolicyBlocked,
    UnknownOutcome,
    FailedTerminal
}

/// <summary>Automatic Testnet execution artifact. It deliberately contains no human authorization identity or decision.</summary>
public sealed record DurableExecutionArtifactV2(
    int ContractVersion,
    string CorrelationId,
    IReadOnlyList<DurableExecutionIntentSnapshotV1> Intents,
    int Leverage,
    bool Isolated,
    string ProviderId,
    string Environment,
    string StrategyId,
    string StrategyVersion,
    DateTimeOffset MarketCollectedAtUtc,
    string MarketDataVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public const int Version = 2;
}

public sealed record DurableExecutionArtifactHashesV2(string ArtifactHash, string IntentHash);

public static class DurableExecutionArtifactCanonicalizerV2
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize(DurableExecutionArtifactV2 artifact)
    {
        var validation = Validate(artifact);
        if (!validation.Valid) throw new ArgumentException(validation.Code, nameof(artifact));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, artifact, includeContext: true);
        return stream.ToArray();
    }

    public static DurableExecutionArtifactHashesV2 ComputeHashes(DurableExecutionArtifactV2 artifact)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, artifact, includeContext: false);
        return new(DurableReviewArtifactCanonicalizer.Sha256Hex(Serialize(artifact)), DurableReviewArtifactCanonicalizer.Sha256Hex(stream.ToArray()));
    }

    public static bool TryDeserializeCanonical(ReadOnlySpan<byte> value, out DurableExecutionArtifactV2? artifact, out string code)
    {
        artifact = null; code = "automatic.artifact-invalid";
        if (value.IsEmpty || value.Length > 128 * 1024) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<DurableExecutionArtifactV2>(value, ReadOptions);
            if (parsed is null || !Validate(parsed).Valid || !value.SequenceEqual(Serialize(parsed))) return false;
            artifact = parsed; code = "automatic.artifact-valid"; return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException) { return false; }
    }

    public static (bool Valid, string Code) Validate(DurableExecutionArtifactV2? artifact)
    {
        if (artifact is null || artifact.ContractVersion != DurableExecutionArtifactV2.Version) return (false, "automatic.artifact-version-invalid");
        var legacyShape = new DurableReviewExecutionArtifactV1(1, artifact.Intents, artifact.Leverage, artifact.Isolated, artifact.ProviderId,
            artifact.Environment, artifact.StrategyId, artifact.StrategyVersion, artifact.MarketCollectedAtUtc, artifact.MarketDataVersion,
            artifact.CreatedAtUtc, artifact.ExpiresAtUtc);
        if (!DurableReviewArtifactCanonicalizer.Validate(legacyShape).Valid || !Token(artifact.CorrelationId, 120)) return (false, "automatic.artifact-invalid");
        return (true, "automatic.artifact-valid");
    }

    public static byte[] SerializeRiskReceipt(DeterministicRiskReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("receiptId", receipt.ReceiptId); writer.WriteString("correlationId", receipt.CorrelationId);
            writer.WriteString("intentHash", receipt.IntentHash); writer.WriteBoolean("approved", receipt.Approved);
            writer.WriteString("issuedAtUtc", Utc(receipt.IssuedAtUtc)); writer.WriteString("expiresAtUtc", Utc(receipt.ExpiresAtUtc));
            if (receipt.RevokedAtUtc is null) writer.WriteNull("revokedAtUtc"); else writer.WriteString("revokedAtUtc", Utc(receipt.RevokedAtUtc.Value));
            if (receipt.ArtifactHash is null) writer.WriteNull("artifactHash"); else writer.WriteString("artifactHash", receipt.ArtifactHash);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static bool TryDeserializeRiskReceipt(ReadOnlySpan<byte> value, out DeterministicRiskReceipt? receipt)
    {
        receipt = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<DeterministicRiskReceipt>(value, ReadOptions);
            if (parsed is null || !value.SequenceEqual(SerializeRiskReceipt(parsed))) return false;
            receipt = parsed; return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException) { return false; }
    }

    private static void Write(Utf8JsonWriter writer, DurableExecutionArtifactV2 artifact, bool includeContext)
    {
        var validation = Validate(artifact); if (!validation.Valid) throw new ArgumentException(validation.Code, nameof(artifact));
        writer.WriteStartObject(); writer.WriteNumber("contractVersion", artifact.ContractVersion);
        if (includeContext) writer.WriteString("correlationId", artifact.CorrelationId);
        writer.WritePropertyName("intents"); writer.WriteStartArray();
        foreach (var intent in artifact.Intents)
        {
            writer.WriteStartObject(); writer.WriteNumber("sequence", intent.Sequence); writer.WriteString("symbol", intent.Symbol); writer.WriteString("side", intent.Side);
            writer.WriteNumber("quantity", intent.Quantity); writer.WriteBoolean("reduceOnly", intent.ReduceOnly); writer.WriteNumber("stopLoss", intent.StopLoss);
            writer.WriteNumber("takeProfit", intent.TakeProfit); writer.WriteString("clientOrderId", intent.ClientOrderId); writer.WriteString("reasonCode", intent.ReasonCode);
            writer.WriteString("action", intent.Action); writer.WriteString("orderType", intent.OrderType); writer.WriteNumber("limitPrice", intent.LimitPrice);
            writer.WriteNumber("expectedPrice", intent.ExpectedPrice); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteNumber("leverage", artifact.Leverage); writer.WriteBoolean("isolated", artifact.Isolated);
        if (includeContext)
        {
            writer.WriteString("providerId", artifact.ProviderId); writer.WriteString("environment", artifact.Environment); writer.WriteString("strategyId", artifact.StrategyId);
            writer.WriteString("strategyVersion", artifact.StrategyVersion); writer.WriteString("marketCollectedAtUtc", Utc(artifact.MarketCollectedAtUtc));
            writer.WriteString("marketDataVersion", artifact.MarketDataVersion); writer.WriteString("createdAtUtc", Utc(artifact.CreatedAtUtc)); writer.WriteString("expiresAtUtc", Utc(artifact.ExpiresAtUtc));
        }
        writer.WriteEndObject();
    }

    private static bool Token(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '#');
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
