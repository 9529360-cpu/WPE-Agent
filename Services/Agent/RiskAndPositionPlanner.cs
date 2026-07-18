using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class RiskAndPositionPlanner
{
    public (IReadOnlyList<ExecutionIntent> Intents,string Result) Plan(DecisionPlan d,EvidencePack e,TradingRule rule,RiskLimits limits,decimal dayHigh,bool safeToIncreaseRisk=true,string? safetyReason=null,PositionSide? lockedSide=null)
    {
        var riskIncreasing=d.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort or DecisionAction.Lock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
        if(!safeToIncreaseRisk&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),safetyReason??L("Risk.Recovery"));
        if(e.Completeness<70&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.Completeness"));if(dayHigh>0&&(dayHigh-e.Account.Equity)/dayHigh>=limits.DailyDrawdownLimit&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.Drawdown"));
        if(d.Instrument is not("BTCUSDT" or "ETHUSDT"))return(Array.Empty<ExecutionIntent>(),L("Agent.InvalidInstrument"));if(!e.Markets.TryGetValue(d.Instrument,out var m))return(Array.Empty<ExecutionIntent>(),L("Risk.MarketMissing"));
        var symbols=e.Positions.Select(x=>x.Symbol).Distinct().ToArray();if(symbols.Length>1&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.MultiAsset"));if(symbols.Length==1&&symbols[0]!=d.Instrument&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),L("Risk.OtherAsset"));
        if(d.Action==DecisionAction.Hold)return(Array.Empty<ExecutionIntent>(),"HOLD");var positions=e.Positions.Where(x=>x.Symbol==d.Instrument).ToArray();var effectiveLeverage=Math.Min(limits.Leverage,rule.MaxLeverage);

        string NewId(string tag){var raw=$"WPE-{DateTime.UtcNow:yyMMddHHmmss}-{tag}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
        string? ValidateProtection(PositionSide side)
        {
            if(d.StopLossPrice<=0||d.TakeProfitPrice<=0)return L("Risk.ProtectionMissing");if(side==PositionSide.Long&&(d.StopLossPrice>=m.Price||d.TakeProfitPrice<=m.Price))return L("Risk.LongProtection");if(side==PositionSide.Short&&(d.StopLossPrice<=m.Price||d.TakeProfitPrice>=m.Price))return L("Risk.ShortProtection");
            var estimatedLiquidation=side==PositionSide.Long?m.Price*(1-1m/effectiveLeverage):m.Price*(1+1m/effectiveLeverage);var boundary=side==PositionSide.Long?estimatedLiquidation+(m.Price-estimatedLiquidation)*.30m:estimatedLiquidation-(estimatedLiquidation-m.Price)*.30m;
            if(side==PositionSide.Long&&d.StopLossPrice<boundary)return L("Risk.LongBuffer");if(side==PositionSide.Short&&d.StopLossPrice>boundary)return L("Risk.ShortBuffer");return null;
        }
        (ExecutionIntent? Intent,string? Error) Increase(PositionSide side,DecisionAction action,PositionSide? closingSide=null)
        {
            var protectionError=ValidateProtection(side);if(protectionError is not null)return(null,protectionError);var current=positions.Where(x=>x.Side==side).Sum(x=>x.Quantity);var currentMargin=current*m.Price/effectiveLeverage;var currentRatio=e.Account.Equity>0?currentMargin/e.Account.Equity:0;
            var currentTier=Array.FindLastIndex(limits.MarginTiers,x=>x<=currentRatio+.0001m)+1;var requestedTier=Math.Clamp(d.TargetTier,1,3);var tier=Math.Min(requestedTier,currentTier+1);
            var targetMargin=e.Account.Equity*limits.MarginTiers[tier-1];var otherMargin=e.Positions.Where(x=>x.Symbol!=d.Instrument||(x.Side!=side&&x.Side!=closingSide)).Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage));
            if(e.Account.Equity<=0||otherMargin+targetMargin>e.Account.Equity*limits.MaxMargin)return(null,L("Risk.MarginLimit"));
            var targetQty=targetMargin*effectiveLeverage/m.Price;var qty=rule.RoundQuantity(Math.Max(0,targetQty-current));if(qty<rule.MinQuantity||qty*m.Price<rule.MinNotional)return(null,L("Risk.Quantity"));
            return(new(d.Instrument,side,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId(action.ToString()[..Math.Min(3,action.ToString().Length)]),d.Reason,action),null);
        }

        if(d.Action==DecisionAction.Lock)
        {
            if(positions.Length!=1||lockedSide is not null)return(Array.Empty<ExecutionIntent>(),L("Risk.LockConstraint"));var primary=positions[0];var lockSide=primary.Side==PositionSide.Long?PositionSide.Short:PositionSide.Long;var protectionError=ValidateProtection(lockSide);if(protectionError is not null)return(Array.Empty<ExecutionIntent>(),protectionError);
            var qty=rule.RoundQuantity(primary.Quantity);var totalMargin=positions.Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage))+qty*m.Price/effectiveLeverage;if(e.Account.Equity<=0||totalMargin>e.Account.Equity*limits.MaxMargin)return(Array.Empty<ExecutionIntent>(),L("Risk.LockMargin"));if(qty<rule.MinQuantity||qty*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),L("Risk.LockQuantity"));
            return([new(d.Instrument,lockSide,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId("LCK"),d.Reason,d.Action)],L("Risk.LockAccepted"));
        }
        if(d.Action==DecisionAction.Unlock)
        {
            if(lockedSide is null)return(Array.Empty<ExecutionIntent>(),L("Risk.UnlockUnknown"));var qty=rule.RoundQuantity(positions.Where(x=>x.Side==lockedSide).Sum(x=>x.Quantity));if(qty<rule.MinQuantity)return(Array.Empty<ExecutionIntent>(),L("Risk.UnlockMissing"));
            return([new(d.Instrument,lockedSide.Value,qty,true,0,0,NewId("ULK"),d.Reason,d.Action)],L("Risk.UnlockAccepted"));
        }
        if(d.Action is DecisionAction.ReverseToLong or DecisionAction.ReverseToShort)
        {
            if(positions.Select(x=>x.Side).Distinct().Count()>1)return(Array.Empty<ExecutionIntent>(),L("Risk.ReverseLocked"));var target=d.Action==DecisionAction.ReverseToLong?PositionSide.Long:PositionSide.Short;var old=target==PositionSide.Long?PositionSide.Short:PositionSide.Long;var oldQty=rule.RoundQuantity(positions.Where(x=>x.Side==old).Sum(x=>x.Quantity));if(oldQty<rule.MinQuantity)return(Array.Empty<ExecutionIntent>(),L("Risk.ReverseMissing"));var opening=Increase(target,d.Action,old);if(opening.Error is not null)return(Array.Empty<ExecutionIntent>(),opening.Error);
            return([new(d.Instrument,old,oldQty,true,0,0,NewId("REV-C"),d.Reason,d.Action),opening.Intent!],L("Risk.ReverseAccepted"));
        }

        var side=d.Action is DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReduceShort or DecisionAction.CloseShort?PositionSide.Short:PositionSide.Long;
        if(d.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort){var opening=Increase(side,d.Action);return opening.Error is null?([opening.Intent!],L("Risk.Accepted")): (Array.Empty<ExecutionIntent>(),opening.Error);}
        var current=positions.Where(x=>x.Side==side).Sum(x=>x.Quantity);var close=d.Action is DecisionAction.CloseLong or DecisionAction.CloseShort;var quantity=rule.RoundQuantity(close?current:current*.5m);if(quantity<rule.MinQuantity||quantity*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),L("Risk.CloseQuantity"));
        return([new(d.Instrument,side,quantity,true,0,0,NewId(close?"CLS":"RED"),d.Reason,d.Action)],L(close?"Risk.CloseAccepted":"Risk.ReduceAccepted"));
    }
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}
