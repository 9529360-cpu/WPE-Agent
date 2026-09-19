using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpeAgent.CrossAssetResearch;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

internal static class ExecutionCostAuthorityV1
{
    private static readonly ResearchCostModel Model = ResearchRealityModel.DefaultCosts;
    internal static readonly string CanonicalSha256 = Hash(
        $"commission={Model.CommissionRate.ToString("G29",CultureInfo.InvariantCulture)}|slippage={Model.SlippageRate.ToString("G29",CultureInfo.InvariantCulture)}|fixed={Model.FixedCostPerTrade.ToString("G29",CultureInfo.InvariantCulture)}|borrow={Model.BorrowRatePerDay.ToString("G29",CultureInfo.InvariantCulture)}");
    internal static readonly string Version = "research-cost-v1-" + CanonicalSha256[..16];

    internal static ResearchCostModel Costs => Model;
    internal static ExecutionRealityCostAssumptionV1 RealityCosts =>
        new(Version,Model.CommissionRate,Model.SlippageRate);

    private static string Hash(string value)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record ExecutionVenueRuleIdentityV1(
    string Version,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal static class ExecutionVenueRuleCanonicalizerV1
{
    internal static ExecutionVenueRuleIdentityV1 Create(string providerId,TradingRule rule)
    {
        if(string.IsNullOrWhiteSpace(providerId))throw new ArgumentException("Provider id is required.",nameof(providerId));
        ArgumentNullException.ThrowIfNull(rule);
        if(string.IsNullOrWhiteSpace(rule.Symbol)
           ||rule.StepSize<=0||rule.TickSize<=0||rule.MinQuantity<=0||rule.MinNotional<=0||rule.MaxLeverage<=0)
            throw new InvalidOperationException("Venue rule is incomplete.");

        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("environment","Testnet");
            writer.WriteNumber("max_leverage",rule.MaxLeverage);
            writer.WriteNumber("min_notional",rule.MinNotional);
            writer.WriteNumber("min_quantity",rule.MinQuantity);
            writer.WriteString("provider_id",providerId);
            writer.WriteNumber("step_size",rule.StepSize);
            writer.WriteString("symbol",rule.Symbol);
            writer.WriteNumber("tick_size",rule.TickSize);
            writer.WriteEndObject();
        }
        var bytes=stream.ToArray();
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new("venue-rule-v1-"+hash[..16],hash,bytes);
    }
}

internal sealed record ExecutionPreTradeSimulationProvenanceV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string ProviderId,
    string Environment,
    string ArtifactSha256,
    string IntentSha256,
    string TimelineSha256,
    string MarketProvenanceSha256,
    byte[] MarketProvenanceCanonicalBytes,
    string CostModelVersion,
    string CostModelSha256,
    string VenueRuleVersion,
    string VenueRuleSha256,
    byte[] VenueRuleCanonicalBytes,
    string SimulatedFillSha256,
    DateTimeOffset CreatedAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class ExecutionPreTradeSimulationProvenanceCanonicalizerV1
{
    internal const string Schema="wpe.execution-pretrade-simulation-provenance/1.0";

    internal static ExecutionPreTradeSimulationProvenanceV1 Create(
        DurableExecutionArtifactV2 artifact,
        ExecutionIntent intent,
        ExecutionSimulationFillV1 fill,
        MarketEvidence market,
        StrategyExposureTimelineArtifactV1 timeline,
        ExecutionVenueRuleIdentityV1 venueRule,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(venueRule);

        var validation=DurableExecutionArtifactCanonicalizerV2.Validate(artifact);
        if(!validation.Valid)throw new InvalidOperationException("Pre-trade artifact is invalid.");
        var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        if(!string.Equals(artifact.Environment,"Testnet",StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade simulation provenance is Testnet-only.");
        if(!MatchesIntent(artifact,intent))
            throw new InvalidOperationException("Pre-trade intent does not match the durable artifact.");
        if(!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill)
           ||!string.Equals(fill.CorrelationId,artifact.CorrelationId,StringComparison.Ordinal)
           ||!string.Equals(fill.ClientOrderId,intent.ClientOrderId,StringComparison.Ordinal)
           ||!string.Equals(fill.StrategyId,artifact.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(fill.StrategyVersion,artifact.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(fill.CostModelVersion,ExecutionCostAuthorityV1.Version,StringComparison.Ordinal)
           ||!string.Equals(fill.VenueRuleVersion,venueRule.Version,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade simulated fill identity is invalid.");
        if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)
           ||market.Provenance is null
           ||!string.Equals(market.Provenance.Environment,"Testnet",StringComparison.Ordinal)
           ||!string.Equals(market.Provenance.ProviderId,artifact.ProviderId,StringComparison.Ordinal)
           ||!string.Equals(market.Symbol,intent.Symbol,StringComparison.Ordinal)
           ||new DateTimeOffset(market.CollectedAt)!=fill.MarketAsOfUtc)
            throw new InvalidOperationException("Pre-trade market provenance is invalid.");
        if(!StrategyExposureTimelineV1.IsCanonical(timeline)
           ||!string.Equals(timeline.StrategyId,artifact.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(timeline.StrategyVersion,artifact.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(timeline.Symbol,intent.Symbol,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade timeline provenance is invalid.");

        var created=createdAtUtc.ToUniversalTime();
        if(createdAtUtc.Offset!=TimeSpan.Zero||created<fill.SimulatedAtUtc)
            throw new InvalidOperationException("Pre-trade provenance time is invalid.");

        var draft=new ExecutionPreTradeSimulationProvenanceV1(
            Schema,
            artifact.CorrelationId,
            intent.ClientOrderId,
            artifact.StrategyId,
            artifact.StrategyVersion,
            artifact.ProviderId,
            artifact.Environment,
            hashes.ArtifactHash,
            hashes.IntentHash,
            timeline.CanonicalSha256,
            market.Provenance.CanonicalSha256,
            market.Provenance.CanonicalBytes,
            fill.CostModelVersion,
            ExecutionCostAuthorityV1.CanonicalSha256,
            venueRule.Version,
            venueRule.CanonicalSha256,
            venueRule.CanonicalBytes,
            fill.CanonicalSha256,
            created,
            [],
            string.Empty);
        var bytes=Serialize(draft);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var value=draft with{CanonicalBytes=bytes,CanonicalSha256=hash};
        if(!IsCanonical(value))throw new InvalidOperationException("Pre-trade provenance failed canonical self-check.");
        return value;
    }

    internal static bool IsCanonical(ExecutionPreTradeSimulationProvenanceV1? value)
    {
        if(value is null
           ||value.Schema!=Schema
           ||value.Environment!="Testnet"
           ||value.CreatedAtUtc.Offset!=TimeSpan.Zero
           ||!Sha(value.ArtifactSha256)
           ||!Sha(value.IntentSha256)
           ||!Sha(value.TimelineSha256)
           ||!Sha(value.MarketProvenanceSha256)
           ||!Sha(value.CostModelSha256)
           ||!Sha(value.VenueRuleSha256)
           ||!Sha(value.SimulatedFillSha256)
           ||!Sha(value.CanonicalSha256)
           ||value.MarketProvenanceCanonicalBytes.Length==0
           ||value.VenueRuleCanonicalBytes.Length==0
           ||value.CanonicalBytes.Length==0
           ||!string.Equals(value.CostModelVersion,ExecutionCostAuthorityV1.Version,StringComparison.Ordinal)
           ||!string.Equals(value.CostModelSha256,ExecutionCostAuthorityV1.CanonicalSha256,StringComparison.Ordinal))
            return false;
        try
        {
            if(!CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(value.MarketProvenanceCanonicalBytes),
                   Convert.FromHexString(value.MarketProvenanceSha256))
               ||!CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(value.VenueRuleCanonicalBytes),
                   Convert.FromHexString(value.VenueRuleSha256)))
                return false;
            var bytes=Serialize(value);
            return CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes),Convert.FromHexString(value.CanonicalSha256));
        }
        catch{return false;}
    }

    private static bool MatchesIntent(DurableExecutionArtifactV2 artifact,ExecutionIntent intent)
    {
        var matches=artifact.Intents.Where(x=>string.Equals(x.ClientOrderId,intent.ClientOrderId,StringComparison.Ordinal)).ToArray();
        if(matches.Length!=1)return false;
        var value=matches[0];
        return string.Equals(value.Symbol,intent.Symbol,StringComparison.Ordinal)
            &&string.Equals(value.Side,intent.Side.ToString(),StringComparison.Ordinal)
            &&value.Quantity==intent.Quantity
            &&value.ReduceOnly==intent.ReduceOnly
            &&value.StopLoss==intent.StopLoss
            &&value.TakeProfit==intent.TakeProfit
            &&string.Equals(value.Action,intent.Action.ToString(),StringComparison.Ordinal)
            &&string.Equals(value.OrderType,intent.OrderType.ToString(),StringComparison.Ordinal)
            &&value.LimitPrice==intent.LimitPrice
            &&value.ExpectedPrice==intent.ExpectedPrice;
    }

    private static byte[] Serialize(ExecutionPreTradeSimulationProvenanceV1 value)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("artifact_sha256",value.ArtifactSha256);
            writer.WriteString("client_order_id",value.ClientOrderId);
            writer.WriteString("correlation_id",value.CorrelationId);
            writer.WriteString("cost_model_sha256",value.CostModelSha256);
            writer.WriteString("cost_model_version",value.CostModelVersion);
            writer.WriteString("created_at_utc",value.CreatedAtUtc.ToUniversalTime());
            writer.WriteString("environment",value.Environment);
            writer.WriteString("intent_sha256",value.IntentSha256);
            writer.WriteString("market_provenance_sha256",value.MarketProvenanceSha256);
            writer.WriteString("provider_id",value.ProviderId);
            writer.WriteString("schema",value.Schema);
            writer.WriteString("simulated_fill_sha256",value.SimulatedFillSha256);
            writer.WriteString("strategy_id",value.StrategyId);
            writer.WriteString("strategy_version",value.StrategyVersion);
            writer.WriteString("timeline_sha256",value.TimelineSha256);
            writer.WriteString("venue_rule_sha256",value.VenueRuleSha256);
            writer.WriteString("venue_rule_version",value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool Sha(string value)=>value.Length==64&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
}

internal sealed record ExecutionPreTradeSimulationItemV1(
    ExecutionSimulationFillV1 Fill,
    ExecutionPreTradeSimulationProvenanceV1 Provenance);

internal sealed record ExecutionPreTradeSimulationBatchV1(
    bool EligibleForRiskApproval,
    IReadOnlyList<ExecutionPreTradeSimulationItemV1> Items,
    IReadOnlyList<string> ReasonCodes);

internal static class ExecutionPreTradeSimulationV1
{
    internal const string SimulationModelVersion="wpe.pretrade-market-taker/1.0";
    internal static readonly TimeSpan MaximumMarketAge=TimeSpan.FromSeconds(30);

    internal static ExecutionPreTradeSimulationBatchV1 Create(
        DurableExecutionArtifactV2 artifact,
        IReadOnlyList<ExecutionIntent> intents,
        MarketEvidence market,
        TradingRule rule,
        StrategyExposureTimelineArtifactV1 timeline,
        DateTimeOffset simulatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(timeline);

        var now=simulatedAtUtc.ToUniversalTime();
        if(simulatedAtUtc.Offset!=TimeSpan.Zero)
            throw new ArgumentException("Simulation time must be UTC.",nameof(simulatedAtUtc));
        if(!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid
           ||artifact.Environment!="Testnet")
            throw new InvalidOperationException("Automatic execution artifact is not eligible for pre-trade simulation.");
        if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)
           ||market.Provenance is null
           ||market.CollectedAt.Kind!=DateTimeKind.Utc
           ||market.Provenance.Environment!="Testnet"
           ||!string.Equals(market.Provenance.ProviderId,artifact.ProviderId,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade market evidence is not canonical Testnet evidence.");
        var marketAt=new DateTimeOffset(market.CollectedAt);
        if(marketAt>now||now-marketAt>MaximumMarketAge)
            throw new InvalidOperationException("Pre-trade market evidence is stale or future.");
        if(!StrategyExposureTimelineV1.IsCanonical(timeline)
           ||!string.Equals(timeline.StrategyId,artifact.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(timeline.StrategyVersion,artifact.StrategyVersion,StringComparison.Ordinal))
            throw new InvalidOperationException("Pre-trade strategy timeline is missing or conflicting.");
        if(!string.Equals(rule.Symbol,market.Symbol,StringComparison.Ordinal))
            throw new InvalidOperationException("Venue rule symbol does not match the market evidence.");

        var venueRule=ExecutionVenueRuleCanonicalizerV1.Create(artifact.ProviderId,rule);
        var reasons=new SortedSet<string>(StringComparer.Ordinal);
        var items=new List<ExecutionPreTradeSimulationItemV1>(intents.Count);

        foreach(var intent in intents)
        {
            if(!string.Equals(intent.Symbol,market.Symbol,StringComparison.Ordinal))
                throw new InvalidOperationException("Pre-trade intent symbol does not match the simulation market.");
            var fill=SimulateIntent(artifact,intent,market,rule,venueRule,now);
            if(fill.State==ExecutionSimulationFillStateV1.Unsupported)
            {
                reasons.Add("simulation.unsupported:"+intent.ClientOrderId);
                if(!intent.ReduceOnly)reasons.Add("simulation.risk-increase-unsupported");
            }
            var provenance=ExecutionPreTradeSimulationProvenanceCanonicalizerV1.Create(
                artifact,intent,fill,market,timeline,venueRule,now);
            items.Add(new(fill,provenance));
        }

        return new(
            !reasons.Contains("simulation.risk-increase-unsupported"),
            items,
            reasons.ToArray());
    }

    private static ExecutionSimulationFillV1 SimulateIntent(
        DurableExecutionArtifactV2 artifact,
        ExecutionIntent intent,
        MarketEvidence market,
        TradingRule rule,
        ExecutionVenueRuleIdentityV1 venueRule,
        DateTimeOffset simulatedAtUtc)
    {
        var unsupported=intent.OrderType!=ExecutionOrderType.Market
            ||rule.RoundQuantity(intent.Quantity)!=intent.Quantity
            ||intent.Quantity<rule.MinQuantity
            ||market.Price<=0;

        var adversePrice=0m;
        if(!unsupported)
        {
            var buy=intent.ReduceOnly?intent.Side==PositionSide.Short:intent.Side==PositionSide.Long;
            var raw=buy
                ?market.Price*(1+ExecutionCostAuthorityV1.Costs.SlippageRate)
                :market.Price*(1-ExecutionCostAuthorityV1.Costs.SlippageRate);
            adversePrice=RoundAdverse(raw,rule.TickSize,buy);
            unsupported=adversePrice<=0||adversePrice*intent.Quantity<rule.MinNotional;
        }

        return ExecutionSimulationFillCanonicalizerV1.Create(
            artifact.CorrelationId,
            intent.ClientOrderId,
            artifact.StrategyId,
            artifact.StrategyVersion,
            ExecutionCostAuthorityV1.Version,
            SimulationModelVersion,
            venueRule.Version,
            intent.Symbol,
            intent.Side,
            intent.ReduceOnly,
            intent.OrderType,
            intent.Quantity,
            unsupported?ExecutionSimulationFillStateV1.Unsupported:ExecutionSimulationFillStateV1.Filled,
            unsupported?0m:intent.Quantity,
            unsupported?0m:adversePrice,
            unsupported?0m:adversePrice*intent.Quantity*ExecutionCostAuthorityV1.Costs.CommissionRate,
            unsupported?ExecutionSimulationFeeRoleV1.Unavailable:ExecutionSimulationFeeRoleV1.Taker,
            false,
            0,
            new DateTimeOffset(market.CollectedAt),
            simulatedAtUtc,
            unsupported?"simulation-unsupported":"market-taker-research-cost");
    }

    private static decimal RoundAdverse(decimal value,decimal tick,bool buy)
    {
        if(tick<=0)return 0;
        var units=value/tick;
        return (buy?Math.Ceiling(units):Math.Floor(units))*tick;
    }
}
