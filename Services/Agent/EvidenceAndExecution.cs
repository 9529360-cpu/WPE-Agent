using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed class EvidenceCollector
{
    private readonly IExchangeAdapter _exchange;private readonly IReadOnlyList<string> _symbols;private readonly RealTimeMarketHub? _realtime;private readonly NewsResearchService _news;
    public EvidenceCollector(IExchangeAdapter exchange,IEnumerable<string>? symbols=null,RealTimeMarketHub? realtime=null,NewsResearchService? news=null){_exchange=exchange;_symbols=(symbols??["BTCUSDT","ETHUSDT"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();_realtime=realtime;_news=news??new();}
    public async Task<EvidencePack> CollectAsync(CancellationToken ct)
    {
        var missing=new List<string>();var markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase);
        var account=await _exchange.GetAccountAsync(ct);var positions=await _exchange.GetPositionsAsync(ct);
        foreach(var symbol in _symbols)try{var market=await _exchange.GetMarketAsync(symbol,ct);markets[symbol]=_realtime?.Enrich(market)??market;}catch{missing.Add(symbol+":market_derivatives");}
        if(_realtime is not null&&!_realtime.Healthy)missing.Add("realtime_stream_unhealthy:"+_realtime.Status);
        var newsResult=await _news.CollectAsync(_symbols,ct);missing.AddRange(newsResult.MissingSources);var news=newsResult.Items;
        foreach(var market in markets.Values)foreach(var anomaly in market.Quality.Anomalies)missing.Add($"{market.Symbol}:{anomaly}");
        var marketScore=_symbols.Count==0?0:(int)Math.Round(markets.Count/(double)_symbols.Count*35);var qualityScore=markets.Count==0?0:(int)Math.Round(markets.Values.Average(x=>x.Quality.QualityScore)*.25);
        var derivScore=markets.Count==0?0:(int)Math.Round(markets.Count(x=>x.Value.Derivatives.OpenInterest>0||x.Value.Derivatives.FundingRate!=0)/(double)markets.Count*15);var newsScore=Math.Min(12,newsResult.SuccessfulSources+(newsResult.FullTextDocuments>0?3:0));var realtimeScore=_realtime is null?4:_realtime.Healthy?8:0;
        return new EvidencePack{Account=account,Positions=positions,Markets=markets,News=news,MissingSources=missing.Distinct().ToArray(),Completeness=Math.Min(100,10+marketScore+qualityScore+derivScore+newsScore+realtimeScore)};
    }
}

public sealed class ReliableOrderExecutor
{
    private readonly IExchangeAdapter _ex;private readonly AgentSqliteStore _db;private readonly RiskLimits _limits;
    public ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db,RiskLimits? limits=null){_ex=ex;_db=db;_limits=limits??new();}
    public async Task<string> ExecutePlanAsync(string cycle,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)
    {var results=new List<string>();foreach(var intent in intents)results.Add(await ExecuteAsync(cycle,intent,leverage,isolated,ct));return string.Join("; ",results);}

    public async Task<string> ExecuteAsync(string cycle,ExecutionIntent intent,int leverage,bool isolated,CancellationToken ct)
    {
        if(!intent.ReduceOnly)try{await PreflightAsync(intent,ct);}catch{await _db.SaveIntentAsync(cycle,intent,"PREFLIGHT_BLOCKED",null,ct);throw;}
        await _db.SaveIntentAsync(cycle,intent,"INTENT",null,ct);
        await _ex.SetHedgeModeAsync(true,ct);await _ex.SetMarginModeAsync(intent.Symbol,isolated,ct);await _ex.SetLeverageAsync(intent.Symbol,leverage,ct);
        ExchangeOrder? order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);
        if(order is null)try
        {
            order=intent.OrderType==ExecutionOrderType.Limit&&intent.LimitPrice>0
                ?await _ex.PlaceLimitAsync(intent.Symbol,intent.Side,intent.Quantity,intent.LimitPrice,intent.ClientOrderId,intent.ReduceOnly,ct)
                :await _ex.PlaceMarketAsync(intent.Symbol,intent.Side,intent.Quantity,intent.ClientOrderId,intent.ReduceOnly,ct);
        }catch{order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(order is null){await _db.SaveIntentAsync(cycle,intent,"UNKNOWN",null,ct);throw;}}
        await _db.SaveIntentAsync(cycle,intent,order.Status,order.OrderId,ct);
        for(var n=0;n<12&&order.Status is not("FILLED" or "CANCELED" or "REJECTED" or "EXPIRED");n++){await Task.Delay(1000,ct);order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct)??order;}
        if(intent.OrderType==ExecutionOrderType.Limit&&order.Status is "NEW" or "PARTIALLY_FILLED")
        {await _ex.CancelOrderAsync(intent.Symbol,order.OrderId,ct);order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct)??order;}
        await _db.SaveIntentAsync(cycle,intent,order.Status,order.OrderId,ct);
        if(order.ExecutedQuantity<=0)
        {
            if(order.Status is "CANCELED" or "EXPIRED"){await _db.SaveIntentAsync(cycle,intent,"CANCELED",order.OrderId,ct);return L("Execution.NoFill");}
            throw new InvalidOperationException(L("Execution.Unconfirmed",order.Status));
        }
        var filledIntent=intent with{Quantity=order.ExecutedQuantity};var recordedOrder=order.ExecutedQuantity>0&&order.Status!="FILLED"?order with{Status="PARTIALLY_FILLED"}:order;await _db.RecordExecutionAsync(cycle,filledIntent,recordedOrder,"wpe-core-v2",ct);
        if(intent.ReduceOnly)
        {
            if(IsFullClose(intent.Action)){await CancelProtectionOrdersAsync(intent.Symbol,intent.Side,ct);await _db.ClearLockedSideIfMatchesAsync(intent.Symbol,intent.Side,ct);}
            if(intent.Action==DecisionAction.Unlock)await _db.ClearLockedSideAsync(intent.Symbol,ct);
            await _db.SaveIntentAsync(cycle,intent,"COMPLETED",order.OrderId,ct);return order.ExecutedQuantity<intent.Quantity?L("Execution.PartialClose",order.ExecutedQuantity,intent.Quantity):L("Execution.CloseConfirmed");
        }
        try
        {
            await _ex.PlaceProtectionAsync(intent.Symbol,intent.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);
            if(intent.Action==DecisionAction.Lock)await _db.SetLockedSideAsync(intent.Symbol,intent.Side,ct);
            var status=order.ExecutedQuantity<intent.Quantity?"PARTIALLY_FILLED_PROTECTED":"PROTECTED";await _db.SaveIntentAsync(cycle,intent,status,order.OrderId,ct);
            return order.ExecutedQuantity<intent.Quantity?L("Execution.PartialProtected",order.ExecutedQuantity,intent.Quantity):L("Execution.Protected");
        }
        catch
        {
            var emergency=filledIntent with{ReduceOnly=true,OrderType=ExecutionOrderType.Market,ClientOrderId=EmergencyId(intent.ClientOrderId)};
            var close=await _ex.PlaceMarketAsync(emergency.Symbol,emergency.Side,emergency.Quantity,emergency.ClientOrderId,true,ct);await _db.SaveIntentAsync(cycle,intent,"EMERGENCY_SUBMITTED",close.OrderId,ct);
            for(var n=0;n<12&&close.Status is not("FILLED" or "CANCELED" or "REJECTED" or "EXPIRED");n++){await Task.Delay(1000,ct);close=await _ex.FindOrderAsync(intent.Symbol,emergency.ClientOrderId,ct)??close;}
            await _db.SaveIntentAsync(cycle,intent,close.Status=="FILLED"?"EMERGENCY_CLOSED":"EMERGENCY_UNKNOWN",close.OrderId,ct);
            if(close.Status=="FILLED")await _db.RecordExecutionAsync(cycle,emergency,close,"wpe-core-v2",ct);
            throw new InvalidOperationException(close.Status=="FILLED"?L("Execution.ProtectionEmergencyClosed"):L("Execution.ProtectionEmergencyUnknown",close.Status));
        }
    }

    public async Task<string> ReplaceProtectionAsync(string cycle,ProtectionAdjustment adjustment,CancellationToken ct)
    {
        await CancelProtectionOrdersAsync(adjustment.Symbol,adjustment.Side,ct);var group=EmergencyId($"WPE-PROT-{DateTime.UtcNow:yyMMddHHmmss}");
        try{await _ex.PlaceProtectionAsync(adjustment.Symbol,adjustment.Side,adjustment.StopLoss,adjustment.TakeProfit,group,ct);return L("Execution.ProtectionAdjusted",adjustment.StopLoss,adjustment.TakeProfit);}
        catch(Exception ex)
        {
            var position=(await _ex.GetPositionsAsync(ct)).FirstOrDefault(x=>x.Symbol==adjustment.Symbol&&x.Side==adjustment.Side);
            if(position is not null&&position.Quantity>0){var intent=new ExecutionIntent(position.Symbol,position.Side,position.Quantity,true,0,0,EmergencyId(group),L("Execution.ProtectionReplaceFailed",ex.Message),position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort,ExpectedPrice:position.MarkPrice);await ExecuteAsync(cycle,intent,Math.Max(1,(int)position.Leverage),true,ct);}
            throw;
        }
    }

    public async Task<RecoveryResult> RecoverPendingAsync(CancellationToken ct)
    {
        var safe=true;var messages=new List<string>();foreach(var saved in await _db.GetRecoverableIntentsAsync(ct))
        {
            var intent=saved.Intent;var order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(order is null){await _db.SaveIntentAsync(saved.CycleId,intent,"UNKNOWN",saved.ExchangeOrderId,ct);safe=false;messages.Add(L("Execution.ExchangeUnknown",intent.ClientOrderId));continue;}
            await _db.SaveIntentAsync(saved.CycleId,intent,order.Status,order.OrderId,ct);
            if(order.ExecutedQuantity>0&&!intent.ReduceOnly)try{await _ex.PlaceProtectionAsync(intent.Symbol,intent.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);await _db.SaveIntentAsync(saved.CycleId,intent,"PROTECTED",order.OrderId,ct);messages.Add(L("Execution.ProtectionRecovered",intent.Symbol,intent.Side));}catch(Exception ex){safe=false;messages.Add(L("Execution.ProtectionRecoveryFailed",intent.Symbol,intent.Side,ex.Message));}
            else if(order.ExecutedQuantity>0){await _db.SaveIntentAsync(saved.CycleId,intent,"COMPLETED",order.OrderId,ct);}
            else if(order.Status is "NEW" or "PARTIALLY_FILLED" or "UNKNOWN"){safe=false;messages.Add(L("Execution.Pending",intent.ClientOrderId,order.Status));}
        }return new(safe,messages);
    }

    public async Task<RecoveryResult> AuditAndRepairProtectionAsync(IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExchangeOrder> orders,CancellationToken ct)
    {
        var safe=true;var messages=new List<string>();foreach(var position in positions)
        {
            var leg=orders.Where(o=>o.Symbol==position.Symbol&&o.PositionSide==position.Side&&o.IsProtection).ToArray();var hasSl=leg.Any(o=>o.Type=="STOP_MARKET");var hasTp=leg.Any(o=>o.Type=="TAKE_PROFIT_MARKET");if(hasSl&&hasTp)continue;
            var intent=await _db.GetLatestOpeningIntentAsync(position.Symbol,position.Side,ct);if(intent is null){safe=false;messages.Add(L("Execution.ProtectionMissing",position.Symbol,position.Side,!hasSl,!hasTp));continue;}
            try{await _ex.PlaceProtectionAsync(position.Symbol,position.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);messages.Add(L("Execution.ProtectionRepaired",position.Symbol,position.Side));}catch(Exception ex){safe=false;messages.Add(L("Execution.ProtectionRepairFailed",position.Symbol,position.Side,ex.Message));}
        }return new(safe,messages);
    }

    private async Task PreflightAsync(ExecutionIntent intent,CancellationToken ct)
    {
        var market=await _ex.GetMarketAsync(intent.Symbol,ct);var quality=market.Quality;
        if(quality.QualityScore<65||quality.LiquidityScore<_limits.MinimumLiquidityScore||quality.SpreadBps>_limits.MaximumSpreadBps||quality.AtrPercent>_limits.MaxAtrPercent)throw new InvalidOperationException(L("Execution.PreflightBlocked",quality.QualityScore,quality.LiquidityScore,quality.SpreadBps,quality.AtrPercent));
        if(intent.ExpectedPrice>0){var slippage=Math.Abs((double)((market.Price-intent.ExpectedPrice)/intent.ExpectedPrice))*10000;if(slippage>_limits.MaximumSlippageBps)throw new InvalidOperationException(L("Execution.SlippageBlocked",slippage,_limits.MaximumSlippageBps));}
    }
    private async Task CancelProtectionOrdersAsync(string symbol,PositionSide side,CancellationToken ct){var orders=await _ex.GetOpenOrdersAsync(symbol,ct);foreach(var order in orders.Where(x=>x.IsProtection&&x.PositionSide==side))await _ex.CancelOrderAsync(symbol,order.OrderId,ct);}
    private static bool IsFullClose(DecisionAction action)=>action is DecisionAction.CloseLong or DecisionAction.CloseShort or DecisionAction.Unlock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
    private static string EmergencyId(string id){const string suffix="-E";var stem=id.Length>36-suffix.Length?id[..(36-suffix.Length)]:id;return stem+suffix;}
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}
