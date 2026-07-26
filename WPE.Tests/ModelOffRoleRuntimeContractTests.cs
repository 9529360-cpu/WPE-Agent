using WpeAgent.ModelOff;
using System.Text.Json;

namespace WPE.Tests;

public sealed class ModelOffRoleRuntimeContractTests
{
    [Fact]
    public void SevenAggregateManifestsAreCompleteDeterministicAndModelIndependent()
    {
        var first=ModelOffRoleRuntimeCatalogV1.CreateAll();var second=ModelOffRoleRuntimeCatalogV1.CreateAll();
        Assert.Equal(7,first.Count);Assert.True(ModelOffRoleRuntimeValidatorV1.ValidateSet(first).Valid);Assert.Equal(JsonSerializer.Serialize(first),JsonSerializer.Serialize(second));
        Assert.Equal(Enum.GetValues<ModelOffRuntimeRoleV1>(),first.Select(x=>x.Role));
        Assert.All(first,x=>{Assert.False(x.ModelPolicy.RequiredForRoutineWork);Assert.Equal("abstain",x.ModelPolicy.UnknownWorkOutcome);Assert.Contains(x.TerminalStates,s=>s==ModelOffRuntimeStateV1.Abstained);Assert.True(x.Recovery.FailClosed);Assert.True(x.Audit.AppendOnly);});
    }

    [Theory]
    [InlineData(ModelOffRuntimeRoleV1.Execution)]
    [InlineData(ModelOffRuntimeRoleV1.Recovery)]
    public void MutationRolesRequireExactTestnetSafetyChain(ModelOffRuntimeRoleV1 role)
    {
        var manifest=ModelOffRoleRuntimeCatalogV1.Create(role);Assert.True(ModelOffRoleRuntimeValidatorV1.Validate(manifest).Valid);
        Assert.Equal(new[]{"RiskGate","TradingExecutionGateway","ReliableOrderExecutor"},manifest.MutationBoundary.RequiredChain);Assert.Equal("Testnet",manifest.MutationBoundary.Environment);Assert.True(manifest.MutationBoundary.TimeOfUseRevalidation);
    }

    [Fact]
    public void NonMutationToolsNeverGrantExecutionAuthority()
    {
        foreach(var manifest in ModelOffRoleRuntimeCatalogV1.CreateAll().Where(x=>x.Role is not ModelOffRuntimeRoleV1.Execution and not ModelOffRuntimeRoleV1.Recovery))
        {Assert.False(manifest.MutationBoundary.ExternalMutation);Assert.NotEqual(ModelOffAuthorityV1.ExecuteTestnet,manifest.MaximumAuthority);Assert.Empty(manifest.MutationBoundary.RequiredChain);}
    }

    [Theory]
    [InlineData(ModelOffRuntimeRoleV1.Market,ModelOffPermissionV1.SubmitTestnetMutation,"authority.tool-escalation")]
    [InlineData(ModelOffRuntimeRoleV1.Market,ModelOffPermissionV1.ReadPositions,"permissions.overlap")]
    public void NonMutationRolesRejectPermissionsOutsideTheirExactAllowlist(ModelOffRuntimeRoleV1 role,ModelOffPermissionV1 permission,string expectedError)
    {
        var manifest=ModelOffRoleRuntimeCatalogV1.Create(role);
        var result=ModelOffRoleRuntimeValidatorV1.Validate(manifest with{Permissions=[..manifest.Permissions,permission]});
        Assert.False(result.Valid);Assert.Contains(expectedError,result.Errors);
    }

    [Theory]
    [InlineData(ModelOffRuntimeRoleV1.Execution,ModelOffPermissionV1.ReconcileTestnetMutation)]
    [InlineData(ModelOffRuntimeRoleV1.Recovery,ModelOffPermissionV1.SubmitTestnetMutation)]
    public void MutationRolesRejectSwappedMutationPermissions(ModelOffRuntimeRoleV1 role,ModelOffPermissionV1 replacement)
    {
        var manifest=ModelOffRoleRuntimeCatalogV1.Create(role);
        var permissions=manifest.Permissions.Select(x=>x is ModelOffPermissionV1.SubmitTestnetMutation or ModelOffPermissionV1.ReconcileTestnetMutation?replacement:x).ToArray();
        var result=ModelOffRoleRuntimeValidatorV1.Validate(manifest with{Permissions=permissions});
        Assert.False(result.Valid);Assert.Contains("mutation.boundary-unsafe",result.Errors);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void SetRejectsMissingDuplicateAndUnknownRoles(string mutation)
    {
        var manifests=ModelOffRoleRuntimeCatalogV1.CreateAll().ToList();
        if(mutation=="missing")manifests.RemoveAt(0);else if(mutation=="duplicate")manifests[0]=manifests[1];else manifests[0]=manifests[0] with{Role=(ModelOffRuntimeRoleV1)999};
        Assert.False(ModelOffRoleRuntimeValidatorV1.ValidateSet(manifests).Valid);
    }

    [Theory]
    [InlineData("duplicate-command")]
    [InlineData("unknown-state")]
    [InlineData("unbounded-items")]
    [InlineData("unbounded-timeout")]
    [InlineData("unbounded-memory")]
    [InlineData("noncanonical-memory")]
    [InlineData("unknown-permission")]
    [InlineData("overlapping-permission")]
    [InlineData("model-required")]
    [InlineData("model-authoritative")]
    [InlineData("mainnet")]
    [InlineData("missing-gateway")]
    [InlineData("tool-authority")]
    [InlineData("unknown-work-completes")]
    public void AdversarialManifestMutationsFailClosed(string mutation)
    {
        var value=ModelOffRoleRuntimeCatalogV1.Create(mutation is "mainnet" or "missing-gateway"?ModelOffRuntimeRoleV1.Execution:ModelOffRuntimeRoleV1.Market);
        value=mutation switch
        {
            "duplicate-command"=>value with{Commands=[value.Commands[0],value.Commands[0]]},
            "unknown-state"=>value with{Commands=[value.Commands[0] with{AllowedStates=[(ModelOffRuntimeStateV1)999]}]},
            "unbounded-items"=>value with{Commands=[value.Commands[0] with{MaximumItems=int.MaxValue}]},
            "unbounded-timeout"=>value with{Commands=[value.Commands[0] with{TimeoutMilliseconds=int.MaxValue}]},
            "unbounded-memory"=>value with{MemoryQueries=[value.MemoryQueries[0] with{Limit=int.MaxValue}]},
            "noncanonical-memory"=>value with{MemoryQueries=[value.MemoryQueries[0] with{CanonicalOnly=false}]},
            "unknown-permission"=>value with{Permissions=[(ModelOffPermissionV1)999]},
            "overlapping-permission"=>value with{Permissions=[ModelOffPermissionV1.SubmitTestnetMutation,ModelOffPermissionV1.ReconcileTestnetMutation]},
            "model-required"=>value with{ModelPolicy=value.ModelPolicy with{RequiredForRoutineWork=true}},
            "model-authoritative"=>value with{ModelPolicy=value.ModelPolicy with{AttachmentAuthoritative=true}},
            "mainnet"=>value with{MutationBoundary=value.MutationBoundary with{Environment="Mainnet"}},
            "missing-gateway"=>value with{MutationBoundary=value.MutationBoundary with{RequiredChain=["RiskGate","ReliableOrderExecutor"]}},
            "tool-authority"=>value with{MaximumAuthority=ModelOffAuthorityV1.ExecuteTestnet},
            "unknown-work-completes"=>value with{ModelPolicy=value.ModelPolicy with{UnknownWorkOutcome="succeeded"}},
            _=>value
        };
        Assert.False(ModelOffRoleRuntimeValidatorV1.Validate(value).Valid);
    }

    [Fact]
    public void RequiredModelOffFixturesCoverReplayLossRevocationStopAndAbstention()
    {
        var tests=ModelOffRoleRuntimeCatalogV1.Create(ModelOffRuntimeRoleV1.Risk).ModelOffTests.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        Assert.True(tests.SetEquals(new[]{"no_model_provider","mid_run_model_loss","deterministic_replay","attachment_removal_invariance","authorization_revoked","emergency_stop","unknown_work_abstains"}));
    }
}
