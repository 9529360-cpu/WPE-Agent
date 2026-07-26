using System.Text.Json;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class ModelOffCanonicalContractTests
{
    private static readonly DateTimeOffset EvaluationTime = new(2026, 7, 23, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GoldenCanonicalDocumentIsByteStableAcrossInputOrdering()
    {
        var first = ModelOffCanonicalSerializerV1.Serialize(Output());
        var second = ModelOffCanonicalSerializerV1.Serialize(Output(reverseCollections: true));

        Assert.Equal(first.Utf8Bytes, second.Utf8Bytes);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal("5c7c6a0badb31ffd1f25996a44fa1acbbc21631b6e116e23dd027891dea73dd4", first.Sha256);
        Assert.Contains("\"evaluation_time_utc\":\"2026-07-23T10:30:00.000Z\"", first.Json);
        Assert.DoesNotContain("model", first.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", first.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplanationRemovalAndMutationCannotChangeCanonicalHashOrEligibility()
    {
        var output = Output();
        var canonical = ModelOffCanonicalSerializerV1.Serialize(output);
        var original = new ModelExplanationAttachmentV1(output.OutputId, canonical.Sha256, "local", "a", EvaluationTime, "Buy because...", ModelExplanationStatusV1.Available);
        var mutated = new ModelExplanationAttachmentV1(output.OutputId, canonical.Sha256, "remote", "b", EvaluationTime.AddYears(1), "Opposite prose", ModelExplanationStatusV1.Failed);

        Assert.False(original.Authoritative);
        Assert.False(original.UsedForDecision);
        Assert.False(mutated.Authoritative);
        Assert.False(mutated.UsedForDecision);
        var attachmentJson = JsonSerializer.Serialize(mutated);
        Assert.Contains("\"schema\":\"wpe.model-explanation/1.0\"", attachmentJson);
        Assert.Contains("\"authoritative\":false", attachmentJson);
        Assert.Contains("\"used_for_decision\":false", attachmentJson);
        Assert.Contains("\"status\":\"failed\"", attachmentJson);
        var afterAttachmentMutation = ModelOffCanonicalSerializerV1.Serialize(output);
        Assert.Equal(canonical.Utf8Bytes, afterAttachmentMutation.Utf8Bytes);
        Assert.Equal(canonical.Sha256, afterAttachmentMutation.Sha256);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(output));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("prompt")]
    [InlineData("brain_response")]
    [InlineData("explanation")]
    public void CanonicalSerializationRejectsModelFieldsAtAnyDepth(string field)
    {
        var facts = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["nested"] = new Dictionary<string, string> { [field] = "forbidden" } });
        var error = Assert.Throws<InvalidOperationException>(() => ModelOffCanonicalSerializerV1.Serialize(Output() with { Facts = facts }));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownOrStaleSourcesAndAbstentionFailClosed()
    {
        var abstained = Output() with
        {
            Status = ModelOffOutputStatusV1.Abstained,
            Sources = [new("missing", ModelOffSourceKindV1.Market, null, null, ModelOffSourceStatusV1.Unknown, null)],
            Decision = new("abstain", true, ["source.unknown"]),
            Uncertainty = new(ModelOffUncertaintyLevelV1.Unknown, ["source.unknown"], ["ticker.price"])
        };

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(abstained));
        Assert.Contains("Decision: abstain | downstream=false", ModelOffFixedTemplatesV1.RenderSummary(abstained));
        Assert.Contains("action=abstain; downstream=false", ModelOffFixedTemplatesV1.RenderReport(abstained));
        Assert.Contains("Missing: ticker.price", ModelOffFixedTemplatesV1.RenderSummary(abstained));
        Assert.Contains("## Uncertainty and Missing Data", ModelOffFixedTemplatesV1.RenderReport(abstained));
    }

    [Fact]
    public void SourcesWithSamePartialIdentityHaveStableCanonicalOrdering()
    {
        var first = new ModelOffSourceV1("same", ModelOffSourceKindV1.Market, EvaluationTime.AddMinutes(-2), EvaluationTime, ModelOffSourceStatusV1.Available, "aa");
        var second = new ModelOffSourceV1("same", ModelOffSourceKindV1.Market, EvaluationTime.AddMinutes(-1), EvaluationTime, ModelOffSourceStatusV1.Available, "bb");

        var ordered = ModelOffCanonicalSerializerV1.Serialize(Output() with { Sources = [first, second] });
        var reversed = ModelOffCanonicalSerializerV1.Serialize(Output() with { Sources = [second, first] });

        Assert.Equal(ordered.Utf8Bytes, reversed.Utf8Bytes);
        Assert.Equal(ordered.Sha256, reversed.Sha256);
    }

    [Theory]
    [InlineData(ModelOffUncertaintyLevelV1.Material)]
    [InlineData(ModelOffUncertaintyLevelV1.Unknown)]
    public void MaterialOrUnknownUncertaintyIsIneligible(ModelOffUncertaintyLevelV1 level)
    {
        var output = Output() with { Uncertainty = new(level, ["uncertainty.present"], []) };

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
    }

    [Fact]
    public void MissingFieldsAreIneligibleEvenWhenUncertaintyIsBounded()
    {
        var output = Output() with { Uncertainty = new(ModelOffUncertaintyLevelV1.Bounded, ["field.missing"], ["price"]) };

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
    }

    [Fact]
    public void EmptySourceSetIsIneligible()
    {
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(Output() with { Sources = [] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BlankSourceIdentityIsRejectedAndIneligible(string? sourceId)
    {
        AssertMalformed(Output() with { Sources = [ValidSource() with { SourceId = sourceId! }] });
    }

    [Fact]
    public void MissingSourceAsOfIsRejectedAndIneligible()
    {
        AssertMalformed(Output() with { Sources = [ValidSource() with { AsOfUtc = null }] });
    }

    [Fact]
    public void MissingSourceReceivedAtIsRejectedAndIneligible()
    {
        AssertMalformed(Output() with { Sources = [ValidSource() with { ReceivedAtUtc = null }] });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void MissingOrBlankSourceArtifactHashIsRejectedAndIneligible(string? artifactHash)
    {
        AssertMalformed(Output() with { Sources = [ValidSource() with { ArtifactHash = artifactHash }] });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BlankDecisionActionIsRejectedAndIneligible(string? action)
    {
        AssertMalformed(Output() with { Decision = new(action!, true, []) });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BlankRuleSetIdIsRejectedAndIneligible(string? id)
    {
        AssertMalformed(Output() with { RuleSet = new(id!, "1.0.0") });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BlankRuleSetVersionIsRejectedAndIneligible(string? version)
    {
        AssertMalformed(Output() with { RuleSet = new("market-rules", version!) });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidSourceEnumValuesAreRejectedAndIneligible(bool invalidKind)
    {
        var source = invalidKind
            ? ValidSource() with { Kind = (ModelOffSourceKindV1)int.MaxValue }
            : ValidSource() with { Status = (ModelOffSourceStatusV1)int.MaxValue };

        AssertMalformed(Output() with { Sources = [source] });
    }

    [Fact]
    public void NullMetadataObjectsAreRejectedAndIneligible()
    {
        AssertMalformed(Output() with { RuleSet = null! });
        AssertMalformed(Output() with { Decision = null! });
        AssertMalformed(Output() with { Sources = null! });
        AssertMalformed(Output() with { Sources = [null!] });
    }

    [Fact]
    public void FixedTemplatesAndHandoffContainOnlyDeterministicCanonicalFields()
    {
        var output = Output();
        var canonical = ModelOffCanonicalSerializerV1.Serialize(output);
        var summary = ModelOffFixedTemplatesV1.RenderSummary(output);
        var alert = ModelOffFixedTemplatesV1.RenderAlert(output, new(ModelOffAlertSeverityV1.Info, "source.available", "none", "publish", null));
        var handoff = ModelOffCanonicalSerializerV1.SerializeHandoff(new("handoff-1", output.CycleId, ModelOffAgentV1.Market, ModelOffAgentV1.Strategy, output.OutputId, canonical.Sha256, ModelOffHandoffStatusV1.Ready, ["consume", "audit"], ["execute"], ["source.available"], EvaluationTime, null));

        Assert.Equal("[market] succeeded | cycle=cycle-1 | as_of=2026-07-23T10:29:00.000Z", summary.Split('\n')[0]);
        Assert.Contains("info source.available", alert);
        Assert.Contains("\"schema\":\"wpe.agent-handoff/1.0\"", handoff.Json);
        Assert.DoesNotContain("model", handoff.Json, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelOffAgentOutputV1 Output(bool reverseCollections = false)
    {
        var sources = new[]
        {
            new ModelOffSourceV1("z-feed", ModelOffSourceKindV1.Market, EvaluationTime.AddMinutes(-1), EvaluationTime, ModelOffSourceStatusV1.Available, "bb"),
            new ModelOffSourceV1("a-config", ModelOffSourceKindV1.Config, EvaluationTime.AddMinutes(-2), EvaluationTime, ModelOffSourceStatusV1.Available, "aa")
        };
        var reasons = new[] { "source.available", "policy.valid" };
        var calculations = new[]
        {
            JsonSerializer.SerializeToElement(new { value = 2.5m, formula = "spread/1.0" }),
            JsonSerializer.SerializeToElement(new { formula = "quality/1.0", value = 1 })
        };
        if (reverseCollections)
        {
            Array.Reverse(sources);
            Array.Reverse(reasons);
            Array.Reverse(calculations);
        }

        return new(
            ModelOffAgentV1.Market, "market-output-1", "cycle-1", EvaluationTime, EvaluationTime,
            "wpe.market-input/1.0", new("market-rules", "1.0.0"), sources,
            ModelOffOutputStatusV1.Succeeded, new(ModelOffUncertaintyLevelV1.None, reasons, []),
            JsonSerializer.SerializeToElement(new { symbol = "BTCUSDT", price = 50000.25m }), calculations,
            new("publish", true, reasons), [JsonSerializer.SerializeToElement(new { hash = "fixture" })],
            "wpe.market-summary/1.0");
    }

    private static ModelOffSourceV1 ValidSource()
        => new("feed", ModelOffSourceKindV1.Market, EvaluationTime.AddMinutes(-1), EvaluationTime, ModelOffSourceStatusV1.Available, "aa");

    private static void AssertMalformed(ModelOffAgentOutputV1 output)
    {
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.Throws<InvalidOperationException>(() => ModelOffCanonicalSerializerV1.Serialize(output));
    }
}
