using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class NumericalTradingIntelligenceTests
{
    private static readonly DateTime CollectedAt = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MarketStructure_DetectsConfirmedBullishBreakoutSequence()
    {
        var market = BullishMarket();
        var structure = new NumericalMarketStructureSkill().Analyze(market);

        Assert.True(structure.Ready);
        Assert.Equal(MarketStructureBias.Bullish, structure.Bias);
        Assert.True(structure.Event is MarketStructureEvent.BreakoutUp or MarketStructureEvent.RetestUp);
        Assert.True(structure.VolumeRatio > 1);
    }

    [Fact]
    public void NumericalStrategy_CanActWhenLegacyEntryReadyIsFalseAndScoreDisagrees()
    {
        var market = BullishMarket();
        var evidence = Pack(market);
        var assessment = LegacyAssessment(entryReady: false, netScore: -.9);
        var context = new AgentContext("WPE Local Brain", false, null, Array.Empty<StructuredOutcomeMemory>(),
            new[] { assessment }, 0);

        var plan = new NumericalStrategySkill().Decide(evidence, context);

        Assert.Equal(DecisionAction.OpenLong, plan.Action);
        Assert.Equal(NumericalStrategySkill.Basis, plan.DecisionBasis);
        Assert.Equal(NumericalStrategySkill.Version, plan.StrategyVersion);
    }

    [Fact]
    public void Reviewer_TreatsLegacyScoresAsAdvisoryForValidatedNumericalPlan()
    {
        var market = BullishMarket();
        var evidence = Pack(market);
        var assessment = LegacyAssessment(entryReady: false, netScore: -.9);
        var context = new AgentContext("WPE Local Brain", false, null, Array.Empty<StructuredOutcomeMemory>(),
            new[] { assessment }, 0);
        var proposed = new NumericalStrategySkill().Decide(evidence, context);
        proposed = new DeterministicPlanSkill().Complete(proposed, market, assessment, new RiskLimits());

        var review = new DecisionGovernanceSkill().Review(proposed, new[] { assessment }, evidence,
            new DecisionPolicy
            {
                MinimumEvidenceCompleteness = 70,
                MinimumConfidence = .99,
                MinimumDirectionalScore = .99,
                MaximumConflictRatio = 0,
                MinimumMarketQuality = 65,
                MaximumEvidenceAgeMinutes = 5
            });

        Assert.True(review.Accepted);
        Assert.Equal(DecisionAction.OpenLong, review.Decision.Action);
        Assert.DoesNotContain(assessment.MissingConditions, item => review.BlockingReasons.Contains(item));
    }

    [Fact]
    public void Reviewer_StillFailsClosedWhenFreshnessIsInvalid()
    {
        var market = BullishMarket();
        var evidence = Pack(market);
        var assessment = LegacyAssessment(entryReady: false, netScore: -.9);
        assessment = new MarketDecisionAssessment
        {
            Symbol = assessment.Symbol,
            Regime = assessment.Regime,
            NetScore = assessment.NetScore,
            Confidence = assessment.Confidence,
            ConflictRatio = assessment.ConflictRatio,
            Fresh = false,
            EntryReady = assessment.EntryReady,
            RecommendedAction = assessment.RecommendedAction,
            Signals = assessment.Signals,
            MissingConditions = assessment.MissingConditions,
            Summary = assessment.Summary
        };
        var context = new AgentContext("WPE Local Brain", false, null, Array.Empty<StructuredOutcomeMemory>(),
            new[] { assessment }, 0);
        var proposed = new NumericalStrategySkill().Decide(evidence, context);
        proposed = new DeterministicPlanSkill().Complete(proposed, market, assessment, new RiskLimits());

        var review = new DecisionGovernanceSkill().Review(proposed, new[] { assessment }, evidence,
            new DecisionPolicy { MinimumEvidenceCompleteness = 70, MinimumMarketQuality = 65 });

        Assert.False(review.Accepted);
        Assert.Equal(DecisionAction.Hold, review.Decision.Action);
    }

    [Fact]
    public async Task DeterministicBrain_FallsBackToLegacyAssessmentWhenRawCandlesAreUnavailable()
    {
        var market = BullishMarket() with { Candles = Array.Empty<CandleEvidence>() };
        market = market with { Provenance = MarketEvidenceProvenanceCanonicalizerV1.Create(market, "test-provider", "Testnet") };
        var evidence = Pack(market);
        var assessment = LegacyAssessment(entryReady: true, netScore: .8);
        var context = new AgentContext("WPE Local Brain", false, null, Array.Empty<StructuredOutcomeMemory>(),
            new[] { assessment }, 0);

        var result = await new DeterministicBrainProvider().DecideAsync(evidence, context, CancellationToken.None);

        Assert.Equal(DecisionAction.OpenLong, result.Decision.Action);
        Assert.Equal("signal-aggregation-v1", result.Decision.DecisionBasis);
    }

    private static EvidencePack Pack(MarketEvidence market) => new()
    {
        Completeness = 100,
        Markets = new Dictionary<string, MarketEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            [market.Symbol] = market
        }
    };

    private static MarketDecisionAssessment LegacyAssessment(bool entryReady, double netScore) => new()
    {
        Symbol = "BTCUSDT",
        Regime = MarketRegime.Trending,
        NetScore = netScore,
        Confidence = .10,
        ConflictRatio = .95,
        Fresh = true,
        EntryReady = entryReady,
        RecommendedAction = netScore >= 0 ? DecisionAction.OpenLong : DecisionAction.OpenShort,
        MissingConditions = entryReady ? Array.Empty<string>() : new[] { "legacy.score-blocked", "legacy.confidence-blocked" },
        Summary = "legacy assessment"
    };

    private static MarketEvidence BullishMarket()
    {
        var candles = new List<CandleEvidence>();
        var start = CollectedAt.AddMinutes(-15 * 48);
        for (var i = 0; i < 42; i++)
        {
            var close = 100m + i * .05m;
            var open = close - .04m;
            candles.Add(new(start.AddMinutes(15 * i), open, close + .35m, open - .30m, close, 100m, 10000m, 100, 55m));
        }

        var recent = new[]
        {
            (102.08m, 102.18m, 101.85m, 102.12m, 115m),
            (102.12m, 102.30m, 101.95m, 102.22m, 120m),
            (102.25m, 103.55m, 102.20m, 103.20m, 175m),
            (103.18m, 103.35m, 102.35m, 103.02m, 165m),
            (103.02m, 103.75m, 102.95m, 103.55m, 180m),
            (103.55m, 104.05m, 103.45m, 103.85m, 190m)
        };
        for (var i = 0; i < recent.Length; i++)
        {
            var value = recent[i];
            candles.Add(new(start.AddMinutes(15 * (42 + i)), value.Item1, value.Item2, value.Item3, value.Item4,
                value.Item5, value.Item5 * value.Item4, 150, value.Item5 * .58m));
        }

        var market = new MarketEvidence("BTCUSDT", 103.85m, 99m, 110m, 58, .012, .018, .025,
            new DerivativesSnapshot(.0001m, 1000m, 1.1m, 1.05m, 1.02m, 1.15m, .001m), CollectedAt)
        {
            Candles = candles,
            Quality = new MarketQualityEvidence
            {
                QualityScore = 92,
                LiquidityScore = .9,
                RelativeVolume = 1.4,
                AtrPercent = .012,
                BestBid = 103.84m,
                BestAsk = 103.86m,
                SpreadBps = 1.9
            }
        };
        return market with { Provenance = MarketEvidenceProvenanceCanonicalizerV1.Create(market, "test-provider", "Testnet") };
    }
}
