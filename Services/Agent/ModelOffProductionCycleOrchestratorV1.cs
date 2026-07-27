using System.Security.Cryptography;
using System.Text.Json;
using WpeAgent.ModelOff;

namespace 币安量化机器人.Services.Agent;

internal sealed record ModelOffProductionInputV1(
    ModelOffAgentOutputV1 Output,
    ModelOffCanonicalDocumentV1 Document);

internal sealed record ModelOffProductionCycleRequestV1(
    string CycleId,
    DateTimeOffset EvaluationTimeUtc,
    IReadOnlyList<ModelOffProductionInputV1> UpstreamInputs,
    bool MainnetRequested = false,
    TimeSpan? MaximumInputAge = null);

internal sealed record ModelOffProductionCycleResultV1(
    IReadOnlyDictionary<ModelOffAgentV1, ModelOffAgentOutputV1> Outputs,
    IReadOnlyDictionary<ModelOffAgentV1, ModelOffCanonicalDocumentV1> Documents,
    IReadOnlyList<ModelOffCanonicalDocumentV1> Handoffs,
    IReadOnlyDictionary<ModelOffAgentV1, string> AuditCoverage,
    bool EligibleForRiskIncrease,
    string Code);

/// <summary>
/// Deterministic production boundary for the seven model-off Agent aggregates.
/// This stage validates and links canonical work; it deliberately performs no external mutation.
/// </summary>
internal sealed class ModelOffProductionCycleOrchestratorV1
{
    internal const string InputSchema = "wpe.model-off-production-cycle/1.0";
    private static readonly TimeSpan DefaultMaximumInputAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumResearchNewsAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaximumResearchMacroAge = TimeSpan.FromDays(62);
    private static readonly TimeSpan MaximumResearchValidationAge = TimeSpan.FromHours(24);
    private static readonly ModelOffAgentV1[] UpstreamRoles =
        [ModelOffAgentV1.Market, ModelOffAgentV1.Research, ModelOffAgentV1.Strategy, ModelOffAgentV1.Risk];
    private static readonly ModelOffAgentV1[] OrderedRoles = Enum.GetValues<ModelOffAgentV1>();
    private readonly AgentSqliteStore _auditStore;

    internal ModelOffProductionCycleOrchestratorV1(AgentSqliteStore auditStore) =>
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));

    internal async Task<ModelOffProductionCycleResultV1> RunAsync(
        ModelOffProductionCycleRequestV1 request,
        CancellationToken ct)
    {
        ValidateRequest(request);
        var maximumAge = request.MaximumInputAge ?? DefaultMaximumInputAge;
        var inputs = request.UpstreamInputs.GroupBy(x => x.Output.Agent).ToDictionary(x => x.Key, x => x.ToArray());
        var inputSetValid = inputs.Keys.All(UpstreamRoles.Contains);
        var outputs = new List<ModelOffAgentOutputV1>(7);
        var documents = new List<ModelOffCanonicalDocumentV1>(7);
        var upstreamReady = true;

        foreach (var role in UpstreamRoles)
        {
            var reasons = ValidateInput(role, request, maximumAge, inputs.GetValueOrDefault(role),
                documents.LastOrDefault(), upstreamReady, inputSetValid);
            if (reasons.Count == 0)
            {
                var input = inputs[role][0];
                outputs.Add(input.Output);
                documents.Add(input.Document);
            }
            else
            {
                upstreamReady = false;
                var blocked = Blocked(role, request, reasons);
                outputs.Add(blocked);
                documents.Add(ModelOffCanonicalSerializerV1.Serialize(blocked));
            }
        }

        var riskReady = upstreamReady && ModelOffEligibilityV1.IsEligibleForDownstream(outputs[^1]);
        var executionReasons = riskReady ? Array.Empty<string>() :
            [request.MainnetRequested ? "execution.mainnet-disabled" : "execution.risk-input-invalid"];
        var execution = Derived(ModelOffAgentV1.Execution, "execution", "no_mutation", request,
            outputs[^1], documents[^1], riskReady, executionReasons,
            new { mutation_attempted = false, required_chain = new[] { "RiskGate", "TradingExecutionGateway", "ReliableOrderExecutor" } });
        outputs.Add(execution); documents.Add(ModelOffCanonicalSerializerV1.Serialize(execution));

        var executionReady = ModelOffEligibilityV1.IsEligibleForDownstream(execution);
        var recovery = Derived(ModelOffAgentV1.Recovery, "recovery", "no_resubmit", request,
            execution, documents[^1], executionReady,
            executionReady ? [] : ["recovery.execution-input-invalid"],
            new { mutation_attempted = false, resubmit_allowed = false, quarantined = !executionReady });
        outputs.Add(recovery); documents.Add(ModelOffCanonicalSerializerV1.Serialize(recovery));

        var cycleReady = outputs.All(ModelOffEligibilityV1.IsEligibleForDownstream);
        var auditSources = outputs.Zip(documents, (output, document) => Source(
            output.OutputId, request.EvaluationTimeUtc, document,
            ModelOffEligibilityV1.IsEligibleForDownstream(output))).ToArray();
        var auditReasons = cycleReady ? Array.Empty<string>() : ["audit.upstream-invalid"];
        var audit = Output(ModelOffAgentV1.Audit, "audit", request, auditSources, cycleReady,
            cycleReady ? "record_audit" : "record_blocked_audit", auditReasons,
            new { expected_output_count = 7, upstream_hashes = documents.Select(x => x.Sha256).ToArray() });
        outputs.Add(audit); documents.Add(ModelOffCanonicalSerializerV1.Serialize(audit));

        var handoffValues = BuildHandoffValues(request, outputs, documents);
        var handoffs = handoffValues.Select(ModelOffCanonicalSerializerV1.SerializeHandoff).ToArray();
        var persisted = true;
        var persistenceCode = "audit.production-cycle-persisted";
        try
        {
            var saved = await _auditStore.SaveModelOffProductionCycleAsync(outputs.Zip(documents).Select(x => (x.First, x.Second)).ToArray(), handoffValues, ct);
            persisted = saved.Succeeded; persistenceCode = saved.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { persisted = false; persistenceCode = "audit.persistence-failed"; }

        var outputMap = outputs.ToDictionary(x => x.Agent);
        var documentMap = outputs.Zip(documents).ToDictionary(x => x.First.Agent, x => x.Second);
        var eligible = cycleReady && ModelOffEligibilityV1.IsEligibleForDownstream(audit) && persisted;
        return new(outputMap, documentMap, handoffs,
            documentMap.ToDictionary(x => x.Key, x => x.Value.Sha256), eligible,
            eligible ? "model-off.production-cycle-ready" :
            persisted ? "model-off.production-cycle-blocked" : persistenceCode);
    }

    private static IReadOnlyList<string> ValidateInput(
        ModelOffAgentV1 role, ModelOffProductionCycleRequestV1 request, TimeSpan maximumAge,
        ModelOffProductionInputV1[]? matches, ModelOffCanonicalDocumentV1? previous, bool upstreamReady,
        bool inputSetValid)
    {
        var reasons = new List<string>();
        if (request.MainnetRequested) reasons.Add("production.mainnet-disabled");
        if (!inputSetValid) reasons.Add("production.input-role-unexpected");
        if (!upstreamReady) reasons.Add($"production.{Token(role)}.upstream-invalid");
        if (matches is null || matches.Length == 0) { reasons.Add($"production.{Token(role)}.missing"); return Sorted(reasons); }
        if (matches.Length != 1) { reasons.Add($"production.{Token(role)}.duplicate"); return Sorted(reasons); }
        var input = matches[0];
        if (input.Output.Agent != role) reasons.Add($"production.{Token(role)}.role-invalid");
        if (!string.Equals(input.Output.CycleId, request.CycleId, StringComparison.Ordinal)) reasons.Add($"production.{Token(role)}.cycle-invalid");
        if (!CanonicalMatches(input)) reasons.Add($"production.{Token(role)}.canonical-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(input.Output)) reasons.Add($"production.{Token(role)}.ineligible");
        if (!Fresh(input.Output.GeneratedAtUtc, request.EvaluationTimeUtc, maximumAge) ||
            !Fresh(input.Output.EvaluationTimeUtc, request.EvaluationTimeUtc, maximumAge) ||
            input.Output.Sources.Any(x => !SourceFresh(role, x, request.EvaluationTimeUtc, maximumAge)))
            reasons.Add($"production.{Token(role)}.stale");
        if (previous is not null && !input.Output.Sources.Any(x =>
                string.Equals(x.ArtifactHash, "sha256:" + previous.Sha256, StringComparison.Ordinal)))
            reasons.Add($"production.{Token(role)}.handoff-invalid");
        return Sorted(reasons);
    }

    private static bool CanonicalMatches(ModelOffProductionInputV1 input)
    {
        try
        {
            var canonical = ModelOffCanonicalSerializerV1.Serialize(input.Output);
            return input.Document.Utf8Bytes.Length > 0 &&
                CryptographicOperations.FixedTimeEquals(canonical.Utf8Bytes, input.Document.Utf8Bytes) &&
                string.Equals(canonical.Sha256, input.Document.Sha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }

    private static bool Fresh(DateTimeOffset value, DateTimeOffset now, TimeSpan maximumAge) =>
        value.Offset == TimeSpan.Zero && value <= now && now - value <= maximumAge;

    private static bool SourceFresh(ModelOffAgentV1 role, ModelOffSourceV1 source, DateTimeOffset now, TimeSpan cycleMaximumAge)
    {
        if (!source.AsOfUtc.HasValue || !source.ReceivedAtUtc.HasValue ||
            !Fresh(source.ReceivedAtUtc.Value, now, cycleMaximumAge)) return false;
        var maximumAge = role == ModelOffAgentV1.Research ? source.Kind switch
        {
            ModelOffSourceKindV1.News => MaximumResearchNewsAge,
            ModelOffSourceKindV1.Macro => MaximumResearchMacroAge,
            ModelOffSourceKindV1.Fundamental or ModelOffSourceKindV1.Strategy => MaximumResearchValidationAge,
            _ => cycleMaximumAge
        } : cycleMaximumAge;
        return Fresh(source.AsOfUtc.Value, now, maximumAge);
    }

    private static ModelOffAgentOutputV1 Derived(
        ModelOffAgentV1 role, string suffix, string action, ModelOffProductionCycleRequestV1 request,
        ModelOffAgentOutputV1 upstream, ModelOffCanonicalDocumentV1 upstreamDocument, bool ready,
        IReadOnlyList<string> reasons, object facts) =>
        Output(role, suffix, request, [Source(upstream.OutputId, request.EvaluationTimeUtc, upstreamDocument, ready)],
            ready, action, reasons, facts);

    private static ModelOffAgentOutputV1 Blocked(
        ModelOffAgentV1 role, ModelOffProductionCycleRequestV1 request, IReadOnlyList<string> reasons) =>
        Output(role, $"blocked-{Token(role)}", request,
            [new($"{request.CycleId}-{Token(role)}-input", ModelOffSourceKindV1.Audit,
                request.EvaluationTimeUtc, request.EvaluationTimeUtc, ModelOffSourceStatusV1.Invalid,
                "sha256:" + new string('0', 64))], false, "block", reasons,
            new { accepted = false, input_role = Token(role) });

    private static ModelOffAgentOutputV1 Output(
        ModelOffAgentV1 role, string suffix, ModelOffProductionCycleRequestV1 request,
        IReadOnlyList<ModelOffSourceV1> sources, bool ready, string action,
        IReadOnlyList<string> reasons, object facts) =>
        new(role, $"{request.CycleId}-production-{suffix}", request.CycleId,
            request.EvaluationTimeUtc, request.EvaluationTimeUtc, InputSchema,
            new($"wpe.production-{Token(role)}", "1.0"), sources,
            ready ? ModelOffOutputStatusV1.Succeeded : ModelOffOutputStatusV1.Blocked,
            new(ready ? ModelOffUncertaintyLevelV1.None : ModelOffUncertaintyLevelV1.Unknown,
                Sorted(reasons), ready ? [] : ["upstream_eligibility"]),
            JsonSerializer.SerializeToElement(facts), [], new(action, ready, Sorted(reasons)), [],
            ModelOffFixedTemplatesV1.SummaryVersion);

    private static ModelOffSourceV1 Source(
        string id, DateTimeOffset at, ModelOffCanonicalDocumentV1 document, bool ready) =>
        new(id, ModelOffSourceKindV1.Audit, at, at,
            ready ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Invalid,
            "sha256:" + document.Sha256);

    private static IReadOnlyList<ModelOffHandoffV1> BuildHandoffValues(
        ModelOffProductionCycleRequestV1 request, IReadOnlyList<ModelOffAgentOutputV1> outputs,
        IReadOnlyList<ModelOffCanonicalDocumentV1> documents)
    {
        var handoffs = new List<ModelOffHandoffV1>(6);
        for (var index = 0; index < OrderedRoles.Length - 1; index++)
        {
            var ready = ModelOffEligibilityV1.IsEligibleForDownstream(outputs[index]);
            handoffs.Add(new(
                $"{request.CycleId}-production-handoff-{index + 1}", request.CycleId,
                OrderedRoles[index], OrderedRoles[index + 1], outputs[index].OutputId,
                documents[index].Sha256, ready ? ModelOffHandoffStatusV1.Ready : ModelOffHandoffStatusV1.Blocked,
                ready ? ["continue"] : [], ["model_override", "direct_mutation"],
                ready ? [] : outputs[index].Decision.ReasonCodes,
                request.EvaluationTimeUtc, request.EvaluationTimeUtc.AddMinutes(5)));
        }
        return handoffs;
    }

    private static void ValidateRequest(ModelOffProductionCycleRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CycleId)) throw new ArgumentException("Cycle id is required.", nameof(request));
        if (request.EvaluationTimeUtc == default || request.EvaluationTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An injected UTC evaluation time is required.", nameof(request));
        if (request.UpstreamInputs is null) throw new ArgumentException("Upstream inputs are required.", nameof(request));
        var maximumAge = request.MaximumInputAge ?? DefaultMaximumInputAge;
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromHours(1))
            throw new ArgumentException("Maximum input age must be positive and no greater than one hour.", nameof(request));
    }

    private static string[] Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static string Token(ModelOffAgentV1 role) => role.ToString().ToLowerInvariant();
}
