using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed record SessionRiskGate(bool AllowsRiskIncrease,IReadOnlyList<string> Reasons)
{
    public static SessionRiskGate Evaluate(RiskHistorySnapshot history,decimal equity,decimal dayHigh,RiskLimits limits)
    {
        var reasons=new List<string>();
        if(equity>0&&history.DailyRealizedPnl<0&&limits.MaxDailyLoss>0)
        {
            var dailyLossRatio=(double)(-history.DailyRealizedPnl/equity);
            if(dailyLossRatio>=(double)limits.MaxDailyLoss)
                reasons.Add($"daily-loss-limit:{dailyLossRatio:P2}");
        }
        if(dayHigh>0&&equity>0&&limits.DailyDrawdownLimit>0)
        {
            var drawdown=(double)Math.Max(0,(dayHigh-equity)/dayHigh);
            if(drawdown>=(double)limits.DailyDrawdownLimit)
                reasons.Add($"intraday-drawdown-limit:{drawdown:P2}");
        }
        return new(reasons.Count==0,reasons);
    }
}

public sealed class RiskAndPositionPlanner
{
    public ModelOffStrategyRiskContractV1 EvaluateModelOffIntent(ModelOffStrategyIntentRequestV1 request) =>
        ModelOffStrategyRiskEvaluatorV1.Evaluate(request);

    public static int SelectEffectiveLeverage(DecisionPlan decision,TradingRule rule,RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(limits);
        var providerMax=Math.Max(1,rule.MaxLeverage);
        var requested=Math.Clamp(limits.Leverage,1,providerMax);
        if(!DeterministicPlanSkill.IsRiskIncreasing(decision.Action)||decision.EntryPrice<=0||decision.StopLossPrice<=0)return requested;
        var stopFraction=Math.Abs(decision.EntryPrice-decision.StopLossPrice)/decision.EntryPrice;
        if(stopFraction<=0)return 1;
        // A futures liquidation price is materially closer than entry*(1-1/leverage)
        // because maintenance margin consumes part of the nominal leverage span. Keep the
        // planned stop inside the same 30% liquidation reserve used by PositionManagementSkill,
        // while applying a conservative maintenance-margin allowance before the position exists.
        const decimal usableLiquidationSpan=.70m;
        var maintenanceAllowance=providerMax>=100?.006m:providerMax>=50?.010m:providerMax>=25?.020m:.030m;
        var requiredNominalSpan=stopFraction/usableLiquidationSpan+maintenanceAllowance;
        var structuralMax=(int)Math.Floor(1m/Math.Max(requiredNominalSpan,.00000001m));
        return Math.Clamp(Math.Min(requested,structuralMax),1,providerMax);
    }

    public static decimal EstimateIsolatedLiquidationPrice(PositionSide side,decimal entryPrice,decimal quantity,int leverage,LeverageBracket bracket)
    {
        if(entryPrice<=0||quantity<=0||leverage<=0)throw new ArgumentOutOfRangeException(nameof(quantity));
        ArgumentNullException.ThrowIfNull(bracket);
        var initialMargin=entryPrice*quantity/leverage;
        return side==PositionSide.Long
            ?(entryPrice*quantity-initialMargin-bracket.MaintenanceAmount)/(quantity*Math.Max(.00000001m,1m-bracket.MaintenanceMarginRate))
            :(entryPrice*quantity+initialMargin+bracket.MaintenanceAmount)/(quantity*(1m+bracket.MaintenanceMarginRate));
    }

    public static bool TryEstimateIsolatedLiquidationPrice(PositionSide side,decimal entryPrice,decimal quantity,int leverage,TradingRule rule,out decimal liquidationPrice,out LeverageBracket? resolvedBracket)
    {
        liquidationPrice=0;resolvedBracket=null;
        if(entryPrice<=0||quantity<=0||leverage<=0||rule is null||!rule.HasVerifiedLeverageBrackets)return false;
        var bracket=rule.BracketForNotional(entryPrice*quantity);
        if(bracket is null)return false;
        for(var i=0;i<8;i++)
        {
            if(leverage>bracket.InitialLeverage)return false;
            var candidate=EstimateIsolatedLiquidationPrice(side,entryPrice,quantity,leverage,bracket);
            if(candidate<=0)return false;
            var next=rule.BracketForNotional(candidate*quantity);
            if(next is null)return false;
            if(next.Bracket==bracket.Bracket)
            {
                liquidationPrice=candidate;resolvedBracket=bracket;return true;
            }
            bracket=next;
        }
        return false;
    }

    public static bool StopRespectsLiquidationBuffer(PositionSide side,decimal entryPrice,decimal stopPrice,decimal liquidationPrice,decimal bufferFraction)
    {
        if(entryPrice<=0||stopPrice<=0||liquidationPrice<=0)return false;
        var buffer=Math.Clamp(bufferFraction,0m,.99m);
        if(side==PositionSide.Long)
        {
            if(liquidationPrice>=entryPrice)return false;
            var boundary=liquidationPrice+(entryPrice-liquidationPrice)*buffer;
            return stopPrice>=boundary;
        }
        if(liquidationPrice<=entryPrice)return false;
        var shortBoundary=liquidationPrice-(liquidationPrice-entryPrice)*buffer;
        return stopPrice<=shortBoundary;
    }

    private sealed record SizingCandidate(decimal IncreaseQuantity,decimal TotalQuantity,int Leverage,decimal AverageEntry,decimal LiquidationPrice,decimal EstimatedRoundTripFee,decimal EstimatedExecutionCost,decimal RiskAtStop,decimal MarginUsed,LeverageBracket Bracket);

    public (IReadOnlyList<ExecutionIntent> Intents,string Result) Plan(
        DecisionPlan d,EvidencePack e,TradingRule rule,RiskLimits limits,
        bool safeToIncreaseRisk=true,string? safetyReason=null,PositionSide? lockedSide=null,decimal existingPortfolioStopRisk=0)
    {
        var riskIncreasing=DeterministicPlanSkill.IsRiskIncreasing(d.Action);
        if(!safeToIncreaseRisk&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),safetyReason??L("Risk.Recovery"));
        if(e.Completeness<70&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.Completeness"));
        if(!e.Markets.TryGetValue(d.Instrument,out var m))return(Array.Empty<ExecutionIntent>(),L("Risk.MarketMissing"));
        if(d.Action==DecisionAction.Hold)return(Array.Empty<ExecutionIntent>(),"HOLD");
        if(riskIncreasing)
        {
            if(limits.TestnetHighOpportunityMode&&!rule.HasVerifiedLeverageBrackets)return(Array.Empty<ExecutionIntent>(),"risk.exchange-leverage-tiers-unavailable");
            if(limits.TestnetHighOpportunityMode&&!rule.FeeRateVerified)return(Array.Empty<ExecutionIntent>(),"risk.exchange-fee-rate-unavailable");
            var openSymbols=e.Positions.Where(x=>x.Quantity>0).Select(x=>x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var opensNewSymbol=!openSymbols.Contains(d.Instrument,StringComparer.OrdinalIgnoreCase);
            if(opensNewSymbol&&openSymbols.Length>=Math.Max(1,limits.MaxConcurrentPositions))
                return(Array.Empty<ExecutionIntent>(),$"risk.max-concurrent-positions:{limits.MaxConcurrentPositions}");
        }

        var positions=e.Positions.Where(x=>x.Symbol==d.Instrument).ToArray();
        var entry=d.EntryPrice>0?d.EntryPrice:m.Price;

        string NewId(string tag){var raw=$"WPE-{DateTime.UtcNow:yyMMddHHmmss}-{tag}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
        string? ValidateProtection(PositionSide side)
        {
            if(d.StopLossPrice<=0||d.TakeProfitPrice<=0)return L("Risk.ProtectionMissing");
            if(side==PositionSide.Long&&(d.StopLossPrice>=entry||d.TakeProfitPrice<=entry))return L("Risk.LongProtection");
            if(side==PositionSide.Short&&(d.StopLossPrice<=entry||d.TakeProfitPrice>=entry))return L("Risk.ShortProtection");
            var rr=Math.Abs(d.TakeProfitPrice-entry)/Math.Max(.00000001m,Math.Abs(entry-d.StopLossPrice));
            const decimal rrComparisonTolerance=.000000001m;
            if(rr+rrComparisonTolerance<(decimal)limits.MinimumRiskReward)return L("Risk.RiskReward",rr,limits.MinimumRiskReward);
            return null;
        }
        (ExecutionIntent? Intent,string? Error) Increase(PositionSide side,DecisionAction action,PositionSide? closingSide=null)
        {
            var protectionError=ValidateProtection(side);if(protectionError is not null)return(null,protectionError);
            if(e.Account.Equity<=0)return(null,L("Risk.MarginLimit",limits.MaxMargin));

            var sidePositions=positions.Where(x=>x.Side==side).ToArray();
            var current=sidePositions.Sum(x=>x.Quantity);
            var currentMargin=sidePositions.Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage));
            var currentRatio=currentMargin/e.Account.Equity;
            var tiers=limits.MarginTiers.Length==0?[.10m]:limits.MarginTiers;
            var currentTier=Array.FindLastIndex(tiers,x=>x<=currentRatio+.0001m)+1;
            var requestedTier=Math.Clamp(d.TargetTier,1,tiers.Length);
            var tier=Math.Min(requestedTier,Math.Min(tiers.Length,currentTier+1));
            var tierMargin=e.Account.Equity*tiers[tier-1];
            var entryMarginCap=e.Account.Equity*Math.Clamp(limits.MaxInitialMarginPerTrade,.005m,.10m);
            var targetMargin=Math.Min(tierMargin,entryMarginCap);
            if(e.Account.AvailableBalance>=0)targetMargin=Math.Min(targetMargin,e.Account.AvailableBalance);
            var otherMargin=e.Positions.Where(x=>x.Symbol!=d.Instrument||(x.Side!=side&&x.Side!=closingSide)).Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage));
            var portfolioMarginRoom=Math.Max(0,e.Account.Equity*limits.MaxMargin-otherMargin);
            targetMargin=Math.Min(targetMargin,portfolioMarginRoom);
            if(targetMargin<=0)return(null,L("Risk.MarginLimit",limits.MaxMargin));

            var roundedStop=rule.RoundPrice(d.StopLossPrice);
            if(roundedStop<=0||(side==PositionSide.Long&&roundedStop>=entry)||(side==PositionSide.Short&&roundedStop<=entry))return(null,L(side==PositionSide.Long?"Risk.LongProtection":"Risk.ShortProtection"));
            var stopDistance=Math.Abs(entry-roundedStop);
            var feeRate=rule.FeeRateVerified?Math.Max(0,rule.TakerFeeRate):0m;
            var spreadBps=double.IsFinite(m.Quality.SpreadBps)?Math.Max(0m,(decimal)m.Quality.SpreadBps):0m;
            var slippageBps=double.IsFinite(limits.MaximumSlippageBps)?Math.Max(0m,(decimal)limits.MaximumSlippageBps):0m;
            var halfSpreadFraction=spreadBps/20_000m;
            var slippageFraction=slippageBps/10_000m;
            var entryExecutionCostPerUnit=entry*(halfSpreadFraction+slippageFraction);
            var exitExecutionCostPerUnit=roundedStop*(halfSpreadFraction+slippageFraction);
            var requestedRisk=d.RiskBudgetFraction>0?Math.Min(d.RiskBudgetFraction,limits.MaxRiskPerTrade):limits.MaxRiskPerTrade;
            var perTradeRiskBudget=e.Account.Equity*requestedRisk;
            var portfolioRiskBudget=e.Account.Equity*Math.Clamp(limits.MaxPortfolioStopRisk,.01m,.05m);
            var portfolioRiskRoom=Math.Max(0,portfolioRiskBudget-Math.Max(0,existingPortfolioStopRisk));
            var riskBudget=Math.Min(perTradeRiskBudget,portfolioRiskRoom);
            if(riskBudget<=0)return(null,"risk.portfolio-stop-budget-exhausted");
            var riskPerNewUnit=stopDistance+(entry+roundedStop)*feeRate+entryExecutionCostPerUnit+exitExecutionCostPerUnit;
            var riskQuantity=riskBudget/Math.Max(riskPerNewUnit,.00000001m);
            var exposureQuantity=e.Account.Equity*limits.MaxSymbolExposure/Math.Max(entry,.00000001m);
            var accountExposure=e.Positions.Where(x=>x.Symbol!=d.Instrument).Sum(x=>x.Quantity*x.MarkPrice);
            var accountRoom=Math.Max(0,e.Account.Equity*limits.MaxAccountExposure-accountExposure);
            var accountQuantity=accountRoom/Math.Max(entry,.00000001m);
            var targetTotalQty=new[]{riskQuantity,exposureQuantity,accountQuantity}.Min();
            var maxIncrease=rule.RoundQuantity(Math.Max(0,targetTotalQty-current));
            if(maxIncrease<rule.MinQuantity||maxIncrease*entry<rule.MinNotional)return(null,L("Risk.Quantity"));

            SizingCandidate? selected=null;
            if(rule.HasVerifiedLeverageBrackets)
            {
                var maximumLeverage=Math.Clamp(Math.Min(limits.Leverage,rule.MaxLeverage),1,Math.Max(1,rule.MaxLeverage));
                for(var leverage=1;leverage<=maximumLeverage;leverage++)
                {
                    var marginTotalQty=targetMargin*leverage/Math.Max(entry,.00000001m);
                    var candidateTotal=Math.Min(targetTotalQty,marginTotalQty);
                    var increase=rule.RoundQuantity(Math.Max(0,candidateTotal-current));
                    if(increase<rule.MinQuantity||increase*entry<rule.MinNotional)continue;
                    var total=current+increase;
                    var currentEntryValue=sidePositions.Sum(x=>x.Quantity*x.EntryPrice);
                    var averageEntry=total>0?(currentEntryValue+increase*entry)/total:entry;
                    var notional=total*entry;
                    if(!TryEstimateIsolatedLiquidationPrice(side,averageEntry,total,leverage,rule,out var liquidation,out var bracket)||bracket is null)continue;
                    if(!StopRespectsLiquidationBuffer(side,averageEntry,roundedStop,liquidation,limits.LiquidationBufferFraction))continue;
                    var grossStopRisk=sidePositions.Sum(x=>x.Quantity*Math.Abs(x.EntryPrice-roundedStop))+increase*stopDistance;
                    var newEntryFee=increase*entry*feeRate;
                    var futureExitFee=total*roundedStop*feeRate;
                    var newEntryExecutionCost=increase*entryExecutionCostPerUnit;
                    var futureExitExecutionCost=total*exitExecutionCostPerUnit;
                    var riskAtStop=grossStopRisk+newEntryFee+futureExitFee+newEntryExecutionCost+futureExitExecutionCost;
                    if(riskAtStop>riskBudget+.00000001m)continue;
                    var marginUsed=notional/leverage;
                    var estimatedRoundTripFee=increase*(entry+roundedStop)*feeRate;
                    var estimatedExecutionCost=increase*(entryExecutionCostPerUnit+exitExecutionCostPerUnit);
                    var candidate=new SizingCandidate(increase,total,leverage,averageEntry,liquidation,estimatedRoundTripFee,estimatedExecutionCost,riskAtStop,marginUsed,bracket);
                    if(selected is null||candidate.IncreaseQuantity>selected.IncreaseQuantity)selected=candidate;
                }
            }
            else
            {
                // Generic-provider fallback. Binance Testnet high-opportunity mode never reaches this path:
                // it fails closed above unless live exchange leverage tiers and fee rates were loaded.
                var leverage=SelectEffectiveLeverage(d,rule,limits);
                var increase=rule.RoundQuantity(Math.Min(maxIncrease,targetMargin*leverage/Math.Max(entry,.00000001m)));
                if(increase>=rule.MinQuantity&&increase*entry>=rule.MinNotional)
                {
                    var total=current+increase;
                    var averageEntry=total>0?(sidePositions.Sum(x=>x.Quantity*x.EntryPrice)+increase*entry)/total:entry;
                    var fallbackBracket=new LeverageBracket(0,0,decimal.MaxValue,leverage,0,0);
                    var liquidation=side==PositionSide.Long?averageEntry*(1m-1m/leverage):averageEntry*(1m+1m/leverage);
                    selected=new(increase,total,leverage,averageEntry,liquidation,increase*(entry+roundedStop)*feeRate,increase*(entryExecutionCostPerUnit+exitExecutionCostPerUnit),increase*riskPerNewUnit,total*entry/leverage,fallbackBracket);
                }
            }

            if(selected is null)return(null,"risk.no-safe-leverage-size");
            var limit=d.OrderType==ExecutionOrderType.Limit?(side==PositionSide.Long?m.Quality.BestAsk:m.Quality.BestBid):0;
            if(limit<=0)limit=entry;
            return(new(d.Instrument,side,selected.IncreaseQuantity,false,roundedStop,rule.RoundPrice(d.TakeProfitPrice),NewId(action.ToString()[..Math.Min(3,action.ToString().Length)]),d.Reason,action,d.OrderType,rule.RoundPrice(limit),entry,null,selected.Leverage,selected.LiquidationPrice,selected.EstimatedRoundTripFee,selected.EstimatedExecutionCost),null);
        }

        if(d.Action==DecisionAction.Lock)
        {
            if(positions.Length!=1||lockedSide is not null)return(Array.Empty<ExecutionIntent>(),L("Risk.LockConstraint"));
            var primary=positions[0];var lockSide=primary.Side==PositionSide.Long?PositionSide.Short:PositionSide.Long;
            var protectionError=ValidateProtection(lockSide);if(protectionError is not null)return(Array.Empty<ExecutionIntent>(),protectionError);
            var qty=rule.RoundQuantity(primary.Quantity);var lockLeverage=Math.Clamp((int)Math.Round(primary.Leverage),1,Math.Max(1,rule.MaxLeverage));var totalMargin=positions.Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage))+qty*m.Price/lockLeverage;
            if(e.Account.Equity<=0||totalMargin>e.Account.Equity*limits.MaxMargin)return(Array.Empty<ExecutionIntent>(),L("Risk.LockMargin",limits.MaxMargin));
            if(qty<rule.MinQuantity||qty*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),L("Risk.LockQuantity"));
            return([new(d.Instrument,lockSide,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId("LCK"),d.Reason,d.Action,d.OrderType,rule.RoundPrice(entry),entry,null,lockLeverage)],L("Risk.LockAccepted"));
        }
        if(d.Action==DecisionAction.Unlock)
        {
            if(lockedSide is null)return(Array.Empty<ExecutionIntent>(),L("Risk.UnlockUnknown"));
            var qty=rule.RoundQuantity(positions.Where(x=>x.Side==lockedSide).Sum(x=>x.Quantity));if(qty<rule.MinQuantity)return(Array.Empty<ExecutionIntent>(),L("Risk.UnlockMissing"));
            return([new(d.Instrument,lockedSide.Value,qty,true,0,0,NewId("ULK"),d.Reason,d.Action,ExpectedPrice:m.Price)],L("Risk.UnlockAccepted"));
        }
        if(d.Action is DecisionAction.ReverseToLong or DecisionAction.ReverseToShort)
        {
            if(positions.Select(x=>x.Side).Distinct().Count()>1)return(Array.Empty<ExecutionIntent>(),L("Risk.ReverseLocked"));
            var target=d.Action==DecisionAction.ReverseToLong?PositionSide.Long:PositionSide.Short;var old=target==PositionSide.Long?PositionSide.Short:PositionSide.Long;
            var oldQty=rule.RoundQuantity(positions.Where(x=>x.Side==old).Sum(x=>x.Quantity));if(oldQty<rule.MinQuantity)return(Array.Empty<ExecutionIntent>(),L("Risk.ReverseMissing"));
            var opening=Increase(target,d.Action,old);if(opening.Error is not null)return(Array.Empty<ExecutionIntent>(),opening.Error);
            return([new(d.Instrument,old,oldQty,true,0,0,NewId("REV-C"),d.Reason,d.Action,ExpectedPrice:m.Price),opening.Intent!],L("Risk.ReverseAccepted"));
        }

        var side=d.Action is DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReduceShort or DecisionAction.CloseShort?PositionSide.Short:PositionSide.Long;
        if(d.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort){var opening=Increase(side,d.Action);return opening.Error is null?([opening.Intent!],L("Risk.Accepted")):(Array.Empty<ExecutionIntent>(),opening.Error);}
        var current=positions.Where(x=>x.Side==side).Sum(x=>x.Quantity);var close=d.Action is DecisionAction.CloseLong or DecisionAction.CloseShort;
        var quantity=rule.RoundQuantity(close?current:current*.5m);if(quantity<rule.MinQuantity||quantity*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),L("Risk.CloseQuantity"));
        return([new(d.Instrument,side,quantity,true,0,0,NewId(close?"CLS":"RED"),d.Reason,d.Action,ExpectedPrice:m.Price)],L(close?"Risk.CloseAccepted":"Risk.ReduceAccepted"));
    }
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}
