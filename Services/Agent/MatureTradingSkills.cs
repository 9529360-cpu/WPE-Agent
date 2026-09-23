using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class DeterministicPlanSkill
{
    public DecisionPlan Complete(DecisionPlan source,MarketEvidence? market,RiskLimits risk)
    {
        if (market is null || !IsRiskIncreasing(source.Action)) return source;
        var longSide = source.Action is DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong;
        var shortSide = source.Action is DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort;
        if (!longSide && !shortSide) return source;
        var entry = source.EntryPrice > 0 ? source.EntryPrice : market.Price;
        var atr = Math.Clamp((decimal)market.Quality.AtrPercent, .003m, .08m);
        var riskDistance = entry * atr * 1.5m;
        decimal stop;
        if (source.StopLossPrice > 0) stop = source.StopLossPrice;
        else if (longSide) stop = Math.Max(entry-riskDistance, market.Support > 0 && market.Support < entry ? market.Support*.998m : 0);
        else stop = Math.Min(entry+riskDistance, market.Resistance > entry ? market.Resistance*1.002m : decimal.MaxValue);
        if (stop is <= 0 or decimal.MaxValue || (longSide && stop >= entry) || (shortSide && stop <= entry)) stop = longSide ? entry-riskDistance : entry+riskDistance;
        var distance = Math.Abs(entry-stop);
        var minimumTake = longSide ? entry+distance*(decimal)risk.MinimumRiskReward : entry-distance*(decimal)risk.MinimumRiskReward;
        decimal take;
        if (source.TakeProfitPrice > 0) take = source.TakeProfitPrice;
        else if (longSide) take = market.Resistance > minimumTake ? market.Resistance*.998m : minimumTake;
        else take = market.Support > 0 && market.Support < minimumTake ? market.Support*1.002m : minimumTake;
        var reward = longSide ? take-entry : entry-take;
        source.EntryPrice=entry;source.StopLossPrice=stop;source.TakeProfitPrice=take;source.RiskRewardRatio=distance>0?(double)(reward/distance):0;
        source.OrderType=market.Quality.BestBid>0&&market.Quality.BestAsk>0&&market.Quality.SpreadBps<=risk.MaximumSpreadBps?ExecutionOrderType.Limit:ExecutionOrderType.Market;
        return source;
    }
    public static bool IsRiskIncreasing(DecisionAction action)=>action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort or DecisionAction.Lock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
}

public enum ModelOffInputStateV1 { Available, Missing, Stale, Unknown, Conflicting, Unsupported }
public sealed record ModelOffInputFactV1(string Id, ModelOffInputStateV1 State, string ArtifactHash, DateTimeOffset AsOfUtc);
public sealed record ModelOffStrategyIntentRequestV1(DecisionAction Action, string Instrument, DateTimeOffset EvaluationTimeUtc, TimeSpan MaximumAge, bool MainnetRequested, IReadOnlyList<ModelOffInputFactV1> Inputs);
public sealed record ModelOffRiskRuleV1(string RuleId, bool Passed, string ReasonCode);
public sealed record ModelOffStrategyRiskContractV1(string IntentSha256, string LedgerSha256, bool EligibleForRiskIncrease, IReadOnlyList<ModelOffRiskRuleV1> Rules);

public static class ModelOffStrategyRiskEvaluatorV1
{
    public static ModelOffStrategyRiskContractV1 Evaluate(ModelOffStrategyIntentRequestV1 request)
    {
        var rules = new List<ModelOffRiskRuleV1>();
        Add("environment.mainnet-disabled", !request.MainnetRequested, "risk.mainnet-disabled");
        Add("intent.action-defined", Enum.IsDefined(request.Action), "risk.action-unknown");
        Add("intent.instrument-required", !string.IsNullOrWhiteSpace(request.Instrument), "risk.instrument-missing");
        Add("intent.evaluation-time-utc", request.EvaluationTimeUtc != default && request.EvaluationTimeUtc.Offset == TimeSpan.Zero, "risk.time-invalid");
        Add("intent.maximum-age-positive", request.MaximumAge > TimeSpan.Zero, "risk.maximum-age-invalid");
        Add("input.present", request.Inputs is { Count: > 0 }, "risk.input-missing");

        foreach (var input in (request.Inputs ?? []).OrderBy(x => x?.Id, StringComparer.Ordinal))
        {
            if (input is null) { Add("input.null", false, "risk.input-malformed"); continue; }
            var id = string.IsNullOrWhiteSpace(input.Id) ? "invalid" : input.Id;
            Add($"input.{id}.identity", !string.IsNullOrWhiteSpace(input.Id) && IsSha256(input.ArtifactHash), "risk.input-malformed");
            Add($"input.{id}.state", Enum.IsDefined(input.State) && input.State == ModelOffInputStateV1.Available, $"risk.input-{input.State.ToString().ToLowerInvariant()}");
            var fresh = input.AsOfUtc != default && input.AsOfUtc.Offset == TimeSpan.Zero && input.AsOfUtc <= request.EvaluationTimeUtc && request.MaximumAge > TimeSpan.Zero && request.EvaluationTimeUtc - input.AsOfUtc <= request.MaximumAge;
            Add($"input.{id}.freshness", fresh, "risk.input-stale");
        }

        var ordered = rules.OrderBy(x => x.RuleId, StringComparer.Ordinal).ThenBy(x => x.ReasonCode, StringComparer.Ordinal).ToArray();
        var intentHash = Hash(JsonSerializer.Serialize(new
        {
            action = request.Action.ToString(), instrument = request.Instrument,
            evaluationTimeUtc = request.EvaluationTimeUtc.ToUniversalTime(), maximumAgeTicks = request.MaximumAge.Ticks,
            mainnetRequested = request.MainnetRequested,
            inputs = (request.Inputs ?? []).Where(x => x is not null).OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new { x.Id, state = x.State.ToString(), x.ArtifactHash, x.AsOfUtc })
        }));
        var ledgerHash = Hash(JsonSerializer.Serialize(ordered));
        var increasing = Enum.IsDefined(request.Action) && DeterministicPlanSkill.IsRiskIncreasing(request.Action);
        var corePassed = ordered.Where(x => !x.RuleId.StartsWith("input.", StringComparison.Ordinal)).All(x => x.Passed);
        var eligible = corePassed && (!increasing || ordered.All(x => x.Passed));
        return new(intentHash, ledgerHash, eligible, ordered);

        void Add(string id, bool passed, string reason) => rules.Add(new(id, passed, passed ? "ok" : reason));
    }

    private static bool IsSha256(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && value.AsSpan(7).ContainsAnyExcept("0123456789abcdef") == false;
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record RiskHistorySnapshot(decimal DailyRealizedPnl,int ConsecutiveLosses,int ApiFailures,bool OrderStateUncertain);

public sealed class IndependentRiskManagerSkill
{
    public IndependentRiskReview Review(DecisionPlan decision,EvidencePack evidence,
        IReadOnlyList<ExecutionIntent> intents,RiskLimits limits,RiskHistorySnapshot history,PortfolioRiskAssessment? portfolio=null)
    {
        var checks=new List<string>();var blocks=new List<string>();var increasing=DeterministicPlanSkill.IsRiskIncreasing(decision.Action);
        if(!increasing)return new(){Approved=true,RiskLevel="LOW",PlannedQuantity=intents.Sum(x=>x.Quantity),Checks=["risk_reducing_action"],Summary=L("RiskReview.Reducing")};
        var market=evidence.Markets.GetValueOrDefault(decision.Instrument);var equity=evidence.Account.Equity;
        Check(evidence.Completeness>=70,"evidence_complete",L("RiskReview.Evidence"));
        Check(market is not null&&DateTime.UtcNow-market.CollectedAt<=TimeSpan.FromMinutes(5),"market_fresh",L("RiskReview.Stale"));
        Check(DirectMarketStructureDecisionSkill.IsDirect(decision),"direct_market_structure_context","risk.direct-context-required");
        if(market is not null&&DirectMarketStructureDecisionSkill.IsDirect(decision))
            Check(DirectMarketStructureDecisionSkill.ContextMatches(decision,market),"direct_context_fresh","risk.direct-context-stale");
        Check(market is not null&&market.Quality.LiquidityScore>=limits.MinimumLiquidityScore,"liquidity",L("RiskReview.Liquidity",market?.Quality.LiquidityScore??0));
        Check(market is not null&&market.Quality.SpreadBps<=limits.MaximumSpreadBps,"spread",L("RiskReview.Spread",market?.Quality.SpreadBps??999));
        Check(market is not null&&market.Quality.AtrPercent<=limits.MaxAtrPercent,"volatility",L("RiskReview.Volatility",market?.Quality.AtrPercent??1));
        Check(decision.RiskRewardRatio>=limits.MinimumRiskReward,"risk_reward",L("RiskReview.RiskReward",decision.RiskRewardRatio,limits.MinimumRiskReward));
        if(history.ConsecutiveLosses>0)checks.Add($"performance_loss_streak_observed:{history.ConsecutiveLosses}");
        if(history.DailyRealizedPnl<0)checks.Add($"performance_daily_loss_observed:{history.DailyRealizedPnl:F2}");
        Check(history.ApiFailures<limits.ApiFailureThreshold,"api_health",L("RiskReview.ApiFailures",history.ApiFailures));
        Check(!history.OrderStateUncertain,"order_state",L("RiskReview.OrderUncertain"));
        Check(portfolio is{Approved:true},"portfolio_risk",L("RiskReview.Portfolio",portfolio?.Summary??"unavailable"));
        var newNotional=intents.Where(x=>!x.ReduceOnly).Sum(x=>x.Quantity*(x.ExpectedPrice>0?x.ExpectedPrice:decision.EntryPrice));var reducedNotional=intents.Where(x=>x.ReduceOnly).Sum(x=>x.Quantity*(x.ExpectedPrice>0?x.ExpectedPrice:decision.EntryPrice));var currentNotional=evidence.Positions.Sum(x=>x.Quantity*x.MarkPrice);var postNotional=Math.Max(0,currentNotional+newNotional-reducedNotional);var exposure=equity>0?postNotional/equity:1;var currentSymbol=evidence.Positions.Where(x=>x.Symbol==decision.Instrument).Sum(x=>x.Quantity*x.MarkPrice);var symbolExposure=equity>0?Math.Max(0,currentSymbol+newNotional-reducedNotional)/equity:1;
        Check(equity>0&&evidence.Account.AvailableBalance>0,"balance",L("RiskReview.Balance"));
        Check(symbolExposure<=limits.MaxSymbolExposure,"symbol_exposure",L("RiskReview.Exposure",symbolExposure,limits.MaxSymbolExposure));
        Check(exposure<=limits.MaxAccountExposure,"account_exposure",L("RiskReview.AccountExposure",exposure,limits.MaxAccountExposure));
        var riskAmount=intents.Sum(x=>x.Quantity*Math.Abs((x.ExpectedPrice>0?x.ExpectedPrice:decision.EntryPrice)-x.StopLoss));
        Check(equity>0&&riskAmount<=equity*limits.MaxRiskPerTrade,"trade_risk",L("RiskReview.TradeRisk",riskAmount,equity*limits.MaxRiskPerTrade));
        var approved=blocks.Count==0;var performanceDegraded=history.ConsecutiveLosses>0||history.DailyRealizedPnl<0;var level=!approved?"BLOCKED":performanceDegraded||exposure>.20m||riskAmount>equity*.0075m?"ELEVATED":"NORMAL";
        return new(){Approved=approved,RiskLevel=level,PlannedQuantity=intents.Sum(x=>x.Quantity),RiskAmount=riskAmount,ExposureAfter=portfolio is null?exposure:equity>0?portfolio.GrossExposure/equity:1,BlockingReasons=blocks,Checks=checks,Summary=approved?L("RiskReview.Approved",level,riskAmount,exposure):L("RiskReview.Blocked",string.Join("; ",blocks))};
        void Check(bool condition,string name,string failure){if(condition)checks.Add(name);else blocks.Add(failure);}
    }
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}

public static class PositionManagementDurableState
{
    public static string ProtectionAdjustmentKey(string adjustmentId)=>"position-protection:"+adjustmentId;
    public static string OwnershipRevocationKey(string openingClientOrderId)=>"position-ownership-revoked:"+openingClientOrderId;
    public static string OwnershipMissingCandidateKey(string openingClientOrderId)=>"position-ownership-missing-candidate:"+openingClientOrderId;
}

public sealed record ProtectionAdjustment(string Symbol,PositionSide Side,decimal StopLoss,decimal TakeProfit,string Reason,string? AdjustmentId=null);
public sealed record PositionManagementResult(IReadOnlyList<ExecutionIntent> Intents,IReadOnlyList<ProtectionAdjustment> ProtectionAdjustments,IReadOnlyList<string> Notes);

public sealed class PositionManagementSkill
{
    public async Task<PositionManagementResult> EvaluateAsync(
        IReadOnlyList<ManagedPosition> positions,
        IReadOnlyDictionary<string,MarketEvidence> markets,
        AgentSqliteStore db,
        CancellationToken ct,
        IReadOnlyList<ExecutionPositionLegV1> managedLegs,
        IReadOnlyDictionary<string,TradingRule>? tradingRules=null)
    {
        ArgumentNullException.ThrowIfNull(managedLegs);
        var intents=new List<ExecutionIntent>();
        var protections=new List<ProtectionAdjustment>();
        var notes=new List<string>();
        foreach(var position in positions)
        {
            if(!IsExactlyManaged(position,positions,managedLegs))continue;
            var opening=await db.GetLatestOpeningIntentAsync(position.Symbol,position.Side,ct);
            if(opening is null)continue;
            if(await db.HasStateAsync(PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId),ct))
            {
                notes.Add($"position-ownership-revoked:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                continue;
            }
            if(await db.HasStateAsync(PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId),ct))
            {
                notes.Add($"position-ownership-uncertain:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                continue;
            }

            var protectionId=ActionId("BE",opening);
            var protectionStateKey=PositionManagementDurableState.ProtectionAdjustmentKey(protectionId);
            var protectionState=await db.GetStateAsync(protectionStateKey,ct);
            var profitLockId=ActionId("LOCK2R",opening);
            var profitLockStateKey=PositionManagementDurableState.ProtectionAdjustmentKey(profitLockId);
            var profitLockState=await db.GetStateAsync(profitLockStateKey,ct);
            var unresolvedProtectionState=protectionState is "PENDING" or "FAILED"
                ?protectionState
                :profitLockState is "PENDING" or "FAILED"?profitLockState:null;
            if(unresolvedProtectionState is not null)
            {
                var actionId=ActionId("PROTFAIL",opening);
                if(await db.GetOrderIntentStatusAsync(actionId,ct) is null)
                    intents.Add(new(
                        position.Symbol,position.Side,position.Quantity,true,0,0,actionId,
                        LocalizationService.Current.T("Execution.ProtectionReplaceFailed",unresolvedProtectionState),
                        position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort,
                        ExpectedPrice:position.MarkPrice>0?position.MarkPrice:position.EntryPrice,
                        ReasonCode:PositionExitReasonCodes.ProtectionReplaceFailed));
                notes.Add($"protection-recovery-close:{position.Symbol}:{position.Side}:{opening.ClientOrderId}:{unresolvedProtectionState}");
                continue;
            }

            if(!markets.TryGetValue(position.Symbol,out var market))continue;

            if(DirectMarketStructureDecisionSkill.PositionInvalidated(position,market))
            {
                var actionId=ActionId("STRUCT",opening);
                if(await db.GetOrderIntentStatusAsync(actionId,ct) is null)
                    intents.Add(new(
                        position.Symbol,
                        position.Side,
                        position.Quantity,
                        true,
                        0,
                        0,
                        actionId,
                        L("Position.StructureInvalidated"),
                        position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort,
                        ExpectedPrice:market.Price,
                        ReasonCode:PositionExitReasonCodes.StructureInvalidated));
                var structure=MarketStructureIntelligence.Analyze(market);
                notes.Add($"direct-structure-invalidated:{position.Symbol}:{position.Side}:{structure.HigherTimeframeBias}:{structure.FifteenMinute.Event}");
                continue;
            }

            var risk=Math.Abs(position.EntryPrice-opening.StopLoss);
            if(risk<=0)continue;
            var favorable=(position.Side==PositionSide.Long?market.Price-position.EntryPrice:position.EntryPrice-market.Price)/risk;
            var liquidationUnsafe=false;
            decimal liquidationBoundary=0;
            if(position.EntryPrice>0&&position.LiquidationPrice>0)
            {
                var liquidationSpan=position.Side==PositionSide.Long
                    ?position.EntryPrice-position.LiquidationPrice
                    :position.LiquidationPrice-position.EntryPrice;
                if(liquidationSpan<=0)liquidationUnsafe=true;
                else
                {
                    liquidationBoundary=position.Side==PositionSide.Long
                        ?position.LiquidationPrice+liquidationSpan*.30m
                        :position.LiquidationPrice-liquidationSpan*.30m;
                    liquidationUnsafe=position.Side==PositionSide.Long
                        ?opening.StopLoss<liquidationBoundary
                        :opening.StopLoss>liquidationBoundary;
                }
            }
            if(liquidationUnsafe)
            {
                notes.Add($"liquidation-safety-compromised:{position.Symbol}:{position.Side}:stop={opening.StopLoss}:boundary={liquidationBoundary}");
                var actionId=ActionId("LIQ",opening);
                if(await db.GetOrderIntentStatusAsync(actionId,ct) is null)
                    intents.Add(new(
                        position.Symbol,position.Side,position.Quantity,true,0,0,actionId,
                        L("Position.LiquidationBuffer"),
                        position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort,
                        ExpectedPrice:market.Price,
                        ReasonCode:PositionExitReasonCodes.LiquidationBuffer));
                continue;
            }

            var partialId=ActionId("TP2",opening);
            var partialState=await db.GetOrderIntentStatusAsync(partialId,ct);
            if(favorable>=2&&partialState is null)
            {
                if(tradingRules is not null&&tradingRules.TryGetValue(position.Symbol,out var rule)&&
                   rule.StepSize>0&&rule.MinQuantity>0)
                {
                    var partialQuantity=rule.RoundQuantity(position.Quantity*.5m);
                    if(partialQuantity>=rule.MinQuantity&&partialQuantity>0&&partialQuantity<position.Quantity)
                    {
                        intents.Add(new(
                            position.Symbol,position.Side,partialQuantity,true,0,0,partialId,
                            L("Position.PartialTake"),
                            position.Side==PositionSide.Long?DecisionAction.ReduceLong:DecisionAction.ReduceShort,
                            ExpectedPrice:market.Price,
                            ReasonCode:PositionExitReasonCodes.PartialTakeProfit2R));
                        notes.Add($"partial-2r:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                        continue;
                    }
                    notes.Add($"partial-2r-quantity-unavailable:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                }
                else notes.Add($"partial-2r-rule-unavailable:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
            }

            if(favorable>=1&&protectionState is null)
            {
                if(tradingRules is null||!tradingRules.TryGetValue(position.Symbol,out var rule)||rule.TickSize<=0)
                {
                    notes.Add($"breakeven-rule-unavailable:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                    continue;
                }
                var openingEntry=opening.ExpectedPrice>0?opening.ExpectedPrice:position.EntryPrice;
                var breakevenAnchor=position.Side==PositionSide.Long
                    ?Math.Max(position.EntryPrice,openingEntry)
                    :Math.Min(position.EntryPrice,openingEntry);
                var rawStop=position.Side==PositionSide.Long?breakevenAnchor*1.0005m:breakevenAnchor*.9995m;
                var stop=RoundProtectiveStop(rawStop,rule.TickSize,position.Side);
                var validStop=stop>0&&opening.TakeProfit>0&&(position.Side==PositionSide.Long
                    ?stop>=breakevenAnchor&&stop<market.Price&&stop<opening.TakeProfit
                    :stop<=breakevenAnchor&&stop>market.Price&&stop>opening.TakeProfit);
                if(!validStop)
                {
                    notes.Add($"breakeven-price-unavailable:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                    continue;
                }
                protections.Add(new(position.Symbol,position.Side,stop,opening.TakeProfit,L("Position.Breakeven"),protectionId));
                notes.Add($"breakeven:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
            }

            var partialCompleted=partialState is "COMPLETED" or "COMPLETED_PARTIAL";
            if(favorable>=2&&partialCompleted&&protectionState=="COMPLETED"&&profitLockState is null)
            {
                if(tradingRules is null||!tradingRules.TryGetValue(position.Symbol,out var rule)||rule.TickSize<=0)
                {
                    notes.Add($"structure-profit-lock-rule-unavailable:{position.Symbol}:{position.Side}:{opening.ClientOrderId}");
                    continue;
                }
                var openingEntry=opening.ExpectedPrice>0?opening.ExpectedPrice:position.EntryPrice;
                var breakevenAnchor=position.Side==PositionSide.Long
                    ?Math.Max(position.EntryPrice,openingEntry)
                    :Math.Min(position.EntryPrice,openingEntry);
                var structureLevel=position.Side==PositionSide.Long?market.Support:market.Resistance;
                var structureBuffer=Math.Max(risk*.10m,market.Price*.0003m);
                var rawStop=position.Side==PositionSide.Long
                    ?structureLevel-structureBuffer
                    :structureLevel+structureBuffer;
                var stop=RoundProtectiveStop(rawStop,rule.TickSize,position.Side);
                var breakevenStop=RoundProtectiveStop(
                    position.Side==PositionSide.Long?breakevenAnchor*1.0005m:breakevenAnchor*.9995m,
                    rule.TickSize,
                    position.Side);
                var validStop=structureLevel>0&&stop>0&&breakevenStop>0&&opening.TakeProfit>0&&(position.Side==PositionSide.Long
                    ?stop>breakevenStop&&stop<market.Price&&stop<opening.TakeProfit
                    :stop<breakevenStop&&stop>market.Price&&stop>opening.TakeProfit);
                if(validStop)
                {
                    protections.Add(new(position.Symbol,position.Side,stop,opening.TakeProfit,L("Position.StructureProfitLock"),profitLockId));
                    notes.Add($"structure-profit-lock:{position.Symbol}:{position.Side}:{opening.ClientOrderId}:level={structureLevel}");
                }
                else notes.Add($"structure-profit-lock-waiting:{position.Symbol}:{position.Side}:{opening.ClientOrderId}:level={structureLevel}");
            }
        }
        return new(intents,protections,notes);
    }

    private static bool IsExactlyManaged(
        ManagedPosition position,IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExecutionPositionLegV1> managedLegs)
    {
        var exchangeMatches=positions.Count(x=>string.Equals(x.Symbol,position.Symbol,StringComparison.OrdinalIgnoreCase)&&x.Side==position.Side);
        if(exchangeMatches!=1)return false;
        var localMatches=managedLegs.Where(x=>string.Equals(x.Symbol,position.Symbol,StringComparison.OrdinalIgnoreCase)&&x.Side==position.Side).ToArray();
        if(localMatches.Length!=1||localMatches[0].Quantity<=0)return false;
        var tolerance=Math.Max(.00000001m,Math.Max(localMatches[0].Quantity,position.Quantity)*.000001m);
        return position.Quantity>0&&Math.Abs(localMatches[0].Quantity-position.Quantity)<=tolerance;
    }

    private static decimal RoundProtectiveStop(decimal value,decimal tickSize,PositionSide side)
    {
        if(value<=0||tickSize<=0)return 0;
        var units=value/tickSize;
        return (side==PositionSide.Long?Math.Ceiling(units):Math.Floor(units))*tickSize;
    }

    private static string ActionId(string tag,ExecutionIntent opening)
    {
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{opening.ClientOrderId}\u001f{tag}"))).ToLowerInvariant();
        return $"WPE-PM-{tag}-{hash[..20]}";
    }
    private static string Id(string tag){var raw=$"WPE-PM-{tag}-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
    private static string L(string key)=>LocalizationService.Current.T(key);
}
