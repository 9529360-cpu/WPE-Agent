using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffStrategyRiskContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AvailableInputsProduceStableIntentAndRuleLedgerHashes()
    {
        var first = Evaluate([Input("market"), Input("account")]);
        var second = Evaluate([Input("account"), Input("market")]);
        Assert.True(first.EligibleForRiskIncrease);
        Assert.Equal(first.IntentSha256, second.IntentSha256);
        Assert.Equal(first.LedgerSha256, second.LedgerSha256);
        Assert.Equal(first.Rules.OrderBy(x => x.RuleId).ToArray(), first.Rules);
    }

    [Theory]
    [InlineData(ModelOffInputStateV1.Missing)]
    [InlineData(ModelOffInputStateV1.Stale)]
    [InlineData(ModelOffInputStateV1.Unknown)]
    [InlineData(ModelOffInputStateV1.Conflicting)]
    [InlineData(ModelOffInputStateV1.Unsupported)]
    public void EveryNonAvailableStateBlocksRiskIncrease(ModelOffInputStateV1 state)
    {
        var result = Evaluate([Input("market") with { State = state }]);
        Assert.False(result.EligibleForRiskIncrease);
        Assert.Contains(result.Rules, x => !x.Passed && x.ReasonCode == $"risk.input-{state.ToString().ToLowerInvariant()}");
    }

    [Fact]
    public void EmptyMalformedFutureAndExpiredInputsBlockRiskIncrease()
    {
        Assert.False(Evaluate([]).EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("")]).EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("market") with { ArtifactHash = "bad" }]).EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("market") with { AsOfUtc = Now.AddMinutes(1) }]).EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("market") with { AsOfUtc = Now.AddMinutes(-6) }]).EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("market") with { State = (ModelOffInputStateV1)999 }]).EligibleForRiskIncrease);
    }

    [Fact]
    public void MainnetUnknownActionAndInvalidTimesAlwaysBlock()
    {
        Assert.False(Evaluate([Input("market")], mainnet: true).EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("market")], action: (DecisionAction)999).EligibleForRiskIncrease);
        var invalidTime = new RiskAndPositionPlanner().EvaluateModelOffIntent(
            new(DecisionAction.OpenLong, "BTCUSDT", default, TimeSpan.FromMinutes(5), false, [Input("market")]));
        Assert.False(invalidTime.EligibleForRiskIncrease);
        Assert.False(Evaluate([Input("market")], maximumAge: TimeSpan.Zero).EligibleForRiskIncrease);
    }

    [Fact]
    public void RiskReductionDoesNotBecomeRiskIncreaseAndStillRecordsFailedInputs()
    {
        var result = Evaluate([Input("market") with { State = ModelOffInputStateV1.Unknown }], DecisionAction.CloseLong);
        Assert.True(result.EligibleForRiskIncrease);
        Assert.Contains(result.Rules, x => !x.Passed && x.ReasonCode == "risk.input-unknown");
    }

    private static ModelOffStrategyRiskContractV1 Evaluate(IReadOnlyList<ModelOffInputFactV1> inputs, DecisionAction action = DecisionAction.OpenLong, bool mainnet = false, DateTimeOffset? evaluation = null, TimeSpan? maximumAge = null) =>
        new RiskAndPositionPlanner().EvaluateModelOffIntent(new(action, "BTCUSDT", evaluation ?? Now, maximumAge ?? TimeSpan.FromMinutes(5), mainnet, inputs));

    private static ModelOffInputFactV1 Input(string id) => new(id, ModelOffInputStateV1.Available, "sha256:" + new string('a', 64), Now.AddMinutes(-1));
}
