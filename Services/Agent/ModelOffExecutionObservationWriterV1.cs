using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

internal sealed record ModelOffExecutionObservationResultV1(
    ModelOffAgentOutputV1 Execution,
    ModelOffAgentOutputV1 Recovery,
    ModelOffAgentOutputV1 Audit,
    IReadOnlyDictionary<ModelOffAgentV1, ModelOffCanonicalDocumentV1> Documents,
    bool Persisted,
    string Code);

/// <summary>Projects the persisted automatic execution state machine into append-only Agent observations.</summary>
internal sealed class ModelOffExecutionObservationWriterV1
{
    internal const string InputSchema = "wpe.automatic-execution-observation/1.0";
    private readonly AgentSqliteStore _store;
    internal ModelOffExecutionObservationWriterV1(AgentSqliteStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    internal async Task<ModelOffExecutionObservationResultV1> WriteAsync(
        PersistedAutomaticExecution item,
        IReadOnlyList<PersistedAutomaticExecutionEvent> events,
        DateTimeOffset evaluationTimeUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(events);
        if (evaluationTimeUtc == default || evaluationTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An injected UTC evaluation time is required.", nameof(evaluationTimeUtc));
        if (events.Count == 0 || events.Any(x => !string.Equals(x.ExecutionId, item.ExecutionId, StringComparison.Ordinal)))
            throw new ArgumentException("Execution events must be non-empty and belong to the execution.", nameof(events));

        var timeline = ModelOffExecutionRecoveryContractV1.Timeline(item.ExecutionId, events);
        var persistedIntents = await _store.GetIntentsByCycleAsync(item.Artifact?.CorrelationId ?? item.ExecutionId,100,ct);
        var correlation = Correlate(item.Artifact,persistedIntents);
        var latestSequence = timeline.Entries.Max(x => x.Sequence);
        var cycleId = item.Artifact?.CorrelationId ?? item.ExecutionId;
        var stateToken = item.Status.ToString().ToLowerInvariant();
        var identity = $"{item.ExecutionId}-observation-{latestSequence}-{stateToken}";
        var sourceStatus = item.ArtifactValid ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Invalid;
        var executionSucceeded = item.Status == AutomaticExecutionQueueStatus.Succeeded && item.ArtifactValid && correlation.Valid;
        var executionReasons = ExecutionReasons(item).Concat(
            item.Status == AutomaticExecutionQueueStatus.Succeeded ? correlation.ReasonCodes : []).ToArray();
        var execution = Output(ModelOffAgentV1.Execution, identity + "-execution", cycleId, evaluationTimeUtc,
            [new(item.ExecutionId, ModelOffSourceKindV1.Audit, item.UpdatedAtUtc, evaluationTimeUtc,
                sourceStatus, NormalizeHash(item.ArtifactHash))], executionSucceeded,
            executionSucceeded ? "execution_observed_succeeded" : "execution_observed_blocked", executionReasons,
            new { state = stateToken, item.AttemptCount, item.ArtifactValid, item.RiskReceiptValid,
                timeline_sha256 = timeline.TimelineSha256, event_count = timeline.Entries.Count,
                expected_intent_count = correlation.ExpectedCount, persisted_intent_count = correlation.PersistedCount,
                correlation_sha256 = correlation.Sha256, order_correlation_confirmed = correlation.Valid,
                mutation_performed_by_observer = false });
        var executionDocument = ModelOffCanonicalSerializerV1.Serialize(execution);

        var recoveryState = RecoveryState(item.Status,correlation.Valid);
        var terminalState = item.Status is AutomaticExecutionQueueStatus.Succeeded or
            AutomaticExecutionQueueStatus.FailedTerminal or AutomaticExecutionQueueStatus.PolicyBlocked or
            AutomaticExecutionQueueStatus.RiskBlocked or AutomaticExecutionQueueStatus.CapabilityUnavailable or
            AutomaticExecutionQueueStatus.MarketStale or AutomaticExecutionQueueStatus.ArtifactInvalid;
        var recoveryReady = terminalState && (item.Status != AutomaticExecutionQueueStatus.Succeeded || correlation.Valid);
        var quarantined = !recoveryReady;
        var recoveryReasons = recoveryReady ? Array.Empty<string>() : ["recovery.execution-state-unresolved"];
        var recovery = Output(ModelOffAgentV1.Recovery, identity + "-recovery", cycleId, evaluationTimeUtc,
            [Source(execution.OutputId, executionDocument, evaluationTimeUtc,
                recoveryReady ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Unknown)],
            recoveryReady, recoveryState, recoveryReasons,
            new { state = recoveryState, quarantined, resubmit_allowed = false,
                reconciliation_required = quarantined, mutation_performed_by_observer = false });
        var recoveryDocument = ModelOffCanonicalSerializerV1.Serialize(recovery);

        var auditReady = executionSucceeded && recoveryReady;
        var auditReasons = auditReady ? Array.Empty<string>() : ["audit.execution-not-successful"];
        var audit = Output(ModelOffAgentV1.Audit, identity + "-audit", cycleId, evaluationTimeUtc,
            [Source(execution.OutputId, executionDocument, evaluationTimeUtc,
                 executionSucceeded ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Invalid),
             Source(recovery.OutputId, recoveryDocument, evaluationTimeUtc,
                 recoveryReady ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Unknown)],
            auditReady, auditReady ? "record_execution_success" : "record_execution_exception",
            auditReasons, new { timeline_sha256 = timeline.TimelineSha256,
                execution_sha256 = executionDocument.Sha256, recovery_sha256 = recoveryDocument.Sha256,
                latest_sequence = latestSequence, observed_state = stateToken });
        var auditDocument = ModelOffCanonicalSerializerV1.Serialize(audit);

        var documents = new Dictionary<ModelOffAgentV1, ModelOffCanonicalDocumentV1>
        {
            [ModelOffAgentV1.Execution] = executionDocument,
            [ModelOffAgentV1.Recovery] = recoveryDocument,
            [ModelOffAgentV1.Audit] = auditDocument
        };
        foreach (var pair in new[] { (execution, executionDocument), (recovery, recoveryDocument), (audit, auditDocument) })
        {
            var saved = await _store.SaveModelOffCanonicalAuditAsync(pair.Item1, pair.Item2, ct);
            if (!saved.Succeeded) return new(execution, recovery, audit, documents, false, saved.Code);
        }
        return new(execution, recovery, audit, documents, true, "execution-observation.persisted");
    }

    private static ModelOffAgentOutputV1 Output(
        ModelOffAgentV1 role, string outputId, string cycleId, DateTimeOffset at,
        IReadOnlyList<ModelOffSourceV1> sources, bool ready, string action,
        IReadOnlyList<string> reasons, object facts) =>
        new(role, outputId, cycleId, at, at, InputSchema,
            new($"wpe.execution-observation-{role.ToString().ToLowerInvariant()}", "1.0"), sources,
            ready ? ModelOffOutputStatusV1.Succeeded : ModelOffOutputStatusV1.Blocked,
            new(ready ? ModelOffUncertaintyLevelV1.None : ModelOffUncertaintyLevelV1.Unknown,
                Sorted(reasons), ready ? [] : ["execution_state"]),
            JsonSerializer.SerializeToElement(facts), [], new(action, ready, Sorted(reasons)), [],
            ModelOffFixedTemplatesV1.SummaryVersion);

    private static ModelOffSourceV1 Source(
        string id, ModelOffCanonicalDocumentV1 document, DateTimeOffset at, ModelOffSourceStatusV1 status) =>
        new(id, ModelOffSourceKindV1.Audit, at, at, status, "sha256:" + document.Sha256);

    private static string[] ExecutionReasons(PersistedAutomaticExecution item)
    {
        if (!item.ArtifactValid) return ["execution.artifact-invalid"];
        if (!item.RiskReceiptValid && item.Status != AutomaticExecutionQueueStatus.Proposed)
            return ["execution.risk-receipt-invalid"];
        return item.Status switch
        {
            AutomaticExecutionQueueStatus.Succeeded => [],
            AutomaticExecutionQueueStatus.UnknownOutcome => ["execution.outcome-unknown"],
            AutomaticExecutionQueueStatus.MarketStale => ["execution.market-stale"],
            AutomaticExecutionQueueStatus.CapabilityUnavailable => ["execution.capability-unavailable"],
            AutomaticExecutionQueueStatus.RiskBlocked => ["execution.risk-blocked"],
            AutomaticExecutionQueueStatus.PolicyBlocked => ["execution.policy-blocked"],
            AutomaticExecutionQueueStatus.FailedTerminal => ["execution.failed-terminal"],
            _ => ["execution.not-terminal"]
        };
    }

    private static string RecoveryState(AutomaticExecutionQueueStatus status,bool correlationValid) =>
        status==AutomaticExecutionQueueStatus.Succeeded&&!correlationValid?"quarantine_order_correlation":status switch
    {
        AutomaticExecutionQueueStatus.Succeeded => "no_recovery_required",
        AutomaticExecutionQueueStatus.UnknownOutcome or AutomaticExecutionQueueStatus.Executing or
            AutomaticExecutionQueueStatus.Reconciling => "quarantine_pending_reconciliation",
        _ => "terminal_no_resubmit"
    };

    private sealed record CorrelationResult(bool Valid,int ExpectedCount,int PersistedCount,string Sha256,IReadOnlyList<string> ReasonCodes);

    private static CorrelationResult Correlate(DurableExecutionArtifactV2? artifact,IReadOnlyList<PersistedIntent> persisted)
    {
        var reasons=new List<string>();
        if(artifact is null)return new(false,0,persisted.Count,HashCorrelation(Array.Empty<object>()),["execution.artifact-invalid"]);
        var expected=artifact.Intents.OrderBy(x=>x.Sequence).ToArray();
        if(expected.Length==0)reasons.Add("execution.intent-missing");
        if(expected.Select(x=>x.ClientOrderId).Distinct(StringComparer.Ordinal).Count()!=expected.Length)reasons.Add("execution.intent-duplicate");
        var rows=persisted.GroupBy(x=>x.Intent.ClientOrderId,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.ToArray(),StringComparer.Ordinal);
        foreach(var intent in expected)
        {
            if(!rows.TryGetValue(intent.ClientOrderId,out var matches)||matches.Length!=1){reasons.Add("execution.intent-missing");continue;}
            var row=matches[0];
            if(!string.Equals(row.Intent.Symbol,intent.Symbol,StringComparison.Ordinal)||
               !string.Equals(row.Intent.Side.ToString(),intent.Side,StringComparison.Ordinal)||
               row.Intent.Quantity!=intent.Quantity||row.Intent.ReduceOnly!=intent.ReduceOnly)
                reasons.Add("execution.intent-conflicting");
            var expectedStatus=intent.ReduceOnly?"COMPLETED":"PROTECTED";
            if(!string.Equals(row.Status,expectedStatus,StringComparison.Ordinal)||string.IsNullOrWhiteSpace(row.ExchangeOrderId))
                reasons.Add("execution.intent-unconfirmed");
        }
        if(persisted.Count!=expected.Length)reasons.Add("execution.intent-count-conflicting");
        var projection=persisted.OrderBy(x=>x.Intent.ClientOrderId,StringComparer.Ordinal).Select(x=>new
        {x.Intent.ClientOrderId,x.Intent.Symbol,Side=x.Intent.Side.ToString(),x.Intent.Quantity,x.Intent.ReduceOnly,x.Status,HasOrderId=!string.IsNullOrWhiteSpace(x.ExchangeOrderId)}).ToArray();
        return new(reasons.Count==0,expected.Length,persisted.Count,HashCorrelation(projection),Sorted(reasons));
    }

    private static string HashCorrelation(object value)=>"sha256:"+Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static string NormalizeHash(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) ? value : "sha256:" + value;
    private static string[] Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
