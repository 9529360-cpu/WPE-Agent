using WpeAgent.ModelOff;
using System.Text.Json.Nodes;

namespace WPE.Tests;

public sealed class ModelOffRoleRuntimeRegistryTests
{
    [Fact]
    public void DefaultRegistryRetainsExactlyThirteenCapabilitiesAcrossSevenUniqueRoles()
    {
        var result = ModelOffRoleRuntimeRegistryBuilderV1.CreateDefault();

        Assert.True(result.Valid, string.Join(',', result.Errors));
        var registry = Assert.IsType<ModelOffRoleRuntimeRegistryV1>(result.Registry);
        Assert.Equal(7, registry.Roles.Count);
        Assert.Equal(7, registry.Roles.Select(x => x.Role).Distinct().Count());
        Assert.Equal(13, registry.Capabilities.Count);
        Assert.Equal(13, registry.Capabilities.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enum.GetValues<ModelOffRuntimeRoleV1>(), registry.Roles.Select(x => x.Role));
        Assert.False(registry.MainnetEnabled);
        Assert.Equal("Testnet", registry.Environment);
        var teacher=registry.Capabilities.Single(x=>x.Id=="teacher");
        Assert.Equal("current",teacher.Lifecycle);Assert.True(teacher.Implemented);Assert.True(teacher.Accepted);
        Assert.Contains("opt-in notification",teacher.AcceptanceScope,StringComparison.Ordinal);
        var news=registry.Capabilities.Single(x=>x.Id=="news");
        Assert.Equal("yes",news.Maturity);Assert.True(news.Implemented);Assert.True(news.Accepted);
        Assert.Contains("article-text exclusion",news.AcceptanceScope,StringComparison.Ordinal);
        var strategy=registry.Capabilities.Single(x=>x.Id=="strategy");
        Assert.Equal("yes",strategy.Maturity);Assert.True(strategy.Implemented);Assert.True(strategy.Accepted);
        Assert.Contains("directional price geometry",strategy.AcceptanceScope,StringComparison.Ordinal);
        var macro=registry.Capabilities.Single(x=>x.Id=="macro");
        Assert.Equal("yes",macro.Maturity);Assert.True(macro.Implemented);Assert.True(macro.Accepted);
        Assert.Contains("append-only revisions",macro.AcceptanceScope,StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRegistryMatchesCanonicalCapabilityAuthority()
    {
        var registry=ModelOffRoleRuntimeRegistryBuilderV1.CreateDefault().Registry!;
        var root=JsonNode.Parse(File.ReadAllText(FindInventoryPath()))!.AsObject();
        var canonical=root["capabilities"]!.AsArray().ToDictionary(x=>x!["id"]!.GetValue<string>(),StringComparer.Ordinal);

        Assert.Equal(canonical.Keys.Order(StringComparer.Ordinal),registry.Capabilities.Select(x=>x.Id).Order(StringComparer.Ordinal));
        foreach(var runtime in registry.Capabilities)
        {
            var authority=canonical[runtime.Id]!;
            Assert.Equal(authority["maturity"]!.GetValue<string>(),runtime.Maturity);
            Assert.Equal(authority["implemented"]!.GetValue<bool>(),runtime.Implemented);
            Assert.Equal(authority["accepted"]!.GetValue<bool>(),runtime.Accepted);
            Assert.Equal(authority["lifecycle"]!.GetValue<string>(),runtime.Lifecycle);
            Assert.Equal(authority["acceptance_scope"]?.GetValue<string>(),runtime.AcceptanceScope);
        }
    }

    [Fact]
    public void CanonicalBytesAndHashAreStableAcrossInputAndNestedCollectionOrder()
    {
        var first = Accepted();
        var reordered = first.Reverse().Select(x => x with
        {
            Manifest = x.Manifest with
            {
                Triggers = x.Manifest.Triggers.Reverse().ToArray(),
                States = x.Manifest.States.Reverse().ToArray(),
                TerminalStates = x.Manifest.TerminalStates.Reverse().ToArray(),
                Commands = x.Manifest.Commands.Reverse().Select(c => c with { AllowedStates = c.AllowedStates.Reverse().ToArray() }).ToArray(),
                MemoryQueries = x.Manifest.MemoryQueries.Reverse().ToArray(),
                Permissions = x.Manifest.Permissions.Reverse().ToArray(),
                ModelOffTests = x.Manifest.ModelOffTests.Reverse().ToArray()
            }
        }).ToArray();

        var a = ModelOffRoleRuntimeRegistryBuilderV1.Compose(first).Registry!;
        var b = ModelOffRoleRuntimeRegistryBuilderV1.Compose(reordered).Registry!;
        Assert.Equal(a.CanonicalBytes, b.CanonicalBytes);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(64, a.Sha256.Length);
    }

    [Fact]
    public void MissingDuplicateUnacceptedAndRevokedRegistrationsFailClosed()
    {
        var accepted = Accepted();
        Reject(accepted[..6], "registry.requires-seven-accepted-roles");
        Reject([.. accepted[..6], accepted[0]], "registry.role-not-unique");
        Reject(accepted.Select((x, i) => i == 0 ? x with { Accepted = false } : x).ToArray(), "registry.manifest-not-accepted");
        Reject(accepted.Select((x, i) => i == 0 ? x with { Revoked = true } : x).ToArray(), "registry.manifest-revoked");
    }

    [Fact]
    public void UnknownMalformedAndIncompleteManifestsFailClosedWithoutPartialRegistry()
    {
        var accepted = Accepted();
        Reject(Replace(accepted, 0, accepted[0].Manifest with { Role = (ModelOffRuntimeRoleV1)999 }), "role.unknown");
        Reject(Replace(accepted, 0, accepted[0].Manifest with { SchemaVersion = "unknown/9" }), "schema.unknown");
        Reject(Replace(accepted, 0, accepted[0].Manifest with { Commands = [] }), "commands.missing-or-duplicate");
        Reject(Replace(accepted, 0, accepted[0].Manifest with { ModelPolicy = accepted[0].Manifest.ModelPolicy with { UnknownWorkOutcome = "succeeded" } }), "model.dependency-or-authority");
        var execution = accepted[(int)ModelOffRuntimeRoleV1.Execution].Manifest;
        Reject(Replace(accepted, (int)ModelOffRuntimeRoleV1.Execution, execution with
        {
            MutationBoundary = execution.MutationBoundary with { RequiredChain = null! }
        }), "registry.manifest-malformed");
    }

    [Fact]
    public void MutationRolesRemainTestnetOnlyAndUseMandatoryExecutionChain()
    {
        var registry = ModelOffRoleRuntimeRegistryBuilderV1.CreateDefault().Registry!;
        foreach (var role in new[] { ModelOffRuntimeRoleV1.Execution, ModelOffRuntimeRoleV1.Recovery })
        {
            var manifest = registry.Roles.Single(x => x.Role == role);
            Assert.True(manifest.MutationBoundary.ExternalMutation);
            Assert.Equal("Testnet", manifest.MutationBoundary.Environment);
            Assert.Equal(new[] { "RiskGate", "TradingExecutionGateway", "ReliableOrderExecutor" }, manifest.MutationBoundary.RequiredChain);
            Assert.True(manifest.MutationBoundary.TimeOfUseRevalidation);
        }

        var execution = Accepted().Single(x => x.Manifest.Role == ModelOffRuntimeRoleV1.Execution);
        var invalid = execution.Manifest with { MutationBoundary = execution.Manifest.MutationBoundary with { Environment = "Mainnet" } };
        Reject(Replace(Accepted(), (int)ModelOffRuntimeRoleV1.Execution, invalid), "mainnet.forbidden");
    }

    private static AcceptedModelOffRoleManifestV1[] Accepted() =>
        ModelOffRoleRuntimeCatalogV1.CreateAll().Select(x => new AcceptedModelOffRoleManifestV1(x, true, false)).ToArray();

    private static string FindInventoryPath()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var candidate=Path.Combine(directory.FullName,"Docs","product","model-off-capability-maturity.json");
            if(File.Exists(candidate))return candidate;
        }
        throw new FileNotFoundException("Could not locate the canonical model-off capability inventory.");
    }

    private static AcceptedModelOffRoleManifestV1[] Replace(AcceptedModelOffRoleManifestV1[] values, int index, ModelOffRoleRuntimeManifestV1 replacement)
    {
        var copy = values.ToArray();
        copy[index] = copy[index] with { Manifest = replacement };
        return copy;
    }

    private static void Reject(IReadOnlyList<AcceptedModelOffRoleManifestV1> values, string error)
    {
        var result = ModelOffRoleRuntimeRegistryBuilderV1.Compose(values);
        Assert.False(result.Valid);
        Assert.Null(result.Registry);
        Assert.Contains(result.Errors, x => x.Contains(error, StringComparison.Ordinal));
    }
}
