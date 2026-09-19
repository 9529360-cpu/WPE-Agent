using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum PostTradeSimulationRealityLinkStateV1
{
    Linked,
    Unavailable,
    Conflict
}

public sealed record PostTradeSimulationRealityLinkV1(
    string Schema,
    string ClientOrderId,
    string CycleId,
    string Symbol,
    string Side,
    string StrategyId,
    string StrategyVersion,
    DateTimeOffset ReviewClosedAtUtc,
    string ReviewOutcome,
    decimal NetPnl,
    decimal ReturnPct,
    PostTradeSimulationRealityLinkStateV1 State,
    string Code,
    string? CostModelVersion,
    string? SimulationModelVersion,
    string? VenueRuleVersion,
    string? ArtifactSha256,
    string? IntentSha256,
    string? SimulationSourceSha256,
    string? MarketProvenanceSha256,
    string? SimulatedFillSha256,
    ExecutionSimulationFillStateV1? SimulatedState,
    string? ObservedDriftSha256,
    ExecutionRealityStateV1? ObservedState,
    string? ComparisonSha256,
    bool? StateMatch,
    decimal? SimulatedFillRatio,
    decimal? ObservedFillRatio,
    decimal? FillRatioDelta,
    bool? PriceComparable,
    decimal? PriceDriftBps,
    bool? FeeComparable,
    decimal? FeeDriftBps,
    bool? LatencyComparable,
    long? LatencyDriftMs,
    bool? TotalComparable,
    decimal? TotalExecutionDriftBps,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public sealed class PostTradeSimulationRealityLinkerV1
{
    public const string Schema="wpe.post-trade-simulation-reality-link/1.0";
    private readonly AgentSqliteStore _store;

    public PostTradeSimulationRealityLinkerV1(AgentSqliteStore store)=>
        _store=store??throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<PostTradeSimulationRealityLinkV1>> GetRecentAsync(
        int limit,
        CancellationToken ct)
    {
        var reviews=await _store.GetRecentPostTradeReviewsAsync(Math.Clamp(limit,1,100),ct);
        var result=new List<PostTradeSimulationRealityLinkV1>(reviews.Count);
        foreach(var review in reviews)result.Add(await LinkAsync(review,ct));
        return result;
    }

    public async Task<PostTradeSimulationRealityLinkV1> LinkAsync(
        PostTradeReviewV1 review,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);
        if(string.IsNullOrWhiteSpace(review.StrategyId))
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Unavailable,"strategy-attribution-unavailable"));

        var facts=await _store.GetExecutionRealityDriftByClientOrderIdAsync(review.ClientOrderId,129,ct);
        if(facts.Count==0)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Unavailable,"observed-drift-unavailable"));
        if(facts.Count>=129)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"observed-drift-overflow"));
        if(facts.Any(x=>!ReviewMatchesObserved(review,x)))
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"observed-review-mismatch"));

        var executionKeys=facts.Select(ObservedExecutionKey).Distinct(StringComparer.Ordinal).ToArray();
        if(executionKeys.Length!=1)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"observed-execution-conflict"));
        var costModels=facts.Select(x=>x.CostModelVersion).Distinct(StringComparer.Ordinal).ToArray();
        if(costModels.Length!=1)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"cost-model-conflict"));
        var feeFacts=facts.Where(x=>x.FeeComparable).ToArray();
        if(feeFacts.Select(ObservedFeeKey).Distinct(StringComparer.Ordinal).Count()>1)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"observed-fee-conflict"));

        var observed=(feeFacts.Length>0?feeFacts:facts.ToArray())
            .OrderByDescending(x=>x.ObservedAtUtc)
            .ThenByDescending(x=>x.CanonicalSha256,StringComparer.Ordinal)
            .First();

        var comparisons=await _store.GetExecutionSimulationComparisonsByClientOrderIdAsync(review.ClientOrderId,129,ct);
        if(comparisons.Count>=129)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"simulation-comparison-overflow"));
        var exact=comparisons.Where(x=>string.Equals(
            x.ObservedCanonicalSha256,observed.CanonicalSha256,StringComparison.Ordinal)).ToArray();
        if(exact.Length==0)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Unavailable,"simulation-comparison-unavailable"));
        if(exact.Any(x=>!ReviewMatchesComparison(review,observed,x)))
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"simulation-comparison-review-mismatch"));

        var simulatedHashes=exact.Select(x=>x.SimulatedCanonicalSha256).Distinct(StringComparer.Ordinal).ToArray();
        var comparisonKeys=exact.Select(ComparisonKey).Distinct(StringComparer.Ordinal).ToArray();
        if(simulatedHashes.Length!=1||comparisonKeys.Length!=1)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"simulation-comparison-conflict"));

        var comparison=exact
            .OrderByDescending(x=>x.ComparedAtUtc)
            .ThenByDescending(x=>x.CanonicalSha256,StringComparer.Ordinal)
            .First();

        var source=await _store.GetExecutionSimulationSourceAsync(review.CycleId,review.ClientOrderId,ct);
        var fill=await _store.GetExecutionSimulationFillAsync(review.CycleId,review.ClientOrderId,ct);
        if(source is null||fill is null)
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Unavailable,"simulation-source-unavailable"));
        if(!ExecutionSimulationSourceCanonicalizerV1.IsCanonical(source)
           ||!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill)
           ||!string.Equals(source.CorrelationId,review.CycleId,StringComparison.Ordinal)
           ||!string.Equals(source.ClientOrderId,review.ClientOrderId,StringComparison.Ordinal)
           ||!string.Equals(source.StrategyId,review.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(source.StrategyVersion,review.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(source.Symbol,review.Symbol,StringComparison.Ordinal)
           ||!string.Equals(fill.CanonicalSha256,comparison.SimulatedCanonicalSha256,StringComparison.Ordinal)
           ||!string.Equals(fill.CostModelVersion,observed.CostModelVersion,StringComparison.Ordinal)
           ||!string.Equals(fill.CostModelVersion,comparison.CostModelVersion,StringComparison.Ordinal)
           ||!string.Equals(fill.SimulationModelVersion,comparison.SimulationModelVersion,StringComparison.Ordinal)
           ||!string.Equals(fill.VenueRuleVersion,comparison.VenueRuleVersion,StringComparison.Ordinal))
            return Finalize(Draft(review,PostTradeSimulationRealityLinkStateV1.Conflict,"simulation-source-chain-conflict"));

        var draft=Draft(review,PostTradeSimulationRealityLinkStateV1.Linked,"linked") with
        {
            CostModelVersion=observed.CostModelVersion,
            SimulationModelVersion=comparison.SimulationModelVersion,
            VenueRuleVersion=comparison.VenueRuleVersion,
            ArtifactSha256=source.ArtifactSha256,
            IntentSha256=source.IntentSha256,
            SimulationSourceSha256=source.CanonicalSha256,
            MarketProvenanceSha256=source.State==ExecutionSimulationSourceStateV1.Available
                ?source.MarketProvenanceSha256:null,
            SimulatedFillSha256=fill.CanonicalSha256,
            SimulatedState=fill.State,
            ObservedDriftSha256=observed.CanonicalSha256,
            ObservedState=observed.State,
            ComparisonSha256=comparison.CanonicalSha256,
            StateMatch=comparison.StateMatch,
            SimulatedFillRatio=comparison.SimulatedFillRatio,
            ObservedFillRatio=comparison.ObservedFillRatio,
            FillRatioDelta=comparison.FillRatioDelta,
            PriceComparable=comparison.PriceComparable,
            PriceDriftBps=comparison.PriceDriftBps,
            FeeComparable=comparison.FeeComparable,
            FeeDriftBps=comparison.FeeDriftBps,
            LatencyComparable=comparison.LatencyComparable,
            LatencyDriftMs=comparison.LatencyDriftMs,
            TotalComparable=comparison.TotalComparable,
            TotalExecutionDriftBps=comparison.TotalExecutionDriftBps
        };
        return Finalize(draft);
    }

    public static bool IsCanonical(PostTradeSimulationRealityLinkV1? value)
    {
        if(value is null
           ||value.Schema!=Schema
           ||value.CanonicalBytes.Length==0
           ||!LowerSha(value.CanonicalSha256)
           ||value.ReviewClosedAtUtc.Offset!=TimeSpan.Zero
           ||string.IsNullOrWhiteSpace(value.ClientOrderId)
           ||string.IsNullOrWhiteSpace(value.CycleId)
           ||string.IsNullOrWhiteSpace(value.Symbol)
           ||string.IsNullOrWhiteSpace(value.Side)
           ||string.IsNullOrWhiteSpace(value.StrategyId)
           ||string.IsNullOrWhiteSpace(value.StrategyVersion)
           ||string.IsNullOrWhiteSpace(value.Code))
            return false;

        if(value.State!=PostTradeSimulationRealityLinkStateV1.Linked)
        {
            if(value.CostModelVersion is not null
               ||value.SimulationModelVersion is not null
               ||value.VenueRuleVersion is not null
               ||value.ArtifactSha256 is not null
               ||value.IntentSha256 is not null
               ||value.SimulationSourceSha256 is not null
               ||value.MarketProvenanceSha256 is not null
               ||value.SimulatedFillSha256 is not null
               ||value.SimulatedState is not null
               ||value.ObservedDriftSha256 is not null
               ||value.ObservedState is not null
               ||value.ComparisonSha256 is not null
               ||value.StateMatch is not null
               ||value.SimulatedFillRatio is not null
               ||value.ObservedFillRatio is not null
               ||value.FillRatioDelta is not null
               ||value.PriceComparable is not null
               ||value.PriceDriftBps is not null
               ||value.FeeComparable is not null
               ||value.FeeDriftBps is not null
               ||value.LatencyComparable is not null
               ||value.LatencyDriftMs is not null
               ||value.TotalComparable is not null
               ||value.TotalExecutionDriftBps is not null)
                return false;
        }

        if(value.State==PostTradeSimulationRealityLinkStateV1.Linked)
        {
            if(!LowerSha(value.ArtifactSha256)
               ||!LowerSha(value.IntentSha256)
               ||!LowerSha(value.SimulationSourceSha256)
               ||!LowerSha(value.SimulatedFillSha256)
               ||!LowerSha(value.ObservedDriftSha256)
               ||!LowerSha(value.ComparisonSha256)
               ||string.IsNullOrWhiteSpace(value.CostModelVersion)
               ||string.IsNullOrWhiteSpace(value.SimulationModelVersion)
               ||string.IsNullOrWhiteSpace(value.VenueRuleVersion)
               ||value.SimulatedState is null
               ||value.ObservedState is null
               ||value.StateMatch is null
               ||value.SimulatedFillRatio is null
               ||value.ObservedFillRatio is null
               ||value.FillRatioDelta is null
               ||value.PriceComparable is null
               ||value.FeeComparable is null
               ||value.LatencyComparable is null
               ||value.TotalComparable is null
               ||value.FillRatioDelta!=value.ObservedFillRatio-value.SimulatedFillRatio
               ||value.PriceComparable!=value.PriceDriftBps.HasValue
               ||value.FeeComparable!=value.FeeDriftBps.HasValue
               ||value.LatencyComparable!=value.LatencyDriftMs.HasValue
               ||value.TotalComparable!=value.TotalExecutionDriftBps.HasValue)
                return false;
        }

        try
        {
            var bytes=Serialize(value);
            return CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes),
                    Convert.FromHexString(value.CanonicalSha256));
        }
        catch{return false;}
    }

    private static bool ReviewMatchesObserved(PostTradeReviewV1 review,ExecutionRealityDriftFactV1 fact)=>
        fact.Terminal
        &&fact.State==ExecutionRealityStateV1.Filled
        &&fact.Comparable
        &&fact.ReduceOnly
        &&string.Equals(fact.CorrelationId,review.CycleId,StringComparison.Ordinal)
        &&string.Equals(fact.ClientOrderId,review.ClientOrderId,StringComparison.Ordinal)
        &&string.Equals(fact.StrategyId,review.StrategyId,StringComparison.Ordinal)
        &&string.Equals(fact.StrategyVersion,review.StrategyVersion,StringComparison.Ordinal)
        &&string.Equals(fact.Symbol,review.Symbol,StringComparison.Ordinal)
        &&string.Equals(fact.Side.ToString(),review.Side,StringComparison.Ordinal)
        &&fact.ExecutedQuantity==review.Quantity
        &&fact.AveragePrice==review.ExitPrice;

    private static bool ReviewMatchesComparison(
        PostTradeReviewV1 review,
        ExecutionRealityDriftFactV1 observed,
        ExecutionSimulationComparisonSummaryV1 comparison)=>
        string.Equals(comparison.CorrelationId,review.CycleId,StringComparison.Ordinal)
        &&string.Equals(comparison.ClientOrderId,review.ClientOrderId,StringComparison.Ordinal)
        &&string.Equals(comparison.StrategyId,review.StrategyId,StringComparison.Ordinal)
        &&string.Equals(comparison.StrategyVersion,review.StrategyVersion,StringComparison.Ordinal)
        &&string.Equals(comparison.Symbol,review.Symbol,StringComparison.Ordinal)
        &&string.Equals(comparison.CostModelVersion,observed.CostModelVersion,StringComparison.Ordinal);

    private static string ObservedExecutionKey(ExecutionRealityDriftFactV1 x)=>Key(
        x.CorrelationId,x.ClientOrderId,x.StrategyId,x.StrategyVersion,x.CostModelVersion,
        x.Symbol,x.Side,x.ReduceOnly,x.OrderType,x.IntendedQuantity,x.ExecutedQuantity,
        x.ExpectedPrice,x.AveragePrice,x.ExchangeStatus,x.State,x.FillRatio,
        x.AdverseSlippageBps,x.ExpectedSlippageBps,x.SlippageDriftBps);

    private static string ObservedFeeKey(ExecutionRealityDriftFactV1 x)=>Key(
        x.ObservedFee,x.FeeBasis,x.ObservedFeeRateBps,x.ExpectedCommissionBps,
        x.FeeDriftBps,x.TotalExecutionDriftBps);

    private static string ComparisonKey(ExecutionSimulationComparisonSummaryV1 x)=>Key(
        x.CorrelationId,x.ClientOrderId,x.StrategyId,x.StrategyVersion,x.CostModelVersion,
        x.SimulationModelVersion,x.VenueRuleVersion,x.Symbol,x.StateMatch,
        x.SimulatedFillRatio,x.ObservedFillRatio,x.FillRatioDelta,x.PriceComparable,
        x.PriceDriftBps,x.FeeComparable,x.FeeDriftBps,x.LatencyComparable,
        x.LatencyDriftMs,x.TotalComparable,x.TotalExecutionDriftBps,x.ReasonCode,
        x.SimulatedCanonicalSha256,x.ObservedCanonicalSha256);

    private static string Key(params object?[] values)=>string.Join(
        "\u001f",
        values.Select(value=>value switch
        {
            null=>string.Empty,
            decimal number=>number.ToString(CultureInfo.InvariantCulture),
            bool flag=>flag?"1":"0",
            _=>Convert.ToString(value,CultureInfo.InvariantCulture)??string.Empty
        }));

    private static PostTradeSimulationRealityLinkV1 Draft(
        PostTradeReviewV1 review,
        PostTradeSimulationRealityLinkStateV1 state,
        string code)=>new(
            Schema,
            review.ClientOrderId,
            review.CycleId,
            review.Symbol,
            review.Side,
            review.StrategyId??string.Empty,
            review.StrategyVersion,
            review.ClosedAtUtc.ToUniversalTime(),
            review.Outcome,
            review.NetPnl,
            review.ReturnPct,
            state,
            code,
            null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
            null,null,null,null,null,null,null,null,null,null,null,
            [],
            string.Empty);

    private static PostTradeSimulationRealityLinkV1 Finalize(PostTradeSimulationRealityLinkV1 draft)
    {
        var bytes=Serialize(draft);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with{CanonicalBytes=bytes,CanonicalSha256=hash};
    }

    private static byte[] Serialize(PostTradeSimulationRealityLinkV1 value)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteString(writer,"artifact_sha256",value.ArtifactSha256);
            writer.WriteString("client_order_id",value.ClientOrderId);
            writer.WriteString("code",value.Code);
            WriteString(writer,"comparison_sha256",value.ComparisonSha256);
            WriteString(writer,"cost_model_version",value.CostModelVersion);
            writer.WriteString("cycle_id",value.CycleId);
            WriteBool(writer,"fee_comparable",value.FeeComparable);
            WriteDecimal(writer,"fee_drift_bps",value.FeeDriftBps);
            WriteDecimal(writer,"fill_ratio_delta",value.FillRatioDelta);
            WriteString(writer,"intent_sha256",value.IntentSha256);
            WriteBool(writer,"latency_comparable",value.LatencyComparable);
            WriteLong(writer,"latency_drift_ms",value.LatencyDriftMs);
            WriteString(writer,"market_provenance_sha256",value.MarketProvenanceSha256);
            writer.WriteNumber("net_pnl",value.NetPnl);
            WriteString(writer,"observed_drift_sha256",value.ObservedDriftSha256);
            WriteDecimal(writer,"observed_fill_ratio",value.ObservedFillRatio);
            if(value.ObservedState is null)writer.WriteNull("observed_state");else writer.WriteString("observed_state",value.ObservedState.Value.ToString());
            WriteBool(writer,"price_comparable",value.PriceComparable);
            WriteDecimal(writer,"price_drift_bps",value.PriceDriftBps);
            writer.WriteString("review_closed_at_utc",value.ReviewClosedAtUtc.ToUniversalTime());
            writer.WriteString("review_outcome",value.ReviewOutcome);
            writer.WriteNumber("return_pct",value.ReturnPct);
            writer.WriteString("schema",value.Schema);
            WriteString(writer,"simulated_fill_sha256",value.SimulatedFillSha256);
            WriteDecimal(writer,"simulated_fill_ratio",value.SimulatedFillRatio);
            if(value.SimulatedState is null)writer.WriteNull("simulated_state");else writer.WriteString("simulated_state",value.SimulatedState.Value.ToString());
            WriteString(writer,"simulation_model_version",value.SimulationModelVersion);
            WriteString(writer,"simulation_source_sha256",value.SimulationSourceSha256);
            writer.WriteString("side",value.Side);
            writer.WriteString("state",value.State.ToString());
            WriteBool(writer,"state_match",value.StateMatch);
            writer.WriteString("strategy_id",value.StrategyId);
            writer.WriteString("strategy_version",value.StrategyVersion);
            writer.WriteString("symbol",value.Symbol);
            WriteBool(writer,"total_comparable",value.TotalComparable);
            WriteDecimal(writer,"total_execution_drift_bps",value.TotalExecutionDriftBps);
            WriteString(writer,"venue_rule_version",value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool LowerSha(string? value)=>
        value is {Length:64}&&value.All(ch=>ch is>='0'and<='9'or>='a'and<='f');

    private static void WriteString(Utf8JsonWriter writer,string name,string? value)
    {if(value is null)writer.WriteNull(name);else writer.WriteString(name,value);}
    private static void WriteBool(Utf8JsonWriter writer,string name,bool? value)
    {if(value is null)writer.WriteNull(name);else writer.WriteBoolean(name,value.Value);}
    private static void WriteDecimal(Utf8JsonWriter writer,string name,decimal? value)
    {if(value is null)writer.WriteNull(name);else writer.WriteNumber(name,value.Value);}
    private static void WriteLong(Utf8JsonWriter writer,string name,long? value)
    {if(value is null)writer.WriteNull(name);else writer.WriteNumber(name,value.Value);}
}
