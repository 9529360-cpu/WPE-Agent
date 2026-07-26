namespace WpeAgent.ProfessionalDistribution;

public enum DistributionChannel
{
    Teacher,
    Course,
    Crm,
    Telegram,
    WhatsApp
}

public enum DistributionRiskLevel
{
    L0Information,
    L1MarketObservation,
    L2TradingSuggestion,
    L3HighRiskAction
}

public enum DistributionFactStatus
{
    Confirmed,
    Unknown,
    Stale,
    Unsupported,
    Error
}

public sealed record DistributionFactReference(
    string ArtifactId,
    string ArtifactType,
    string TraceId,
    DateTime ObservedAtUtc,
    DateTime ExpiresAtUtc,
    DistributionFactStatus Status);

public sealed record HumanReviewEvidence(
    string ReviewId,
    string ReviewerId,
    string ArtifactId,
    string ContentVersion,
    string TraceId,
    DateTime ReviewedAtUtc,
    bool Approved);

public sealed record ClientConsentEvidence(
    string ConsentId,
    string ClientId,
    string Channel,
    DateTime GrantedAtUtc,
    DateTime? ExpiresAtUtc,
    bool PersonalizedContentAllowed,
    bool Revoked = false);

public sealed record DistributionAuditEvidence(
    string AuditId,
    string RequestId,
    string ArtifactId,
    string ContentVersion,
    string TraceId,
    string ActorId,
    DateTime RecordedAtUtc);

public sealed record SuitabilityEvidence(
    string AssessmentId,
    string ClientId,
    string ArtifactId,
    string ContentVersion,
    DateTime AssessedAtUtc,
    DateTime ExpiresAtUtc,
    bool Approved);

public sealed record ConsentWithdrawalEvidence(
    string ConsentId,
    string ClientId,
    string TraceId,
    string AuditId,
    DateTime WithdrawnAtUtc);

public sealed record WithdrawalControl(
    string WithdrawalId,
    string ArtifactId,
    string ContentVersion,
    bool Enabled);

public sealed record DistributionVersionBinding(
    string PolicyHash,
    string ConsentScopeHash,
    string ConsentVersion,
    string SuitabilityVersion);

public sealed record DistributionRequest(
    string RequestId,
    string ArtifactId,
    string ContentVersion,
    string TraceId,
    DistributionChannel Channel,
    DistributionRiskLevel RiskLevel,
    IReadOnlyList<DistributionFactReference> Facts,
    bool SendRequested = false,
    string? ClientId = null,
    HumanReviewEvidence? HumanReview = null,
    ClientConsentEvidence? ClientConsent = null,
    DistributionAuditEvidence? Audit = null,
    WithdrawalControl? Withdrawal = null,
    HumanReviewEvidence? SecondaryHumanReview = null,
    SuitabilityEvidence? Suitability = null,
    DistributionVersionBinding? VersionBinding = null);

public enum DistributionRequestState
{
    Denied,
    Authorized,
    Withdrawn,
    DuplicateRejected
}

public sealed record DistributionDecision(
    bool Allowed,
    IReadOnlyList<string> ReasonCodes,
    DistributionRequestState State = DistributionRequestState.Denied)
{
    public static DistributionDecision Denied(params string[] reasonCodes) => new(false, reasonCodes);
    public static DistributionDecision Approved() =>
        new(true, Array.Empty<string>(), DistributionRequestState.Authorized);
}
