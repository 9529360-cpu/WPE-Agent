using WpeAgent.ModelOff;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using Microsoft.Data.Sqlite;

namespace WPE.Tests;

public sealed class ModelOffLiveCycleInputComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidRuntimeTruthCreatesFourCanonicalOrderedInputs()
    {
        var inputs = ModelOffLiveCycleInputComposerV1.Compose(Request());

        Assert.Equal(new[] { ModelOffAgentV1.Market, ModelOffAgentV1.Research, ModelOffAgentV1.Strategy, ModelOffAgentV1.Risk },
            inputs.Select(x => x.Output.Agent));
        Assert.All(inputs, input =>
        {
            Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(input.Output));
            Assert.Equal(ModelOffCanonicalSerializerV1.Serialize(input.Output).Sha256, input.Document.Sha256);
        });
        for (var index = 1; index < inputs.Count; index++)
            Assert.Contains(inputs[index].Output.Sources, source => source.ArtifactHash == "sha256:" + inputs[index - 1].Document.Sha256);
    }

    [Fact]
    public void ReorderedRuntimeMapsRemainCanonicalAndDeterministic()
    {
        var request = Request();
        var reversedMarkets = request.Evidence.Markets.Reverse().ToDictionary(x => x.Key, x => x.Value);
        var reversedResearch = request.Research.Reverse().ToDictionary(x => x.Key, x => x.Value);
        var first = ModelOffLiveCycleInputComposerV1.Compose(request);
        var second = ModelOffLiveCycleInputComposerV1.Compose(request with
        {
            Evidence = new EvidencePack { CollectedAt = request.Evidence.CollectedAt, Completeness = 100, Markets = reversedMarkets },
            Research = reversedResearch
        });
        Assert.Equal(first.Select(x => x.Document.Sha256), second.Select(x => x.Document.Sha256));
    }

    [Fact]
    public void PersistedMacroFactsEnterCanonicalResearchWithoutTradingInference()
    {
        var macro=new PersistedMacroObservation("CUUR0000SA0",new(2026,6,1,0,0,0,TimeSpan.Zero),2,"US","monthly","index",334.1m,"bls-public-api-v2",new string('a',64),Now.AddMinutes(-2));
        var inputs=ModelOffLiveCycleInputComposerV1.Compose(Request() with{MacroObservations=[macro]});
        var research=inputs.Single(x=>x.Output.Agent==ModelOffAgentV1.Research);

        var persisted=Assert.Single(research.Output.Facts.GetProperty("macro_observations").EnumerateArray());
        Assert.Equal(2,persisted.GetProperty("Revision").GetInt32());
        Assert.Equal(334.1m,persisted.GetProperty("Value").GetDecimal());
        Assert.DoesNotContain("forecast",research.Document.Json,StringComparison.OrdinalIgnoreCase);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(research.Output));
    }

    [Fact]
    public void InvalidPersistedMacroFactFailsResearchClosed()
    {
        var future=new PersistedMacroObservation("CUUR0000SA0",new(2026,6,1,0,0,0,TimeSpan.Zero),1,"US","monthly","index",334.1m,"bls-public-api-v2",new string('a',64),Now.AddMinutes(1));
        var inputs=ModelOffLiveCycleInputComposerV1.Compose(Request() with{MacroObservations=[future]});
        var research=inputs.Single(x=>x.Output.Agent==ModelOffAgentV1.Research);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(research.Output));
        Assert.Contains("live.research.macro-invalid",research.Output.Decision.ReasonCodes);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(inputs[^1].Output));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("empty-market")]
    [InlineData("review-blocked")]
    [InlineData("risk-blocked")]
    public void UnsafeRuntimeTruthBlocksAtOrBeforeRisk(string defect)
    {
        var request = Request();
        if (defect == "stale") request = request with { Evidence = Evidence(Now.AddMinutes(-6).UtcDateTime) };
        if (defect == "empty-market") request = request with { Evidence = new EvidencePack { CollectedAt = Now.UtcDateTime, Completeness = 100 } };
        if (defect == "review-blocked") request = request with { DecisionReview = Review(false) };
        if (defect == "risk-blocked") request = request with { RiskReview = Risk(false) };

        var inputs = ModelOffLiveCycleInputComposerV1.Compose(request);
        Assert.Contains(inputs, input => !ModelOffEligibilityV1.IsEligibleForDownstream(input.Output));
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(inputs[^1].Output));
    }

    [Fact]
    public void ComposerHasNoModelNetworkOrMutationDependency()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Services", "Agent", "ModelOffLiveCycleInputComposerV1.cs"));
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAssistantProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReliableOrderExecutor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TradingExecutionGateway", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeTextRuntimeFieldsCannotEnterCanonicalAudit()
    {
        const string secret = "sk-live-shadow-secret-123456";
        var request = Request() with
        {
            Research = new Dictionary<string, ResearchValidationResult>
            {
                ["BTCUSDT"] = new() { Symbol = "BTCUSDT", StrategyVersion = secret, QualityScore = .8, CoverageDays = 90 }
            },
            RiskReview = new IndependentRiskReview { Approved = true, RiskLevel = secret }
        };
        var inputs = ModelOffLiveCycleInputComposerV1.Compose(request);
        Assert.All(inputs, input => Assert.DoesNotContain(secret, input.Document.Json, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProductionShadowPersistsSevenOutputsWithoutChangingExecution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-live-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new AgentSqliteStore(Path.Combine(directory, "agent.db"), () => Now);
            var request = Request();
            var result = await AutoTradingAgent.RunModelOffProductionShadowAsync(store, request.CycleId,
                request.EvaluationTimeUtc, request.Evidence, request.Research, request.Assessments,
                request.DecisionReview, request.RiskReview, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.EligibleForRiskIncrease);
            Assert.Equal(7, (await store.GetModelOffCanonicalAuditsAsync(request.CycleId, CancellationToken.None)).Count);
            Assert.Equal("no_mutation", result.Outputs[ModelOffAgentV1.Execution].Decision.Action);
            Assert.False(result.Outputs[ModelOffAgentV1.Execution].Facts.GetProperty("mutation_attempted").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LiveLoopInvokesShadowAfterRiskReviewAndBeforeOrderAuthorization()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Services", "AutoTradingAgent.cs"));
        var risk = source.IndexOf("var riskReview=", StringComparison.Ordinal);
        var shadow = source.IndexOf("RunModelOffProductionShadowAsync(Db,cycle", risk, StringComparison.Ordinal);
        var authorization = source.IndexOf("if(intents.Count>0&&tradingRule is not null)", shadow, StringComparison.Ordinal);
        Assert.True(risk >= 0 && shadow > risk && authorization > shadow);
        Assert.Contains("ApplyModelOffProductionRiskIncreaseGate(intents,modelOffCycle)", source[shadow..authorization], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RiskIncreaseGateRequiresCompletePersistedSevenAgentCycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-live-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var request = Request();
            var ready = await AutoTradingAgent.RunModelOffProductionShadowAsync(
                new AgentSqliteStore(Path.Combine(directory, "ready.db"), () => Now), request.CycleId,
                request.EvaluationTimeUtc, request.Evidence, request.Research, request.Assessments,
                request.DecisionReview, request.RiskReview, CancellationToken.None);
            var blocked = await AutoTradingAgent.RunModelOffProductionShadowAsync(
                new AgentSqliteStore(Path.Combine(directory, "blocked.db"), () => Now), request.CycleId + "-blocked",
                request.EvaluationTimeUtc, request.Evidence, request.Research, request.Assessments,
                request.DecisionReview, Risk(false), CancellationToken.None);

            Assert.True(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(ready));
            Assert.False(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(blocked));
            Assert.False(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(null));
            Assert.False(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(ready! with { Handoffs = [] }));
            var tamperedDocuments = ready!.Documents.ToDictionary(x => x.Key, x => x.Value);
            tamperedDocuments[ModelOffAgentV1.Market] = tamperedDocuments[ModelOffAgentV1.Market] with { Sha256 = new string('a', 64) };
            Assert.False(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(ready with { Documents = tamperedDocuments }));
            Assert.False(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(ready with { AuditCoverage = new Dictionary<ModelOffAgentV1, string>() }));
            var tamperedHandoffs = ready.Handoffs.ToArray();
            tamperedHandoffs[0] = tamperedHandoffs[0] with { Sha256 = new string('b', 64) };
            Assert.False(AutoTradingAgent.ModelOffProductionGateAllowsRiskIncrease(ready with { Handoffs = tamperedHandoffs }));

            var increase = new ExecutionIntent("BTCUSDT", PositionSide.Long, .01m, false, 98_000m, 104_000m, "increase", "test");
            var reduce = new ExecutionIntent("BTCUSDT", PositionSide.Short, .01m, true, 0, 0, "reduce", "test");
            Assert.Equal(new[] { reduce }, AutoTradingAgent.ApplyModelOffProductionRiskIncreaseGate([increase, reduce], blocked));
            Assert.Equal(new[] { increase, reduce }, AutoTradingAgent.ApplyModelOffProductionRiskIncreaseGate([increase, reduce], ready));
            Assert.Equal(new[] { reduce }, AutoTradingAgent.ApplyModelOffProductionRiskIncreaseGate([reduce], null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static ModelOffLiveCycleInputRequestV1 Request() => new(
        "live-cycle", Now, Evidence(Now.UtcDateTime),
        new Dictionary<string, ResearchValidationResult>
        {
            ["ETHUSDT"] = Research("ETHUSDT"), ["BTCUSDT"] = Research("BTCUSDT")
        },
        [new MarketDecisionAssessment { Symbol = "BTCUSDT", Fresh = true, EntryReady = true,
            RecommendedAction = DecisionAction.OpenLong, Confidence = .8 }], Review(true), Risk(true));

    private static EvidencePack Evidence(DateTime collectedAt) => new()
    {
        CollectedAt = collectedAt, Completeness = 100,
        Markets = new Dictionary<string, MarketEvidence>
        {
            ["ETHUSDT"] = Market("ETHUSDT", 3500m, collectedAt),
            ["BTCUSDT"] = Market("BTCUSDT", 100000m, collectedAt)
        }
    };
    private static MarketEvidence Market(string symbol, decimal price, DateTime at) =>
        new(symbol, price, price * .98m, price * 1.02m, 55, .2, .3, .4, new(0, 1, 1, 1, 1, 1, 0), at);
    private static ResearchValidationResult Research(string symbol) => new()
    { Symbol = symbol, StrategyVersion = "strategy-v1", SampleSize = 200, Trades = 30, QualityScore = .8, Approved = true, Promoted = true, CoverageDays = 90 };
    private static DecisionReview Review(bool accepted) => new()
    {
        Accepted = accepted,
        Decision = new DecisionPlan { Action = DecisionAction.OpenLong, Instrument = "BTCUSDT", Confidence = .8,
            EntryPrice = 100000m, StopLossPrice = 98000m, TakeProfitPrice = 104000m, RiskRewardRatio = 2 }
    };
    private static IndependentRiskReview Risk(bool approved) => new()
    { Approved = approved, RiskLevel = approved ? "LOW" : "BLOCKED", PlannedQuantity = approved ? .01m : 0, Checks = ["risk-gate"] };
    private static string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
