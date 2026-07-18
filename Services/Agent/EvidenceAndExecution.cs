using System.Xml.Linq;
using System.Net.Http;

namespace 币安量化机器人.Services.Agent;

public sealed class EvidenceCollector
{
    private static readonly (string Source,string Url,string Reliability)[] Feeds=
    [
        ("SEC","https://www.sec.gov/news/pressreleases.rss","官方"),("CFTC","https://www.cftc.gov/RSS/RSSENF.xml","官方"),
        ("CoinDesk","https://www.coindesk.com/arc/outboundfeeds/rss/","主流媒体"),("Cointelegraph","https://cointelegraph.com/rss","主流媒体")
    ];
    private readonly IExchangeAdapter _exchange; public EvidenceCollector(IExchangeAdapter exchange)=>_exchange=exchange;
    public async Task<EvidencePack> CollectAsync(CancellationToken ct)
    {
        var missing=new List<string>();AccountSnapshot account;IReadOnlyList<ManagedPosition> positions;var markets=new Dictionary<string,MarketEvidence>();var news=new List<NewsEvidence>();
        try{account=await _exchange.GetAccountAsync(ct);positions=await _exchange.GetPositionsAsync(ct);}catch{throw;}
        foreach(var s in new[]{"BTCUSDT","ETHUSDT"})try{markets[s]=await _exchange.GetMarketAsync(s,ct);}catch{missing.Add(s+"市场/衍生品");}
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(8)};http.DefaultRequestHeaders.UserAgent.ParseAdd("WPE-Agent/1.0");
        foreach(var f in Feeds)try{await using var stream=await http.GetStreamAsync(f.Url,ct);var doc=await XDocument.LoadAsync(stream,LoadOptions.None,ct);var items=doc.Descendants().Where(x=>x.Name.LocalName is "item" or "entry").Take(20);foreach(var i in items){string V(string n)=>i.Elements().FirstOrDefault(x=>x.Name.LocalName==n)?.Value??"";var title=V("title");var link=V("link");if(string.IsNullOrWhiteSpace(link))link=i.Elements().FirstOrDefault(x=>x.Name.LocalName=="link")?.Attribute("href")?.Value??"";DateTime? published=DateTimeOffset.TryParse(V("pubDate")+V("published")+V("updated"),out var dt)?dt.UtcDateTime:null;var group=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title.ToLowerInvariant())))[..16];var assets=new List<string>();if(title.Contains("bitcoin",StringComparison.OrdinalIgnoreCase)||title.Contains("btc",StringComparison.OrdinalIgnoreCase))assets.Add("BTC");if(title.Contains("ethereum",StringComparison.OrdinalIgnoreCase)||title.Contains("eth",StringComparison.OrdinalIgnoreCase))assets.Add("ETH");news.Add(new(f.Source,title,link,published,DateTime.UtcNow,f.Reliability,group,assets));}}catch{missing.Add(f.Source);}
        news=news.GroupBy(x=>x.DuplicateGroup).Select(x=>x.First()).OrderByDescending(x=>x.PublishedAt).Take(50).ToList();
        var marketScore=markets.Count==2?40:markets.Count*20;var derivScore=markets.Count(x=>x.Value.Derivatives.OpenInterest>0||x.Value.Derivatives.FundingRate!=0)*15;var newsScore=news.Count>0?20:0;var accountScore=10;
        return new EvidencePack{Account=account,Positions=positions,Markets=markets,News=news,MissingSources=missing,Completeness=Math.Min(100,marketScore+derivScore+newsScore+accountScore)};
    }
}
public sealed class ReliableOrderExecutor
{
    private readonly IExchangeAdapter _ex;private readonly AgentSqliteStore _db;public ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db){_ex=ex;_db=db;}
    public async Task<string> ExecutePlanAsync(string cycle,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)
    {
        var results=new List<string>();foreach(var intent in intents)results.Add(await ExecuteAsync(cycle,intent,leverage,isolated,ct));return string.Join("；",results);
    }
    public async Task<string> ExecuteAsync(string cycle,ExecutionIntent i,int leverage,bool isolated,CancellationToken ct)
    {
        await _db.SaveIntentAsync(cycle,i,"INTENT",null,ct);await _ex.SetHedgeModeAsync(true,ct);await _ex.SetMarginModeAsync(i.Symbol,isolated,ct);await _ex.SetLeverageAsync(i.Symbol,leverage,ct);
        ExchangeOrder? order=await _ex.FindOrderAsync(i.Symbol,i.ClientOrderId,ct);if(order is null)try{order=await _ex.PlaceMarketAsync(i.Symbol,i.Side,i.Quantity,i.ClientOrderId,i.ReduceOnly,ct);}catch(Exception){order=await _ex.FindOrderAsync(i.Symbol,i.ClientOrderId,ct);if(order is null)throw;}
        await _db.SaveIntentAsync(cycle,i,order.Status,order.OrderId,ct);for(var n=0;n<12&&order.Status is not("FILLED" or "CANCELED" or "REJECTED" or "EXPIRED");n++){await Task.Delay(1000,ct);order=await _ex.FindOrderAsync(i.Symbol,i.ClientOrderId,ct)??order;}
        await _db.SaveIntentAsync(cycle,i,order.Status,order.OrderId,ct);if(order.Status!="FILLED")throw new InvalidOperationException($"订单未确认成交：{order.Status}");if(i.ReduceOnly){if(IsFullClose(i.Action)){await CancelProtectionOrdersAsync(i.Symbol,i.Side,ct);await _db.ClearLockedSideIfMatchesAsync(i.Symbol,i.Side,ct);}if(i.Action==DecisionAction.Unlock)await _db.ClearLockedSideAsync(i.Symbol,ct);await _db.SaveIntentAsync(cycle,i,"COMPLETED",order.OrderId,ct);return"减仓/平仓已确认";}
        try{await _ex.PlaceProtectionAsync(i.Symbol,i.Side,i.StopLoss,i.TakeProfit,i.ClientOrderId,ct);if(i.Action==DecisionAction.Lock)await _db.SetLockedSideAsync(i.Symbol,i.Side,ct);await _db.SaveIntentAsync(cycle,i,"PROTECTED",order.OrderId,ct);return"成交并已挂交易所止损止盈";}
        catch
        {
            var emergency=i with{ReduceOnly=true,ClientOrderId=EmergencyId(i.ClientOrderId)};var close=await _ex.PlaceMarketAsync(i.Symbol,emergency.Side,i.Quantity,emergency.ClientOrderId,true,ct);await _db.SaveIntentAsync(cycle,i,"EMERGENCY_SUBMITTED",close.OrderId,ct);
            for(var n=0;n<12&&close.Status is not("FILLED" or "CANCELED" or "REJECTED" or "EXPIRED");n++){await Task.Delay(1000,ct);close=await _ex.FindOrderAsync(i.Symbol,emergency.ClientOrderId,ct)??close;}
            await _db.SaveIntentAsync(cycle,i,close.Status=="FILLED"?"EMERGENCY_CLOSED":"EMERGENCY_UNKNOWN",close.OrderId,ct);throw new InvalidOperationException(close.Status=="FILLED"?"保护单失败，已确认紧急平掉新仓":$"保护单失败，紧急平仓状态未确认：{close.Status}");
        }
    }
    public async Task<RecoveryResult> RecoverPendingAsync(CancellationToken ct)
    {
        var safe=true;var messages=new List<string>();foreach(var saved in await _db.GetRecoverableIntentsAsync(ct))
        {
            var i=saved.Intent;var order=await _ex.FindOrderAsync(i.Symbol,i.ClientOrderId,ct);if(order is null){await _db.SaveIntentAsync(saved.CycleId,i,"UNKNOWN",saved.ExchangeOrderId,ct);safe=false;messages.Add($"{i.ClientOrderId} 在交易所状态未知");continue;}
            await _db.SaveIntentAsync(saved.CycleId,i,order.Status,order.OrderId,ct);if(order.Status=="FILLED"&&!i.ReduceOnly)try{await _ex.PlaceProtectionAsync(i.Symbol,i.Side,i.StopLoss,i.TakeProfit,i.ClientOrderId,ct);await _db.SaveIntentAsync(saved.CycleId,i,"PROTECTED",order.OrderId,ct);messages.Add($"已恢复 {i.Symbol} {i.Side} 保护单");}catch(Exception ex){safe=false;messages.Add($"{i.Symbol} {i.Side} 保护恢复失败：{ex.Message}");}
            else if(order.Status=="FILLED"){await _db.SaveIntentAsync(saved.CycleId,i,"COMPLETED",order.OrderId,ct);}else if(order.Status is "NEW" or "PARTIALLY_FILLED"){safe=false;messages.Add($"{i.ClientOrderId} 仍在处理中：{order.Status}");}
        }return new(safe,messages);
    }
    public async Task<RecoveryResult> AuditAndRepairProtectionAsync(IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExchangeOrder> orders,CancellationToken ct)
    {
        var safe=true;var messages=new List<string>();foreach(var p in positions)
        {
            var leg=orders.Where(o=>o.Symbol==p.Symbol&&o.PositionSide==p.Side&&o.IsProtection).ToArray();var hasSl=leg.Any(o=>o.Type=="STOP_MARKET");var hasTp=leg.Any(o=>o.Type=="TAKE_PROFIT_MARKET");if(hasSl&&hasTp)continue;
            var intent=await _db.GetLatestOpeningIntentAsync(p.Symbol,p.Side,ct);if(intent is null){safe=false;messages.Add($"{p.Symbol} {p.Side} 缺少{(!hasSl?"止损":"")}{(!hasSl&&!hasTp?"和":"")}{(!hasTp?"止盈":"")}，本地没有可验证的保护参数");continue;}
            try{await _ex.PlaceProtectionAsync(p.Symbol,p.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);messages.Add($"已修复 {p.Symbol} {p.Side} 保护单");}catch(Exception ex){safe=false;messages.Add($"{p.Symbol} {p.Side} 保护修复失败：{ex.Message}");}
        }return new(safe,messages);
    }
    private async Task CancelProtectionOrdersAsync(string symbol,PositionSide side,CancellationToken ct){var orders=await _ex.GetOpenOrdersAsync(symbol,ct);foreach(var order in orders.Where(x=>x.IsProtection&&x.PositionSide==side))await _ex.CancelOrderAsync(symbol,order.OrderId,ct);}
    private static bool IsFullClose(DecisionAction action)=>action is DecisionAction.CloseLong or DecisionAction.CloseShort or DecisionAction.Unlock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
    private static string EmergencyId(string id){const string suffix="-E";var stem=id.Length>36-suffix.Length?id[..(36-suffix.Length)]:id;return stem+suffix;}
}
