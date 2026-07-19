using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class RiskAndPositionPlanner
{
    public (IReadOnlyList<ExecutionIntent> Intents,string Result) Plan(
        DecisionPlan d,EvidencePack e,TradingRule rule,RiskLimits limits,decimal dayHigh,
        bool safeToIncreaseRisk=true,string? safetyReason=null,PositionSide? lockedSide=null)
    {
        var riskIncreasing=DeterministicPlanSkill.IsRiskIncreasing(d.Action);
        if(!safeToIncreaseRisk&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),safetyReason??L("Risk.Recovery"));
        if(e.Completeness<70&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.Completeness"));
        if(dayHigh>0&&(dayHigh-e.Account.Equity)/dayHigh>=limits.DailyDrawdownLimit&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.Drawdown"));
        if(!e.Markets.TryGetValue(d.Instrument,out var m))return(Array.Empty<ExecutionIntent>(),L("Risk.MarketMissing"));
        if(d.Action==DecisionAction.Hold)return(Array.Empty<ExecutionIntent>(),"HOLD");

        var symbols=e.Positions.Select(x=>x.Symbol).Distinct().ToArray();
        if(symbols.Length>1&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.MultiAsset"));
        if(symbols.Length==1&&symbols[0]!=d.Instrument&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.OtherAsset"));
        var positions=e.Positions.Where(x=>x.Symbol==d.Instrument).ToArray();
        var effectiveLeverage=Math.Max(1,Math.Min(limits.Leverage,rule.MaxLeverage));
        var entry=d.EntryPrice>0?d.EntryPrice:m.Price;

        string NewId(string tag){var raw=$"WPE-{DateTime.UtcNow:yyMMddHHmmss}-{tag}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
        string? ValidateProtection(PositionSide side)
        {
            if(d.StopLossPrice<=0||d.TakeProfitPrice<=0)return L("Risk.ProtectionMissing");
            if(side==PositionSide.Long&&(d.StopLossPrice>=entry||d.TakeProfitPrice<=entry))return L("Risk.LongProtection");
            if(side==PositionSide.Short&&(d.StopLossPrice<=entry||d.TakeProfitPrice>=entry))return L("Risk.ShortProtection");
            var rr=Math.Abs(d.TakeProfitPrice-entry)/Math.Max(.00000001m,Math.Abs(entry-d.StopLossPrice));
            if((double)rr<limits.MinimumRiskReward)return L("Risk.RiskReward",rr,limits.MinimumRiskReward);
            var estimatedLiquidation=side==PositionSide.Long?entry*(1-1m/effectiveLeverage):entry*(1+1m/effectiveLeverage);
            var boundary=side==PositionSide.Long?estimatedLiquidation+(entry-estimatedLiquidation)*.30m:estimatedLiquidation-(estimatedLiquidation-entry)*.30m;
            if(side==PositionSide.Long&&d.StopLossPrice<boundary)return L("Risk.LongBuffer");
            if(side==PositionSide.Short&&d.StopLossPrice>boundary)return L("Risk.ShortBuffer");
            return null;
        }
        (ExecutionIntent? Intent,string? Error) Increase(PositionSide side,DecisionAction action,PositionSide? closingSide=null)
        {
            var protectionError=ValidateProtection(side);if(protectionError is not null)return(null,protectionError);
            if(e.Account.Equity<=0)return(null,L("Risk.MarginLimit",limits.MaxMargin));
            var current=positions.Where(x=>x.Side==side).Sum(x=>x.Quantity);
            var currentMargin=current*m.Price/effectiveLeverage;
            var currentRatio=currentMargin/e.Account.Equity;
            var tiers=limits.MarginTiers.Length==0?[.10m]:limits.MarginTiers;
            var currentTier=Array.FindLastIndex(tiers,x=>x<=currentRatio+.0001m)+1;
            var requestedTier=Math.Clamp(d.TargetTier,1,tiers.Length);
            var tier=Math.Min(requestedTier,Math.Min(tiers.Length,currentTier+1));
            var targetMargin=e.Account.Equity*tiers[tier-1];
            var otherMargin=e.Positions.Where(x=>x.Symbol!=d.Instrument||(x.Side!=side&&x.Side!=closingSide)).Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage));
            if(otherMargin+targetMargin>e.Account.Equity*limits.MaxMargin)return(null,L("Risk.MarginLimit",limits.MaxMargin));

            var stopDistance=Math.Abs(entry-d.StopLossPrice);
            var riskQuantity=e.Account.Equity*limits.MaxRiskPerTrade/Math.Max(stopDistance,.00000001m);
            var exposureQuantity=e.Account.Equity*limits.MaxSymbolExposure/Math.Max(entry,.00000001m);
            var accountExposure=e.Positions.Where(x=>x.Symbol!=d.Instrument).Sum(x=>x.Quantity*x.MarkPrice);
            var accountRoom=Math.Max(0,e.Account.Equity*limits.MaxAccountExposure-accountExposure);
            var accountQuantity=accountRoom/Math.Max(entry,.00000001m);
            var tierQuantity=targetMargin*effectiveLeverage/Math.Max(entry,.00000001m);
            var targetQty=new[]{riskQuantity,exposureQuantity,accountQuantity,tierQuantity}.Min();
            var qty=rule.RoundQuantity(Math.Max(0,targetQty-current));
            if(qty<rule.MinQuantity||qty*entry<rule.MinNotional)return(null,L("Risk.Quantity"));
            var limit=d.OrderType==ExecutionOrderType.Limit?(side==PositionSide.Long?m.Quality.BestAsk:m.Quality.BestBid):0;
            if(limit<=0)limit=entry;
            return(new(d.Instrument,side,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId(action.ToString()[..Math.Min(3,action.ToString().Length)]),d.Reason,action,d.OrderType,rule.RoundPrice(limit),entry),null);
        }

        if(d.Action==DecisionAction.Lock)
        {
            if(positions.Length!=1||lockedSide is not null)return(Array.Empty<ExecutionIntent>(),L("Risk.LockConstraint"));
            var primary=positions[0];var lockSide=primary.Side==PositionSide.Long?PositionSide.Short:PositionSide.Long;
            var protectionError=ValidateProtection(lockSide);if(protectionError is not null)return(Array.Empty<ExecutionIntent>(),protectionError);
            var qty=rule.RoundQuantity(primary.Quantity);var totalMargin=positions.Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage))+qty*m.Price/effectiveLeverage;
            if(e.Account.Equity<=0||totalMargin>e.Account.Equity*limits.MaxMargin)return(Array.Empty<ExecutionIntent>(),L("Risk.LockMargin",limits.MaxMargin));
            if(qty<rule.MinQuantity||qty*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),L("Risk.LockQuantity"));
            return([new(d.Instrument,lockSide,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId("LCK"),d.Reason,d.Action,d.OrderType,rule.RoundPrice(entry),entry)],L("Risk.LockAccepted"));
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
