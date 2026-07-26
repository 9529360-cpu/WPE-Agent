using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpeAgent.ModelOff;

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffAgentV1>))]
public enum ModelOffAgentV1 { Market, Research, Strategy, Risk, Execution, Recovery, Audit }

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffSourceKindV1>))]
public enum ModelOffSourceKindV1 { Market, Account, News, Macro, Config, Strategy, Audit }

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffSourceStatusV1>))]
public enum ModelOffSourceStatusV1 { Available, Stale, Unknown, Unsupported, Inconsistent, Invalid, Error }

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffOutputStatusV1>))]
public enum ModelOffOutputStatusV1 { Succeeded, Degraded, Abstained, Blocked, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffUncertaintyLevelV1>))]
public enum ModelOffUncertaintyLevelV1 { None, Bounded, Material, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffHandoffStatusV1>))]
public enum ModelOffHandoffStatusV1 { Ready, Degraded, Blocked, Quarantined }

[JsonConverter(typeof(JsonStringEnumConverter<ModelOffAlertSeverityV1>))]
public enum ModelOffAlertSeverityV1 { Critical, High, Medium, Info }

public sealed record ModelOffRuleSetV1(string Id, string Version);

public sealed record ModelOffSourceV1(
    string SourceId,
    ModelOffSourceKindV1 Kind,
    DateTimeOffset? AsOfUtc,
    DateTimeOffset? ReceivedAtUtc,
    ModelOffSourceStatusV1 Status,
    string? ArtifactHash);

public sealed record ModelOffUncertaintyV1(
    ModelOffUncertaintyLevelV1 Level,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> MissingFields);

public sealed record ModelOffDecisionV1(
    string Action,
    bool EligibleForDownstream,
    IReadOnlyList<string> ReasonCodes);

public sealed record ModelOffAgentOutputV1(
    ModelOffAgentV1 Agent,
    string OutputId,
    string CycleId,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset EvaluationTimeUtc,
    string InputSchema,
    ModelOffRuleSetV1 RuleSet,
    IReadOnlyList<ModelOffSourceV1> Sources,
    ModelOffOutputStatusV1 Status,
    ModelOffUncertaintyV1 Uncertainty,
    JsonElement Facts,
    IReadOnlyList<JsonElement> Calculations,
    ModelOffDecisionV1 Decision,
    IReadOnlyList<JsonElement> Artifacts,
    string TemplateVersion)
{
    public const string Schema = "wpe.agent-output/1.0";
}

public sealed record ModelOffHandoffV1(
    string HandoffId,
    string CycleId,
    ModelOffAgentV1 FromAgent,
    ModelOffAgentV1 ToAgent,
    string CanonicalOutputId,
    string CanonicalSha256,
    ModelOffHandoffStatusV1 Status,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string> ProhibitedActions,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset? AsOfUtc,
    DateTimeOffset? ExpiresAtUtc)
{
    public const string Schema = "wpe.agent-handoff/1.0";
}

public sealed record ModelOffAlertV1(
    ModelOffAlertSeverityV1 Severity,
    string ReasonCode,
    string BlockedOrDegradedScope,
    string DeterministicAction,
    string? RecheckCondition);

public static class ModelOffEligibilityV1
{
    public static bool IsEligibleForDownstream(ModelOffAgentOutputV1 output)
        => ModelOffCanonicalMetadataV1.IsValid(output)
           && output.Status == ModelOffOutputStatusV1.Succeeded
           && output.Decision.EligibleForDownstream
           && output.Sources.Count > 0
           && output.Uncertainty.Level is ModelOffUncertaintyLevelV1.None or ModelOffUncertaintyLevelV1.Bounded
           && output.Uncertainty.MissingFields.Count == 0
           && output.Sources.All(source => source.Status == ModelOffSourceStatusV1.Available);
}

internal static class ModelOffCanonicalMetadataV1
{
    internal static bool IsValid(ModelOffAgentOutputV1? output)
        => output is not null
           && output.RuleSet is not null
           && !string.IsNullOrWhiteSpace(output.RuleSet.Id)
           && !string.IsNullOrWhiteSpace(output.RuleSet.Version)
           && output.Decision is not null
           && !string.IsNullOrWhiteSpace(output.Decision.Action)
           && output.Sources is not null
           && output.Sources.All(IsValid);

    private static bool IsValid(ModelOffSourceV1? source)
        => source is not null
           && !string.IsNullOrWhiteSpace(source.SourceId)
           && Enum.IsDefined(source.Kind)
           && source.AsOfUtc.HasValue
           && source.ReceivedAtUtc.HasValue
           && Enum.IsDefined(source.Status)
           && !string.IsNullOrWhiteSpace(source.ArtifactHash);
}

public static class ModelOffFixedTemplatesV1
{
    public const string SummaryVersion = "wpe.agent-summary/1.0";
    public const string ReportVersion = "wpe.agent-report/1.0";
    public const string AlertVersion = "wpe.agent-alert/1.0";

    public static string RenderSummary(ModelOffAgentOutputV1 output)
    {
        var eligibleForDownstream = ModelOffEligibilityV1.IsEligibleForDownstream(output);
        var counts = Enum.GetValues<ModelOffSourceStatusV1>()
            .ToDictionary(status => status, status => output.Sources.Count(source => source.Status == status));
        var asOf = output.Sources.Where(source => source.AsOfUtc.HasValue)
            .Select(source => source.AsOfUtc!.Value).DefaultIfEmpty().Max();
        var reasons = output.Decision.ReasonCodes.Concat(output.Uncertainty.ReasonCodes);

        return $"[{Token(output.Agent)}] {Token(output.Status)} | cycle={output.CycleId} | as_of={UtcOrUnknown(asOf == default ? null : asOf)}\n" +
               $"Decision: {output.Decision.Action} | downstream={eligibleForDownstream.ToString().ToLowerInvariant()}\n" +
               $"Reasons: {ListOrNone(reasons)}\n" +
               $"Sources: available={counts[ModelOffSourceStatusV1.Available]} stale={counts[ModelOffSourceStatusV1.Stale]} unknown={counts[ModelOffSourceStatusV1.Unknown]} unsupported={counts[ModelOffSourceStatusV1.Unsupported]} error={counts[ModelOffSourceStatusV1.Error]}\n" +
               $"Missing: {ListOrNone(output.Uncertainty.MissingFields)}\n" +
               $"Artifact: {output.OutputId}";
    }

    public static string RenderReport(ModelOffAgentOutputV1 output)
    {
        var eligibleForDownstream = ModelOffEligibilityV1.IsEligibleForDownstream(output);
        var sections = new List<(string Name, string Value)>
        {
            ("Identity", $"agent={Token(output.Agent)}; cycle={output.CycleId}; output={output.OutputId}"),
            ("Input Sources", output.Sources.Count == 0 ? "none" : string.Join("\n", output.Sources.OrderBy(x => x.SourceId, StringComparer.Ordinal).Select(x => $"{x.SourceId} {Token(x.Status)} {UtcOrUnknown(x.AsOfUtc)}"))),
            ("Facts", Compact(output.Facts)),
            ("Calculations", ElementsOrNone(output.Calculations)),
            ("Decision", $"action={output.Decision.Action}; downstream={eligibleForDownstream.ToString().ToLowerInvariant()}; reasons={ListOrNone(output.Decision.ReasonCodes)}"),
            ("Uncertainty and Missing Data", $"level={Token(output.Uncertainty.Level)}; reasons={ListOrNone(output.Uncertainty.ReasonCodes)}; missing={ListOrNone(output.Uncertainty.MissingFields)}"),
            ("Artifacts", ElementsOrNone(output.Artifacts)),
            ("Next Deterministic Step", ModelOffEligibilityV1.IsEligibleForDownstream(output) ? output.Decision.Action : "none")
        };

        if (output.Agent is ModelOffAgentV1.Market or ModelOffAgentV1.Research)
            sections.Insert(4, ("Method and Version", $"{output.RuleSet.Id}/{output.RuleSet.Version}"));
        if (output.Agent == ModelOffAgentV1.Research)
            sections.Insert(5, ("Dataset/Period/Universe/Costs/Benchmark/Sample/OOS", "none"));
        if (output.Agent is ModelOffAgentV1.Execution or ModelOffAgentV1.Recovery)
            sections.Insert(4, ("State Timeline", "none"));
        if (output.Agent == ModelOffAgentV1.Audit)
            sections.Insert(4, ("Coverage", "none"));

        return string.Join("\n", sections.Select(section => $"## {section.Name}\n{section.Value}"));
    }

    public static string RenderAlert(ModelOffAgentOutputV1 output, ModelOffAlertV1 alert)
    {
        var asOf = output.Sources.Where(source => source.AsOfUtc.HasValue)
            .Select(source => source.AsOfUtc!.Value).DefaultIfEmpty().Max();
        return $"{Token(alert.Severity)} {alert.ReasonCode}\n" +
               $"Agent: {Token(output.Agent)}  Cycle: {output.CycleId}\n" +
               $"State: {Token(output.Status)}  As-of: {UtcOrUnknown(asOf == default ? null : asOf)}\n" +
               $"Effect: {alert.BlockedOrDegradedScope}\n" +
               $"Automatic action: {alert.DeterministicAction}\n" +
               $"Recheck condition: {alert.RecheckCondition ?? "none"}";
    }

    private static string ElementsOrNone(IEnumerable<JsonElement> elements)
    {
        var values = elements.Select(Compact).Order(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? "none" : string.Join("\n", values);
    }

    private static string Compact(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "none" : ModelOffCanonicalSerializerV1.CanonicalizeElement(value);
    private static string ListOrNone(IEnumerable<string> values) => string.Join(",", values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) is { Length: > 0 } text ? text : "none";
    internal static string Token<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    internal static string UtcOrUnknown(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") ?? "unknown";
}
