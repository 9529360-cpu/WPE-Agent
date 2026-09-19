using WpeAgent.CrossAssetResearch;

namespace WPE.Tests;

public sealed class ResearchExecutionCostAuthorityV1Tests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalResearchArtifactProjectsStableExecutionCompatibleCostIdentity()
    {
        var artifact = Artifact(new(.0004m, .0003m));

        var first = ResearchExecutionCostAuthorityV1.Project(artifact);
        var second = ResearchExecutionCostAuthorityV1.Project(artifact);

        Assert.Equal(ResearchExecutionCostProjectionStateV1.Available, first.State);
        Assert.Equal("available", first.Code);
        var value = Assert.IsType<ResearchExecutionCostProjectionV1>(first.Value);
        Assert.Equal(.0004m, value.CommissionRate);
        Assert.Equal(.0003m, value.SlippageRate);
        Assert.StartsWith("rcm1-", value.CostModelVersion, StringComparison.Ordinal);
        Assert.Equal(69, value.CostModelVersion.Length);
        Assert.Equal(64, value.CostModelSha256.Length);
        Assert.Equal(artifact.ArtifactHash, value.ResearchArtifactSha256);
        Assert.False(value.ExecutionAuthority);
        var repeated=Assert.IsType<ResearchExecutionCostProjectionV1>(second.Value);
        Assert.Equal(value.CanonicalSha256,repeated.CanonicalSha256);
        Assert.True(value.CanonicalBytes.SequenceEqual(repeated.CanonicalBytes));
        Assert.Equal(value.CostModelVersion,repeated.CostModelVersion);
        Assert.Equal(value.ResearchArtifactSha256,repeated.ResearchArtifactSha256);
        Assert.True(ResearchExecutionCostAuthorityV1.IsCanonical(value));
        Assert.True(ResearchExecutionCostAuthorityV1.IsCanonical(repeated));
    }

    [Fact]
    public void SameCostNumbersAcrossDifferentResearchArtifactsShareCostModelVersion()
    {
        var firstArtifact = Artifact(new(.0004m, .0003m), datasetHash:new string('a', 64));
        var secondArtifact = Artifact(new(.0004m, .0003m), datasetHash:new string('c', 64));

        var first = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(firstArtifact).Value);
        var second = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(secondArtifact).Value);

        Assert.NotEqual(first.ResearchArtifactSha256, second.ResearchArtifactSha256);
        Assert.Equal(first.CostModelVersion, second.CostModelVersion);
        Assert.Equal(first.CostModelSha256, second.CostModelSha256);
        Assert.NotEqual(first.CanonicalSha256, second.CanonicalSha256);
    }

    [Fact]
    public void ChangedCommissionOrSlippageChangesCostModelIdentity()
    {
        var baseline = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(Artifact(new(.0004m, .0003m))).Value);
        var commission = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(Artifact(new(.0005m, .0003m))).Value);
        var slippage = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(Artifact(new(.0004m, .0004m))).Value);

        Assert.NotEqual(baseline.CostModelVersion, commission.CostModelVersion);
        Assert.NotEqual(baseline.CostModelVersion, slippage.CostModelVersion);
        Assert.NotEqual(commission.CostModelVersion, slippage.CostModelVersion);
    }

    [Fact]
    public void TamperedResearchArtifactIsInvalidAndProducesNoCostFact()
    {
        var artifact = Artifact(new(.0004m, .0003m));
        var tampered = artifact with { CostModel = new(.0009m, .0003m) };

        var result = ResearchExecutionCostAuthorityV1.Project(tampered);

        Assert.Equal(ResearchExecutionCostProjectionStateV1.Invalid, result.State);
        Assert.Equal("research-artifact-invalid", result.Code);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(true, false, "fixed-cost-not-execution-compatible")]
    [InlineData(false, true, "carry-cost-not-execution-compatible")]
    public void FixedOrCarryCostsCannotBeSilentlyDroppedForExecution(bool fixedCost, bool carry, string code)
    {
        var artifact = Artifact(new(
            .0004m,
            .0003m,
            FixedCostPerTrade: fixedCost ? 1m : 0m,
            BorrowRatePerDay: carry ? .0001m : 0m));

        var result = ResearchExecutionCostAuthorityV1.Project(artifact);

        Assert.Equal(ResearchExecutionCostProjectionStateV1.Unsupported, result.State);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("commission")]
    [InlineData("slippage")]
    [InlineData("fixed")]
    [InlineData("borrow")]
    public void InvalidCostDomainFailsClosed(string field)
    {
        var costs = field switch
        {
            "commission" => new ResearchCostModel(1m, .0003m),
            "slippage" => new ResearchCostModel(.0004m, 1m),
            "fixed" => new ResearchCostModel(.0004m, .0003m, -1m),
            _ => new ResearchCostModel(.0004m, .0003m, 0m, -.0001m)
        };
        var result = ResearchExecutionCostAuthorityV1.Project(Artifact(costs));

        Assert.Equal(ResearchExecutionCostProjectionStateV1.Invalid, result.State);
        Assert.Equal("cost-model-invalid", result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void CanonicalProjectionDetectsVersionRateAndAuthorityTampering()
    {
        var value = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(Artifact(new(.0004m, .0003m))).Value);

        Assert.False(ResearchExecutionCostAuthorityV1.IsCanonical(value with { CostModelVersion = "rcm1-" + new string('0', 64) }));
        Assert.False(ResearchExecutionCostAuthorityV1.IsCanonical(value with { CommissionRate = .0005m }));
        Assert.False(ResearchExecutionCostAuthorityV1.IsCanonical(value with { ExecutionAuthority = true }));
        Assert.False(ResearchExecutionCostAuthorityV1.IsCanonical(value with { ResearchArtifactSha256 = new string('z', 64) }));
    }

    [Fact]
    public void CostProjectionBindsStrategyVersionButDoesNotUseItAsCostIdentity()
    {
        var v1 = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(Artifact(new(.0004m, .0003m), strategyVersion:"v1")).Value);
        var v2 = Assert.IsType<ResearchExecutionCostProjectionV1>(
            ResearchExecutionCostAuthorityV1.Project(Artifact(new(.0004m, .0003m), strategyVersion:"v2")).Value);

        Assert.Equal(v1.CostModelVersion, v2.CostModelVersion);
        Assert.NotEqual(v1.CanonicalSha256, v2.CanonicalSha256);
        Assert.Equal("v1", v1.StrategyVersion);
        Assert.Equal("v2", v2.StrategyVersion);
    }

    private static ResearchEvidenceArtifact Artifact(
        ResearchCostModel costs,
        string strategyVersion = "v7",
        string? datasetHash = null)
    {
        var draft = new ResearchEvidenceArtifact(
            datasetHash ?? new string('a', 64),
            new string('b', 64),
            "wpe-research-code-v1",
            strategyVersion,
            42,
            Now.AddDays(-30),
            Now.AddDays(-1),
            costs,
            "adjustment-v1",
            "session-v1",
            Now,
            string.Empty);
        return draft with { ArtifactHash = CrossAssetResearchService.ComputeCanonicalHash(draft) };
    }
}
