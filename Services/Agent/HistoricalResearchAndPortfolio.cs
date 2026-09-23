using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed record HistoricalSyncResult(string Symbol,int Downloaded,int Stored,DateTime? First,DateTime? Last,int CoverageDays,string Status);

public sealed class HistoricalDataService(IExchangeAdapter exchange,AgentSqliteStore db)
{
    public async Task<IReadOnlyList<HistoricalSyncResult>> SyncAsync(IEnumerable<string> symbols,string interval,int years,CancellationToken ct)
    {
        var results=new List<HistoricalSyncResult>();foreach(var symbol in symbols)
        {
            var latest=await db.GetLatestHistoricalCandleAsync(symbol,interval,ct);var start=latest?.AddHours(1)??DateTime.UtcNow.AddYears(-Math.Clamp(years,1,3));var downloaded=0;var cursor=start;var pages=0;
            while(cursor<DateTime.UtcNow.AddHours(-1)&&pages++<40&&!ct.IsCancellationRequested)
            {
                var page=await exchange.GetCandlesRangeAsync(symbol,interval,cursor,DateTime.UtcNow,1000,ct);var fresh=page.Where(x=>x.OpenTime>=cursor).OrderBy(x=>x.OpenTime).ToArray();if(fresh.Length==0)break;await db.UpsertHistoricalCandlesAsync(symbol,interval,fresh,ct);downloaded+=fresh.Length;var next=fresh[^1].OpenTime.AddHours(1);if(next<=cursor)break;cursor=next;await Task.Delay(80,ct);
            }
            var stored=await db.LoadHistoricalCandlesAsync(symbol,interval,100000,ct);var first=stored.FirstOrDefault()?.OpenTime;var last=stored.LastOrDefault()?.OpenTime;var coverage=first is null||last is null?0:(int)(last.Value-first.Value).TotalDays;results.Add(new(symbol,downloaded,stored.Count,first,last,coverage,stored.Count>=1000?"READY":"LIMITED"));
        }return results;
    }
}

public sealed class PortfolioRiskSkill
{
    public PortfolioRiskAssessment Evaluate(EvidencePack evidence,IReadOnlyDictionary<string,IReadOnlyList<CandleEvidence>> history,IReadOnlyList<ExecutionIntent> intents,RiskLimits limits)
    {
        var equity=evidence.Account.Equity;if(equity<=0)return new(){Approved=false,BlockingReasons=[L("Portfolio.EquityUnavailable")],Summary=L("Portfolio.EquityUnavailable")};var exposure=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);foreach(var p in evidence.Positions)exposure[p.Symbol]=exposure.GetValueOrDefault(p.Symbol)+(p.Side==PositionSide.Long?1:-1)*p.Quantity*p.MarkPrice;foreach(var i in intents){var price=i.ExpectedPrice>0?i.ExpectedPrice:evidence.Markets.GetValueOrDefault(i.Symbol)?.Price??0;var change=i.Quantity*price*(i.Side==PositionSide.Long?1:-1)*(i.ReduceOnly?-1:1);exposure[i.Symbol]=exposure.GetValueOrDefault(i.Symbol)+change;}exposure=exposure.Where(x=>Math.Abs(x.Value)>0).ToDictionary();if(exposure.Count==0)return new(){Approved=true,Summary=L("Portfolio.FlatRisk")};var gross=exposure.Values.Sum(Math.Abs);var net=Math.Abs(exposure.Values.Sum());var largest=(double)(exposure.Values.Max(Math.Abs)/gross);var returns=history.ToDictionary(x=>x.Key,x=>x.Value.Skip(1).Select((c,i)=>x.Value[i].Close>0?(double)(c.Close/x.Value[i].Close-1):0).TakeLast(4000).ToArray(),StringComparer.OrdinalIgnoreCase);var correlations=new Dictionary<string,double>();var symbols=exposure.Keys.Order().ToArray();var maxCorr=0d;for(var a=0;a<symbols.Length;a++)for(var b=a+1;b<symbols.Length;b++){var corr=Correlation(returns.GetValueOrDefault(symbols[a])??[],returns.GetValueOrDefault(symbols[b])??[]);correlations[$"{symbols[a]}:{symbols[b]}"]=corr;maxCorr=Math.Max(maxCorr,Math.Abs(corr));}var count=symbols.Select(x=>returns.GetValueOrDefault(x)?.Length??0).DefaultIfEmpty().Min();var portfolio=new List<double>();for(var n=0;n<count;n++){var value=0d;foreach(var symbol in symbols){var series=returns.GetValueOrDefault(symbol)??[];value+=(double)(exposure[symbol]/equity)*series[series.Length-count+n];}portfolio.Add(value);}var var95=QuantileLoss(portfolio,.95);var var99=QuantileLoss(portfolio,.99);var cvar=portfolio.Count==0?0:portfolio.Where(x=>-x>=var99).Select(x=>-x).DefaultIfEmpty(var99).Average();var stress=(double)(gross/equity)*.15;var blocks=new List<string>();if(var99>limits.MaxPortfolioVaR99)blocks.Add(L("Portfolio.VaRLimit",var99,limits.MaxPortfolioVaR99));if(cvar>limits.MaxPortfolioCVaR99)blocks.Add(L("Portfolio.CVaRLimit",cvar,limits.MaxPortfolioCVaR99));if(largest>limits.MaxLargestPositionShare&&exposure.Count>1)blocks.Add(L("Portfolio.ConcentrationLimit",largest,limits.MaxLargestPositionShare));foreach(var pair in correlations.Where(x=>Math.Abs(x.Value)>.85)){var names=pair.Key.Split(':');var combined=(Math.Abs(exposure[names[0]])+Math.Abs(exposure[names[1]]))/equity;if((double)combined>limits.MaxCorrelatedExposure)blocks.Add(L("Portfolio.CorrelationLimit",pair.Key,combined));}return new(){GrossExposure=gross,NetExposure=net,LargestPositionShare=largest,VaR95=var95,VaR99=var99,CVaR99=cvar,StressLoss=stress,MaximumPairCorrelation=maxCorr,Correlations=correlations,BlockingReasons=blocks,Approved=blocks.Count==0,Summary=L("Portfolio.RiskSummary",gross/equity,net/equity,largest,var99,cvar,stress,maxCorr,L(blocks.Count==0?"Audit.Status.APPROVED":"Audit.Status.BLOCKED"))};
    }
    private static double QuantileLoss(IReadOnlyList<double> values,double confidence){if(values.Count==0)return 0;var sorted=values.Order().ToArray();var index=Math.Clamp((int)Math.Floor((1-confidence)*sorted.Length),0,sorted.Length-1);return Math.Max(0,-sorted[index]);}
    private static double Correlation(IReadOnlyList<double> a,IReadOnlyList<double> b){var n=Math.Min(a.Count,b.Count);if(n<30)return 0;var aa=a.Skip(a.Count-n).ToArray();var bb=b.Skip(b.Count-n).ToArray();var ma=aa.Average();var mb=bb.Average();var numerator=0d;var da=0d;var db=0d;for(var i=0;i<n;i++){var x=aa[i]-ma;var y=bb[i]-mb;numerator+=x*y;da+=x*x;db+=y*y;}return da>0&&db>0?numerator/Math.Sqrt(da*db):0;}
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}
