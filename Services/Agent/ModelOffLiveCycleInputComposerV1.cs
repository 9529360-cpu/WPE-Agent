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
    IReadOnlyList<PersistedMacroObservation>? MacroObservations = null);

/// <summary>Maps already-computed local runtime truth into canonical Agent inputs without invoking a model or a network.</summary>
internal static class ModelOffLiveCycleInputComposerV1
{
    internal const string InputSchema = "wpe.live-cycle-snapshot/1.0";
    private static readonly TimeSpan MaximumMarketAge = TimeSpan.FromMinutes(5);

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
            if (!TryUtc(market.CollectedAt, out var collectedAt) || !Fresh(collectedAt, request.EvaluationTimeUtc))
                {marketReasons.Add("live.market.stale");sourceValid=false;}
            marketSources.Add(new(SafeToken(market.Symbol) ? market.Symbol : "unknown-market",
                ModelOffSourceKindV1.Market, collectedAt, request.EvaluationTimeUtc,
                sourceValid ? ModelOffSourceStatusV1.Available : ModelOffSourceStatusV1.Invalid,
                Hash(new { market.Symbol, market.Price, market.Support, market.Resistance, market.Rsi,
                    market.Trend15m, market.Trend1h, market.Trend4h, market.Quality.QualityScore,
                    market.Quality.SpreadBps, market.Quality.LiquidityScore, CollectedAtUtc = collectedAt })));
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
        var targetMatches=request.Research.Values.Where(x=>string.Equals(x.Symbol,target,StringComparison.OrdinalIgnoreCase)).ToArray();
        var targetResearch=targetMatches.Length==1?targetMatches[0]:null;
        var targetResearchValid=targetResearch is not null&&ValidResearch(targetResearch);
        if(targetMatches.Length>1)researchReasons.Add("live.research.target-conflicting");
        if(targetResearch is null)researchReasons.Add("live.research.target-missing");
        else
        {
            if(!targetResearchValid)researchReasons.Add("live.research.target-invalid");
            if(!targetResearch.Approved)researchReasons.Add("live.research.target-not-approved");
            if(!targetResearch.Promoted)researchReasons.Add("live.research.target-not-promoted");
        }
        if(macro.GroupBy(x=>x.IndicatorId,StringComparer.Ordinal).Any(x=>x.Count()>1)||macro.Any(x=>!ValidMacro(x,request.EvaluationTimeUtc)))
            researchReasons.Add("live.research.macro-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(marketOutput)) researchReasons.Add("live.research.market-invalid");
        var researchSources=new List<ModelOffSourceV1>{UpstreamSource(marketOutput,outputs[^1].Document,request.EvaluationTimeUtc)};
        if(targetResearch is not null)researchSources.Add(new($"strategy-validation-{(SafeToken(targetResearch.Symbol)?targetResearch.Symbol:"unknown")}",ModelOffSourceKindV1.Strategy,request.EvaluationTimeUtc,request.EvaluationTimeUtc,
            targetResearchValid&&targetResearch.Approved&&targetResearch.Promoted?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Invalid,
            targetResearchValid?Hash(new{targetResearch.Symbol,targetResearch.StrategyVersion,targetResearch.SampleSize,targetResearch.Trades,targetResearch.WinRate,targetResearch.ProfitFactor,targetResearch.Expectancy,targetResearch.MaxDrawdown,targetResearch.Sharpe,targetResearch.OutOfSampleReturn,targetResearch.WalkForwardScore,targetResearch.MonteCarloLossProbability,targetResearch.QualityScore,targetResearch.Approved,targetResearch.Promoted,targetResearch.CoverageDays,targetResearch.OutOfSampleTrades,targetResearch.StrategyReturn,targetResearch.BenchmarkReturn}):Hash(new{target=SafeToken(target)?target:"unknown",state="invalid"})));
        var research = Output(ModelOffAgentV1.Research, request,
            researchSources, researchReasons.Count == 0,
            researchReasons.Count == 0 ? "publish_research" : "block", researchReasons,
            new { target_symbol=SafeToken(target)?target:"unknown",target_validation_state=targetResearchValid?"valid":"invalid",validations = (!targetResearchValid?Array.Empty<ResearchValidationResult>():[targetResearch!]).Select(x => new
                { Symbol = SafeToken(x.Symbol) ? x.Symbol : "unknown", strategy_version_present = !string.IsNullOrWhiteSpace(x.StrategyVersion),
                    x.SampleSize, x.Trades, x.QualityScore, x.Approved, x.Promoted, x.CoverageDays }).ToArray(),
                macro_observations=macro.OrderBy(x=>x.IndicatorId,StringComparer.Ordinal).Select(x=>new{x.IndicatorId,x.ObservationAtUtc,x.Revision,x.Geography,x.Frequency,x.Unit,x.Value,x.SourceArtifactHash,x.FirstObservedAtUtc}).ToArray() });
        outputs.Add(Input(research));

        var decision = request.DecisionReview.Decision;
        var strategyReasons = new List<string>();
        if (!request.DecisionReview.Accepted) strategyReasons.Add("live.strategy.review-blocked");
        if (!SafeToken(decision.Instrument)) strategyReasons.Add("live.strategy.instrument-invalid");
        if (!Finite(decision.Confidence) || !Finite(decision.RiskRewardRatio)) strategyReasons.Add("live.strategy.numeric-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(research)) strategyReasons.Add("live.strategy.research-invalid");
        var strategy = Output(ModelOffAgentV1.Strategy, request,
            [UpstreamSource(research, outputs[^1].Document, request.EvaluationTimeUtc)], strategyReasons.Count == 0,
            strategyReasons.Count == 0 ? "publish_strategy" : "block", strategyReasons,
            new { action = decision.Action.ToString().ToLowerInvariant(),
                instrument = SafeToken(decision.Instrument) ? decision.Instrument : "unknown",
                decision.TargetTier, decision.Confidence, decision.EntryPrice, decision.StopLossPrice,
                decision.TakeProfitPrice, decision.RiskRewardRatio, order_type = decision.OrderType.ToString().ToLowerInvariant(),
                assessment_count = request.Assessments.Count });
        outputs.Add(Input(strategy));

        var riskReasons = new List<string>();
        if (!request.RiskReview.Approved) riskReasons.Add("live.risk.not-approved");
        if (request.RiskReview.PlannedQuantity < 0 || request.RiskReview.RiskAmount < 0 || request.RiskReview.ExposureAfter < 0)
            riskReasons.Add("live.risk.numeric-invalid");
        if (!ModelOffEligibilityV1.IsEligibleForDownstream(strategy)) riskReasons.Add("live.risk.strategy-invalid");
        var risk = Output(ModelOffAgentV1.Risk, request,
            [UpstreamSource(strategy, outputs[^1].Document, request.EvaluationTimeUtc)], riskReasons.Count == 0,
            riskReasons.Count == 0 ? "risk_approved" : "block", riskReasons,
            new { request.RiskReview.Approved, RiskLevel = SafeToken(request.RiskReview.RiskLevel) ? request.RiskReview.RiskLevel : "UNKNOWN",
                request.RiskReview.PlannedQuantity, request.RiskReview.RiskAmount,
                request.RiskReview.ExposureAfter, check_count = request.RiskReview.Checks.Count,
                blocking_reason_count = request.RiskReview.BlockingReasons.Count,
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

    private static bool Fresh(DateTimeOffset value, DateTimeOffset now) =>
        value != default && value.Offset == TimeSpan.Zero && value <= now && now - value <= MaximumMarketAge;

    private static bool TryUtc(DateTime value, out DateTimeOffset result)
    {
        if (value.Kind != DateTimeKind.Utc) { result = default; return false; }
        result = new(value); return true;
    }

    private static bool SafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool ValidResearch(ResearchValidationResult value)=>SafeToken(value.Symbol)&&SafeIdentityToken(value.StrategyVersion)&&value.SampleSize>0&&value.Trades>0&&value.CoverageDays>0&&value.OutOfSampleTrades>=0&&
        new[]{value.WinRate,value.ProfitFactor,value.Expectancy,value.MaxDrawdown,value.Sharpe,value.OutOfSampleReturn,value.WalkForwardScore,value.MonteCarloLossProbability,value.QualityScore,value.StrategyReturn,value.BenchmarkReturn}.All(Finite);
    private static bool SafeIdentityToken(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=64&&value.All(character=>char.IsAsciiLetterOrDigit(character)||character is '.' or '_' or '-');
    private static bool ValidMacro(PersistedMacroObservation value,DateTimeOffset now)=>
        SafeToken(value.IndicatorId)&&value.Revision>0&&!string.IsNullOrWhiteSpace(value.Geography)&&!string.IsNullOrWhiteSpace(value.Frequency)&&!string.IsNullOrWhiteSpace(value.Unit)&&value.ObservationAtUtc.Offset==TimeSpan.Zero&&value.FirstObservedAtUtc.Offset==TimeSpan.Zero&&value.ObservationAtUtc<=value.FirstObservedAtUtc&&value.FirstObservedAtUtc<=now&&value.SourceArtifactHash.Length==64&&value.SourceArtifactHash.All(Uri.IsHexDigit);
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
