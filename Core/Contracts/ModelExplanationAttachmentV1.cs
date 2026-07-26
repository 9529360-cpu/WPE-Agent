using System.Text.Json.Serialization;

namespace WpeAgent.ModelOff;

public enum ModelExplanationStatusV1 { Available, Omitted, Blocked, Failed }

public sealed record ModelExplanationAttachmentV1
{
    public const string SchemaId = "wpe.model-explanation/1.0";

    public ModelExplanationAttachmentV1(
        string canonicalOutputId,
        string canonicalSha256,
        string? provider,
        string? model,
        DateTimeOffset generatedAtUtc,
        string? text,
        ModelExplanationStatusV1 status)
    {
        CanonicalOutputId = canonicalOutputId;
        CanonicalSha256 = canonicalSha256;
        Provider = provider;
        Model = model;
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        Text = text;
        Status = status.ToString().ToLowerInvariant();
    }

    [JsonPropertyName("schema")]
    public string Schema => SchemaId;
    [JsonPropertyName("canonical_output_id")]
    public string CanonicalOutputId { get; }
    [JsonPropertyName("canonical_sha256")]
    public string CanonicalSha256 { get; }
    [JsonPropertyName("authoritative")]
    public bool Authoritative => false;
    [JsonPropertyName("used_for_decision")]
    public bool UsedForDecision => false;
    [JsonPropertyName("provider")]
    public string? Provider { get; }
    [JsonPropertyName("model")]
    public string? Model { get; }
    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }
    [JsonPropertyName("text")]
    public string? Text { get; }
    [JsonPropertyName("status")]
    public string Status { get; }
}
