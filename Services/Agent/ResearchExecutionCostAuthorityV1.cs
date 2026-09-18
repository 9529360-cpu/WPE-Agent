using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WpeAgent.CrossAssetResearch;

public enum ResearchExecutionCostProjectionStateV1
{
    Available,
    Unsupported,
    Invalid
}

public sealed record ResearchExecutionCostProjectionResultV1(
    ResearchExecutionCostProjectionStateV1 State,
    string Code,
    ResearchExecutionCostProjectionV1? Value);

public sealed record ResearchExecutionCostProjectionV1(
    string Schema,
    string ResearchArtifactSha256,
    string StrategyVersion,
    string CostModelVersion,
    string CostModelSha256,
    decimal CommissionRate,
    decimal SlippageRate,
    bool ExecutionAuthority,
    byte[] CanonicalBytes,
    string CanonicalSha256);

/// <summary>
/// Projects execution-compatible variable costs from an already canonical research evidence artifact.
/// It does not own cost numbers and does not grant trading authority.
/// </summary>
public static class ResearchExecutionCostAuthorityV1
{
    public const string Schema = "wpe.research-execution-cost-authority/1.0";
    private const string CostSchema = "wpe.research-execution-cost-model/1.0";
    private const string CostVersionPrefix = "rcm1-";

    public static ResearchExecutionCostProjectionResultV1 Project(ResearchEvidenceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (!ValidSha(artifact.ArtifactHash)
            || !FixedEquals(artifact.ArtifactHash, CrossAssetResearchService.ComputeCanonicalHash(artifact)))
            return new(ResearchExecutionCostProjectionStateV1.Invalid, "research-artifact-invalid", null);

        if (string.IsNullOrWhiteSpace(artifact.StrategyVersion))
            return new(ResearchExecutionCostProjectionStateV1.Invalid, "strategy-version-missing", null);

        var costs = artifact.CostModel;
        if (costs is null
            || costs.CommissionRate < 0 || costs.CommissionRate >= 1
            || costs.SlippageRate < 0 || costs.SlippageRate >= 1
            || costs.FixedCostPerTrade < 0
            || costs.BorrowRatePerDay < 0)
            return new(ResearchExecutionCostProjectionStateV1.Invalid, "cost-model-invalid", null);

        if (costs.FixedCostPerTrade != 0)
            return new(ResearchExecutionCostProjectionStateV1.Unsupported, "fixed-cost-not-execution-compatible", null);
        if (costs.BorrowRatePerDay != 0)
            return new(ResearchExecutionCostProjectionStateV1.Unsupported, "carry-cost-not-execution-compatible", null);

        var costBytes = SerializeCosts(costs);
        var costHash = Hash(costBytes);
        var version = CostVersionPrefix + costHash;
        var draft = new ResearchExecutionCostProjectionV1(
            Schema,
            artifact.ArtifactHash,
            artifact.StrategyVersion,
            version,
            costHash,
            costs.CommissionRate,
            costs.SlippageRate,
            false,
            Array.Empty<byte>(),
            string.Empty);
        var bytes = Serialize(draft);
        var hash = Hash(bytes);
        return new(
            ResearchExecutionCostProjectionStateV1.Available,
            "available",
            draft with { CanonicalBytes = bytes, CanonicalSha256 = hash });
    }

    public static bool IsCanonical(ResearchExecutionCostProjectionV1 value)
    {
        if (value.Schema != Schema
            || value.ExecutionAuthority
            || !ValidSha(value.ResearchArtifactSha256)
            || !ValidSha(value.CostModelSha256)
            || value.CommissionRate < 0 || value.CommissionRate >= 1
            || value.SlippageRate < 0 || value.SlippageRate >= 1
            || value.CanonicalBytes.Length == 0
            || !ValidSha(value.CanonicalSha256))
            return false;

        var expectedCostHash = Hash(SerializeCosts(new ResearchCostModel(value.CommissionRate, value.SlippageRate)));
        if (!FixedEquals(expectedCostHash, value.CostModelSha256)
            || !string.Equals(value.CostModelVersion, CostVersionPrefix + expectedCostHash, StringComparison.Ordinal))
            return false;

        var bytes = Serialize(value);
        var hash = Hash(bytes);
        return FixedEquals(hash, value.CanonicalSha256)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static byte[] SerializeCosts(ResearchCostModel costs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("borrow_rate_per_day", costs.BorrowRatePerDay);
            writer.WriteNumber("commission_rate", costs.CommissionRate);
            writer.WriteString("schema", CostSchema);
            writer.WriteNumber("fixed_cost_per_trade", costs.FixedCostPerTrade);
            writer.WriteNumber("slippage_rate", costs.SlippageRate);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] Serialize(ResearchExecutionCostProjectionV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("commission_rate", value.CommissionRate);
            writer.WriteString("cost_model_sha256", value.CostModelSha256);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            writer.WriteBoolean("execution_authority", value.ExecutionAuthority);
            writer.WriteString("research_artifact_sha256", value.ResearchArtifactSha256);
            writer.WriteString("schema", value.Schema);
            writer.WriteNumber("slippage_rate", value.SlippageRate);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool ValidSha(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
}
