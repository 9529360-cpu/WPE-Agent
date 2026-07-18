namespace 币安量化机器人.Services.Agent;

public sealed class RiskAndPositionPlanner
{
    public (IReadOnlyList<ExecutionIntent> Intents,string Result) Plan(DecisionPlan d,EvidencePack e,TradingRule rule,RiskLimits limits,decimal dayHigh,bool safeToIncreaseRisk=true,string? safetyReason=null,PositionSide? lockedSide=null)
    {
        var riskIncreasing=d.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort or DecisionAction.Lock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
        if(!safeToIncreaseRisk&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),safetyReason??"账户恢复或保护状态未确认，禁止增加风险");
        if(e.Completeness<70&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),"证据完整度低于70，禁止增加风险");if(dayHigh>0&&(dayHigh-e.Account.Equity)/dayHigh>=limits.DailyDrawdownLimit&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),"当日回撤熔断");
        if(d.Instrument is not("BTCUSDT" or "ETHUSDT"))return(Array.Empty<ExecutionIntent>(),"品种无效");if(!e.Markets.TryGetValue(d.Instrument,out var m))return(Array.Empty<ExecutionIntent>(),"缺少该品种行情");
        var symbols=e.Positions.Select(x=>x.Symbol).Distinct().ToArray();if(symbols.Length>1&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),"同时存在BTC和ETH仓位，已进入降级模式");if(symbols.Length==1&&symbols[0]!=d.Instrument&&riskIncreasing)return(Array.Empty<ExecutionIntent>(),"已有其他主动品种");
        if(d.Action==DecisionAction.Hold)return(Array.Empty<ExecutionIntent>(),"HOLD");var positions=e.Positions.Where(x=>x.Symbol==d.Instrument).ToArray();var effectiveLeverage=Math.Min(limits.Leverage,rule.MaxLeverage);

        string NewId(string tag){var raw=$"WPE-{DateTime.UtcNow:yyMMddHHmmss}-{tag}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
        string? ValidateProtection(PositionSide side)
        {
            if(d.StopLossPrice<=0||d.TakeProfitPrice<=0)return"缺少保护价格";if(side==PositionSide.Long&&(d.StopLossPrice>=m.Price||d.TakeProfitPrice<=m.Price))return"多仓保护价格方向错误";if(side==PositionSide.Short&&(d.StopLossPrice<=m.Price||d.TakeProfitPrice>=m.Price))return"空仓保护价格方向错误";
            var estimatedLiquidation=side==PositionSide.Long?m.Price*(1-1m/effectiveLeverage):m.Price*(1+1m/effectiveLeverage);var boundary=side==PositionSide.Long?estimatedLiquidation+(m.Price-estimatedLiquidation)*.30m:estimatedLiquidation-(estimatedLiquidation-m.Price)*.30m;
            if(side==PositionSide.Long&&d.StopLossPrice<boundary)return"多仓止损距离估算强平价不足30%安全缓冲";if(side==PositionSide.Short&&d.StopLossPrice>boundary)return"空仓止损距离估算强平价不足30%安全缓冲";return null;
        }
        (ExecutionIntent? Intent,string? Error) Increase(PositionSide side,DecisionAction action,PositionSide? closingSide=null)
        {
            var protectionError=ValidateProtection(side);if(protectionError is not null)return(null,protectionError);var current=positions.Where(x=>x.Side==side).Sum(x=>x.Quantity);var currentMargin=current*m.Price/effectiveLeverage;var currentRatio=e.Account.Equity>0?currentMargin/e.Account.Equity:0;
            var currentTier=Array.FindLastIndex(limits.MarginTiers,x=>x<=currentRatio+.0001m)+1;var requestedTier=Math.Clamp(d.TargetTier,1,3);var tier=Math.Min(requestedTier,currentTier+1);
            var targetMargin=e.Account.Equity*limits.MarginTiers[tier-1];var otherMargin=e.Positions.Where(x=>x.Symbol!=d.Instrument||(x.Side!=side&&x.Side!=closingSide)).Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage));
            if(e.Account.Equity<=0||otherMargin+targetMargin>e.Account.Equity*limits.MaxMargin)return(null,"聚合保证金将超过60%硬上限");
            var targetQty=targetMargin*effectiveLeverage/m.Price;var qty=rule.RoundQuantity(Math.Max(0,targetQty-current));if(qty<rule.MinQuantity||qty*m.Price<rule.MinNotional)return(null,"数量低于交易所最小值或已达到目标档位");
            return(new(d.Instrument,side,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId(action.ToString()[..Math.Min(3,action.ToString().Length)]),d.Reason,action),null);
        }

        if(d.Action==DecisionAction.Lock)
        {
            if(positions.Length!=1||lockedSide is not null)return(Array.Empty<ExecutionIntent>(),"LOCK 仅允许对单一主动仓位建立一条反向腿");var primary=positions[0];var lockSide=primary.Side==PositionSide.Long?PositionSide.Short:PositionSide.Long;var protectionError=ValidateProtection(lockSide);if(protectionError is not null)return(Array.Empty<ExecutionIntent>(),protectionError);
            var qty=rule.RoundQuantity(primary.Quantity);var totalMargin=positions.Sum(x=>x.Quantity*x.MarkPrice/Math.Max(1,x.Leverage))+qty*m.Price/effectiveLeverage;if(e.Account.Equity<=0||totalMargin>e.Account.Equity*limits.MaxMargin)return(Array.Empty<ExecutionIntent>(),"锁仓后聚合保证金将超过60%硬上限");if(qty<rule.MinQuantity||qty*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),"锁仓数量低于交易所最小值");
            return([new(d.Instrument,lockSide,qty,false,rule.RoundPrice(d.StopLossPrice),rule.RoundPrice(d.TakeProfitPrice),NewId("LCK"),d.Reason,d.Action)],"通过：建立反向锁仓腿");
        }
        if(d.Action==DecisionAction.Unlock)
        {
            if(lockedSide is null)return(Array.Empty<ExecutionIntent>(),"无法从本地状态确认锁仓腿，禁止猜测解锁");var qty=rule.RoundQuantity(positions.Where(x=>x.Side==lockedSide).Sum(x=>x.Quantity));if(qty<rule.MinQuantity)return(Array.Empty<ExecutionIntent>(),"记录的锁仓腿已不存在");
            return([new(d.Instrument,lockedSide.Value,qty,true,0,0,NewId("ULK"),d.Reason,d.Action)],"通过：关闭已记录锁仓腿");
        }
        if(d.Action is DecisionAction.ReverseToLong or DecisionAction.ReverseToShort)
        {
            if(positions.Select(x=>x.Side).Distinct().Count()>1)return(Array.Empty<ExecutionIntent>(),"双向持仓必须先明确解锁，禁止直接反转");var target=d.Action==DecisionAction.ReverseToLong?PositionSide.Long:PositionSide.Short;var old=target==PositionSide.Long?PositionSide.Short:PositionSide.Long;var oldQty=rule.RoundQuantity(positions.Where(x=>x.Side==old).Sum(x=>x.Quantity));if(oldQty<rule.MinQuantity)return(Array.Empty<ExecutionIntent>(),"没有可反转的旧方向仓位");var opening=Increase(target,d.Action,old);if(opening.Error is not null)return(Array.Empty<ExecutionIntent>(),opening.Error);
            return([new(d.Instrument,old,oldQty,true,0,0,NewId("REV-C"),d.Reason,d.Action),opening.Intent!],"通过：先平旧方向，再开新方向");
        }

        var side=d.Action is DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReduceShort or DecisionAction.CloseShort?PositionSide.Short:PositionSide.Long;
        if(d.Action is DecisionAction.OpenLong or DecisionAction.OpenShort or DecisionAction.AddLong or DecisionAction.AddShort){var opening=Increase(side,d.Action);return opening.Error is null?([opening.Intent!],"通过"): (Array.Empty<ExecutionIntent>(),opening.Error);}
        var current=positions.Where(x=>x.Side==side).Sum(x=>x.Quantity);var close=d.Action is DecisionAction.CloseLong or DecisionAction.CloseShort;var quantity=rule.RoundQuantity(close?current:current*.5m);if(quantity<rule.MinQuantity||quantity*m.Price<rule.MinNotional)return(Array.Empty<ExecutionIntent>(),"数量低于交易所最小值或没有对应仓位");
        return([new(d.Instrument,side,quantity,true,0,0,NewId(close?"CLS":"RED"),d.Reason,d.Action)],close?"通过：全平对应方向":"通过：减仓50%");
    }
}
