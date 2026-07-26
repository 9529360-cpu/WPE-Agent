using WpeAgent.ProfessionalDistribution;

namespace WPE.Tests;

public sealed class ProfessionalDistributionVersionBindingTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
    private static readonly string PolicyHash = new('A', 64);
    private static readonly string ScopeHash = new('B', 64);

    [Fact]
    public void MatchingCurrentBindings_AllowExistingDualApproval()
    {
        var decision = DistributionVersionBindingPolicy.Evaluate([Receipt()], Current(), Now);

        Assert.True(decision.Allowed);
        Assert.False(decision.RequiresFreshDualReview);
        Assert.Matches("^[A-F0-9]{64}$", decision.ReceiptHash);
        Assert.Empty(decision.ReasonCodes);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("facts")]
    [InlineData("consent-scope")]
    [InlineData("consent-version")]
    [InlineData("suitability-version")]
    public void AnyBindingChange_DeniesAndRequiresFreshDualReview(string changed)
    {
        var current = Current();
        current = changed switch
        {
            "policy" => current with { PolicyHash = new string('C', 64) },
            "facts" => current with { Facts = [Fact() with { ArtifactId = "fact-2" }] },
            "consent-scope" => current with { ConsentScopeHash = new string('D', 64) },
            "consent-version" => current with { ConsentVersion = "consent-v2" },
            _ => current with { SuitabilityVersion = "suitability-v2" }
        };

        var decision = DistributionVersionBindingPolicy.Evaluate([Receipt()], current, Now);

        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresFreshDualReview);
        Assert.Contains("FRESH_DUAL_REVIEW_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void UnknownBinding_FailsClosedWithoutRawMetadataOutput()
    {
        var decision = DistributionVersionBindingPolicy.Evaluate(
            [Receipt()], Current() with { ConsentVersion = "unknown" }, Now);
        var names = typeof(DistributionReapprovalDecision).GetProperties().Select(x => x.Name).ToArray();

        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresFreshDualReview);
        Assert.Contains("VERSION_BINDING_UNKNOWN", decision.ReasonCodes);
        Assert.Equal(["Allowed", "RequiresFreshDualReview", "ReceiptHash", "ReasonCodes"], names);
    }

    [Fact]
    public void HighRiskPolicy_RejectsMissingVersionBindingBeforeReceiptCreation()
    {
        var decision = DistributionPolicy.Evaluate(Request() with { VersionBinding = null }, Now);

        Assert.False(decision.Allowed);
        Assert.Contains("VERSION_BINDING_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void ExpiredApproval_DeniesAndRequiresFreshDualReview()
    {
        var request = Request() with
        {
            Suitability = new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-2), Now.AddSeconds(-1), true)
        };
        var receipt = Create(request);

        var decision = DistributionVersionBindingPolicy.Evaluate([receipt], Current(), Now);

        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresFreshDualReview);
        Assert.Contains("SUITABILITY_EXPIRED", decision.ReasonCodes);
        Assert.Contains("FRESH_DUAL_REVIEW_REQUIRED", decision.ReasonCodes);
    }

    [Fact]
    public void ReceiptBindingTamper_ProducesLedgerErrorAndCannotReuseApproval()
    {
        var decision = DistributionVersionBindingPolicy.Evaluate(
            [Receipt() with { PolicyHash = new string('C', 64) }], Current(), Now);

        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresFreshDualReview);
        Assert.Contains("RECEIPT_TAMPERED", decision.ReasonCodes);
    }

    private static DistributionApprovalReceipt Receipt() => Create(Request());

    private static DistributionApprovalReceipt Create(DistributionRequest request) =>
        DistributionApprovalReceiptFactory.Create("receipt-1", 1, request, DistributionDecision.Approved(), Now);

    private static CurrentDistributionApprovalBinding Current() =>
        new(PolicyHash, [Fact()], ScopeHash, "consent-v1", "suitability-v1");

    private static DistributionFactReference Fact() =>
        new("fact-1", "Risk", "trace-1", Now.AddMinutes(-10), Now.AddMinutes(10), DistributionFactStatus.Confirmed);

    private static DistributionRequest Request() => new(
        "request-1", "content-1", "v1", "trace-1", DistributionChannel.WhatsApp,
        DistributionRiskLevel.L3HighRiskAction, [Fact()], true, "client-1",
        new("review-1", "reviewer-1", "content-1", "v1", "trace-1", Now.AddMinutes(-5), true),
        new("consent-1", "client-1", "WhatsApp", Now.AddDays(-1), Now.AddDays(1), true),
        new("audit-1", "request-1", "content-1", "v1", "trace-1", "operator-1", Now.AddMinutes(-1)),
        new("withdrawal-1", "content-1", "v1", true),
        new("review-2", "reviewer-2", "content-1", "v1", "trace-1", Now.AddMinutes(-3), true),
        new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-1), Now.AddDays(1), true),
        new(PolicyHash, ScopeHash, "consent-v1", "suitability-v1"));
}
