using WpeAgent.ProfessionalDistribution;

namespace WPE.Tests;

public sealed class ProfessionalDistributionPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Distribution_IsDeniedByDefault()
    {
        var decision = DistributionPolicy.Evaluate(Request() with { SendRequested = false }, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("SEND_NOT_REQUESTED", decision.ReasonCodes);
    }

    [Theory]
    [InlineData(DistributionFactStatus.Unknown, "FACT_UNKNOWN")]
    [InlineData(DistributionFactStatus.Stale, "FACT_STALE")]
    [InlineData(DistributionFactStatus.Unsupported, "FACT_UNSUPPORTED")]
    [InlineData(DistributionFactStatus.Error, "FACT_ERROR")]
    public void UntraceableFactState_FailsClosed(DistributionFactStatus status, string reason)
    {
        var request = Request() with
        {
            SendRequested = true,
            Facts = [Fact() with { Status = status }]
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Contains(reason, decision.ReasonCodes);
    }

    [Fact]
    public void HighRiskContent_RequiresReviewConsentAuditAndWithdrawal()
    {
        var decision = DistributionPolicy.Evaluate(Request() with
        {
            SendRequested = true,
            RiskLevel = DistributionRiskLevel.L2TradingSuggestion,
            ClientId = "client-1"
        }, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("HUMAN_REVIEW_REQUIRED", decision.ReasonCodes);
        Assert.Contains("CLIENT_CONSENT_REQUIRED", decision.ReasonCodes);
        Assert.Contains("AUDIT_EVIDENCE_REQUIRED", decision.ReasonCodes);
        Assert.Contains("WITHDRAWAL_CONTROL_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void CompleteHighRiskEvidence_AllowsPolicyDecisionOnly()
    {
        var request = HighRiskRequest();

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.True(decision.Allowed);
        Assert.Empty(decision.ReasonCodes);
    }

    [Theory]
    [InlineData(DistributionRiskLevel.L2TradingSuggestion)]
    [InlineData(DistributionRiskLevel.L3HighRiskAction)]
    public void L2AndL3_RequireIndependentSecondReviewer(DistributionRiskLevel level)
    {
        var request = HighRiskRequest() with
        {
            RiskLevel = level,
            SecondaryHumanReview = new("review-2", "reviewer-1", "content-1", "v1", "trace-1", Now.AddMinutes(-3), true)
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("INDEPENDENT_SECOND_REVIEW_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void ExpiredSuitability_FailsClosed()
    {
        var request = HighRiskRequest() with
        {
            Suitability = new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-2), Now.AddSeconds(-1), true)
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("SUITABILITY_REQUIRED_OR_EXPIRED", decision.ReasonCodes);
    }

    [Fact]
    public void FactExpiringExactlyNow_FailsClosed()
    {
        var request = Request() with
        {
            SendRequested = true,
            Facts = [Fact() with { ExpiresAtUtc = Now }]
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Equal(DistributionRequestState.Denied, decision.State);
        Assert.Contains("FACT_STALE", decision.ReasonCodes);
    }

    [Fact]
    public void ConsentExpiringExactlyNow_FailsClosed()
    {
        var request = HighRiskRequest() with
        {
            ClientConsent = HighRiskRequest().ClientConsent! with { ExpiresAtUtc = Now }
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Equal(DistributionRequestState.Denied, decision.State);
        Assert.Contains("CLIENT_CONSENT_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void SuitabilityExpiringExactlyNow_FailsClosed()
    {
        var request = HighRiskRequest() with
        {
            Suitability = HighRiskRequest().Suitability! with { ExpiresAtUtc = Now }
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Equal(DistributionRequestState.Denied, decision.State);
        Assert.Contains("SUITABILITY_REQUIRED_OR_EXPIRED", decision.ReasonCodes);
    }

    [Fact]
    public void AuditMustCorrelateRequestArtifactVersionAndTrace()
    {
        var request = HighRiskRequest() with
        {
            Audit = new("audit-1", "different-request", "content-1", "v1", "trace-1", "operator-1", Now.AddMinutes(-1))
        };

        var decision = DistributionPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("AUDIT_EVIDENCE_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void StateMachine_DefaultsDeniedAndRejectsDuplicateRequest()
    {
        var machine = new DistributionPolicyStateMachine();
        var request = HighRiskRequest();

        Assert.Equal(DistributionRequestState.Denied, machine.InitialState);
        Assert.True(machine.Process(request, Now).Allowed);
        var duplicate = machine.Process(request, Now);

        Assert.False(duplicate.Allowed);
        Assert.Equal(DistributionRequestState.DuplicateRejected, duplicate.State);
        Assert.Contains("DUPLICATE_REQUEST", duplicate.ReasonCodes);
    }

    [Fact]
    public void ConsentWithdrawal_IsAuditedAndBlocksConsentReuse()
    {
        var machine = new DistributionPolicyStateMachine();
        var request = HighRiskRequest();
        Assert.True(machine.Process(request, Now).Allowed);

        var withdrawn = machine.WithdrawConsent(
            request.RequestId,
            new("consent-1", "client-1", "trace-1", "audit-1", Now),
            Now);
        var reuse = machine.Process(request with { RequestId = "request-2" }, Now);

        Assert.Equal(DistributionRequestState.Withdrawn, withdrawn.State);
        Assert.False(reuse.Allowed);
        Assert.Contains("CONSENT_WITHDRAWN", reuse.ReasonCodes);
    }

    [Fact]
    public void RevokedConsent_FailsClosed()
    {
        var request = HighRiskRequest() with
        {
            ClientConsent = new("consent-1", "client-1", "WhatsApp", Now.AddDays(-1), Now.AddDays(1), true, true)
        };

        Assert.Contains("CLIENT_CONSENT_REQUIRED", DistributionPolicy.Evaluate(request, Now).ReasonCodes);
    }

    [Fact]
    public void PolicySurface_HasNoNetworkOrSendOperation()
    {
        var methods = typeof(DistributionPolicy).GetMethods()
            .Where(method => method.DeclaringType == typeof(DistributionPolicy))
            .Select(method => method.Name);

        Assert.Equal(["Evaluate"], methods);
    }

    private static DistributionRequest Request() => new(
        "request-1", "content-1", "v1", "trace-1", DistributionChannel.WhatsApp,
        DistributionRiskLevel.L0Information, [Fact()]);

    private static DistributionRequest HighRiskRequest() => Request() with
    {
        SendRequested = true,
        RiskLevel = DistributionRiskLevel.L3HighRiskAction,
        ClientId = "client-1",
        HumanReview = new("review-1", "reviewer-1", "content-1", "v1", "trace-1", Now.AddMinutes(-5), true),
        SecondaryHumanReview = new("review-2", "reviewer-2", "content-1", "v1", "trace-1", Now.AddMinutes(-3), true),
        ClientConsent = new("consent-1", "client-1", "WhatsApp", Now.AddDays(-1), Now.AddDays(1), true),
        Audit = new("audit-1", "request-1", "content-1", "v1", "trace-1", "operator-1", Now.AddMinutes(-1)),
        Withdrawal = new("withdraw-1", "content-1", "v1", true),
        Suitability = new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-1), Now.AddDays(1), true),
        VersionBinding = new(new string('A', 64), new string('B', 64), "consent-v1", "suitability-v1")
    };

    private static DistributionFactReference Fact() => new(
        "risk-artifact-1", "Risk", "trace-1", Now.AddMinutes(-10), Now.AddMinutes(10),
        DistributionFactStatus.Confirmed);
}
