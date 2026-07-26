using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingAuthorizationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TradingAuthorizationMode.Research)]
    [InlineData(TradingAuthorizationMode.Signal)]
    public void ReadOnlyModes_AlwaysRejectMutation(TradingAuthorizationMode mode)
    {
        var decision = TradingAuthorizationPolicy.Evaluate(Request(mode), Now);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Review_RequiresApprovalReceipt()
    {
        var decision = TradingAuthorizationPolicy.Evaluate(Request(TradingAuthorizationMode.Review) with { ApprovalReceipt = null }, Now);

        Assert.False(decision.Allowed);
        Assert.Equal("authorization.review-approval-required", decision.Code);
    }

    [Fact]
    public void Review_AllowsMatchingUnexpiredUnusedApproval()
    {
        var decision = TradingAuthorizationPolicy.Evaluate(Request(TradingAuthorizationMode.Review), Now);

        Assert.True(decision.Allowed);
        Assert.Equal("authorization.review-approved", decision.Code);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("consumed")]
    [InlineData("user")]
    [InlineData("device")]
    [InlineData("session")]
    [InlineData("hash")]
    [InlineData("correlation")]
    public void Review_RejectsInvalidApprovalStateOrContext(string mutation)
    {
        var request = Request(TradingAuthorizationMode.Review);
        var receipt = request.ApprovalReceipt!;
        receipt = mutation switch
        {
            "expired" => receipt with { ExpiresAtUtc = Now },
            "revoked" => receipt with { RevokedAtUtc = Now.AddMinutes(-1) },
            "consumed" => receipt with { ConsumedAtUtc = Now.AddMinutes(-1) },
            "user" => receipt with { UserId = "different-user" },
            "device" => receipt with { DeviceId = "different-device" },
            "session" => receipt with { SessionId = "different-session" },
            "hash" => receipt with { IntentHash = "different-hash" },
            _ => receipt with { CorrelationId = "different-correlation" }
        };

        var decision = TradingAuthorizationPolicy.Evaluate(request with { ApprovalReceipt = receipt }, Now);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Auto_RejectsMainnetEvenWithValidRiskReceipt()
    {
        var decision = TradingAuthorizationPolicy.Evaluate(Request(TradingAuthorizationMode.Auto) with { IsTestnet = false }, Now);

        Assert.False(decision.Allowed);
        Assert.Equal("authorization.testnet-required", decision.Code);
    }

    [Fact]
    public void Auto_RequiresValidDeterministicRiskReceipt()
    {
        var missing = TradingAuthorizationPolicy.Evaluate(Request(TradingAuthorizationMode.Auto) with { RiskReceipt = null }, Now);
        var expiredRisk = Request(TradingAuthorizationMode.Auto).RiskReceipt! with { ExpiresAtUtc = Now };
        var expired = TradingAuthorizationPolicy.Evaluate(Request(TradingAuthorizationMode.Auto) with { RiskReceipt = expiredRisk }, Now);

        Assert.False(missing.Allowed);
        Assert.False(expired.Allowed);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("revoked")]
    [InlineData("hash")]
    [InlineData("correlation")]
    public void Auto_RejectsInvalidDeterministicRiskReceipt(string mutation)
    {
        var request = Request(TradingAuthorizationMode.Auto);
        var receipt = request.RiskReceipt!;
        receipt = mutation switch
        {
            "rejected" => receipt with { Approved = false },
            "revoked" => receipt with { RevokedAtUtc = Now.AddMinutes(-1) },
            "hash" => receipt with { IntentHash = "different-hash" },
            _ => receipt with { CorrelationId = "different-correlation" }
        };

        var decision = TradingAuthorizationPolicy.Evaluate(request with { RiskReceipt = receipt }, Now);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Auto_AllowsTestnetWithMatchingValidRiskReceipt()
    {
        var decision = TradingAuthorizationPolicy.Evaluate(Request(TradingAuthorizationMode.Auto), Now);

        Assert.True(decision.Allowed);
        Assert.Equal("authorization.auto-approved", decision.Code);
    }

    [Fact]
    public void DecisionCode_DoesNotEchoAuthorizationIdentifiers()
    {
        var request = Request(TradingAuthorizationMode.Review) with
        {
            ApprovalReceipt = Request(TradingAuthorizationMode.Review).ApprovalReceipt! with { UserId = "private-user-id" }
        };

        var decision = TradingAuthorizationPolicy.Evaluate(request, Now);

        Assert.False(decision.Allowed);
        Assert.DoesNotContain("private-user-id", decision.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(request.IntentHash, decision.Code, StringComparison.Ordinal);
    }

    private static TradingAuthorizationRequest Request(TradingAuthorizationMode mode)
    {
        const string correlation = "correlation-1";
        const string hash = "intent-hash-1";
        const string user = "user-1";
        const string device = "device-1";
        const string session = "session-1";
        var risk = new DeterministicRiskReceipt("risk-1", correlation, hash, true, Now.AddMinutes(-1), Now.AddMinutes(5));
        var approval = new TradingApprovalReceipt("approval-1", correlation, hash, user, device, session, true, Now.AddMinutes(-1), Now.AddMinutes(5));
        return new(mode, true, correlation, hash, user, device, session, risk, approval);
    }
}
