using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WpeAgent.ModelOff;

public sealed record ModelOffCanonicalDocumentV1(byte[] Utf8Bytes, string Sha256)
{
    public string Json => Encoding.UTF8.GetString(Utf8Bytes);
}

public static class ModelOffCanonicalSerializerV1
{
    private static readonly HashSet<string> ForbiddenModelFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "model_id", "model_name", "prompt", "prompt_text", "response", "model_response",
        "brain_request", "brain_response", "explanation", "model_explanation", "authoritative", "used_for_decision"
    };

    public static ModelOffCanonicalDocumentV1 Serialize(ModelOffAgentOutputV1 output)
    {
        Validate(output);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteOutput(writer, output);
        var bytes = stream.ToArray();
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public static ModelOffCanonicalDocumentV1 SerializeHandoff(ModelOffHandoffV1 handoff)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("allowed_actions"); WriteStrings(writer, handoff.AllowedActions);
            writer.WriteString("as_of_utc", Utc(handoff.AsOfUtc));
            writer.WriteString("canonical_output_id", handoff.CanonicalOutputId);
            writer.WriteString("canonical_sha256", handoff.CanonicalSha256);
            writer.WriteString("cycle_id", handoff.CycleId);
            writer.WriteString("expires_at_utc", Utc(handoff.ExpiresAtUtc));
            writer.WriteString("from_agent", Token(handoff.FromAgent));
            writer.WriteString("handoff_id", handoff.HandoffId);
            writer.WritePropertyName("prohibited_actions"); WriteStrings(writer, handoff.ProhibitedActions);
            writer.WritePropertyName("reason_codes"); WriteStrings(writer, handoff.ReasonCodes);
            writer.WriteString("schema", ModelOffHandoffV1.Schema);
            writer.WriteString("status", Token(handoff.Status));
            writer.WriteString("to_agent", Token(handoff.ToAgent));
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void WriteOutput(Utf8JsonWriter writer, ModelOffAgentOutputV1 output)
    {
        writer.WriteStartObject();
        writer.WriteString("agent", Token(output.Agent));
        writer.WritePropertyName("artifacts"); WriteElements(writer, output.Artifacts);
        writer.WritePropertyName("calculations"); WriteElements(writer, output.Calculations);
        writer.WriteString("cycle_id", output.CycleId);
        writer.WritePropertyName("decision");
        writer.WriteStartObject();
        writer.WriteString("action", output.Decision.Action);
        writer.WriteBoolean("eligible_for_downstream", output.Decision.EligibleForDownstream);
        writer.WritePropertyName("reason_codes"); WriteStrings(writer, output.Decision.ReasonCodes);
        writer.WriteEndObject();
        writer.WriteString("evaluation_time_utc", Utc(output.EvaluationTimeUtc));
        writer.WritePropertyName("facts"); WriteElement(writer, output.Facts);
        writer.WriteString("generated_at_utc", Utc(output.GeneratedAtUtc));
        writer.WriteString("input_schema", output.InputSchema);
        writer.WriteString("output_id", output.OutputId);
        writer.WritePropertyName("rule_set");
        writer.WriteStartObject(); writer.WriteString("id", output.RuleSet.Id); writer.WriteString("version", output.RuleSet.Version); writer.WriteEndObject();
        writer.WriteString("schema", ModelOffAgentOutputV1.Schema);
        writer.WritePropertyName("sources");
        writer.WriteStartArray();
        foreach (var source in output.Sources.OrderBy(CanonicalSource, StringComparer.Ordinal)) WriteSource(writer, source);
        writer.WriteEndArray();
        writer.WriteString("status", Token(output.Status));
        writer.WriteString("template_version", output.TemplateVersion);
        writer.WritePropertyName("uncertainty");
        writer.WriteStartObject();
        writer.WriteString("level", Token(output.Uncertainty.Level));
        writer.WritePropertyName("missing_fields"); WriteStrings(writer, output.Uncertainty.MissingFields);
        writer.WritePropertyName("reason_codes"); WriteStrings(writer, output.Uncertainty.ReasonCodes);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter writer, ModelOffSourceV1 source)
    {
        writer.WriteStartObject();
        if (source.ArtifactHash is null) writer.WriteNull("artifact_hash"); else writer.WriteString("artifact_hash", source.ArtifactHash);
        if (source.AsOfUtc is null) writer.WriteNull("as_of_utc"); else writer.WriteString("as_of_utc", Utc(source.AsOfUtc.Value));
        writer.WriteString("kind", Token(source.Kind));
        if (source.ReceivedAtUtc is null) writer.WriteNull("received_at_utc"); else writer.WriteString("received_at_utc", Utc(source.ReceivedAtUtc.Value));
        writer.WriteString("source_id", source.SourceId);
        writer.WriteString("status", Token(source.Status));
        writer.WriteEndObject();
    }

    private static string CanonicalSource(ModelOffSourceV1 source)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteSource(writer, source);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElements(Utf8JsonWriter writer, IEnumerable<JsonElement> elements)
    {
        writer.WriteStartArray();
        foreach (var element in elements.Select(CanonicalElement).Order(StringComparer.Ordinal)) writer.WriteRawValue(element, skipInputValidation: false);
        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element) => writer.WriteRawValue(CanonicalElement(element), skipInputValidation: false);

    private static string CanonicalElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonicalElement(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (IsForbiddenModelField(property.Name)) throw new InvalidOperationException($"Canonical model field is forbidden: {property.Name}");
                    writer.WritePropertyName(property.Name); WriteCanonicalElement(writer, property.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var value in element.EnumerateArray()) WriteCanonicalElement(writer, value); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out var number)) writer.WriteRawValue(number.ToString(CultureInfo.InvariantCulture));
                else throw new InvalidOperationException("Canonical numbers must be finite decimals.");
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidOperationException($"Unsupported canonical JSON value: {element.ValueKind}");
        }
    }

    internal static string CanonicalizeElement(JsonElement element) => CanonicalElement(element);

    private static bool IsForbiddenModelField(string name)
        => ForbiddenModelFields.Contains(name)
           || name.StartsWith("model_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("prompt_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("brain_", StringComparison.OrdinalIgnoreCase)
           || name.Equals("llm", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("llm_", StringComparison.OrdinalIgnoreCase);

    private static void Validate(ModelOffAgentOutputV1 output)
    {
        if (!ModelOffCanonicalMetadataV1.IsValid(output))
            throw new InvalidOperationException("Canonical source, decision, and rule-set metadata are required and must be valid.");
        if (output.GeneratedAtUtc.Offset != TimeSpan.Zero || output.EvaluationTimeUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Canonical timestamps must be supplied in UTC.");
        if (string.IsNullOrWhiteSpace(output.OutputId) || string.IsNullOrWhiteSpace(output.CycleId) || string.IsNullOrWhiteSpace(output.InputSchema))
            throw new InvalidOperationException("Canonical identity fields are required.");
        _ = CanonicalElement(output.Facts);
        foreach (var value in output.Calculations.Concat(output.Artifacts)) _ = CanonicalElement(value);
    }

    private static string Token<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string? Utc(DateTimeOffset? value) => value is null ? null : Utc(value.Value);
}
