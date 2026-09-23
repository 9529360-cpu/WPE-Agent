using WpeAgent.FinancialEvidence;
using WpeAgent.ModelOff;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

internal static class ModelOffFixtureCycleTestHarness
{
    internal sealed record Request(
        string CycleId,
        DateTimeOffset EvaluationTimeUtc,
        IReadOnlyList<FinancialEvidenceRecordV1>? Fixtures,
        FinancialEvidenceRetrievalRequestV1 Retrieval,
        bool MainnetRequested=false);

    internal sealed record Result(
        IReadOnlyDictionary<ModelOffAgentV1,ModelOffAgentOutputV1> Outputs,
        IReadOnlyDictionary<ModelOffAgentV1,ModelOffCanonicalDocumentV1> Documents,
        IReadOnlyDictionary<ModelOffAgentV1,string> Reports,
        IReadOnlyDictionary<ModelOffAgentV1,string> Alerts,
        IReadOnlyList<ModelOffCanonicalDocumentV1> Handoffs,
        IReadOnlyDictionary<ModelOffAgentV1,string> AuditCoverage,
        bool EligibleForRiskIncrease,
        string Code);

    internal static async Task<Result> RunAsync(
        Request request,
        AgentSqliteStore auditStore,
        ModelExplanationAttachmentV1? explanation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(auditStore);
        if(string.IsNullOrWhiteSpace(request.CycleId))throw new ArgumentException("Cycle id is required.",nameof(request));
        if(request.EvaluationTimeUtc==default||request.EvaluationTimeUtc.Offset!=TimeSpan.Zero)throw new ArgumentException("An injected UTC evaluation time is required.",nameof(request));
        _=explanation;

        AuthorizedLocalCorpusResultV1 corpus;
        try{corpus=new AuthorizedLocalCorpusV1().Collect(request.Fixtures,request.Retrieval);}
        catch(Exception ex) when(ex is ArgumentException or FinancialEvidenceValidationException)
        {corpus=new(false,[],["evidence.fixture-invalid"]);}

        var corpusReasons=corpus.ReasonCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var marketSources=corpus.Accepted
            ?corpus.Records.Select(record=>new ModelOffSourceV1(
                record.RecordId,ModelOffSourceKindV1.Market,record.ObservedAt,record.Draft.RecordedAt,
                ModelOffSourceStatusV1.Available,NormalizeHash(record.RecordHash))).ToArray()
            :[BlockedSource("market-fixture-gate",request.EvaluationTimeUtc,corpusReasons)];

        var market=Output(ModelOffAgentV1.Market,request,"market",marketSources,corpus.Accepted,"record_market",corpusReasons,
            new{fixture_count=corpus.Records.Count,source="authorized_local_fixture"});
        var marketDocument=ModelOffCanonicalSerializerV1.Serialize(market);

        var researchSource=DownstreamSource("market:BTCUSDT",ModelOffSourceKindV1.Market,request.EvaluationTimeUtc,marketDocument,ModelOffEligibilityV1.IsEligibleForDownstream(market));
        var marketEvidenceSha256=researchSource.ArtifactHash!["sha256:".Length..];
        var research=DeterministicResearchCapabilityProducerV1.Produce(new(
            ModelOffResearchCapabilityV1.Technical,Id(request,"research"),request.CycleId,request.EvaluationTimeUtc,
            DeterministicResearchCapabilityProducerV1.InputSchema,"wpe.technical-method",DeterministicResearchCapabilityProducerV1.MethodVersion,
            [researchSource],System.Text.Json.JsonSerializer.SerializeToElement(new{
                schema="wpe.technical-assessment/1.0",symbol="BTCUSDT",marketSymbol="BTCUSDT",
                observedAtUtc=request.EvaluationTimeUtc,marketEvidenceSha256,rsi=50d,trend15m=0d,trend1h=0d,trend4h=0d}),[]));
        var researchDocument=ModelOffCanonicalSerializerV1.Serialize(research);

        var researchReady=ModelOffEligibilityV1.IsEligibleForDownstream(research);
        var strategyReasons=researchReady?Array.Empty<string>():["strategy.research-ineligible"];
        var strategy=Output(ModelOffAgentV1.Strategy,request,"strategy",
            [DownstreamSource("research-output",ModelOffSourceKindV1.Strategy,request.EvaluationTimeUtc,researchDocument,researchReady)],
            researchReady,"hold",strategyReasons,new{action="hold",deterministic_tie_break=false});
        var strategyDocument=ModelOffCanonicalSerializerV1.Serialize(strategy);

        var riskInputState=ModelOffEligibilityV1.IsEligibleForDownstream(strategy)?ModelOffInputStateV1.Available:ModelOffInputStateV1.Unknown;
        var riskContract=new RiskAndPositionPlanner().EvaluateModelOffIntent(new(
            DecisionAction.Hold,"BTCUSDT",request.EvaluationTimeUtc,TimeSpan.FromMinutes(5),request.MainnetRequested,
            [new("strategy",riskInputState,"sha256:"+strategyDocument.Sha256,request.EvaluationTimeUtc)]));
        var riskReasons=riskContract.Rules.Where(rule=>!rule.Passed).Select(rule=>rule.ReasonCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var riskReady=researchReady&&riskContract.EligibleForRiskIncrease&&!request.MainnetRequested;
        var risk=Output(ModelOffAgentV1.Risk,request,"risk",
            [DownstreamSource("strategy-output",ModelOffSourceKindV1.Strategy,request.EvaluationTimeUtc,strategyDocument,riskReady)],
            riskReady,riskReady?"risk_approved":"block",riskReasons,
            new{riskContract.IntentSha256,riskContract.LedgerSha256,eligible=riskReady,mainnet=request.MainnetRequested},
            riskContract.Rules.Select(rule=>System.Text.Json.JsonSerializer.SerializeToElement(rule)).ToArray());
        var riskDocument=ModelOffCanonicalSerializerV1.Serialize(risk);

        var executionReady=ModelOffEligibilityV1.IsEligibleForDownstream(risk)&&!request.MainnetRequested;
        var executionReasons=executionReady?Array.Empty<string>():[request.MainnetRequested?"execution.mainnet-disabled":"execution.risk-blocked"];
        var execution=Output(ModelOffAgentV1.Execution,request,"execution",
            [DownstreamSource("risk-output",ModelOffSourceKindV1.Audit,request.EvaluationTimeUtc,riskDocument,executionReady)],
            executionReady,"no_mutation",executionReasons,new{mutation_attempted=false,gateway_required=true,executor="ReliableOrderExecutor"});
        var executionDocument=ModelOffCanonicalSerializerV1.Serialize(execution);

        var recoveryReady=ModelOffEligibilityV1.IsEligibleForDownstream(execution);
        var recoveryReasons=recoveryReady?Array.Empty<string>():["recovery.execution-blocked"];
        var recovery=Output(ModelOffAgentV1.Recovery,request,"recovery",
            [DownstreamSource("execution-output",ModelOffSourceKindV1.Audit,request.EvaluationTimeUtc,executionDocument,recoveryReady)],
            recoveryReady,"no_recovery_required",recoveryReasons,new{quarantined=!recoveryReady,resubmit_allowed=false});
        var recoveryDocument=ModelOffCanonicalSerializerV1.Serialize(recovery);

        var upstream=new[]{marketDocument,researchDocument,strategyDocument,riskDocument,executionDocument,recoveryDocument};
        var upstreamOutputs=new[]{market,research,strategy,risk,execution,recovery};
        var auditReady=upstreamOutputs.All(ModelOffEligibilityV1.IsEligibleForDownstream);
        var auditReasons=auditReady?Array.Empty<string>():["audit.upstream-ineligible"];
        var auditSources=upstreamOutputs.Zip(upstream,(output,document)=>DownstreamSource(
            output.OutputId,ModelOffSourceKindV1.Audit,request.EvaluationTimeUtc,document,
            ModelOffEligibilityV1.IsEligibleForDownstream(output))).ToArray();
        var audit=Output(ModelOffAgentV1.Audit,request,"audit",auditSources,auditReady,auditReady?"record_audit":"block",auditReasons,
            new{expected_output_count=7,upstream_hashes=upstream.Select(document=>document.Sha256).ToArray(),self=Id(request,"audit")});
        var auditDocument=ModelOffCanonicalSerializerV1.Serialize(audit);

        var outputArray=upstreamOutputs.Append(audit).ToArray();
        var documentArray=upstream.Append(auditDocument).ToArray();
        var outputs=outputArray.ToDictionary(output=>output.Agent);
        var documents=outputArray.Zip(documentArray).ToDictionary(pair=>pair.First.Agent,pair=>pair.Second);

        var persisted=true;
        var persistenceCode="audit.persisted";
        try
        {
            foreach(var pair in outputArray.Zip(documentArray))
            {
                var saved=await auditStore.SaveModelOffCanonicalAuditAsync(pair.First,pair.Second,ct);
                if(!saved.Succeeded){persisted=false;persistenceCode=saved.Code;break;}
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{persisted=false;persistenceCode="audit.persistence-failed";}

        var reports=outputs.ToDictionary(pair=>pair.Key,pair=>ModelOffFixedTemplatesV1.RenderReport(pair.Value));
        var alerts=outputs.ToDictionary(pair=>pair.Key,pair=>ModelOffFixedTemplatesV1.RenderAlert(pair.Value,new(
            ModelOffEligibilityV1.IsEligibleForDownstream(pair.Value)?ModelOffAlertSeverityV1.Info:ModelOffAlertSeverityV1.High,
            pair.Value.Decision.ReasonCodes.FirstOrDefault()??"model-off.ready",pair.Key.ToString().ToLowerInvariant(),
            ModelOffEligibilityV1.IsEligibleForDownstream(pair.Value)?"continue_deterministically":"block_risk_increase",null)));

        var handoffs=new List<ModelOffCanonicalDocumentV1>();
        for(var index=0;index<outputArray.Length-1;index++)
        {
            var ready=ModelOffEligibilityV1.IsEligibleForDownstream(outputArray[index]);
            handoffs.Add(ModelOffCanonicalSerializerV1.SerializeHandoff(new(
                $"{request.CycleId}-handoff-{index+1}",request.CycleId,outputArray[index].Agent,outputArray[index+1].Agent,
                outputArray[index].OutputId,documentArray[index].Sha256,
                ready?ModelOffHandoffStatusV1.Ready:ModelOffHandoffStatusV1.Blocked,
                ready?["continue"]:[],["model_override","direct_mutation"],
                ready?[]:outputArray[index].Decision.ReasonCodes,
                request.EvaluationTimeUtc,request.EvaluationTimeUtc.AddMinutes(5))));
        }

        var coverage=documents.ToDictionary(pair=>pair.Key,pair=>pair.Value.Sha256);
        var eligible=auditReady&&ModelOffEligibilityV1.IsEligibleForDownstream(audit)&&persisted;
        return new(outputs,documents,reports,alerts,handoffs,coverage,eligible,
            eligible?"model-off.cycle-ready":persistenceCode=="audit.persisted"?"model-off.cycle-blocked":persistenceCode);
    }

    internal static Request RequestFor(string cycle,DateTimeOffset at)=>new(
        cycle,at,[ModelOffCollectionCorpusTests.MarketRecord()],ModelOffCollectionCorpusTests.Request());

    private static ModelOffAgentOutputV1 Output(
        ModelOffAgentV1 agent,Request request,string suffix,IReadOnlyList<ModelOffSourceV1> sources,
        bool succeeded,string action,IReadOnlyList<string> reasons,object facts,
        IReadOnlyList<System.Text.Json.JsonElement>? calculations=null)
        =>new(agent,Id(request,suffix),request.CycleId,request.EvaluationTimeUtc,request.EvaluationTimeUtc,
            "wpe.model-off-cycle-input/1.0",new($"wpe.{suffix}-composition","1.0"),sources,
            succeeded?ModelOffOutputStatusV1.Succeeded:ModelOffOutputStatusV1.Blocked,
            new(succeeded?ModelOffUncertaintyLevelV1.None:ModelOffUncertaintyLevelV1.Unknown,reasons,succeeded?[]:["downstream_eligibility"]),
            System.Text.Json.JsonSerializer.SerializeToElement(facts),calculations??[],new(action,succeeded,reasons),[],
            ModelOffFixedTemplatesV1.SummaryVersion);

    private static string Id(Request request,string suffix)=>$"{request.CycleId}-{suffix}";
    private static ModelOffSourceV1 DownstreamSource(string id,ModelOffSourceKindV1 kind,DateTimeOffset at,ModelOffCanonicalDocumentV1 document,bool ready)=>
        new(id,kind,at,at,ready?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Unknown,"sha256:"+document.Sha256);
    private static ModelOffSourceV1 BlockedSource(string id,DateTimeOffset at,IReadOnlyList<string> reasons)=>
        new(id,ModelOffSourceKindV1.Config,at,at,ModelOffSourceStatusV1.Unknown,Hash(string.Join("|",reasons.DefaultIfEmpty("evidence.rejected"))));
    private static string NormalizeHash(string hash)=>hash.StartsWith("sha256:",StringComparison.Ordinal)?hash:"sha256:"+hash;
    private static string Hash(string value)=>"sha256:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
