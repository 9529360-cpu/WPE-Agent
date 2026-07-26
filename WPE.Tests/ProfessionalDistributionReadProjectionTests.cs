using WpeAgent.ProfessionalDistribution;

namespace WPE.Tests;

public sealed class ProfessionalDistributionReadProjectionTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DistributionRetentionPolicy Retention = new(TimeSpan.FromDays(30), TimeSpan.FromDays(365));

    [Fact]
    public void AuthorizedProjection_ContainsOnlyMinimizedApprovalMetadata()
    {
        var projection = DistributionReadProjector.Project([Receipt()], Retention, Now);
        var names = typeof(DistributionReadProjection).GetProperties().Select(x => x.Name).ToArray();

        Assert.True(projection.Allowed);
        Assert.Equal(DistributionReadStatus.Authorized, projection.Status);
        Assert.Equal([DistributionApprovalRole.PrimaryReviewer, DistributionApprovalRole.SecondaryReviewer], projection.ApprovalRoles);
        Assert.Equal(Now.AddDays(1), projection.ValidUntilUtc);
        Assert.Matches("^[A-F0-9]{64}$", projection.AuditCorrelationHash);
        Assert.DoesNotContain(names, name => Forbidden(name));
    }

    [Fact]
    public void ConsentExpiry_UsesEarliestValidityAndDeniesWhenExpired()
    {
        var request = Request(consentExpiry: Now.AddMinutes(-1));
        var receipt = Create(request);

        var projection = DistributionReadProjector.Project([receipt], Retention, Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionReadStatus.Denied, projection.Status);
        Assert.Contains("CONSENT_EXPIRED", projection.ReasonCodes);
    }

    [Fact]
    public void WithdrawnReceipt_IsDeniedAndVisibleAsWithdrawn()
    {
        var request = Request();
        var receipt = DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), Now,
            consentWithdrawal: new("consent-1", "client-1", "trace-1", "audit-1", Now));

        var projection = DistributionReadProjector.Project([receipt], Retention, Now);

        Assert.False(projection.Allowed);
        Assert.True(projection.Withdrawn);
        Assert.Equal(DistributionReadStatus.Withdrawn, projection.Status);
    }

    [Fact]
    public void BrokenLedger_ProjectsErrorWithoutCorrelationMetadata()
    {
        var projection = DistributionReadProjector.Project(
            [Receipt() with { ImmutableHash = "tampered" }], Retention, Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionReadStatus.Error, projection.Status);
        Assert.Null(projection.AuditCorrelationHash);
        Assert.Null(projection.ReceiptHash);
    }

    [Fact]
    public void MetadataRetention_LeavesOnlyAuditTombstoneAndHashes()
    {
        var receipt = Create(Request(), Now.AddDays(-31));

        var projection = DistributionReadProjector.Project([receipt], Retention, Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionReadStatus.Tombstone, projection.Status);
        Assert.Empty(projection.ApprovalRoles);
        Assert.Null(projection.ValidUntilUtc);
        Assert.Matches("^[A-F0-9]{64}$", projection.AuditCorrelationHash);
        Assert.Matches("^[A-F0-9]{64}$", projection.ReceiptHash);
    }

    [Fact]
    public void AuditRetentionExpiry_RemovesHashesAndRemainsDenied()
    {
        var receipt = Create(Request(), Now.AddDays(-366));

        var projection = DistributionReadProjector.Project([receipt], Retention, Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionReadStatus.Denied, projection.Status);
        Assert.Null(projection.AuditCorrelationHash);
        Assert.Null(projection.ReceiptHash);
        Assert.Contains("AUDIT_RETENTION_EXPIRED", projection.ReasonCodes);
    }

    [Fact]
    public void InvalidRetentionPolicy_FailsClosed()
    {
        var projection = DistributionReadProjector.Project(
            [Receipt()], new(TimeSpan.FromDays(30), TimeSpan.FromDays(1)), Now);

        Assert.False(projection.Allowed);
        Assert.Equal(DistributionReadStatus.Error, projection.Status);
        Assert.Contains("RETENTION_POLICY_INVALID", projection.ReasonCodes);
    }

    private static bool Forbidden(string name) =>
        name.Contains("Body", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Message", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Opinion", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ReviewerId", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ConsentId", StringComparison.OrdinalIgnoreCase);

    private static DistributionApprovalReceipt Receipt() => Create(Request());

    private static DistributionApprovalReceipt Create(DistributionRequest request, DateTime? recordedAt = null) =>
        DistributionApprovalReceiptFactory.Create(
            "receipt-1", 1, request, DistributionDecision.Approved(), recordedAt ?? Now);

    private static DistributionRequest Request(DateTime? consentExpiry = null) => new(
        "request-1", "content-1", "v1", "trace-1", DistributionChannel.WhatsApp,
        DistributionRiskLevel.L3HighRiskAction,
        [new("fact-1", "Risk", "trace-1", Now.AddMinutes(-10), Now.AddMinutes(10), DistributionFactStatus.Confirmed)],
        true,
        "client-1",
        new("review-1", "reviewer-1", "content-1", "v1", "trace-1", Now.AddMinutes(-5), true),
        new("consent-1", "client-1", "WhatsApp", Now.AddDays(-1), consentExpiry ?? Now.AddDays(2), true),
        new("audit-1", "request-1", "content-1", "v1", "trace-1", "operator-1", Now.AddMinutes(-1)),
        new("withdrawal-1", "content-1", "v1", true),
        new("review-2", "reviewer-2", "content-1", "v1", "trace-1", Now.AddMinutes(-3), true),
        new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-1), Now.AddDays(1), true),
        new(new string('A', 64), new string('B', 64), "consent-v1", "suitability-v1"));
}
