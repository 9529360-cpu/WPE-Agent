using System.Text.Json;
using WpeAgent.ProfessionalDistribution;
using WpeAgent.RuntimeServices;

namespace WPE.Tests;

public sealed class RuntimeDistributionStateStoreTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DistributionRetentionPolicy Retention = new(TimeSpan.FromDays(30), TimeSpan.FromDays(365));

    [Fact]
    public void DefaultProjectionIsDenied()
    {
        var value = new RuntimeDistributionStateStore().Read().Value;
        Assert.Equal("Denied", value.Status);
        Assert.False(value.Allowed);
        Assert.Contains("LEDGER_EMPTY", value.ReasonCodes);
    }

    [Fact]
    public void MatchingLedgerPublishesOnlyMinimizedAuthorizedEvidence()
    {
        var store = new RuntimeDistributionStateStore();
        store.Publish([Receipt()], Retention, Current(), Now);
        var value = store.Read().Value;
        Assert.Equal("Authorized", value.Status);
        Assert.True(value.Allowed);
        Assert.Equal(["PrimaryReviewer", "SecondaryReviewer"], value.ApprovalRoles);
        Assert.Equal(Now.AddDays(1), value.ValidUntilUtc);

        var json = JsonSerializer.Serialize(value);
        foreach (var forbidden in new[] { "client-1", "reviewer-1", "reviewer-2", "operator-1", "content-1", "WhatsApp", "Recipient", "Destination", "ReviewText", "Opinion", "Send", "Retry", "Approve", "Config" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("consent")]
    [InlineData("suitability")]
    public void VersionChangesFailClosedDenied(string changed)
    {
        var current = Current();
        current = changed switch
        {
            "policy" => current with { PolicyHash = new string('C', 64) },
            "consent" => current with { ConsentVersion = "consent-v2" },
            _ => current with { SuitabilityVersion = "suitability-v2" }
        };
        var store = new RuntimeDistributionStateStore();
        store.Publish([Receipt()], Retention, current, Now);
        Assert.Equal("Denied", store.Read().Value.Status);
        Assert.False(store.Read().Value.Allowed);
        Assert.Contains("FRESH_DUAL_REVIEW_REQUIRED", store.Read().Value.ReasonCodes);
    }

    [Fact]
    public void WithdrawalFailsClosedDenied()
    {
        var request = Request();
        var receipt = DistributionApprovalReceiptFactory.Create("receipt-1", 1, request,
            DistributionDecision.Approved(), Now,
            consentWithdrawal: new("consent-1", "client-1", "trace-1", "audit-1", Now));
        var store = new RuntimeDistributionStateStore();
        store.Publish([receipt], Retention, Current(), Now);
        Assert.Equal("Denied", store.Read().Value.Status);
        Assert.True(store.Read().Value.Withdrawn);
        Assert.False(store.Read().Value.Allowed);
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("chain")]
    public void InvalidLedgerFailsClosedErrorWithoutUntrustedHashes(string failure)
    {
        var receipt = Receipt();
        receipt = failure == "tamper"
            ? receipt with { ImmutableHash = new string('F', 64) }
            : receipt with { PreviousHash = new string('E', 64) };
        var store = new RuntimeDistributionStateStore();
        store.Publish([receipt], Retention, Current(), Now);
        var value = store.Read().Value;
        Assert.Equal("Error", value.Status);
        Assert.False(value.Allowed);
        Assert.Null(value.PolicyHash);
        Assert.Null(value.ReceiptHash);
        Assert.Contains(value.ReasonCodes, code => code is "RECEIPT_TAMPERED" or "LEDGER_CHAIN_BROKEN");
    }

    private static DistributionApprovalReceipt Receipt() =>
        DistributionApprovalReceiptFactory.Create("receipt-1", 1, Request(), DistributionDecision.Approved(), Now);

    private static CurrentDistributionApprovalBinding Current() =>
        new(new string('A', 64), [Fact()], new string('B', 64), "consent-v1", "suitability-v1");

    private static DistributionFactReference Fact() =>
        new("fact-1", "Risk", "trace-1", Now.AddMinutes(-10), Now.AddMinutes(10), DistributionFactStatus.Confirmed);

    private static DistributionRequest Request() => new(
        "request-1", "content-1", "v1", "trace-1", DistributionChannel.WhatsApp,
        DistributionRiskLevel.L3HighRiskAction, [Fact()], true, "client-1",
        new("review-1", "reviewer-1", "content-1", "v1", "trace-1", Now.AddMinutes(-5), true),
        new("consent-1", "client-1", "WhatsApp", Now.AddDays(-1), Now.AddDays(2), true),
        new("audit-1", "request-1", "content-1", "v1", "trace-1", "operator-1", Now.AddMinutes(-1)),
        new("withdrawal-1", "content-1", "v1", true),
        new("review-2", "reviewer-2", "content-1", "v1", "trace-1", Now.AddMinutes(-3), true),
        new("suitability-1", "client-1", "content-1", "v1", Now.AddDays(-1), Now.AddDays(1), true),
        new(new string('A', 64), new string('B', 64), "consent-v1", "suitability-v1"));
}
