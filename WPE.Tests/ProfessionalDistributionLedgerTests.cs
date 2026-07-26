using WpeAgent.ProfessionalDistribution;

namespace WPE.Tests;

public sealed class ProfessionalDistributionLedgerTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ValidReceipt_ProjectsAuthorizedMetadataWithoutSensitiveBody()
    {
        var projection = DistributionApprovalLedger.Project([Receipt()], Now);
        var propertyNames = typeof(DistributionApprovalReceipt).GetProperties().Select(x => x.Name).ToArray();

        Assert.True(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Authorized, projection.Status);
        Assert.Equal("request-1", projection.RequestId);
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Body", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ContentText", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Message", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateRequestOrReceipt_ProjectsErrorDenied()
    {
        var first = Receipt();
        var duplicate = DistributionApprovalReceiptFactory.Create(
            first.ReceiptId, 2, Request(), DistributionDecision.Approved(), Now, first.ImmutableHash);

        var projection = DistributionApprovalLedger.Project([first, duplicate], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Error, projection.Status);
        Assert.Contains("LEDGER_DUPLICATE", projection.ReasonCodes);
    }

    [Fact]
    public void ModifiedHashedField_ProjectsTamperError()
    {
        var projection = DistributionApprovalLedger.Project([Receipt() with { ConsentId = "changed" }], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Error, projection.Status);
        Assert.Contains("RECEIPT_TAMPERED", projection.ReasonCodes);
    }

    [Fact]
    public void IncorrectPreviousHash_ProjectsChainError()
    {
        var projection = DistributionApprovalLedger.Project(
            [Receipt() with { PreviousHash = "BROKEN" }], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Error, projection.Status);
        Assert.Contains("LEDGER_CHAIN_BROKEN", projection.ReasonCodes);
    }

    [Fact]
    public void AuditMismatchWithValidRecomputedHash_ProjectsCorrelationError()
    {
        var changed = Receipt() with { AuditTraceId = "other-trace" };
        changed = changed with { ImmutableHash = DistributionApprovalLedger.ComputeHash(changed) };

        var projection = DistributionApprovalLedger.Project([changed], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Error, projection.Status);
        Assert.Contains("AUDIT_CORRELATION_BROKEN", projection.ReasonCodes);
    }

    [Fact]
    public void ExpiredSuitability_ProjectsDenied()
    {
        var original = Request();
        Assert.NotNull(original.Suitability);
        var request = original with
        {
            Suitability = original.Suitability with { ExpiresAtUtc = Now.AddSeconds(-1) }
        };
        var receipt = DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), Now);

        var projection = DistributionApprovalLedger.Project([receipt], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Denied, projection.Status);
        Assert.Contains("SUITABILITY_EXPIRED", projection.ReasonCodes);
    }

    [Fact]
    public void ConsentExpiringExactlyNow_ProjectsDenied()
    {
        var original = Request();
        Assert.NotNull(original.ClientConsent);
        var request = original with
        {
            ClientConsent = original.ClientConsent with { ExpiresAtUtc = Now }
        };
        var receipt = DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), Now);

        var projection = DistributionApprovalLedger.Project([receipt], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Denied, projection.Status);
        Assert.Contains("CONSENT_EXPIRED", projection.ReasonCodes);
    }

    [Fact]
    public void SuitabilityExpiringExactlyNow_ProjectsDenied()
    {
        var original = Request();
        Assert.NotNull(original.Suitability);
        var request = original with
        {
            Suitability = original.Suitability with { ExpiresAtUtc = Now }
        };
        var receipt = DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), Now);

        var projection = DistributionApprovalLedger.Project([receipt], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Denied, projection.Status);
        Assert.Contains("SUITABILITY_EXPIRED", projection.ReasonCodes);
    }

    [Fact]
    public void AuditedConsentWithdrawal_ProjectsWithdrawnDenied()
    {
        var request = Request();
        var receipt = DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), Now,
            consentWithdrawal: new("consent-1", "client-1", "trace-1", "audit-1", Now));

        var projection = DistributionApprovalLedger.Project([receipt], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Withdrawn, projection.Status);
        Assert.Contains("CONSENT_WITHDRAWN", projection.ReasonCodes);
    }

    [Fact]
    public void WithdrawalWithMismatchedAudit_ProjectsCorrelationError()
    {
        var request = Request();
        var receipt = DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), Now,
            consentWithdrawal: new("consent-1", "client-1", "trace-1", "other-audit", Now));

        var projection = DistributionApprovalLedger.Project([receipt], Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionLedgerStatus.Error, projection.Status);
        Assert.Contains("AUDIT_CORRELATION_BROKEN", projection.ReasonCodes);
    }

    private static DistributionApprovalReceipt Receipt() => DistributionApprovalReceiptFactory.Create(
        "receipt-1", 1, Request(), DistributionDecision.Approved(), Now);

    private static DistributionRequest Request() => new(
        "request-1", "content-1", "v1", "trace-1", DistributionChannel.WhatsApp,
        DistributionRiskLevel.L3HighRiskAction,
        [new("fact-1", "Risk", "trace-1", Now.AddMinutes(-10), Now.AddMinutes(10), DistributionFactStatus.Confirmed)],
        true,
        "client-1",
        new("review-1", "reviewer-1", "content-1", "v1", "trace-1", Now.AddMinutes(-5), true),
        new("consent-1", "client-1", "WhatsApp", Now.AddDays(-1), Now.AddDays(1), true),
        new("audit-1", "request-1", "content-1", "v1", "trace-1", "operator-1", Now.AddMinutes(-1)),
        new("withdrawal-1", "content-1", "v1", true),
        new("review-2", "reviewer-2", "content-1", "v1", "trace-1", Now.AddMinutes(-3), true),
        new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-1), Now.AddDays(1), true),
        new(new string('A', 64), new string('B', 64), "consent-v1", "suitability-v1"));
}
