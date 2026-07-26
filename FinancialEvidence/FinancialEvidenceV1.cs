using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace WpeAgent.FinancialEvidence;

public enum FinancialEvidenceCollectionTypeV1 { RawIntake, QuarantinedMaterial, ApprovedFact, SpecialistAnalysis, StrategyBacktestArtifact, LessonOrInstrumentIdea, Decision, Outcome, Correction, CapabilityEvaluation }
public enum LifecycleStateV1 { Unapproved, Approved, Withdrawn }
public enum CustodyStateV1 { Quarantined, Released }
public enum ConflictStateV1 { Clear, Conflicted, Resolved }
public enum EntitlementStateV1 { Unknown, Unentitled, Entitled }
public enum SupersessionStateV1 { Current, Superseded }
public enum FinancialEvidenceRetrievalOutcomeV1 { Returned, Empty, Rejected, Error }
public enum FinancialEvidenceEntitlementAuthorityStateV1 { Entitled, Unentitled, Unknown, Error }

public sealed record FinancialEvidenceEntitlementAuthorityResultV1(FinancialEvidenceEntitlementAuthorityStateV1 State, string ReasonCode);

public interface IFinancialEvidenceEntitlementAuthorityV1
{
    Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(
        FinancialEvidenceRecordV1 record,
        FinancialEvidenceRetrievalRequestV1 request,
        CancellationToken cancellationToken);
}

public sealed record FinancialEvidenceRecordRefV1(string RecordId, string ContentHash, string RecordHash, string RecordVersion);

public sealed record FinancialEvidenceRecordDraftV1(
    string RecordId,
    string RecordVersion,
    FinancialEvidenceCollectionTypeV1 CollectionType,
    string SchemaId,
    string SchemaVersion,
    string PayloadMediaType,
    byte[] Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset ObservedAt,
    DateTimeOffset EffectiveAt,
    DateTimeOffset? ExpiresAt,
    string Timezone,
    string SourceProvider,
    string SourceUriOrDatasetId,
    string Publisher,
    string EntitlementReference,
    string LicenseReference,
    string ExtractionMethod,
    string ExtractionVersion,
    string InstrumentId,
    string Symbol,
    string InstrumentFullName,
    string AssetClass,
    string Venue,
    string Currency,
    string AuthoringAgent,
    string MethodVersion,
    string ModelVersion,
    string RuleVersion,
    IReadOnlyList<FinancialEvidenceRecordRefV1> InputRecordRefs,
    decimal Confidence,
    string Limitations,
    IReadOnlyList<FinancialEvidenceRecordRefV1> ContradictionRefs,
    LifecycleStateV1 LifecycleState,
    CustodyStateV1 CustodyState,
    ConflictStateV1 ConflictState,
    EntitlementStateV1 EntitlementState,
    SupersessionStateV1 SupersessionState,
    string TraceId,
    string CorrelationId,
    IReadOnlyList<FinancialEvidenceRecordRefV1> SupersedesRecordRefs,
    IReadOnlyList<FinancialEvidenceRecordRefV1> SupersededByRecordRefs,
    FinancialEvidenceRecordRefV1? WithdrawalRecordRef,
    IReadOnlyList<FinancialEvidenceRecordRefV1> ConflictResolutionRecordRefs,
    DateTimeOffset RecordedAt,
    string RecordedBy);

public sealed record FinancialEvidenceRecordV1
{
    public const int MaxPayloadBytes = 1_048_576;
    public const int MaxReferences = 128;
    public const int MaxTraversalDepth = 64;
    public const int MaxCandidates = 100;
    public const int MaxAuditBytes = 65_536;
    public const string CanonicalizerVersion = "wpe-financial-evidence-c14n-v1";

    public required FinancialEvidenceRecordDraftV1 Draft { get; init; }
    public required string ContentHash { get; init; }
    public required string RecordHash { get; init; }
    public string RecordId => Draft.RecordId;
    public string RecordVersion => Draft.RecordVersion;
    public string SchemaId => Draft.SchemaId;
    public string SchemaVersion => Draft.SchemaVersion;
    public DateTimeOffset ObservedAt => Draft.ObservedAt;
    public string TraceId => Draft.TraceId;

    public FinancialEvidenceRecordRefV1 ExactRef() => new(RecordId, ContentHash, RecordHash, RecordVersion);

    public static FinancialEvidenceRecordV1 Create(FinancialEvidenceRecordDraftV1 draft)
    {
        ValidateDraft(draft);
        var canonicalPayload = FinancialEvidenceCanonicalizerV1.CanonicalizeJson(draft.Payload);
        if (!canonicalPayload.AsSpan().SequenceEqual(draft.Payload))
            throw new FinancialEvidenceValidationException("evidence.payload-noncanonical");
        var contentHash = FinancialEvidenceCanonicalizerV1.Hash(canonicalPayload);
        var recordHash = FinancialEvidenceCanonicalizerV1.Hash(FinancialEvidenceCanonicalizerV1.CanonicalEnvelope(draft, contentHash));
        return new() { Draft = draft with { Payload = canonicalPayload }, ContentHash = contentHash, RecordHash = recordHash };
    }

    public void VerifyIntegrity()
    {
        ValidateDraft(Draft);
        FinancialEvidenceCanonicalizerV1.ValidateHash(ContentHash);
        FinancialEvidenceCanonicalizerV1.ValidateHash(RecordHash);
        var canonicalPayload = FinancialEvidenceCanonicalizerV1.CanonicalizeJson(Draft.Payload);
        if (!canonicalPayload.AsSpan().SequenceEqual(Draft.Payload) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ContentHash), Encoding.ASCII.GetBytes(FinancialEvidenceCanonicalizerV1.Hash(canonicalPayload))))
            throw new FinancialEvidenceValidationException("evidence.integrity-failed");
        var computed = FinancialEvidenceCanonicalizerV1.Hash(FinancialEvidenceCanonicalizerV1.CanonicalEnvelope(Draft, ContentHash));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(RecordHash), Encoding.ASCII.GetBytes(computed)))
            throw new FinancialEvidenceValidationException("evidence.integrity-failed");
    }

    internal static void ValidateDraft(FinancialEvidenceRecordDraftV1 d)
    {
        if (!Enum.IsDefined(d.CollectionType) || !Enum.IsDefined(d.LifecycleState) || !Enum.IsDefined(d.CustodyState) || !Enum.IsDefined(d.ConflictState) || !Enum.IsDefined(d.EntitlementState) || !Enum.IsDefined(d.SupersessionState))
            throw new FinancialEvidenceValidationException("evidence.schema-incompatible");
        var mandatory = new[] { d.RecordId, d.RecordVersion, d.SchemaId, d.SchemaVersion, d.PayloadMediaType, d.Timezone, d.SourceProvider, d.SourceUriOrDatasetId, d.Publisher, d.EntitlementReference, d.LicenseReference, d.ExtractionMethod, d.ExtractionVersion, d.InstrumentId, d.Symbol, d.InstrumentFullName, d.AssetClass, d.Venue, d.Currency, d.AuthoringAgent, d.MethodVersion, d.RuleVersion, d.TraceId, d.CorrelationId, d.RecordedBy };
        if (mandatory.Any(string.IsNullOrWhiteSpace) || d.Payload is null || d.Payload.Length == 0 || d.Payload.Length > MaxPayloadBytes || d.Confidence is < 0 or > 1)
            throw new FinancialEvidenceValidationException("evidence.mandatory-field-missing");
        if (!string.Equals(d.SchemaId, "wpe.financial-evidence", StringComparison.Ordinal) || !string.Equals(d.SchemaVersion, "1", StringComparison.Ordinal) || !string.Equals(d.PayloadMediaType, "application/json", StringComparison.Ordinal))
            throw new FinancialEvidenceValidationException("evidence.schema-incompatible");
        if (d.CreatedAt == default || d.ObservedAt == default || d.EffectiveAt == default || d.RecordedAt == default || d.CreatedAt.Offset != TimeSpan.Zero || d.ObservedAt.Offset != TimeSpan.Zero || d.EffectiveAt.Offset != TimeSpan.Zero || d.RecordedAt.Offset != TimeSpan.Zero || (d.ExpiresAt is { } expires && expires.Offset != TimeSpan.Zero) || d.Timezone != "UTC")
            throw new FinancialEvidenceValidationException("evidence.invalid-time");
        var refs = d.InputRecordRefs.Concat(d.ContradictionRefs).Concat(d.SupersedesRecordRefs).Concat(d.SupersededByRecordRefs).Concat(d.ConflictResolutionRecordRefs).Append(d.WithdrawalRecordRef).Where(x => x is not null).Cast<FinancialEvidenceRecordRefV1>().ToArray();
        if (refs.Length > MaxReferences || refs.Any(r => string.IsNullOrWhiteSpace(r.RecordId) || string.IsNullOrWhiteSpace(r.RecordVersion)))
            throw new FinancialEvidenceValidationException("evidence.reference-invalid");
        foreach (var reference in refs) { FinancialEvidenceCanonicalizerV1.ValidateHash(reference.ContentHash); FinancialEvidenceCanonicalizerV1.ValidateHash(reference.RecordHash); }
    }
}

public sealed record FinancialEvidenceRetrievalRequestV1(
    string ConsumerId,
    string ConsumerCorrelationId,
    string Purpose,
    string Jurisdiction,
    IReadOnlySet<FinancialEvidenceCollectionTypeV1> CollectionTypes,
    string InstrumentId,
    string Venue,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset EffectiveTo,
    DateTimeOffset AsOf,
    TimeSpan MaximumAge,
    IReadOnlySet<string> CompatibleSchemaVersions,
    string EntitlementScope,
    string CallerTraceId,
    int MaximumResults = 100);

public sealed record FinancialEvidenceEligibilityV1(bool Eligible, IReadOnlyList<string> ReasonCodes);
public sealed record AuthorizedLocalCorpusResultV1(bool Accepted, IReadOnlyList<FinancialEvidenceRecordV1> Records, IReadOnlyList<string> ReasonCodes);
public sealed record AuthorizedFixtureProjectionV1<T>(bool Accepted, IReadOnlyList<T> Items, IReadOnlyList<string> ReasonCodes);
public sealed record FinancialEvidenceRetrievalResultV1(FinancialEvidenceRetrievalOutcomeV1 Outcome, IReadOnlyList<FinancialEvidenceRecordV1> Records, FinancialEvidenceRetrievalAuditV1? Audit, IReadOnlyList<string> ReasonCodes);

public sealed record FinancialEvidenceRetrievalAuditV1(
    string AuditId,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    string GateVersion,
    string SchemaVersion,
    string RuleVersion,
    string ConsumerId,
    string ConsumerCorrelationId,
    string Purpose,
    string Jurisdiction,
    string CallerTraceId,
    string RequestHash,
    DateTimeOffset AsOf,
    TimeSpan MaximumAge,
    int CandidateCount,
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyList<FinancialEvidenceRecordRefV1> ReturnedRecordRefs,
    FinancialEvidenceRetrievalOutcomeV1 Outcome,
    IReadOnlyList<string> ReasonCodes);

public sealed class FinancialEvidenceRetrievalGateV1
{
    private long _invocations;
    public long InvocationCount => Interlocked.Read(ref _invocations);

    public FinancialEvidenceEligibilityV1 Evaluate(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request)
    {
        Interlocked.Increment(ref _invocations);
        var reasons = new List<string>();
        try { record.VerifyIntegrity(); } catch (FinancialEvidenceValidationException) { reasons.Add("evidence.integrity-failed"); }
        ValidateRequest(request, reasons);
        var d = record.Draft;
        if (d.CustodyState != CustodyStateV1.Released) reasons.Add("evidence.quarantined");
        if (d.LifecycleState == LifecycleStateV1.Withdrawn) reasons.Add("evidence.withdrawn"); else if (d.LifecycleState != LifecycleStateV1.Approved) reasons.Add("evidence.unapproved");
        if (d.ConflictState == ConflictStateV1.Conflicted || (d.ConflictState == ConflictStateV1.Resolved && d.ConflictResolutionRecordRefs.Count == 0)) reasons.Add("evidence.conflicted");
        if (d.EntitlementState != EntitlementStateV1.Entitled || !string.Equals(d.EntitlementReference, request.EntitlementScope, StringComparison.Ordinal)) reasons.Add("evidence.unentitled");
        if (d.SupersessionState != SupersessionStateV1.Current) reasons.Add("evidence.superseded");
        if (!request.CompatibleSchemaVersions.Contains(d.SchemaVersion) || d.SchemaId != "wpe.financial-evidence") reasons.Add("evidence.schema-incompatible");
        if (d.ExpiresAt is null || request.AsOf >= d.ExpiresAt || request.AsOf < d.ObservedAt || request.AsOf - d.ObservedAt > request.MaximumAge) reasons.Add("evidence.stale");
        if (!request.CollectionTypes.Contains(d.CollectionType) || d.InstrumentId != request.InstrumentId || d.Venue != request.Venue || d.EffectiveAt < request.EffectiveFrom || d.EffectiveAt > request.EffectiveTo) reasons.Add("evidence.scope-mismatch");
        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static void ValidateRequest(FinancialEvidenceRetrievalRequestV1 r, List<string>? reasons = null)
    {
        void Invalid(string code) { if (reasons is null) throw new FinancialEvidenceValidationException(code); reasons.Add(code); }
        if (new[] { r.ConsumerId, r.ConsumerCorrelationId, r.Purpose, r.Jurisdiction, r.InstrumentId, r.Venue, r.EntitlementScope, r.CallerTraceId }.Any(string.IsNullOrWhiteSpace)) Invalid("evidence.scope-mismatch");
        if (r.AsOf == default || r.AsOf.Offset != TimeSpan.Zero || r.EffectiveFrom == default || r.EffectiveTo == default || r.EffectiveFrom.Offset != TimeSpan.Zero || r.EffectiveTo.Offset != TimeSpan.Zero || r.EffectiveFrom > r.EffectiveTo || r.MaximumAge <= TimeSpan.Zero) Invalid("evidence.stale");
        try { _ = r.AsOf - r.MaximumAge; }
        catch (ArgumentOutOfRangeException) { Invalid("evidence.stale"); }
        if (r.CollectionTypes is null || r.CollectionTypes.Count == 0 || r.CollectionTypes.Any(x => !Enum.IsDefined(x)) || r.CompatibleSchemaVersions is null || r.CompatibleSchemaVersions.Count == 0 || r.CompatibleSchemaVersions.Any(string.IsNullOrWhiteSpace) || r.MaximumResults is < 1 or > FinancialEvidenceRecordV1.MaxCandidates) Invalid("evidence.schema-incompatible");
    }
}

public sealed class AuthorizedLocalCorpusV1(FinancialEvidenceRetrievalGateV1? gate = null)
{
    private readonly FinancialEvidenceRetrievalGateV1 _gate = gate ?? new();

    public AuthorizedLocalCorpusResultV1 Collect(IReadOnlyList<FinancialEvidenceRecordV1>? fixtures, FinancialEvidenceRetrievalRequestV1 request)
    {
        if (fixtures is null || fixtures.Count == 0) return new(false, [], ["evidence.corpus-empty"]);
        if (fixtures.Count > FinancialEvidenceRecordV1.MaxCandidates) return new(false, [], ["evidence.candidate-limit"]);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var accepted = new Dictionary<string, FinancialEvidenceRecordV1>(StringComparer.Ordinal);
        foreach (var record in fixtures)
        {
            if (record is null) { reasons.Add("evidence.payload-malformed"); continue; }
            var eligibility = _gate.Evaluate(record, request);
            foreach (var reason in eligibility.ReasonCodes) reasons.Add(reason);
            if (!eligibility.Eligible) continue;
            if (ids.TryGetValue(record.RecordId, out var hash) && hash != record.RecordHash) reasons.Add("evidence.corpus-conflict");
            else { ids[record.RecordId] = record.RecordHash; accepted.TryAdd(record.RecordHash, record); }
        }
        return reasons.Count > 0
            ? new(false, [], reasons.Order(StringComparer.Ordinal).ToArray())
            : new(true, accepted.Values.OrderBy(x => x.RecordId, StringComparer.Ordinal).ThenBy(x => x.RecordVersion, StringComparer.Ordinal).ToArray(), []);
    }
}

public static class FinancialEvidenceCanonicalizerV1
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    public static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 71 || !hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new FinancialEvidenceValidationException("evidence.integrity-failed");
    }
    public static byte[] CanonicalizeJson(ReadOnlySpan<byte> input)
    {
        try
        {
            using var document = JsonDocument.Parse(input.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
            WriteElement(document.RootElement, writer); writer.Flush(); return stream.ToArray();
        }
        catch (JsonException ex) { throw new FinancialEvidenceValidationException("evidence.payload-malformed", ex); }
    }
    private static void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject(); var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { if (!names.Add(p.Name)) throw new FinancialEvidenceValidationException("evidence.payload-duplicate-key"); writer.WritePropertyName(p.Name); WriteElement(p.Value, writer); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteElement(item, writer); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(element.GetRawText(), skipInputValidation: false); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new FinancialEvidenceValidationException("evidence.payload-malformed");
        }
    }
    internal static byte[] CanonicalEnvelope(FinancialEvidenceRecordDraftV1 draft, string contentHash)
    {
        // The forward hash chain is authoritative. Reverse links are derived and checked
        // against the append-only link index, avoiding an impossible circular hash dependency.
        var hashableDraft = draft with { SupersededByRecordRefs = [] };
        return JsonSerializer.SerializeToUtf8Bytes(new { canonicalizer = FinancialEvidenceRecordV1.CanonicalizerVersion, draft = hashableDraft, contentHash }, JsonOptions);
    }
    internal static byte[] SerializeRecord(FinancialEvidenceRecordV1 record) => JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
    internal static FinancialEvidenceRecordV1 DeserializeRecord(byte[] bytes) => JsonSerializer.Deserialize<FinancialEvidenceRecordV1>(bytes, JsonOptions) ?? throw new FinancialEvidenceValidationException("evidence.serialization-failed");
    internal static string RequestHash(FinancialEvidenceRetrievalRequestV1 request) => Hash(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions));
}

public sealed class FinancialEvidenceValidationException : Exception
{
    public FinancialEvidenceValidationException(string reasonCode, Exception? inner = null) : base(reasonCode, inner) => ReasonCode = reasonCode;
    public string ReasonCode { get; }
}
