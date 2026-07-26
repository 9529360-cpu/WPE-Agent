using System.Text.Json;
using WpeAgent.ModelOff;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffResearchCapabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 11, 20, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ModelOffResearchCapabilityV1.News, ModelOffSourceKindV1.News)]
    [InlineData(ModelOffResearchCapabilityV1.Technical, ModelOffSourceKindV1.Market)]
    [InlineData(ModelOffResearchCapabilityV1.Backtest, ModelOffSourceKindV1.Strategy)]
    public void SupportedCapabilitiesProduceVersionedCanonicalRepeatableOutputs(ModelOffResearchCapabilityV1 capability, ModelOffSourceKindV1 kind)
    {
        var input = Input(capability, [Source(kind)]);
        var first = DeterministicResearchCapabilityProducerV1.Produce(input);
        var second = DeterministicResearchCapabilityProducerV1.Produce(input);
        var firstDocument = ModelOffCanonicalSerializerV1.Serialize(first);
        var secondDocument = ModelOffCanonicalSerializerV1.Serialize(second);

        Assert.Equal(ModelOffOutputStatusV1.Succeeded, first.Status);
        Assert.Equal("record_research", first.Decision.Action);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(first));
        Assert.Equal(Now, first.GeneratedAtUtc);
        Assert.Equal(firstDocument.Sha256, secondDocument.Sha256);
        Assert.Equal(firstDocument.Utf8Bytes, secondDocument.Utf8Bytes);
        Assert.DoesNotContain("recommend", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("probability", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caus", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedFundamentalCapabilityAbstainsWithoutInventingFacts()
    {
        var capability = ModelOffResearchCapabilityV1.Fundamental;
        var output = DeterministicMemoryService.ProduceUnsupportedModelOff(Input(capability, [Source(ModelOffSourceKindV1.Config)]));

        Assert.Equal(ModelOffOutputStatusV1.Abstained, output.Status);
        Assert.Equal("abstain", output.Decision.Action);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.Contains($"research.unsupported.{capability.ToString().ToLowerInvariant()}", output.Decision.ReasonCodes);
        Assert.Equal("observed", output.Facts.GetProperty("state").GetString());
    }

    [Fact]
    public void MacroFactsProduceCanonicalRepeatableOutputWithoutModelInference()
    {
        var input = MacroInput();

        var first = DeterministicMemoryService.ProduceMacroModelOff(input);
        var second = DeterministicMemoryService.ProduceMacroModelOff(input);
        var firstDocument = ModelOffCanonicalSerializerV1.Serialize(first);
        var secondDocument = ModelOffCanonicalSerializerV1.Serialize(second);

        Assert.Equal(ModelOffOutputStatusV1.Succeeded, first.Status);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(first));
        Assert.Equal("record_research", first.Decision.Action);
        Assert.Equal("CPI_ALL_ITEMS", first.Facts.GetProperty("indicatorId").GetString());
        Assert.Equal(firstDocument.Sha256, secondDocument.Sha256);
        Assert.DoesNotContain("forecast", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caus", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("wrong-source")]
    [InlineData("wrong-schema")]
    [InlineData("missing-indicator")]
    [InlineData("invalid-value")]
    [InlineData("non-utc")]
    [InlineData("future-release")]
    [InlineData("temporal-order")]
    [InlineData("source-before-release")]
    public void InvalidMacroFactsAbstainAndRemainIneligible(string fixture)
    {
        var input = MacroInput();
        input = fixture switch
        {
            "missing-source" => input with { Sources = [] },
            "wrong-source" => input with { Sources = [Source(ModelOffSourceKindV1.News)] },
            "wrong-schema" => input with { Facts = MacroFacts(schema: "wpe.macro-facts/2.0") },
            "missing-indicator" => input with { Facts = MacroFacts(indicatorId: " ") },
            "invalid-value" => input with { Facts = MacroFacts(value: "not-a-number") },
            "non-utc" => input with { Facts = MacroFacts(releasedAt: Now.ToOffset(TimeSpan.FromHours(9)).AddMinutes(-3)) },
            "future-release" => input with { Facts = MacroFacts(releasedAt: Now.AddMinutes(1)) },
            "temporal-order" => input with { Facts = MacroFacts(observedAt: Now.AddMinutes(-1), releasedAt: Now.AddMinutes(-3)) },
            "source-before-release" => input with { Sources = [Source(ModelOffSourceKindV1.Macro) with { AsOfUtc = Now.AddMinutes(-4) }] },
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };

        var output = DeterministicMemoryService.ProduceMacroModelOff(input);

        Assert.Equal(ModelOffOutputStatusV1.Abstained, output.Status);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.NotEmpty(output.Decision.ReasonCodes);
    }

    [Theory]
    [InlineData("missing-sources")]
    [InlineData("missing-required-kind")]
    [InlineData("stale")]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("inconsistent")]
    [InlineData("invalid")]
    [InlineData("error")]
    [InlineData("future-as-of")]
    [InlineData("future-received")]
    [InlineData("missing-source-id")]
    [InlineData("missing-artifact-hash")]
    [InlineData("missing-facts")]
    public void KnownRejectionFixturesFailClosed(string fixture)
    {
        var source = Source(ModelOffSourceKindV1.News);
        IReadOnlyList<ModelOffSourceV1> sources = fixture switch
        {
            "missing-sources" => [],
            "missing-required-kind" => [source with { Kind = ModelOffSourceKindV1.Market }],
            "stale" => [source with { Status = ModelOffSourceStatusV1.Stale }],
            "unknown" => [source with { Status = ModelOffSourceStatusV1.Unknown }],
            "unsupported" => [source with { Status = ModelOffSourceStatusV1.Unsupported }],
            "inconsistent" => [source with { Status = ModelOffSourceStatusV1.Inconsistent }],
            "invalid" => [source with { Status = ModelOffSourceStatusV1.Invalid }],
            "error" => [source with { Status = ModelOffSourceStatusV1.Error }],
            "future-as-of" => [source with { AsOfUtc = Now.AddSeconds(1) }],
            "future-received" => [source with { ReceivedAtUtc = Now.AddSeconds(1) }],
            "missing-source-id" => [source with { SourceId = " " }],
            "missing-artifact-hash" => [source with { ArtifactHash = null }],
            "missing-facts" => [source],
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };

        var input = Input(ModelOffResearchCapabilityV1.News, sources);
        if (fixture == "missing-facts") input = input with { Facts = JsonSerializer.SerializeToElement(new { }) };
        var output = DeterministicResearchCapabilityProducerV1.Produce(input);
        Assert.Equal(ModelOffOutputStatusV1.Abstained, output.Status);
        Assert.False(output.Decision.EligibleForDownstream);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.NotEmpty(output.Decision.ReasonCodes);
        var refusal = ModelOffCanonicalSerializerV1.Serialize(output);
        Assert.NotEmpty(refusal.Sha256);
        Assert.Contains("\"status\":\"abstained\"", refusal.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("cycle")]
    [InlineData("schema")]
    [InlineData("method")]
    [InlineData("version")]
    [InlineData("wrong-schema-version")]
    [InlineData("wrong-method")]
    [InlineData("wrong-method-version")]
    [InlineData("non-utc")]
    [InlineData("non-object-facts")]
    public void MalformedContractsAreRejected(string fixture)
    {
        var input = Input(ModelOffResearchCapabilityV1.News, [Source(ModelOffSourceKindV1.News)]);
        input = fixture switch
        {
            "output" => input with { OutputId = " " },
            "cycle" => input with { CycleId = " " },
            "schema" => input with { InputSchema = "research" },
            "method" => input with { MethodId = " " },
            "version" => input with { MethodVersion = " " },
            "wrong-schema-version" => input with { InputSchema = "wpe.research-input/2.0" },
            "wrong-method" => input with { MethodId = "wpe.other-method" },
            "wrong-method-version" => input with { MethodVersion = "2.0" },
            "non-utc" => input with { EvaluationTimeUtc = Now.ToOffset(TimeSpan.FromHours(9)) },
            "non-object-facts" => input with { Facts = JsonSerializer.SerializeToElement(42) },
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        Assert.Throws<ArgumentException>(() => DeterministicResearchCapabilityProducerV1.Produce(input));
    }

    [Fact]
    public void AdjacentInvariantsPreserveExplicitFactsAndCanonicalSourceOrdering()
    {
        var later = Source(ModelOffSourceKindV1.News) with { SourceId = "z", ArtifactHash = "bb", AsOfUtc = Now.AddMinutes(-1) };
        var earlier = Source(ModelOffSourceKindV1.News) with { SourceId = "a", ArtifactHash = "aa", AsOfUtc = Now.AddMinutes(-2) };
        var input = Input(ModelOffResearchCapabilityV1.News, [later, earlier]);
        var reversed = input with { Sources = [earlier, later] };
        var first = ModelOffCanonicalSerializerV1.Serialize(DeterministicMemoryService.ProduceNewsModelOff(input));
        var second = ModelOffCanonicalSerializerV1.Serialize(DeterministicMemoryService.ProduceNewsModelOff(reversed));

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal("observed", input.Facts.GetProperty("state").GetString());
    }

    [Fact]
    public void CapabilitySpecificEntryPointsRejectCrossCapabilityCalls()
    {
        var news = Input(ModelOffResearchCapabilityV1.News, [Source(ModelOffSourceKindV1.News)]);
        Assert.Throws<ArgumentException>(() => StrategyResearchAgent.ProduceTechnicalModelOff(news));
        Assert.Throws<ArgumentException>(() => StrategyResearchAgent.ProduceBacktestModelOff(news));
        Assert.Throws<ArgumentException>(() => DeterministicMemoryService.ProduceMacroModelOff(news));
        Assert.Throws<ArgumentException>(() => DeterministicMemoryService.ProduceUnsupportedModelOff(news));
    }

    [Fact]
    public void CapabilitySpecificEntryPointsProduceTheSameCanonicalContract()
    {
        var technical = Input(ModelOffResearchCapabilityV1.Technical, [Source(ModelOffSourceKindV1.Market)]);
        var backtest = Input(ModelOffResearchCapabilityV1.Backtest, [Source(ModelOffSourceKindV1.Strategy)]);
        var news = Input(ModelOffResearchCapabilityV1.News, [Source(ModelOffSourceKindV1.News)]);
        var macro = MacroInput();

        Assert.Equal(ModelOffOutputStatusV1.Succeeded, StrategyResearchAgent.ProduceTechnicalModelOff(technical).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, StrategyResearchAgent.ProduceBacktestModelOff(backtest).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, DeterministicMemoryService.ProduceNewsModelOff(news).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, DeterministicMemoryService.ProduceMacroModelOff(macro).Status);
    }

    private static ModelOffResearchInputV1 Input(ModelOffResearchCapabilityV1 capability, IReadOnlyList<ModelOffSourceV1> sources) => new(
        capability, $"{capability.ToString().ToLowerInvariant()}-output", "cycle-1", Now,
        "wpe.research-input/1.0", $"wpe.{capability.ToString().ToLowerInvariant()}-method", "1.0",
        sources, JsonSerializer.SerializeToElement(new { state = "observed" }), []);

    private static ModelOffSourceV1 Source(ModelOffSourceKindV1 kind) =>
        new("fixture", kind, Now.AddMinutes(-2), Now.AddMinutes(-1), ModelOffSourceStatusV1.Available, "aa");

    private static ModelOffResearchInputV1 MacroInput() => new(
        ModelOffResearchCapabilityV1.Macro, "macro-output", "cycle-1", Now,
        "wpe.research-input/1.0", "wpe.macro-method", "1.0",
        [Source(ModelOffSourceKindV1.Macro)], MacroFacts(), []);

    private static JsonElement MacroFacts(
        string schema = "wpe.macro-facts/1.0",
        string indicatorId = "CPI_ALL_ITEMS",
        object? value = null,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? releasedAt = null) => JsonSerializer.SerializeToElement(new
        {
            schema,
            indicatorId,
            geography = "US",
            frequency = "monthly",
            unit = "index",
            observationAtUtc = observedAt ?? Now.AddDays(-30),
            releasedAtUtc = releasedAt ?? Now.AddMinutes(-3),
            value = value ?? 319.7m
        });
}
