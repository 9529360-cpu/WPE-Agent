using System.Security.Cryptography;
using System.Text.Json;
using WpeAgent.ModelOff;

namespace 币安量化机器人.Services.Agent;

internal sealed record ModelOffLiveCycleInputRequestV1(
    string CycleId,
    DateTimeOffset EvaluationTimeUtc,
    EvidencePack Evidence,
    IReadOnlyDictionary<string, ResearchValidationResult> Research,
    IReadOnlyList<MarketDecisionAssessment> Assessments,
    DecisionReview DecisionReview,
    IndependentRiskReview RiskReview,
    IReadOnlyList<PersistedMacroObservation>? MacroObservations = null,
    PositionReconciliationReportV1? PositionReconciliation = null,
    ProtectionReconciliationReportV1? ProtectionReconciliation = null,
    ExternalPositionIsolationReportV1? ExternalPositionIsolation = null);

/// <summary>Maps already-computed local runtime truth into canonical Agent inputs without invoking a model or a network.</summary>
internal static class ModelOffLiveCycleInputComposerV1
{
    internal const string InputSchema = "wpe.live-cycle-snapshot/1.0";
    private static readonly TimeSpan MaximumMarketAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumNewsAge = TimeSpan.FromDays(7);
    private static readonly IReadOnlyDictionary<string,string[]> NewsSourceHosts=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["SEC"]=["sec.gov"],["CFTC"]=["cftc.gov"],["Federal Reserve"]=["federalreserve.gov"],["ECB"]=["ecb.europa.eu"],
        ["CoinDesk"]=["coindesk.com"],["Cointelegraph"]=["cointelegraph.com"],["Google News"]=["news.google.com"]
    };
    private static readonly HashSet<string> NewsEventTypes=new(StringComparer.Ordinal){"SECURITY","REGULATION","ETF","MACRO","EXCHANGE","MARKET","GENERAL"};

    internal static IReadOnlyList<ModelOffProductionInputV1> Compose(ModelOffLiveCycleInputRequestV1 request)
    {
        ValidateRequest(request);
        var outputs = new List<ModelOffProductionInputV1>(4);

        var marketReasons = new List<string>();
        var marketSources = new List<ModelOffSourceV1>();
        var account=request.Evidence.Account;
        var accountTimestampValid=TryUtc(account.Timestamp,out var accountAt);
        var accountValid=account.WalletBalance>=0&&account.AvailableBalance>=0&&account.Equity>0&&account.AvailableBalance<=account.Equity&&
            accountTimestampValid&&Fresh(accountAt,request.EvaluationTimeUtc);
        if(!accountValid)marketReasons.Add("live.account.invalid-or-stale");
        marketSources.Add(new("account-snapshot",ModelOffSourceKindV1.Account,accountAt,request.EvaluationTimeUtc,
            accountValid?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Invalid,
            Hash(new{account.WalletBalance,account.AvailableBalance,account.Equity,TimestampUtc=accountAt})));
        var positionsValid=request.Evidence.Positions.All(position=>SafeToken(position.Symbol)&&position.Quantity>0&&position.EntryPrice>0&&position.MarkPrice>0&&position.Leverage>0&&
            Enum.IsDefined(position.Side))&&!request.Evidence.Positions.GroupBy(position=>(position.Symbol,position.Side)).Any(group=>group.Count()>1);
        if(!positionsValid)marketReasons.Add("live.positions.invalid");
        foreach (var market in request.Evidence.Markets.Values.OrderBy(x => x.Symbol, StringComparer.Ordinal))
        {
            var sourceValid=true;
            if (!SafeToken(market.Symbol) || market.Price <= 0){marketReasons.Add("live.market.invalid");sourceValid=false;}
            if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)){marketReasons.Add("live.market.provenance-invalid");sourceValid=false;}
            if (!TryUtc(market.CollectedAt, out var collectedAt) || !Fresh(collectedAt, request.EvaluationTimeUtc))
                {marketReasons.Add("live.market.stale");sourceValid=false;}
            marketSources.Add(new(SafeToken(market.Symbol) ? market.Symbol : "unknown-market",
                ModelOffSourceKindV1.Market, collectedAt, request.EvaluationTimeUtc,
                sourceValid ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Invalid,
                sourceValid?"sha256:"+market.Provenance!.CanonicalSha256:Hash(new { market.Symbol, state="invalid", CollectedAtUtc = collectedAt })));
        }
        if (marketSources.Count == 0) marketReasons.Add("live.market.missing");
        if (request.Evidence.Completeness is < 0 or > 100) marketReasons.Add("live.evidence.completeness-invalid");
        var marketOutput = Output(ModelOffAgentV1.Market, request, marketSources, marketReasons.Count == 0,
            marketReasons.Count == 0 ? "publish_market" : "block",
            marketReasons, new { completeness = request.Evidence.Completeness,
                market_count = request.Evidence.Markets.Count, position_count=request.Evidence.Positions.Count,
                account_available=accountValid, positions_valid=positionsValid,
                missing_source_count = request.Evidence.MissingSources.Count });
        outputs.Add(Input(marketOutput));

        var researchReasons = new List<string>();
        var macro=request.MacroObservations??[];
        var target=request.DecisionReview.Decision.Instrument;
        var targetStrategyId=request.DecisionReview.Decision.StrategyId;
        var targetStrategyVersion=request.DecisionReview.Decision.StrategyVersion;
        var assessmentMatches=request.Assessments.Where(x=>string.Equals(x.Symbol,target,StringComparison.OrdinalIgnoreCase)).ToArray();
        var targetAssessment=assessmentMatches.Length==1?assessmentMatches[0]:null;
        var targetAssessmentValid=targetAssessment is not null&&ValidAssessment(targetAssessment,target);
        request.Evidence.Markets.TryGetValue(target,out var technicalMarket);
        var technicalEvidenceValid=targetAssessmentValid&&technicalMarket is not null&&MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(technicalMarket);
        if(assessmentMatches.Length==0)researchReasons.Add("live.research.technical-missing");
        if(assessmentMatches.Length>1)researchReasons.Add("live.research.technical-conflicting");
        if(targetAssessment is not null&&!targetAssessmentValid)researchReasons.Add("live.research.technical-invalid");
        if(targetAssessmentValid&&!technicalEvidenceValid)researchReasons.Add("live.research.technical-source-invalid");
        request.Evidence.Fundamentals.TryGetValue(target,out var targetFundamental);var targetFundamentalValid=targetFundamental is not null&&CryptoInstrumentFundamentalCanonicalizerV1.IsCanonical(targetFundamental,request.EvaluationTimeUtc)&&string.Equals(targetFundamental.Symbol,target,StringComparison.OrdinalIgnoreCase)&&targetFundamental.Environment=="Testnet";
        if(targetFundamental is null)researchReasons.Add("live.research.fundamental-missing");else if(!targetFundamentalValid)researchReasons.Add("live.research.fundamental-invalid");
        var news=request.Evidence.News.OrderBy(x=>x.DuplicateGroup,StringComparer.Ordinal).ToArray();
        var newsValid=news.All(x=>ValidNews(x,request.EvaluationTimeUtc))&&!news.GroupBy(x=>x.DuplicateGroup,StringComparer.OrdinalIgnoreCase).Any(x=>x.Count()>1);
        var missingNewsSources=request.Evidence.MissingSources.Count(x=>NewsSourceHosts.ContainsKey(x));
        if(!newsValid)researchReasons.Add("live.research.news-invalid");
        if(news.Length==0&&missingNewsSources==NewsSourceHosts.Count)researchReasons.Add("live.research.news-unavailable");
        var targetMatches=request.Research.Values.Where(x=>string.Equals(x.Symbol,target,StringComparison.Ordinal)).ToArray();
        var exactResearchMatches=targetMatches.Where(x=>string.Equals(x.StrategyId,targetStrategyId,StringComparison.Ordinal)&&string.Equals(x.StrategyVersion,targetStrategyVersion,StringComparison.Ordinal)).ToArray();
        var targetResearch=exactResearchMatches.Length==1?exactResearchMatches[0]:null;
        var targetBacktestFact=targetResearch is null?null:CanonicalBacktestFact(targetResearch,request.EvaluationTimeUtc);
        var targetResearchValid=targetBacktestFact is not null;
        if(targetMatches.Length>1||exactResearchMatches.Length>1)researchReasons.Add("live.research.target-conflicting");
        if(targetMatches.Length>0&&exactResearchMatches.Length==0)researchReasons.Add("live.research.target-identity-mismatch");
        if(targetResearch is null)researchReasons.Add("live.research.target-missing");
        else
        {
            if(!targetResearchValid)researchReasons.Add("live.research.target-invalid");
            if(!targetResearch.Approved)researchReasons.Add("live.research.target-not-approved");
            if(!targetResearch.Promoted)researchReasons.Add("live.research.target-not-promoted");
        }
        var macroValid=!macro.GroupBy(x=>x.IndicatorId,StringComparer.Ordinal).Any(x=>x.Count()>1)&&macro.All(x=>ValidMacro(x,request.EvaluationTimeUtc));
        if(!macroValid)
            researchReasons.Add("live.research.macro-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(marketOutput)) researchReasons.Add("live.research.market-invalid");
        var researchSources=new List<ModelOffSourceV1>{UpstreamSource(marketOutput,outputs[^1].Document,request.EvaluationTimeUtc)};
        if(newsValid)foreach(var item in news.Take(12))
        {
            var published=NewsTimestamp(item);
            researchSources.Add(new("news-"+item.DuplicateGroup.ToLowerInvariant(),ModelOffSourceKindV1.News,published,new DateTimeOffset(item.CollectedAt),
                ModelOffSourceStatusV1.Available,Hash(NewsCanonicalFact(item,published))));
        }
        if(macroValid)foreach(var item in macro.OrderBy(x=>x.IndicatorId,StringComparer.Ordinal))
            researchSources.Add(new($"macro-{item.IndicatorId.ToLowerInvariant()}-r{item.Revision}",ModelOffSourceKindV1.Macro,item.FirstObservedAtUtc,request.EvaluationTimeUtc,ModelOffSourceStatusV1.Available,"sha256:"+item.SourceArtifactHash.ToLowerInvariant()));
        if(technicalEvidenceValid)
            researchSources.Add(new($"technical-assessment-{target.ToLowerInvariant()}",ModelOffSourceKindV1.Market,new DateTimeOffset(technicalMarket!.CollectedAt),request.EvaluationTimeUtc,ModelOffSourceStatusV1.Available,Hash(TechnicalAssessmentFact(targetAssessment!,technicalMarket))));
        if(targetResearch is not null)researchSources.Add(new($"backtest:{(SafeToken(targetResearch.Symbol)?targetResearch.Symbol:"unknown")}:{(SafeIdentityToken(targetResearch.StrategyId)?targetResearch.StrategyId:"unknown")}:{(SafeIdentityToken(targetResearch.StrategyVersion)?targetResearch.StrategyVersion:"unknown")}",ModelOffSourceKindV1.Strategy,targetResearch.ValidatedAtUtc,request.EvaluationTimeUtc,
            targetResearchValid&&targetResearch.Approved&&targetResearch.Promoted?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Invalid,
            targetBacktestFact is not null?"sha256:"+targetBacktestFact.CanonicalSha256:Hash(new{target=SafeToken(target)?target:"unknown",strategyId=SafeIdentityToken(targetStrategyId)?targetStrategyId:"unknown",strategyVersion=SafeIdentityToken(targetStrategyVersion)?targetStrategyVersion:"unknown",state="invalid"})));
        if(targetFundamental is not null)researchSources.Add(new("fundamental-"+(SafeToken(target)?target.ToLowerInvariant():"unknown"),ModelOffSourceKindV1.Fundamental,targetFundamental.ObservedAtUtc,request.EvaluationTimeUtc,targetFundamentalValid?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Invalid,"sha256:"+targetFundamental.CanonicalSha256));
        var research = Output(ModelOffAgentV1.Research, request,
            researchSources, researchReasons.Count == 0,
            researchReasons.Count == 0 ? "publish_research" : "block", researchReasons,
            new { target_symbol=SafeToken(target)?target:"unknown",target_validation_state=targetResearchValid?"valid":"invalid",validations = (!targetResearchValid?Array.Empty<ResearchValidationResult>():[targetResearch!]).Select(x => new
                { x.ValidatedAtUtc,Symbol = SafeToken(x.Symbol) ? x.Symbol : "unknown",StrategyId=SafeIdentityToken(x.StrategyId)?x.StrategyId:"unknown",StrategyVersion=SafeIdentityToken(x.StrategyVersion)?x.StrategyVersion:"unknown",
                    x.SampleSize,x.Trades,x.QualityScore,x.Approved,x.Promoted,x.CoverageDays,x.HistoricalPassed,x.WorstRegimeReturn,x.TrainTestExpectancyGap,x.PassingRegimes,x.EvaluatedRegimes,
                    trial_count=x.ParameterSearch?.TrialCount??0,selected_trial_index=x.ParameterSearch?.SelectedTrialIndex??-1,search_space_hash=x.ParameterSearch?.SearchSpaceHash??string.Empty,
                    selection_dataset_hash=x.ParameterSearch?.SelectionDatasetHash??string.Empty,historical_oos_dataset_hash=x.ParameterSearch?.HistoricalOosDatasetHash??string.Empty,
                    selected_trial_p_value=x.ParameterSearch?.SelectedTrialPValue??1,corrected_threshold=x.ParameterSearch?.CorrectedSignificanceThreshold??0,
                    x.ForwardObservations,x.ForwardQualified,canonical_backtest_sha256=CanonicalBacktestFact(x,request.EvaluationTimeUtc)?.CanonicalSha256??string.Empty }).ToArray(),
                news_evidence_count=newsValid?news.Length:0,
                news_target_count=newsValid?news.Count(x=>NewsTargets(x,target)):0,
                news_evidence_hash=newsValid?Hash(news.Select(x=>NewsCanonicalFact(x,NewsTimestamp(x))).ToArray()):Hash(new{state="invalid"}),
                technical_state=technicalEvidenceValid?"verified":"unavailable",technical_evidence_hash=technicalEvidenceValid?Hash(TechnicalAssessmentFact(targetAssessment!,technicalMarket!)):string.Empty,
                fundamental_state=targetFundamentalValid?"verified":"unavailable",fundamental_evidence_hash=targetFundamentalValid?targetFundamental!.CanonicalSha256:string.Empty,
                macro_state=macro.Count==0?"unavailable":macroValid?"verified":"invalid",macro_observations=macro.OrderBy(x=>x.IndicatorId,StringComparer.Ordinal).Select(x=>new{x.IndicatorId,x.ObservationAtUtc,x.Revision,x.Geography,x.Frequency,x.Unit,x.Value,x.SourceArtifactHash,x.FirstObservedAtUtc,x.ReleasedAtUtc,x.ReleaseTimeBasis,x.ReleaseCalendarArtifactHash,x.ReleaseCalendarEventId}).ToArray() });
        outputs.Add(Input(research));

        var decision = request.DecisionReview.Decision;
        var strategyReasons = new List<string>();
        if (!request.DecisionReview.Accepted) strategyReasons.Add("live.strategy.review-blocked");
        if (!Enum.IsDefined(decision.Action)) strategyReasons.Add("live.strategy.action-invalid");
        if (!SafeToken(decision.Instrument)) strategyReasons.Add("live.strategy.instrument-invalid");
        if (!Finite(decision.Confidence) || decision.Confidence is <0 or >1 || !Finite(decision.RiskRewardRatio) || decision.TargetTier is <0 or >3) strategyReasons.Add("live.strategy.numeric-invalid");
        if (!SafeIdentityToken(decision.StrategyId)) strategyReasons.Add("live.strategy.id-invalid");
        if (!SafeIdentityToken(decision.StrategyVersion)) strategyReasons.Add("live.strategy.version-invalid");
        if(targetResearchValid&&(!string.Equals(decision.StrategyId,targetResearch!.StrategyId,StringComparison.Ordinal)||!string.Equals(decision.StrategyVersion,targetResearch.StrategyVersion,StringComparison.Ordinal)))strategyReasons.Add("live.strategy.research-identity-conflict");
        if(assessmentMatches.Length==0)strategyReasons.Add("live.strategy.assessment-missing");
        if(assessmentMatches.Length>1)strategyReasons.Add("live.strategy.assessment-conflicting");
        if(targetAssessment is not null&&!targetAssessmentValid)strategyReasons.Add("live.strategy.assessment-ineligible");
        if(targetAssessment is not null&&(!string.Equals(targetAssessment.StrategyId,decision.StrategyId,StringComparison.Ordinal)||!string.Equals(targetAssessment.StrategyVersion,decision.StrategyVersion,StringComparison.Ordinal)))strategyReasons.Add("live.strategy.assessment-identity-conflict");
        if(targetAssessment is not null&&!RecommendationMatches(decision.Action,targetAssessment.RecommendedAction))strategyReasons.Add("live.strategy.direction-conflict");
        if(DeterministicPlanSkill.IsRiskIncreasing(decision.Action)&&!ValidPlanGeometry(decision))strategyReasons.Add("live.strategy.plan-geometry-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(research)) strategyReasons.Add("live.strategy.research-invalid");
        var strategy = Output(ModelOffAgentV1.Strategy, request,
            [UpstreamSource(research, outputs[^1].Document, request.EvaluationTimeUtc)], strategyReasons.Count == 0,
            strategyReasons.Count == 0 ? "publish_strategy" : "block", strategyReasons,
            new { action = decision.Action.ToString().ToLowerInvariant(),
                instrument = SafeToken(decision.Instrument) ? decision.Instrument : "unknown",
                decision.TargetTier, decision.Confidence, decision.EntryPrice, decision.StopLossPrice,
                decision.TakeProfitPrice, decision.RiskRewardRatio, order_type = decision.OrderType.ToString().ToLowerInvariant(),
                strategy_id=targetResearchValid?targetResearch!.StrategyId:"unknown",strategy_version=targetResearchValid?targetResearch!.StrategyVersion:"unknown",
                assessment_count = request.Assessments.Count, target_assessment_present=targetAssessment is not null });
        outputs.Add(Input(strategy));

        var riskReasons = new List<string>();
        var positionValid=request.PositionReconciliation is not null&&PositionReconciliationServiceV1.IsCanonical(request.PositionReconciliation)&&CurrentPositionReport(request.PositionReconciliation.ObservedAtUtc,request.PositionReconciliation.EvaluatedAtUtc,request.EvaluationTimeUtc);
        var protectionValid=request.ProtectionReconciliation is not null&&ProtectionReconciliationServiceV1.IsCanonical(request.ProtectionReconciliation)&&CurrentPositionReport(request.ProtectionReconciliation.ObservedAtUtc,request.ProtectionReconciliation.EvaluatedAtUtc,request.EvaluationTimeUtc);
        var isolationValid=request.ExternalPositionIsolation is not null&&ExternalPositionIsolationServiceV1.IsCanonical(request.ExternalPositionIsolation)&&CurrentPositionReport(request.ExternalPositionIsolation.ObservedAtUtc,request.ExternalPositionIsolation.EvaluatedAtUtc,request.EvaluationTimeUtc);
        if(!positionValid)riskReasons.Add("live.risk.position-reconciliation-invalid");
        else if(!request.PositionReconciliation!.AllowsRiskIncrease)riskReasons.Add("live.risk.position-reconciliation-blocked");
        if(!protectionValid)riskReasons.Add("live.risk.protection-reconciliation-invalid");
        else if(!request.ProtectionReconciliation!.AllowsRiskIncrease)riskReasons.Add("live.risk.protection-reconciliation-blocked");
        if(!isolationValid)riskReasons.Add("live.risk.external-position-isolation-invalid");
        else if(!request.ExternalPositionIsolation!.AllowsRiskIncrease)riskReasons.Add("live.risk.external-position-isolation-blocked");
        if (!request.RiskReview.Approved) riskReasons.Add("live.risk.not-approved");
        if (request.RiskReview.PlannedQuantity < 0 || request.RiskReview.RiskAmount < 0 || request.RiskReview.ExposureAfter < 0)
            riskReasons.Add("live.risk.numeric-invalid");
        var riskIncreasing=DeterministicPlanSkill.IsRiskIncreasing(decision.Action);
        if(riskIncreasing&&request.RiskReview.Approved&&(request.RiskReview.PlannedQuantity<=0||request.RiskReview.RiskAmount<=0||request.RiskReview.ExposureAfter<=0))riskReasons.Add("live.risk.approval-inconsistent");
        if(request.RiskReview.Approved&&request.RiskReview.BlockingReasons.Count>0)riskReasons.Add("live.risk.approval-has-blocks");
        var checks=request.RiskReview.Checks.Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();
        if(checks.Length==0||checks.Any(x=>!SafeIdentityToken(x))||checks.Distinct(StringComparer.Ordinal).Count()!=checks.Length)riskReasons.Add("live.risk.checks-invalid");
        if(!SafeIdentityToken(request.RiskReview.RiskLevel)||request.RiskReview.Approved&&string.Equals(request.RiskReview.RiskLevel,"BLOCKED",StringComparison.OrdinalIgnoreCase))riskReasons.Add("live.risk.level-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(strategy)) riskReasons.Add("live.risk.strategy-invalid");
        var riskSources=new List<ModelOffSourceV1>{UpstreamSource(strategy, outputs[^1].Document, request.EvaluationTimeUtc)};
        AddPositionSource(riskSources,"position-reconciliation",request.PositionReconciliation,positionValid,request.EvaluationTimeUtc);
        AddPositionSource(riskSources,"protection-reconciliation",request.ProtectionReconciliation,protectionValid,request.EvaluationTimeUtc);
        AddPositionSource(riskSources,"external-position-isolation",request.ExternalPositionIsolation,isolationValid,request.EvaluationTimeUtc);
        var risk = Output(ModelOffAgentV1.Risk, request,
            riskSources, riskReasons.Count == 0,
            riskReasons.Count == 0 ? "risk_approved" : "block", riskReasons,
            new { request.RiskReview.Approved, RiskLevel = SafeToken(request.RiskReview.RiskLevel) ? request.RiskReview.RiskLevel : "UNKNOWN",
                request.RiskReview.PlannedQuantity, request.RiskReview.RiskAmount,
                request.RiskReview.ExposureAfter, check_count = checks.Length,
                check_set_hash=Hash(checks.Order(StringComparer.Ordinal).ToArray()),
                blocking_reason_count = request.RiskReview.BlockingReasons.Count,
                position_reconciliation=positionValid?request.PositionReconciliation!.State.ToString().ToLowerInvariant():"invalid",
                protection_reconciliation=protectionValid?request.ProtectionReconciliation!.State.ToString().ToLowerInvariant():"invalid",
                external_position_isolation=isolationValid?request.ExternalPositionIsolation!.State.ToString().ToLowerInvariant():"invalid",
                result_state = request.RiskReview.Approved ? "approved" : "blocked" });
        outputs.Add(Input(risk));
        return outputs;
    }

    private static ModelOffAgentOutputV1 Output(
        ModelOffAgentV1 role, ModelOffLiveCycleInputRequestV1 request, IReadOnlyList<ModelOffSourceV1> sources,
        bool ready, string action, IReadOnlyList<string> reasons, object facts) =>
        new(role, $"{request.CycleId}-live-{role.ToString().ToLowerInvariant()}", request.CycleId,
            request.EvaluationTimeUtc, request.EvaluationTimeUtc, InputSchema,
            new($"wpe.live-{role.ToString().ToLowerInvariant()}", "1.0"), sources,
            ready ? ModelOffOutputStatusV1.Succeeded : ModelOffOutputStatusV1.Blocked,
            new(ready ? ModelOffUncertaintyLevelV1.None : ModelOffUncertaintyLevelV1.Unknown,
                Sorted(reasons), ready ? [] : ["downstream_eligibility"]),
            JsonSerializer.SerializeToElement(facts), [], new(action, ready, Sorted(reasons)), [],
            ModelOffFixedTemplatesV1.SummaryVersion);

    private static ModelOffProductionInputV1 Input(ModelOffAgentOutputV1 output) =>
        new(output, ModelOffCanonicalSerializerV1.Serialize(output));

    private static ModelOffSourceV1 UpstreamSource(
        ModelOffAgentOutputV1 upstream, ModelOffCanonicalDocumentV1 document, DateTimeOffset at) =>
        new(upstream.OutputId, ModelOffSourceKindV1.Audit, at, at,
            ModelOffEligibilityV1.IsEligibleForDownstream(upstream) ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Invalid,
            "sha256:" + document.Sha256);

    private static string Hash(object value) => "sha256:" + Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();

    private static void AddPositionSource<T>(List<ModelOffSourceV1> sources,string sourceId,T? report,bool valid,DateTimeOffset at) where T:class
    {
        var (observed,hash)=report switch
        {
            PositionReconciliationReportV1 value=>(value.ObservedAtUtc,value.CanonicalSha256),
            ProtectionReconciliationReportV1 value=>(value.ObservedAtUtc,value.CanonicalSha256),
            ExternalPositionIsolationReportV1 value=>(value.ObservedAtUtc,value.CanonicalSha256),
            _=>(at,string.Empty)
        };
        sources.Add(new(sourceId,ModelOffSourceKindV1.Account,observed,at,
            valid?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Invalid,
            valid?"sha256:"+hash:Hash(new{sourceId,state="invalid"})));
    }

    private static bool Fresh(DateTimeOffset value, DateTimeOffset now) =>
        value != default && value.Offset == TimeSpan.Zero && value <= now && now - value <= MaximumMarketAge;

    private static bool CurrentPositionReport(DateTimeOffset observed,DateTimeOffset evaluated,DateTimeOffset now)=>
        observed.Offset==TimeSpan.Zero&&evaluated.Offset==TimeSpan.Zero&&observed<=evaluated&&evaluated<=now&&now-evaluated<=MaximumMarketAge;

    private static bool ValidNews(NewsEvidence value,DateTimeOffset now)
    {
        if(!NewsSourceHosts.TryGetValue(value.Source,out var hosts)||!Uri.TryCreate(value.Url,UriKind.Absolute,out var uri)||uri.Scheme!=Uri.UriSchemeHttps||
           !hosts.Any(host=>uri.Host.Equals(host,StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith("."+host,StringComparison.OrdinalIgnoreCase)))return false;
        if(string.IsNullOrWhiteSpace(value.Title)||value.Title.Length>500||!TryUtc(value.CollectedAt,out var collected)||!Fresh(collected,now))return false;
        if(value.PublishedAt is {Kind:not DateTimeKind.Utc})return false;
        var published=NewsTimestamp(value);if(published.Offset!=TimeSpan.Zero||published>collected||now-published>MaximumNewsAge)return false;
        if(value.Reliability is not ("official" or "mainstream" or "aggregator")||value.DuplicateGroup.Length is not (20 or 64)||!value.DuplicateGroup.All(Uri.IsHexDigit))return false;
        if(value.AffectedAssets.Any(x=>!SafeToken(x))||value.AffectedAssets.Distinct(StringComparer.Ordinal).Count()!=value.AffectedAssets.Count)return false;
        return Finite(value.Confidence)&&value.Confidence is >=0 and <=1&&value.CorroboratingSources>0&&NewsEventTypes.Contains(value.EventType)&&Finite(value.Sentiment)&&value.Sentiment is >=-1 and <=1;
    }
    private static DateTimeOffset NewsTimestamp(NewsEvidence value)=>value.PublishedAt is {Kind:DateTimeKind.Utc} published?new DateTimeOffset(published):new DateTimeOffset(value.CollectedAt);
    private static bool NewsTargets(NewsEvidence value,string symbol)
    {
        var asset=symbol.EndsWith("USDT",StringComparison.OrdinalIgnoreCase)?symbol[..^4]:symbol;
        return value.AffectedAssets.Contains(asset,StringComparer.OrdinalIgnoreCase)||value.AffectedAssets.Contains(symbol,StringComparer.OrdinalIgnoreCase);
    }
    private static object NewsCanonicalFact(NewsEvidence value,DateTimeOffset published)=>new
    {
        source=value.Source,source_uri=value.Url,published_at_utc=published,collected_at_utc=new DateTimeOffset(value.CollectedAt),
        reliability=value.Reliability,duplicate_group=value.DuplicateGroup.ToLowerInvariant(),assets=value.AffectedAssets.Order(StringComparer.Ordinal).ToArray(),
        confidence=value.Confidence,corroborating_sources=value.CorroboratingSources,event_type=value.EventType,is_breaking=value.IsBreaking,sentiment=value.Sentiment,
        title_sha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.Title))).ToLowerInvariant()
    };
    private static object TechnicalAssessmentFact(MarketDecisionAssessment value,MarketEvidence market)=>new
    {
        schema="wpe.live-technical-assessment/1.0",symbol=value.Symbol,market_evidence_sha256=market.Provenance!.CanonicalSha256,
        regime=value.Regime.ToString().ToLowerInvariant(),value.NetScore,value.Confidence,value.ConflictRatio,value.Fresh,value.EntryReady,
        recommended_action=value.RecommendedAction.ToString().ToLowerInvariant()
    };

    private static bool TryUtc(DateTime value, out DateTimeOffset result)
    {
        if (value.Kind != DateTimeKind.Utc) { result = default; return false; }
        result = new(value); return true;
    }

    private static bool SafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool ValidAssessment(MarketDecisionAssessment value,string symbol)=>string.Equals(value.Symbol,symbol,StringComparison.Ordinal)&&SafeToken(value.Symbol)&&Enum.IsDefined(value.Regime)&&Enum.IsDefined(value.RecommendedAction)&&value.Fresh&&value.EntryReady&&Finite(value.Confidence)&&value.Confidence is>=0 and<=1&&Finite(value.NetScore)&&value.NetScore is>=-1 and<=1&&Finite(value.ConflictRatio)&&value.ConflictRatio is>=0 and<=1;
    private static bool ValidResearch(ResearchValidationResult value,DateTimeOffset now)=>CanonicalBacktestFact(value,now) is not null;

    private static BacktestValidationFactV1? CanonicalBacktestFact(ResearchValidationResult value,DateTimeOffset now)
    {
        if(value.ParameterSearch is null)return null;
        if(value.ValidatedAtUtc==default||value.ValidatedAtUtc.Offset!=TimeSpan.Zero||!SafeToken(value.Symbol)||!SafeIdentityToken(value.StrategyId)||!SafeIdentityToken(value.StrategyVersion))return null;
        var fact=BacktestValidationCanonicalizerV1.Create(
            value.Symbol,value.StrategyId,value.StrategyVersion,value.ValidatedAtUtc,
            value.SampleSize,value.Trades,value.OutOfSampleTrades,value.CoverageDays,
            value.WinRate,value.ProfitFactor,value.Expectancy,value.MaxDrawdown,value.Sharpe,
            value.OutOfSampleReturn,value.WalkForwardScore,value.MonteCarloLossProbability,value.QualityScore,
            value.StrategyReturn,value.BenchmarkReturn,value.WorstRegimeReturn,value.TrainTestExpectancyGap,
            value.PassingRegimes,value.EvaluatedRegimes,value.HistoricalPassed,value.ParameterSearch,
            value.ForwardObservations,value.ForwardQualified,value.Approved,value.Promoted);
        return BacktestValidationCanonicalizerV1.IsCanonical(fact,now)?fact:null;
    }
    private static bool SafeIdentityToken(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=64&&value.All(character=>char.IsAsciiLetterOrDigit(character)||character is '.' or '_' or '-');
    private static bool RecommendationMatches(DecisionAction action,DecisionAction recommendation)=>action switch{DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong=>recommendation==DecisionAction.OpenLong,DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort=>recommendation==DecisionAction.OpenShort,DecisionAction.Lock=>recommendation is DecisionAction.OpenLong or DecisionAction.OpenShort,_=>true};
    private static bool ValidPlanGeometry(DecisionPlan value)
    {
        if(value.EntryPrice<=0||value.StopLossPrice<=0||value.TakeProfitPrice<=0||value.RiskRewardRatio<=0||!Enum.IsDefined(value.OrderType))return false;
        return value.Action switch{DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong=>value.StopLossPrice<value.EntryPrice&&value.EntryPrice<value.TakeProfitPrice,DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort=>value.TakeProfitPrice<value.EntryPrice&&value.EntryPrice<value.StopLossPrice,DecisionAction.Lock=>value.StopLossPrice!=value.EntryPrice&&value.TakeProfitPrice!=value.EntryPrice&&Math.Sign(value.StopLossPrice-value.EntryPrice)!=Math.Sign(value.TakeProfitPrice-value.EntryPrice),_=>true};
    }
    private static bool ValidMacro(PersistedMacroObservation value,DateTimeOffset now)
    {
        var formal=value.ReleaseTimeBasis=="official-release-calendar";
        var observed=value.ReleaseTimeBasis=="official-endpoint-first-observed";
        return SafeToken(value.IndicatorId)&&value.Revision>0&&!string.IsNullOrWhiteSpace(value.Geography)&&!string.IsNullOrWhiteSpace(value.Frequency)&&!string.IsNullOrWhiteSpace(value.Unit)&&value.ObservationAtUtc.Offset==TimeSpan.Zero&&value.FirstObservedAtUtc.Offset==TimeSpan.Zero&&value.ObservationAtUtc<=value.FirstObservedAtUtc&&value.FirstObservedAtUtc<=now&&value.SourceArtifactHash.Length==64&&value.SourceArtifactHash.All(Uri.IsHexDigit)&&
               (observed&&value.ReleaseCalendarArtifactHash is null&&value.ReleaseCalendarEventId is null||formal&&value.ReleasedAtUtc.HasValue&&value.ReleasedAtUtc.Value.Offset==TimeSpan.Zero&&value.ObservationAtUtc<=value.ReleasedAtUtc.Value&&value.ReleasedAtUtc.Value<=value.FirstObservedAtUtc&&value.ReleaseCalendarArtifactHash is {Length:64} hash&&hash.All(Uri.IsHexDigit)&&SafeIdentityToken(value.ReleaseCalendarEventId));
    }
    private static string[] Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static void ValidateRequest(ModelOffLiveCycleInputRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CycleId)) throw new ArgumentException("Cycle id is required.", nameof(request));
        if (request.EvaluationTimeUtc == default || request.EvaluationTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An injected UTC evaluation time is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Evidence);
        ArgumentNullException.ThrowIfNull(request.Research);
        ArgumentNullException.ThrowIfNull(request.Assessments);
        ArgumentNullException.ThrowIfNull(request.DecisionReview);
        ArgumentNullException.ThrowIfNull(request.RiskReview);
    }
}
