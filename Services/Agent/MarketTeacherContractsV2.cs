using System.Text.Json.Serialization;

namespace 币安量化机器人.Services.Agent;

public enum TeacherLessonKindV2 { Morning,Afternoon,Evening,Event }
public enum TeacherEvidenceAvailabilityV2 { Available,Stale,Unknown,Unsupported,Conflicting,Invalid,Error }
public enum TeacherReportBlockKindV2 { Conclusion,CitedFact,AgentView,Hypothesis,Counterargument,Recommendation,Risk,Unknown,Correction,Provenance }
public enum TeacherRecommendationStateV2 { ResearchCandidate,PriorityWatch,WaitForConfirmation,AvoidHighRisk,Expired,Unavailable }
public enum TeacherMemoryTierV2 { Working,Episodic,LongTerm }

public sealed record MarketTeacherPersonaV2(string Version,string RoleName,int PersonaAge,int ExperienceYears,IReadOnlyList<string> Traits,IReadOnlyList<string> Boundaries)
{
    public static MarketTeacherPersonaV2 Default { get; }=new("wpe.market-teacher-persona/2.0","资深跨市场金融教授",68,45,
        ["evidence-first","plain-spoken","calm","patient","candid","historically-informed"],
        ["no-order-authority","no-strategy-mutation","no-risk-approval","no-secret-access","no-provider-configuration","no-quiz-or-user-scoring"]);
}

public sealed record TeacherEvidenceReferenceV2(string Agent,string OutputId,string CycleId,string CanonicalSha256,DateTimeOffset AsOfUtc,TeacherEvidenceAvailabilityV2 Availability);
public sealed record TeacherEvidenceEnvelopeV2(string Identity,string Category,DateTimeOffset EvaluatedAtUtc,IReadOnlyList<TeacherEvidenceReferenceV2> AgentReferences,TeacherEvidenceAvailabilityV2 Availability);
public sealed record TeacherCausalLinkV2(string From,string To,string Strength,IReadOnlyList<string> SupportingEvidence,IReadOnlyList<string> ContradictingEvidence,IReadOnlyList<string> MissingEvidence);
public sealed record TeacherCausalExplanationV2(IReadOnlyList<string> ConfirmedFacts,IReadOnlyList<string> ObservedMarketReaction,IReadOnlyList<TeacherCausalLinkV2> Hypotheses,IReadOnlyList<string> AlternativeHypotheses,IReadOnlyList<string> Uncertainties);
public sealed record TeacherReportBlockV2(string BlockId,TeacherReportBlockKindV2 Kind,string Heading,string Content,IReadOnlyList<string> EvidenceHashes,string Sha256);

public sealed record TeacherLessonV2(string LessonId,string Schema,TeacherLessonKindV2 Kind,string TimeZoneId,DateTimeOffset ScheduledForUtc,DateTimeOffset GeneratedAtUtc,string Language,string TeachingLevel,string PersonaVersion,IReadOnlyList<TeacherEvidenceReferenceV2> Evidence,IReadOnlyList<TeacherReportBlockV2> Blocks,string ContentSha256,bool ExecutionAuthority=false);

public sealed record TeacherRecommendationV2(string RecommendationId,string Schema,int Version,string Instrument,string AssetClass,TeacherRecommendationStateV2 State,string Horizon,DateTimeOffset IssuedAtUtc,DateTimeOffset ExpiresAtUtc,string Thesis,IReadOnlyList<string> EvidenceHashes,IReadOnlyList<string> ConfirmationConditions,IReadOnlyList<string> InvalidationConditions,IReadOnlyList<string> MaterialRisks,string? SupersedesId,bool ExecutionAuthority=false);
public sealed record TeacherCorrectionV2(string CorrectionId,string Schema,string SupersededLessonId,string ReasonCode,DateTimeOffset IssuedAtUtc,IReadOnlyList<TeacherReportBlockV2> ReplacementBlocks,string Sha256);
public sealed record TeacherDeliveryStateV2(string DeliveryId,string LessonId,string DestinationKind,string State,int Attempt,DateTimeOffset RecordedAtUtc,string ReasonCode);
public sealed record TeacherMemoryV2(string MemoryId,TeacherMemoryTierV2 Tier,DateTimeOffset RecordedAtUtc,DateTimeOffset? ExpiresAtUtc,string Kind,string CanonicalReference,string SummaryHash);

public sealed record TeacherPersistenceResult(bool Succeeded,bool Idempotent,string Code);
