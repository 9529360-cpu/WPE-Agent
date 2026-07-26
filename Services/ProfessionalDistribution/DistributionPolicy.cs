namespace WpeAgent.ProfessionalDistribution;

/// <summary>Pure, fail-closed authorization policy. It never sends or calls a provider.</summary>
public static class DistributionPolicy
{
    public static DistributionDecision Evaluate(DistributionRequest? request, DateTime nowUtc)
    {
        if (request is null) return DistributionDecision.Denied("REQUEST_MISSING");

        var reasons = new List<string>();
        if (!request.SendRequested) reasons.Add("SEND_NOT_REQUESTED");
        if (string.IsNullOrWhiteSpace(request.RequestId)) reasons.Add("REQUEST_ID_MISSING");
        if (string.IsNullOrWhiteSpace(request.ArtifactId)) reasons.Add("ARTIFACT_ID_MISSING");
        if (string.IsNullOrWhiteSpace(request.ContentVersion)) reasons.Add("CONTENT_VERSION_MISSING");
        if (string.IsNullOrWhiteSpace(request.TraceId)) reasons.Add("TRACE_ID_MISSING");

        if (request.Facts is null || request.Facts.Count == 0)
        {
            reasons.Add("FACT_REFERENCES_MISSING");
        }
        else
        {
            foreach (var fact in request.Facts)
            {
                if (fact is null || string.IsNullOrWhiteSpace(fact.ArtifactId) ||
                    string.IsNullOrWhiteSpace(fact.ArtifactType) || string.IsNullOrWhiteSpace(fact.TraceId))
                {
                    reasons.Add("FACT_REFERENCE_INVALID");
                    continue;
                }

                if (!string.Equals(fact.TraceId, request.TraceId, StringComparison.Ordinal))
                    reasons.Add("FACT_TRACE_MISMATCH");
                if (fact.Status != DistributionFactStatus.Confirmed)
                    reasons.Add($"FACT_{fact.Status.ToString().ToUpperInvariant()}");
                if (fact.ObservedAtUtc > nowUtc || fact.ExpiresAtUtc <= nowUtc || fact.ExpiresAtUtc < fact.ObservedAtUtc)
                    reasons.Add("FACT_STALE");
            }
        }

        if (request.RiskLevel >= DistributionRiskLevel.L2TradingSuggestion)
            EvaluateHighRisk(request, nowUtc, reasons);

        return reasons.Count == 0
            ? DistributionDecision.Approved()
            : DistributionDecision.Denied(reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void EvaluateHighRisk(DistributionRequest request, DateTime nowUtc, List<string> reasons)
    {
        if (!DistributionVersionBindingPolicy.IsKnown(request.VersionBinding))
            reasons.Add("VERSION_BINDING_REQUIRED");

        var review = request.HumanReview;
        if (!ValidReview(review, request, nowUtc))
            reasons.Add("HUMAN_REVIEW_REQUIRED");

        var secondaryReview = request.SecondaryHumanReview;
        if (!ValidReview(secondaryReview, request, nowUtc) ||
            string.Equals(review?.ReviewerId, secondaryReview?.ReviewerId, StringComparison.Ordinal) ||
            string.Equals(review?.ReviewId, secondaryReview?.ReviewId, StringComparison.Ordinal))
            reasons.Add("INDEPENDENT_SECOND_REVIEW_REQUIRED");

        var consent = request.ClientConsent;
        if (string.IsNullOrWhiteSpace(request.ClientId) || consent is null || consent.Revoked ||
            !string.Equals(consent.ClientId, request.ClientId, StringComparison.Ordinal) ||
            !string.Equals(consent.Channel, request.Channel.ToString(), StringComparison.Ordinal) ||
            consent.GrantedAtUtc > nowUtc || consent.ExpiresAtUtc <= nowUtc || !consent.PersonalizedContentAllowed)
            reasons.Add("CLIENT_CONSENT_REQUIRED");

        var audit = request.Audit;
        if (audit is null || string.IsNullOrWhiteSpace(audit.AuditId) || string.IsNullOrWhiteSpace(audit.ActorId) ||
            audit.RecordedAtUtc > nowUtc || !Matches(audit.ArtifactId, audit.ContentVersion, request) ||
            !string.Equals(audit.RequestId, request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(audit.TraceId, request.TraceId, StringComparison.Ordinal))
            reasons.Add("AUDIT_EVIDENCE_REQUIRED");

        var withdrawal = request.Withdrawal;
        if (withdrawal is null || !withdrawal.Enabled || string.IsNullOrWhiteSpace(withdrawal.WithdrawalId) ||
            !Matches(withdrawal.ArtifactId, withdrawal.ContentVersion, request))
            reasons.Add("WITHDRAWAL_CONTROL_REQUIRED");

        var suitability = request.Suitability;
        if (suitability is null || !suitability.Approved || suitability.AssessedAtUtc > nowUtc ||
            suitability.ExpiresAtUtc <= nowUtc || suitability.ExpiresAtUtc < suitability.AssessedAtUtc ||
            !string.Equals(suitability.ClientId, request.ClientId, StringComparison.Ordinal) ||
            !Matches(suitability.ArtifactId, suitability.ContentVersion, request))
            reasons.Add("SUITABILITY_REQUIRED_OR_EXPIRED");
    }

    private static bool ValidReview(HumanReviewEvidence? review, DistributionRequest request, DateTime nowUtc) =>
        review is not null && review.Approved && review.ReviewedAtUtc <= nowUtc &&
        !string.IsNullOrWhiteSpace(review.ReviewId) && !string.IsNullOrWhiteSpace(review.ReviewerId) &&
        string.Equals(review.TraceId, request.TraceId, StringComparison.Ordinal) &&
        Matches(review.ArtifactId, review.ContentVersion, request);

    private static bool Matches(string artifactId, string contentVersion, DistributionRequest request) =>
        string.Equals(artifactId, request.ArtifactId, StringComparison.Ordinal) &&
        string.Equals(contentVersion, request.ContentVersion, StringComparison.Ordinal);
}

/// <summary>Local authorization state only; it has no transport or provider dependency.</summary>
public sealed class DistributionPolicyStateMachine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DistributionRequestState> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DistributionRequest> _acceptedRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withdrawnConsentIds = new(StringComparer.Ordinal);

    public DistributionRequestState InitialState => DistributionRequestState.Denied;

    public DistributionDecision Process(DistributionRequest? request, DateTime nowUtc)
    {
        if (request is null) return DistributionDecision.Denied("REQUEST_MISSING");
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
                return DistributionDecision.Denied("REQUEST_ID_MISSING");
            if (_requests.ContainsKey(request.RequestId))
                return new(false, ["DUPLICATE_REQUEST"], DistributionRequestState.DuplicateRejected);
            if (request.ClientConsent is not null && _withdrawnConsentIds.Contains(request.ClientConsent.ConsentId))
            {
                _requests[request.RequestId] = DistributionRequestState.Denied;
                return DistributionDecision.Denied("CONSENT_WITHDRAWN");
            }

            var decision = DistributionPolicy.Evaluate(request, nowUtc);
            _requests[request.RequestId] = decision.State;
            if (decision.Allowed) _acceptedRequests[request.RequestId] = request;
            return decision;
        }
    }

    public DistributionDecision WithdrawConsent(string requestId, ConsentWithdrawalEvidence? withdrawal, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_acceptedRequests.TryGetValue(requestId, out var request) || withdrawal is null ||
                request.ClientConsent is null || withdrawal.WithdrawnAtUtc > nowUtc ||
                string.IsNullOrWhiteSpace(withdrawal.AuditId) ||
                !string.Equals(withdrawal.ConsentId, request.ClientConsent.ConsentId, StringComparison.Ordinal) ||
                !string.Equals(withdrawal.ClientId, request.ClientId, StringComparison.Ordinal) ||
                !string.Equals(withdrawal.TraceId, request.TraceId, StringComparison.Ordinal) ||
                !string.Equals(withdrawal.AuditId, request.Audit?.AuditId, StringComparison.Ordinal))
                return DistributionDecision.Denied("CONSENT_WITHDRAWAL_EVIDENCE_INVALID");

            _withdrawnConsentIds.Add(withdrawal.ConsentId);
            _requests[requestId] = DistributionRequestState.Withdrawn;
            return new(false, ["CONSENT_WITHDRAWN"], DistributionRequestState.Withdrawn);
        }
    }
}
