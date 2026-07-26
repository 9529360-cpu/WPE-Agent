namespace WpeAgent.ModelOff;

public enum ModelOffRuntimeRoleV1 { Market,Research,Strategy,Risk,Execution,Recovery,Audit }
public enum ModelOffRuntimeStateV1 { Idle,Running,Succeeded,Degraded,Abstained,Blocked,Failed }
public enum ModelOffPermissionV1 { ReadAuthorizedMarket,ReadAuthorizedNews,ReadCanonicalKnowledge,ReadPositions,WriteCanonicalOutput,WriteAudit,SubmitTestnetMutation,ReconcileTestnetMutation }
public enum ModelOffAuthorityV1 { Observe,Propose,ExecuteTestnet }

public sealed record ModelOffRuntimeTriggerV1(string Id,string InputSchema);
public sealed record ModelOffRuntimeCommandV1(string Id,string Handler,string InputSchema,string OutputSchema,int MaximumItems,int TimeoutMilliseconds,IReadOnlyList<ModelOffRuntimeStateV1> AllowedStates,ModelOffRuntimeStateV1 SuccessState,ModelOffRuntimeStateV1 FailureState);
public sealed record ModelOffMemoryQueryV1(string Id,string Schema,int Limit,TimeSpan MaximumAge,bool CanonicalOnly);
public sealed record ModelOffRecoveryV1(int RetryLimit,string ResumeFrom,bool FailClosed,bool EmergencyStop);
public sealed record ModelOffAuditContractV1(string Schema,bool RecordInputHash,bool RecordOutputHash,bool RecordStateTransitions,bool AppendOnly);
public sealed record ModelOffModelPolicyV1(bool RequiredForRoutineWork,bool AttachmentAuthoritative,bool AttachmentUsedForDecision,string UnknownWorkOutcome);
public sealed record ModelOffRuntimeTestV1(string Id,string Fixture);
public sealed record ModelOffMutationBoundaryV1(bool ExternalMutation,string Environment,IReadOnlyList<string> RequiredChain,bool TimeOfUseRevalidation);

public sealed record ModelOffRoleRuntimeManifestV1(
    string SchemaVersion,ModelOffRuntimeRoleV1 Role,IReadOnlyList<ModelOffRuntimeTriggerV1> Triggers,
    IReadOnlyList<ModelOffRuntimeStateV1> States,ModelOffRuntimeStateV1 InitialState,IReadOnlyList<ModelOffRuntimeStateV1> TerminalStates,
    string InputSchema,string OutputSchema,IReadOnlyList<ModelOffRuntimeCommandV1> Commands,IReadOnlyList<ModelOffMemoryQueryV1> MemoryQueries,
    IReadOnlyList<ModelOffPermissionV1> Permissions,ModelOffAuthorityV1 MaximumAuthority,ModelOffRecoveryV1 Recovery,
    ModelOffAuditContractV1 Audit,ModelOffModelPolicyV1 ModelPolicy,IReadOnlyList<ModelOffRuntimeTestV1> ModelOffTests,
    ModelOffMutationBoundaryV1 MutationBoundary)
{
    public const string Schema="wpe.model-off-role-runtime/1.0";
}

public sealed record ModelOffRoleRuntimeValidationV1(bool Valid,IReadOnlyList<string> Errors);

public static class ModelOffRoleRuntimeValidatorV1
{
    private static readonly HashSet<ModelOffRuntimeStateV1> RequiredStates=Enum.GetValues<ModelOffRuntimeStateV1>().ToHashSet();
    private static readonly HashSet<string> RequiredTests=new(StringComparer.Ordinal){"no_model_provider","mid_run_model_loss","deterministic_replay","attachment_removal_invariance","authorization_revoked","emergency_stop","unknown_work_abstains"};
    private static readonly string[] MutationChain=["RiskGate","TradingExecutionGateway","ReliableOrderExecutor"];
    private static readonly IReadOnlyDictionary<ModelOffRuntimeRoleV1,HashSet<ModelOffPermissionV1>> RequiredPermissions=new Dictionary<ModelOffRuntimeRoleV1,HashSet<ModelOffPermissionV1>>
    {
        [ModelOffRuntimeRoleV1.Market]=[ModelOffPermissionV1.ReadAuthorizedMarket,ModelOffPermissionV1.WriteCanonicalOutput],
        [ModelOffRuntimeRoleV1.Research]=[ModelOffPermissionV1.ReadAuthorizedNews,ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.WriteCanonicalOutput],
        [ModelOffRuntimeRoleV1.Strategy]=[ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.WriteCanonicalOutput],
        [ModelOffRuntimeRoleV1.Risk]=[ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.ReadPositions,ModelOffPermissionV1.WriteCanonicalOutput],
        [ModelOffRuntimeRoleV1.Execution]=[ModelOffPermissionV1.ReadPositions,ModelOffPermissionV1.SubmitTestnetMutation,ModelOffPermissionV1.WriteAudit],
        [ModelOffRuntimeRoleV1.Recovery]=[ModelOffPermissionV1.ReadPositions,ModelOffPermissionV1.ReconcileTestnetMutation,ModelOffPermissionV1.WriteAudit],
        [ModelOffRuntimeRoleV1.Audit]=[ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.WriteAudit]
    };

    public static ModelOffRoleRuntimeValidationV1 Validate(ModelOffRoleRuntimeManifestV1? manifest)
    {
        var errors=new List<string>();
        if(manifest is null)return new(false,["manifest.missing"]);
        Require(manifest.SchemaVersion==ModelOffRoleRuntimeManifestV1.Schema,"schema.unknown");
        Require(Enum.IsDefined(manifest.Role),"role.unknown");
        Require(manifest.Triggers is {Count:>0}&&Unique(manifest.Triggers.Select(x=>x.Id))&&manifest.Triggers.All(x=>Token(x.Id)&&Schema(x.InputSchema)),"triggers.invalid");
        Require(manifest.States is not null&&manifest.States.Count==RequiredStates.Count&&manifest.States.ToHashSet().SetEquals(RequiredStates),"states.invalid");
        Require(manifest.InitialState==ModelOffRuntimeStateV1.Idle,"states.initial-invalid");
        Require(manifest.TerminalStates is not null&&Unique(manifest.TerminalStates)&&manifest.TerminalStates.All(x=>RequiredStates.Contains(x))&&manifest.TerminalStates.Contains(ModelOffRuntimeStateV1.Abstained),"states.terminal-invalid");
        Require(Schema(manifest.InputSchema)&&Schema(manifest.OutputSchema),"schemas.invalid");
        Require(manifest.Commands is {Count:>0}&&Unique(manifest.Commands.Select(x=>x.Id)),"commands.missing-or-duplicate");
        if(manifest.Commands is not null)foreach(var command in manifest.Commands)
        {
            Require(Token(command.Id)&&Token(command.Handler)&&Schema(command.InputSchema)&&Schema(command.OutputSchema),"command.unknown-or-unbounded");
            Require(command.MaximumItems is>=1 and<=100&&command.TimeoutMilliseconds is>=1 and<=30000,"command.unbounded");
            Require(command.AllowedStates is {Count:>0}&&Unique(command.AllowedStates)&&command.AllowedStates.All(RequiredStates.Contains)&&RequiredStates.Contains(command.SuccessState)&&RequiredStates.Contains(command.FailureState),"command.states-invalid");
        }
        Require(manifest.MemoryQueries is {Count:>0}&&Unique(manifest.MemoryQueries.Select(x=>x.Id)),"memory.missing-or-duplicate");
        if(manifest.MemoryQueries is not null)foreach(var query in manifest.MemoryQueries)Require(Token(query.Id)&&Schema(query.Schema)&&query.Limit is>=1 and<=100&&query.MaximumAge>TimeSpan.Zero&&query.MaximumAge<=TimeSpan.FromDays(30)&&query.CanonicalOnly,"memory.unbounded-or-noncanonical");
        Require(manifest.Permissions is not null&&Unique(manifest.Permissions)&&manifest.Permissions.All(Enum.IsDefined),"permissions.duplicate-or-unknown");
        if(manifest.Permissions is not null)Require(!Overlaps(manifest.Permissions),"permissions.overlap");
        if(manifest.Permissions is not null&&RequiredPermissions.TryGetValue(manifest.Role,out var requiredPermissions)&&!manifest.Permissions.ToHashSet().SetEquals(requiredPermissions))
        {
            if(manifest.Role is ModelOffRuntimeRoleV1.Execution or ModelOffRuntimeRoleV1.Recovery)errors.Add("mutation.boundary-unsafe");
            else if(manifest.Permissions.Any(x=>x is ModelOffPermissionV1.SubmitTestnetMutation or ModelOffPermissionV1.ReconcileTestnetMutation))errors.Add("authority.tool-escalation");
            else errors.Add("permissions.overlap");
        }
        Require(Enum.IsDefined(manifest.MaximumAuthority),"authority.unknown");
        Require(manifest.Recovery is not null&&manifest.Recovery.RetryLimit is>=0 and<=3&&Token(manifest.Recovery.ResumeFrom)&&manifest.Recovery.FailClosed&&manifest.Recovery.EmergencyStop,"recovery.invalid");
        Require(manifest.Audit is not null&&Schema(manifest.Audit.Schema)&&manifest.Audit.RecordInputHash&&manifest.Audit.RecordOutputHash&&manifest.Audit.RecordStateTransitions&&manifest.Audit.AppendOnly,"audit.invalid");
        Require(manifest.ModelPolicy is not null&&!manifest.ModelPolicy.RequiredForRoutineWork&&!manifest.ModelPolicy.AttachmentAuthoritative&&!manifest.ModelPolicy.AttachmentUsedForDecision&&manifest.ModelPolicy.UnknownWorkOutcome=="abstain","model.dependency-or-authority");
        Require(manifest.ModelOffTests is not null&&Unique(manifest.ModelOffTests.Select(x=>x.Id))&&manifest.ModelOffTests.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(RequiredTests)&&manifest.ModelOffTests.All(x=>Token(x.Fixture)),"model-off-tests.invalid");
        var mutating=manifest.Role is ModelOffRuntimeRoleV1.Execution or ModelOffRuntimeRoleV1.Recovery;
        Require(manifest.MutationBoundary is not null,"mutation.boundary-missing");
        if(manifest.MutationBoundary is not null)
        {
            if(mutating)Require(manifest.MutationBoundary.ExternalMutation&&manifest.MaximumAuthority==ModelOffAuthorityV1.ExecuteTestnet&&manifest.MutationBoundary.Environment=="Testnet"&&manifest.MutationBoundary.TimeOfUseRevalidation&&manifest.MutationBoundary.RequiredChain.SequenceEqual(MutationChain,StringComparer.Ordinal),"mutation.boundary-unsafe");
            else Require(!manifest.MutationBoundary.ExternalMutation&&manifest.MaximumAuthority!=ModelOffAuthorityV1.ExecuteTestnet&&manifest.MutationBoundary.RequiredChain.Count==0,"authority.tool-escalation");
            Require(!string.Equals(manifest.MutationBoundary.Environment,"Mainnet",StringComparison.OrdinalIgnoreCase),"mainnet.forbidden");
        }
        return new(errors.Count==0,errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        void Require(bool condition,string error){if(!condition)errors.Add(error);}
    }

    public static ModelOffRoleRuntimeValidationV1 ValidateSet(IReadOnlyList<ModelOffRoleRuntimeManifestV1>? manifests)
    {
        var errors=new List<string>();
        if(manifests is null||manifests.Count!=7)return new(false,["set.requires-seven"]);
        if(!Unique(manifests.Select(x=>x.Role)))errors.Add("set.duplicate-role");
        if(!manifests.Select(x=>x.Role).ToHashSet().SetEquals(Enum.GetValues<ModelOffRuntimeRoleV1>()))errors.Add("set.missing-or-unknown-role");
        foreach(var manifest in manifests)errors.AddRange(Validate(manifest).Errors.Select(x=>$"{manifest.Role}:{x}"));
        return new(errors.Count==0,errors.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool Token(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=128&&value.All(c=>char.IsLetterOrDigit(c)||c is '-' or '_' or '.' or '/');
    private static bool Schema(string? value)=>Token(value)&&value!.Contains('/',StringComparison.Ordinal);
    private static bool Unique<T>(IEnumerable<T> values) where T:notnull{var seen=new HashSet<T>();return values.All(seen.Add);}
    private static bool Overlaps(IReadOnlyList<ModelOffPermissionV1> permissions)=>permissions.Contains(ModelOffPermissionV1.SubmitTestnetMutation)&&permissions.Contains(ModelOffPermissionV1.ReconcileTestnetMutation);
}

public static class ModelOffRoleRuntimeCatalogV1
{
    public static IReadOnlyList<ModelOffRoleRuntimeManifestV1> CreateAll()=>Enum.GetValues<ModelOffRuntimeRoleV1>().Select(Create).ToArray();
    public static ModelOffRoleRuntimeManifestV1 Create(ModelOffRuntimeRoleV1 role)
    {
        if(!Enum.IsDefined(role))throw new ArgumentOutOfRangeException(nameof(role));
        var mutation=role is ModelOffRuntimeRoleV1.Execution or ModelOffRuntimeRoleV1.Recovery;
        var permissions=role switch
        {
            ModelOffRuntimeRoleV1.Market=>new[]{ModelOffPermissionV1.ReadAuthorizedMarket,ModelOffPermissionV1.WriteCanonicalOutput},
            ModelOffRuntimeRoleV1.Research=>new[]{ModelOffPermissionV1.ReadAuthorizedNews,ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.WriteCanonicalOutput},
            ModelOffRuntimeRoleV1.Strategy=>new[]{ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.WriteCanonicalOutput},
            ModelOffRuntimeRoleV1.Risk=>new[]{ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.ReadPositions,ModelOffPermissionV1.WriteCanonicalOutput},
            ModelOffRuntimeRoleV1.Execution=>new[]{ModelOffPermissionV1.ReadPositions,ModelOffPermissionV1.SubmitTestnetMutation,ModelOffPermissionV1.WriteAudit},
            ModelOffRuntimeRoleV1.Recovery=>new[]{ModelOffPermissionV1.ReadPositions,ModelOffPermissionV1.ReconcileTestnetMutation,ModelOffPermissionV1.WriteAudit},
            _=>new[]{ModelOffPermissionV1.ReadCanonicalKnowledge,ModelOffPermissionV1.WriteAudit}
        };
        var id=role.ToString().ToLowerInvariant();
        return new(ModelOffRoleRuntimeManifestV1.Schema,role,[new($"{id}-canonical-ready",$"wpe.{id}-input/1.0")],Enum.GetValues<ModelOffRuntimeStateV1>(),ModelOffRuntimeStateV1.Idle,[ModelOffRuntimeStateV1.Succeeded,ModelOffRuntimeStateV1.Degraded,ModelOffRuntimeStateV1.Abstained,ModelOffRuntimeStateV1.Blocked,ModelOffRuntimeStateV1.Failed],$"wpe.{id}-input/1.0",ModelOffAgentOutputV1.Schema,[new($"run-{id}",$"deterministic-{id}",$"wpe.{id}-input/1.0",ModelOffAgentOutputV1.Schema,100,30000,[ModelOffRuntimeStateV1.Idle,ModelOffRuntimeStateV1.Degraded],ModelOffRuntimeStateV1.Succeeded,ModelOffRuntimeStateV1.Failed)],[new($"{id}-recent-canonical",ModelOffAgentOutputV1.Schema,32,TimeSpan.FromDays(7),true)],permissions,mutation?ModelOffAuthorityV1.ExecuteTestnet:role==ModelOffRuntimeRoleV1.Audit?ModelOffAuthorityV1.Observe:ModelOffAuthorityV1.Propose,new(2,"last-verified-checkpoint",true,true),new("wpe.role-runtime-audit/1.0",true,true,true,true),new(false,false,false,"abstain"),Tests(),mutation?new(true,"Testnet",["RiskGate","TradingExecutionGateway","ReliableOrderExecutor"],true):new(false,"None",[],false));
    }
    private static IReadOnlyList<ModelOffRuntimeTestV1> Tests()=>[new("no_model_provider","no-model"),new("mid_run_model_loss","model-loss"),new("deterministic_replay","replay"),new("attachment_removal_invariance","remove-attachment"),new("authorization_revoked","authorization-revoked"),new("emergency_stop","emergency-stop"),new("unknown_work_abstains","unknown-work")];
}
