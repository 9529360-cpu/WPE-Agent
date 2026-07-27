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
            Evidence = CopyEvidence(request.Evidence,markets:reversedMarkets),
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
        var macroSource=Assert.Single(research.Output.Sources,x=>x.Kind==ModelOffSourceKindV1.Macro);
        Assert.Equal("macro-cuur0000sa0-r2",macroSource.SourceId);Assert.Equal("sha256:"+new string('a',64),macroSource.ArtifactHash);
        Assert.Equal("verified",research.Output.Facts.GetProperty("macro_state").GetString());
        Assert.DoesNotContain("forecast",research.Document.Json,StringComparison.OrdinalIgnoreCase);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(research.Output));
    }

    [Fact]
    public void ResearchBindsTechnicalAssessmentToCanonicalMarketEvidence()
    {
        var research=ModelOffLiveCycleInputComposerV1.Compose(Request()).Single(x=>x.Output.Agent==ModelOffAgentV1.Research);
        var source=Assert.Single(research.Output.Sources,x=>x.SourceId=="technical-assessment-btcusdt");
        Assert.Equal(ModelOffSourceKindV1.Market,source.Kind);Assert.StartsWith("sha256:",source.ArtifactHash,StringComparison.Ordinal);
        Assert.Equal("verified",research.Output.Facts.GetProperty("technical_state").GetString());Assert.Equal(source.ArtifactHash,research.Output.Facts.GetProperty("technical_evidence_hash").GetString());
    }

    [Fact]
    public void ResearchRejectsCrossIdentityTechnicalAssessmentBeforeStrategy()
    {
        var request=Request() with{Assessments=[Assessment(symbol:"btcusdt")]};
        var research=ModelOffLiveCycleInputComposerV1.Compose(request).Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output;
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(research));Assert.Contains("live.research.technical-invalid",research.Decision.ReasonCodes);
        Assert.DoesNotContain(research.Sources,x=>x.SourceId.StartsWith("technical-assessment-",StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateMacroIndicatorBlocksWithoutPublishingMacroSource()
    {
        var macro=new PersistedMacroObservation("CUUR0000SA0",new(2026,6,1,0,0,0,TimeSpan.Zero),2,"US","monthly","index",334.1m,"bls-public-api-v2",new string('a',64),Now.AddMinutes(-2));
        var research=ModelOffLiveCycleInputComposerV1.Compose(Request() with{MacroObservations=[macro,macro]}).Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output;
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(research));Assert.Contains("live.research.macro-invalid",research.Decision.ReasonCodes);
        Assert.DoesNotContain(research.Sources,x=>x.Kind==ModelOffSourceKindV1.Macro);
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

    [Fact]
    public void CanonicalNewsEvidenceEntersResearchByHashWithoutBodyText()
    {
        var request=Request();var evidence=CopyEvidence(request.Evidence,news:[News()]);
        var research=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=evidence}).Single(x=>x.Output.Agent==ModelOffAgentV1.Research);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(research.Output));
        Assert.Equal(1,research.Output.Facts.GetProperty("news_evidence_count").GetInt32());
        Assert.Equal(1,research.Output.Facts.GetProperty("news_target_count").GetInt32());
        var source=Assert.Single(research.Output.Sources,x=>x.Kind==ModelOffSourceKindV1.News);
        Assert.StartsWith("sha256:",source.ArtifactHash,StringComparison.Ordinal);
        Assert.DoesNotContain("private full article body",research.Document.Json,StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wrong-host")]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("invalid-confidence")]
    [InlineData("non-utc")]
    [InlineData("all-sources-unavailable")]
    public void InvalidOrUnavailableNewsEvidenceFailsResearchClosed(string defect)
    {
        var request=Request();var item=News();IReadOnlyList<NewsEvidence> news=[item];IReadOnlyList<string> missing=[];
        if(defect=="wrong-host")news=[item with{Url="https://attacker.example/news"}];
        if(defect=="stale")news=[item with{CollectedAt=Now.AddMinutes(-6).UtcDateTime}];
        if(defect=="duplicate")news=[item,item];
        if(defect=="invalid-confidence")news=[item with{Confidence=double.NaN}];
        if(defect=="non-utc")news=[item with{PublishedAt=DateTime.SpecifyKind(item.PublishedAt!.Value,DateTimeKind.Unspecified)}];
        if(defect=="all-sources-unavailable"){news=[];missing=["SEC","CFTC","Federal Reserve","ECB","CoinDesk","Cointelegraph","Google News"];}
        var evidence=CopyEvidence(request.Evidence,news:news,missingSources:missing);
        var research=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=evidence}).Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output;
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(research));
        Assert.Contains(research.Decision.ReasonCodes,x=>x.StartsWith("live.research.news-",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-approved")]
    [InlineData("not-promoted")]
    [InlineData("invalid-number")]
    [InlineData("stale-validation")]
    [InlineData("future-validation")]
    public void ResearchAgentRequiresEligibleEvidenceForCurrentDecisionInstrument(string defect)
    {
        var request=Request();var btc=Research("BTCUSDT");var research=request.Research.ToDictionary(x=>x.Key,x=>x.Value);
        if(defect=="missing")research.Remove("BTCUSDT");
        if(defect=="not-approved")research["BTCUSDT"]=CopyResearch(btc,approved:false);
        if(defect=="not-promoted")research["BTCUSDT"]=CopyResearch(btc,promoted:false);
        if(defect=="invalid-number")research["BTCUSDT"]=CopyResearch(btc,qualityScore:double.NaN);
        if(defect=="stale-validation")research["BTCUSDT"]=CopyResearch(btc,validatedAt:Now.AddHours(-25));
        if(defect=="future-validation")research["BTCUSDT"]=CopyResearch(btc,validatedAt:Now.AddSeconds(1));
        var output=ModelOffLiveCycleInputComposerV1.Compose(request with{Research=research}).Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output;Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));Assert.Contains(output.Decision.ReasonCodes,reason=>reason.StartsWith("live.research.target-",StringComparison.Ordinal));
    }

    [Fact]
    public void UnrelatedResearchCannotSubstituteForCurrentInstrument()
    {
        var request=Request() with{Research=new Dictionary<string,ResearchValidationResult>{{"ETHUSDT",Research("ETHUSDT")}}};var output=ModelOffLiveCycleInputComposerV1.Compose(request).Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output;Assert.Contains("live.research.target-missing",output.Decision.ReasonCodes);Assert.DoesNotContain(output.Sources,source=>source.SourceId=="strategy-validation-ETHUSDT");
    }

    [Fact]
    public void BacktestValidationSourceRetainsItsOwnValidationTime()
    {
        var request=Request();var expected=request.Research["BTCUSDT"].ValidatedAtUtc;
        var research=ModelOffLiveCycleInputComposerV1.Compose(request).Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output;
        var source=Assert.Single(research.Sources,x=>x.SourceId=="strategy-validation-BTCUSDT");Assert.Equal(expected,source.AsOfUtc);
        Assert.Equal(expected,research.Facts.GetProperty("validations")[0].GetProperty("ValidatedAtUtc").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("missing-assessment")]
    [InlineData("stale-assessment")]
    [InlineData("direction-conflict")]
    [InlineData("bad-stop")]
    [InlineData("bad-take")]
    [InlineData("bad-rr")]
    [InlineData("bad-confidence")]
    [InlineData("bad-tier")]
    [InlineData("version-conflict")]
    [InlineData("lowercase-assessment")]
    [InlineData("bad-assessment-confidence")]
    public void StrategyAgentIndependentlyRejectsInvalidExecutablePlan(string defect)
    {
        var request=Request();var assessments=request.Assessments;var review=request.DecisionReview;
        if(defect=="missing-assessment")assessments=[];
        if(defect=="stale-assessment")assessments=[Assessment(fresh:false)];
        if(defect=="direction-conflict")assessments=[Assessment(recommended:DecisionAction.OpenShort)];
        if(defect=="bad-stop")review=Review(true,stop:101000m);
        if(defect=="bad-take")review=Review(true,take:99000m);
        if(defect=="bad-rr")review=Review(true,riskReward:0);
        if(defect=="bad-confidence")review=Review(true,confidence:1.1);
        if(defect=="bad-tier")review=Review(true,targetTier:4);
        if(defect=="version-conflict")review=Review(true,strategyVersion:"strategy-v2");
        if(defect=="lowercase-assessment")assessments=[Assessment(symbol:"btcusdt")];
        if(defect=="bad-assessment-confidence")assessments=[Assessment(confidence:1.1)];
        var strategy=ModelOffLiveCycleInputComposerV1.Compose(request with{Assessments=assessments,DecisionReview=review}).Single(x=>x.Output.Agent==ModelOffAgentV1.Strategy).Output;Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(strategy));Assert.Contains(strategy.Decision.ReasonCodes,reason=>reason.StartsWith("live.strategy.",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("zero-quantity")]
    [InlineData("zero-risk")]
    [InlineData("zero-exposure")]
    [InlineData("no-checks")]
    [InlineData("duplicate-checks")]
    [InlineData("approved-with-blocks")]
    [InlineData("blocked-level")]
    public void RiskAgentRejectsInternallyInconsistentApproval(string defect)
    {
        var risk=Risk(true);risk=defect switch{"zero-quantity"=>CopyRisk(risk,quantity:0),"zero-risk"=>CopyRisk(risk,riskAmount:0),"zero-exposure"=>CopyRisk(risk,exposure:0),"no-checks"=>CopyRisk(risk,checks:[]),"duplicate-checks"=>CopyRisk(risk,checks:["risk-gate","risk-gate"]),"approved-with-blocks"=>CopyRisk(risk,blocks:["unexpected"]),"blocked-level"=>CopyRisk(risk,level:"BLOCKED"),_=>risk};var output=ModelOffLiveCycleInputComposerV1.Compose(Request() with{RiskReview=risk}).Last().Output;Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));Assert.Contains(output.Decision.ReasonCodes,reason=>reason.StartsWith("live.risk.",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-position")]
    [InlineData("tampered-position")]
    [InlineData("stale-position")]
    [InlineData("replayed-confirmed-position")]
    [InlineData("incomplete-protection")]
    [InlineData("external-position")]
    public void RiskAgentRequiresCanonicalPositionManagementTruth(string defect)
    {
        var request=Request();
        if(defect=="missing-position")request=request with{PositionReconciliation=null};
        if(defect=="tampered-position")request=request with{PositionReconciliation=request.PositionReconciliation! with{CanonicalBytes=[..request.PositionReconciliation!.CanonicalBytes,0]}};
        if(defect=="stale-position")request=request with{PositionReconciliation=PositionReconciliationServiceV1.Reconcile([],[],Now-PositionReconciliationServiceV1.MaximumAge-TimeSpan.FromSeconds(1),Now)};
        if(defect=="replayed-confirmed-position")request=request with{PositionReconciliation=PositionReconciliationServiceV1.Reconcile([],[],Now-TimeSpan.FromHours(1),Now-TimeSpan.FromHours(1))};
        if(defect=="incomplete-protection")request=request with{ProtectionReconciliation=ProtectionReconciliationServiceV1.Reconcile([new ManagedPosition("BTCUSDT",PositionSide.Long,.01m,100000m,100100m,1m,2m,true,50000m)],[],Now,Now)};
        if(defect=="external-position")request=request with{ExternalPositionIsolation=ExternalPositionIsolationServiceV1.Evaluate([],[new ManagedPosition("ETHUSDT",PositionSide.Short,1m,3500m,3490m,0,0,false,3500m)],Now,Now)};

        var risk=ModelOffLiveCycleInputComposerV1.Compose(request).Last().Output;
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(risk));
        Assert.Contains(risk.Decision.ReasonCodes,reason=>reason.StartsWith("live.risk.",StringComparison.Ordinal)&&
            (reason.Contains("position-reconciliation",StringComparison.Ordinal)||reason.Contains("protection-reconciliation",StringComparison.Ordinal)||reason.Contains("external-position-isolation",StringComparison.Ordinal)));
    }

    [Fact]
    public void RiskAgentCarriesCanonicalPositionEvidenceHashes()
    {
        var request=Request();var risk=ModelOffLiveCycleInputComposerV1.Compose(request).Last().Output;
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(risk));
        Assert.Equal("sha256:"+request.PositionReconciliation!.CanonicalSha256,risk.Sources.Single(x=>x.SourceId=="position-reconciliation").ArtifactHash);
        Assert.Equal("sha256:"+request.ProtectionReconciliation!.CanonicalSha256,risk.Sources.Single(x=>x.SourceId=="protection-reconciliation").ArtifactHash);
        Assert.Equal("sha256:"+request.ExternalPositionIsolation!.CanonicalSha256,risk.Sources.Single(x=>x.SourceId=="external-position-isolation").ArtifactHash);
        Assert.Equal("confirmed",risk.Facts.GetProperty("position_reconciliation").GetString());
        Assert.Equal("confirmed",risk.Facts.GetProperty("protection_reconciliation").GetString());
        Assert.Equal("clear",risk.Facts.GetProperty("external_position_isolation").GetString());
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
    public void MarketAgentRequiresFreshAccountAndValidPositionStructure()
    {
        var request=Request();var staleAccount=request.Evidence.Account with{Timestamp=Now.AddMinutes(-6).UtcDateTime};var stale=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=CopyEvidence(request.Evidence,account:staleAccount)}).First().Output;Assert.Contains("live.account.invalid-or-stale",stale.Decision.ReasonCodes);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(stale));
        var duplicate=new ManagedPosition("BTCUSDT",PositionSide.Long,.01m,100000m,100100m,1m,2m,true,50000m);var invalid=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=CopyEvidence(request.Evidence,positions:[duplicate,duplicate])}).First().Output;Assert.Contains("live.positions.invalid",invalid.Decision.ReasonCodes);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(invalid));
    }

    [Fact]
    public void ResearchAgentRequiresFreshCanonicalTargetFundamentalEvidence()
    {
        var request=Request();var missing=CopyEvidence(request.Evidence);missing=new EvidencePack{CollectedAt=missing.CollectedAt,Completeness=missing.Completeness,Account=missing.Account,Positions=missing.Positions,Markets=missing.Markets,News=missing.News,Fundamentals=new Dictionary<string,CryptoInstrumentFundamentalV1>(),MissingSources=missing.MissingSources};var missingResearch=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=missing})[1].Output;Assert.Contains("live.research.fundamental-missing",missingResearch.Decision.ReasonCodes);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(missingResearch));
        var tampered=request.Evidence.Fundamentals.ToDictionary(x=>x.Key,x=>x.Value);tampered["BTCUSDT"]=tampered["BTCUSDT"] with{CanonicalBytes=[..tampered["BTCUSDT"].CanonicalBytes,0]};var badEvidence=new EvidencePack{CollectedAt=request.Evidence.CollectedAt,Completeness=request.Evidence.Completeness,Account=request.Evidence.Account,Positions=request.Evidence.Positions,Markets=request.Evidence.Markets,News=request.Evidence.News,Fundamentals=tampered,MissingSources=request.Evidence.MissingSources};var invalid=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=badEvidence})[1].Output;Assert.Contains("live.research.fundamental-invalid",invalid.Decision.ReasonCodes);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(invalid));
    }

    [Fact]
    public void InvalidMarketDoesNotRelabelIndependentValidSource()
    {
        var request=Request();var markets=new Dictionary<string,MarketEvidence>{{"BAD",Market("BAD",0,Now.UtcDateTime)},{"BTCUSDT",Market("BTCUSDT",100000m,Now.UtcDateTime)}};var market=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=CopyEvidence(request.Evidence,markets:markets)}).First().Output;Assert.Equal(ModelOffSourceStatusV1.Invalid,market.Sources.Single(source=>source.SourceId=="BAD").Status);Assert.Equal(ModelOffSourceStatusV1.Available,market.Sources.Single(source=>source.SourceId=="BTCUSDT").Status);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(market));
    }

    [Fact]
    public void MarketAgentRejectsMissingOrTamperedProviderProvenance()
    {
        var request=Request();var original=request.Evidence.Markets["BTCUSDT"];
        var missing=request.Evidence.Markets.ToDictionary(x=>x.Key,x=>x.Value);missing["BTCUSDT"]=original with{Provenance=null};
        var missingOutput=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=CopyEvidence(request.Evidence,markets:missing)}).First().Output;Assert.Contains("live.market.provenance-invalid",missingOutput.Decision.ReasonCodes);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(missingOutput));
        var tampered=request.Evidence.Markets.ToDictionary(x=>x.Key,x=>x.Value);tampered["BTCUSDT"]=original with{Price=original.Price+1};
        var tamperedOutput=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=CopyEvidence(request.Evidence,markets:tampered)}).First().Output;Assert.Contains("live.market.provenance-invalid",tamperedOutput.Decision.ReasonCodes);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(tamperedOutput));
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
                request.DecisionReview, request.RiskReview, request.PositionReconciliation!,
                request.ProtectionReconciliation!,request.ExternalPositionIsolation!,CancellationToken.None);

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
    public async Task ProductionResearchUsesSourceSpecificFreshnessWithoutRelabelingEvidence()
    {
        var directory=Path.Combine(Path.GetTempPath(),"wpe-research-freshness-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var request=Request();
            var research=request.Research.ToDictionary(x=>x.Key,x=>CopyResearch(x.Value,validatedAt:Now.AddHours(-12)));
            var fundamentals=request.Evidence.Fundamentals.ToDictionary(x=>x.Key,x=>Fundamental(x.Key,Now.AddHours(-12).UtcDateTime));
            var oldNews=News() with{PublishedAt=Now.AddDays(-6).UtcDateTime,CollectedAt=Now.AddMinutes(-1).UtcDateTime};
            var evidence=CopyEvidence(request.Evidence,news:[oldNews]);
            evidence=new EvidencePack{CollectedAt=evidence.CollectedAt,Completeness=evidence.Completeness,Account=evidence.Account,Positions=evidence.Positions,Markets=evidence.Markets,News=evidence.News,Fundamentals=fundamentals,MissingSources=evidence.MissingSources};
            var macro=new PersistedMacroObservation("CUUR0000SA0",new(2026,6,1,0,0,0,TimeSpan.Zero),1,"US","monthly","index",334.1m,"bls-public-api-v2",new string('a',64),Now.AddDays(-30));
            var inputs=ModelOffLiveCycleInputComposerV1.Compose(request with{Evidence=evidence,Research=research,MacroObservations=[macro]});
            var result=await new ModelOffProductionCycleOrchestratorV1(new AgentSqliteStore(Path.Combine(directory,"agent.db"),()=>Now)).RunAsync(new(request.CycleId,Now,inputs),CancellationToken.None);

            Assert.True(result.EligibleForRiskIncrease);
            Assert.Equal("model-off.production-cycle-ready",result.Code);
            var canonicalResearch=result.Outputs[ModelOffAgentV1.Research];
            Assert.Contains(canonicalResearch.Sources,x=>x.Kind==ModelOffSourceKindV1.News&&x.AsOfUtc==Now.AddDays(-6));
            Assert.Contains(canonicalResearch.Sources,x=>x.Kind==ModelOffSourceKindV1.Macro&&x.AsOfUtc==Now.AddDays(-30));
            Assert.Contains(canonicalResearch.Sources,x=>x.Kind==ModelOffSourceKindV1.Fundamental&&x.AsOfUtc==Now.AddHours(-12));
            Assert.Contains(canonicalResearch.Sources,x=>x.Kind==ModelOffSourceKindV1.Strategy&&x.AsOfUtc==Now.AddHours(-12));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try{Directory.Delete(directory,true);}catch(IOException){}
        }
    }

    [Fact]
    public async Task ProductionResearchRejectsMacroEvidenceOutsideTheBoundedWindow()
    {
        var directory=Path.Combine(Path.GetTempPath(),"wpe-research-stale-macro-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var request=Request();
            var macro=new PersistedMacroObservation("CUUR0000SA0",Now.AddDays(-70),1,"US","monthly","index",334.1m,"bls-public-api-v2",new string('a',64),Now.AddDays(-63));
            var inputs=ModelOffLiveCycleInputComposerV1.Compose(request with{MacroObservations=[macro]});
            Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(inputs.Single(x=>x.Output.Agent==ModelOffAgentV1.Research).Output));

            var result=await new ModelOffProductionCycleOrchestratorV1(new AgentSqliteStore(Path.Combine(directory,"agent.db"),()=>Now)).RunAsync(new(request.CycleId,Now,inputs),CancellationToken.None);

            Assert.False(result.EligibleForRiskIncrease);
            Assert.Contains("production.research.stale",result.Outputs[ModelOffAgentV1.Research].Decision.ReasonCodes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try{Directory.Delete(directory,true);}catch(IOException){}
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
                request.DecisionReview, request.RiskReview,request.PositionReconciliation!,
                request.ProtectionReconciliation!,request.ExternalPositionIsolation!,CancellationToken.None);
            var blocked = await AutoTradingAgent.RunModelOffProductionShadowAsync(
                new AgentSqliteStore(Path.Combine(directory, "blocked.db"), () => Now), request.CycleId + "-blocked",
                request.EvaluationTimeUtc, request.Evidence, request.Research, request.Assessments,
                request.DecisionReview, Risk(false),request.PositionReconciliation!,
                request.ProtectionReconciliation!,request.ExternalPositionIsolation!,CancellationToken.None);

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
        [Assessment()], Review(true), Risk(true),null,PositionReport(),ProtectionReport(),IsolationReport());

    private static PositionReconciliationReportV1 PositionReport()=>
        PositionReconciliationServiceV1.Reconcile([],[],Now,Now);
    private static ProtectionReconciliationReportV1 ProtectionReport()=>
        ProtectionReconciliationServiceV1.Reconcile([],[],Now,Now);
    private static ExternalPositionIsolationReportV1 IsolationReport()=>
        ExternalPositionIsolationServiceV1.Evaluate([],[],Now,Now);

    private static EvidencePack Evidence(DateTime collectedAt) => new()
    {
        CollectedAt = collectedAt, Completeness = 100,
        Account=new(10000m,9000m,10000m,collectedAt),
        Markets = new Dictionary<string, MarketEvidence>
        {
            ["ETHUSDT"] = Market("ETHUSDT", 3500m, collectedAt),
            ["BTCUSDT"] = Market("BTCUSDT", 100000m, collectedAt)
        },
        Fundamentals=new Dictionary<string,CryptoInstrumentFundamentalV1>{{"BTCUSDT",Fundamental("BTCUSDT",collectedAt)},{"ETHUSDT",Fundamental("ETHUSDT",collectedAt)}}
    };
    private static EvidencePack CopyEvidence(EvidencePack source,AccountSnapshot? account=null,IReadOnlyList<ManagedPosition>? positions=null,IReadOnlyDictionary<string,MarketEvidence>? markets=null,IReadOnlyList<NewsEvidence>? news=null,IReadOnlyList<string>? missingSources=null)=>new(){CollectedAt=source.CollectedAt,Completeness=source.Completeness,Account=account??source.Account,Positions=positions??source.Positions,Markets=markets??source.Markets,News=news??source.News,Fundamentals=source.Fundamentals,MissingSources=missingSources??source.MissingSources};
    private static CryptoInstrumentFundamentalV1 Fundamental(string symbol,DateTime observed){var baseAsset=symbol[..^4];return CryptoInstrumentFundamentalCanonicalizerV1.Create("binance-futures","Testnet",symbol,symbol,baseAsset,"USDT","USDT","PERPETUAL","TRADING",Now.AddYears(-2),new DateTimeOffset(observed),new string('a',64));}
    private static MarketEvidence Market(string symbol, decimal price, DateTime at)
    {
        var market=new MarketEvidence(symbol, price, price * .98m, price * 1.02m, 55, .2, .3, .4, new(0, 1, 1, 1, 1, 1, 0), at);
        return market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"test-provider","Testnet")};
    }
    private static NewsEvidence News()=>new("SEC","Official digital asset market update","https://www.sec.gov/news/press-release/test",Now.AddMinutes(-2).UtcDateTime,Now.AddMinutes(-1).UtcDateTime,"official",new string('a',64),["BTC"],"Private full article body must not enter canonical audit.",.9,1,"REGULATION",false,.1);
    private static ResearchValidationResult Research(string symbol) => new()
    { ValidatedAtUtc=Now.AddMinutes(-1),Symbol = symbol, StrategyVersion = "strategy-v1", SampleSize = 200, Trades = 30, QualityScore = .8, Approved = true, Promoted = true, CoverageDays = 90 };
    private static ResearchValidationResult CopyResearch(ResearchValidationResult value,bool? approved=null,bool? promoted=null,double? qualityScore=null,DateTimeOffset? validatedAt=null)=>new(){ValidatedAtUtc=validatedAt??value.ValidatedAtUtc,Symbol=value.Symbol,StrategyVersion=value.StrategyVersion,SampleSize=value.SampleSize,Trades=value.Trades,WinRate=value.WinRate,ProfitFactor=value.ProfitFactor,Expectancy=value.Expectancy,MaxDrawdown=value.MaxDrawdown,Sharpe=value.Sharpe,OutOfSampleReturn=value.OutOfSampleReturn,WalkForwardScore=value.WalkForwardScore,MonteCarloLossProbability=value.MonteCarloLossProbability,QualityScore=qualityScore??value.QualityScore,Approved=approved??value.Approved,Promoted=promoted??value.Promoted,CoverageDays=value.CoverageDays,OutOfSampleTrades=value.OutOfSampleTrades,StrategyReturn=value.StrategyReturn,BenchmarkReturn=value.BenchmarkReturn,RegimeReturns=value.RegimeReturns,Summary=value.Summary};
    private static MarketDecisionAssessment Assessment(bool fresh=true,DecisionAction recommended=DecisionAction.OpenLong,string symbol="BTCUSDT",double confidence=.8)=>new(){Symbol=symbol,Fresh=fresh,EntryReady=true,RecommendedAction=recommended,Confidence=confidence,NetScore=.5,ConflictRatio=.1};
    private static DecisionReview Review(bool accepted,decimal stop=98000m,decimal take=104000m,double riskReward=2,double confidence=.8,int targetTier=1,string strategyVersion="strategy-v1") => new()
    {
        Accepted = accepted,
        Decision = new DecisionPlan { Action = DecisionAction.OpenLong, Instrument = "BTCUSDT", Confidence = confidence,TargetTier=targetTier,StrategyVersion=strategyVersion,
            EntryPrice = 100000m, StopLossPrice = stop, TakeProfitPrice = take, RiskRewardRatio = riskReward }
    };
    private static IndependentRiskReview Risk(bool approved) => new()
    { Approved = approved, RiskLevel = approved ? "NORMAL" : "BLOCKED", PlannedQuantity = approved ? .01m : 0, RiskAmount=approved ? 10m : 0, ExposureAfter=approved ? .1m : 0, Checks = ["risk-gate"] };
    private static IndependentRiskReview CopyRisk(IndependentRiskReview value,decimal? quantity=null,decimal? riskAmount=null,decimal? exposure=null,IReadOnlyList<string>? checks=null,IReadOnlyList<string>? blocks=null,string? level=null)=>new(){Approved=value.Approved,RiskLevel=level??value.RiskLevel,PlannedQuantity=quantity??value.PlannedQuantity,RiskAmount=riskAmount??value.RiskAmount,ExposureAfter=exposure??value.ExposureAfter,Checks=checks??value.Checks,BlockingReasons=blocks??value.BlockingReasons,Summary=value.Summary};
    private static string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
