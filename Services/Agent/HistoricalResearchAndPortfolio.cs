using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Agent;

public sealed record HistoricalSyncResult(string Symbol,int Downloaded,int Stored,DateTime? First,DateTime? Last,int CoverageDays,string Status);

public sealed class HistoricalDataService(IExchangeAdapter exchange,AgentSqliteStore db)
{
    public async Task<IReadOnlyList<HistoricalSyncResult>> SyncAsync(IEnumerable<string> symbols,string interval,int years,CancellationToken ct)
    {
        var step=IntervalDuration(interval);var clampedYears=Math.Clamp(years,1,3);var maxPages=RequiredPages(step,clampedYears);
        var results=new List<HistoricalSyncResult>();foreach(var symbol in symbols)
        {
            var now=DateTime.UtcNow;var cutoff=now-step;var latest=await db.GetLatestHistoricalCandleAsync(symbol,interval,ct);var start=latest?.Add(step)??now.AddYears(-clampedYears);var downloaded=0;var cursor=start;var pages=0;
            while(cursor<=cutoff&&pages++<maxPages&&!ct.IsCancellationRequested)
            {
                var page=await exchange.GetCandlesRangeAsync(symbol,interval,cursor,cutoff,1000,ct);
                var fresh=page.Where(x=>x.OpenTime>=cursor&&x.OpenTime<=cutoff).GroupBy(x=>x.OpenTime).Select(x=>x.Last()).OrderBy(x=>x.OpenTime).ToArray();
                if(fresh.Length==0)break;
                await db.UpsertHistoricalCandlesAsync(symbol,interval,fresh,ct);downloaded+=fresh.Length;
                var next=fresh[^1].OpenTime.Add(step);if(next<=cursor)break;cursor=next;
                await Task.Delay(80,ct);
            }
            var stored=await db.LoadHistoricalCandlesAsync(symbol,interval,100000,ct);var first=stored.FirstOrDefault()?.OpenTime;var last=stored.LastOrDefault()?.OpenTime;var coverage=first is null||last is null?0:(int)(last.Value-first.Value).TotalDays;var readyDays=Math.Max(1,Math.Min(365,clampedYears*365-2));results.Add(new(symbol,downloaded,stored.Count,first,last,coverage,coverage>=readyDays?"READY":"LIMITED"));
        }return results;
    }

    internal static TimeSpan IntervalDuration(string interval)
    {
        if(string.IsNullOrWhiteSpace(interval)||interval.Length<2)throw new ArgumentOutOfRangeException(nameof(interval),"Historical interval is invalid.");
        var token=interval.Trim().ToLowerInvariant();if(!int.TryParse(token[..^1],out var value)||value<=0||value>10000)throw new ArgumentOutOfRangeException(nameof(interval),"Historical interval is invalid.");
        return token[^1] switch{'m'=>TimeSpan.FromMinutes(value),'h'=>TimeSpan.FromHours(value),'d'=>TimeSpan.FromDays(value),_=>throw new ArgumentOutOfRangeException(nameof(interval),"Historical interval unit is unsupported.")};
    }

    private static int RequiredPages(TimeSpan step,int years)
    {
        var bars=Math.Ceiling(TimeSpan.FromDays(366d*years).TotalMinutes/step.TotalMinutes);
        return Math.Clamp((int)Math.Ceiling(bars/1000d)+2,4,140);
    }
}

public sealed class LongHorizonResearchSkill
{
    private const double RoundTripCost=.0014;
    public ResearchValidationResult Evaluate(string symbol,IReadOnlyList<CandleEvidence> candles,RiskLimits limits,string version="wpe-hourly-v3",DateTimeOffset? validatedAtUtc=null)
    {
        var validatedAt=(validatedAtUtc??DateTimeOffset.UtcNow).ToUniversalTime();
        if(candles.Count<500)return new(){ValidatedAtUtc=validatedAt,Symbol=symbol,StrategyVersion=version,SampleSize=candles.Count,CoverageDays=Coverage(candles),Approved=false,Promoted=false,Summary=$"{symbol} historical coverage insufficient: {candles.Count} hourly candles"};
        var returns=Simulate(candles);var split=Math.Clamp((int)(returns.Count*.65),1,returns.Count);var train=returns.Take(split).ToArray();var test=returns.Skip(split).ToArray();var all=Metrics(returns.Select(x=>x.Return).ToArray());var oos=Metrics(test.Select(x=>x.Return).ToArray());var trades=returns.Count(x=>x.Trade);var oosTrades=test.Count(x=>x.Trade);var coverage=Coverage(candles);var walk=WalkForward(candles);var mc=MonteCarlo(returns.Select(x=>x.Return).ToArray());var benchmark=(double)(candles[^1].Close/candles[0].Close-1);var regimes=returns.GroupBy(x=>x.Regime).ToDictionary(x=>x.Key,x=>x.Aggregate(1d,(v,r)=>v*(1+r.Return))-1);var score=Math.Clamp(.20*Math.Min(1,all.ProfitFactor/1.5)+.20*Math.Max(0,(oos.TotalReturn+.10)/.30)+.20*(1-Math.Min(1,all.MaxDrawdown/.25))+.20*walk+.20*(1-mc),0,1);var promoted=coverage>=limits.MinimumHistoricalDays&&trades>=limits.MinimumBacktestTrades&&oos.Expectancy>0&&all.ProfitFactor>=1.1&&all.MaxDrawdown<=.25&&walk>=.5&&mc<=.45;
        return new(){ValidatedAtUtc=validatedAt,Symbol=symbol,StrategyVersion=version,SampleSize=candles.Count,Trades=trades,OutOfSampleTrades=oosTrades,CoverageDays=coverage,WinRate=all.WinRate,ProfitFactor=all.ProfitFactor,Expectancy=all.Expectancy,MaxDrawdown=all.MaxDrawdown,Sharpe=all.Sharpe,OutOfSampleReturn=oos.TotalReturn,WalkForwardScore=walk,MonteCarloLossProbability=mc,QualityScore=score,Approved=promoted,Promoted=promoted,StrategyReturn=all.TotalReturn,BenchmarkReturn=benchmark,RegimeReturns=regimes,Summary=$"{symbol} {coverage}d/{candles.Count}h trades={trades} OOS={oos.TotalReturn:P1} PF={all.ProfitFactor:F2} DD={all.MaxDrawdown:P1} Sharpe={all.Sharpe:F2} WF={walk:F2} MC-loss={mc:P0} promoted={promoted}"};
    }
    private sealed record BarResult(double Return,bool Trade,string Regime);
    private static List<BarResult> Simulate(IReadOnlyList<CandleEvidence> c)
    {
        var result=new List<BarResult>();var side=0;for(var i=120;i<c.Count;i++)
        {
            var fast=c.Skip(i-24).Take(24).Average(x=>x.Close);var slow=c.Skip(i-96).Take(96).Average(x=>x.Close);var high=c.Skip(i-48).Take(48).Max(x=>x.High);var low=c.Skip(i-48).Take(48).Min(x=>x.Low);var atr=(double)c.Skip(i-24).Take(24).Average(x=>x.High-x.Low)/(double)c[i].Close;var next=c[i].Close>=high*.999m&&fast>slow?1:c[i].Close<=low*1.001m&&fast<slow?-1:Math.Abs((double)(fast/slow-1))<.002?0:side;var changed=next!=side;var bar=c[i-1].Close>0?(double)(c[i].Close/c[i-1].Close-1)*side:0;if(changed)bar-=RoundTripCost/2;var regime=atr>.025?"EXTREME":Math.Abs((double)(fast/slow-1))>.01?"TREND":"RANGE";result.Add(new(bar,changed&&next!=0,regime));side=next;
        }return result;
    }
    private static (double WinRate,double ProfitFactor,double Expectancy,double MaxDrawdown,double Sharpe,double TotalReturn) Metrics(IReadOnlyList<double> r){if(r.Count==0)return(0,0,0,0,0,0);var wins=r.Where(x=>x>0).Sum();var losses=-r.Where(x=>x<0).Sum();var equity=1d;var high=1d;var dd=0d;foreach(var x in r){equity*=Math.Max(.01,1+x);high=Math.Max(high,equity);dd=Math.Max(dd,(high-equity)/high);}var avg=r.Average();var sd=Math.Sqrt(r.Select(x=>(x-avg)*(x-avg)).Average());return(r.Count(x=>x>0)/(double)r.Count,losses>0?wins/losses:wins>0?9:0,avg,dd,sd>0?avg/sd*Math.Sqrt(24*365):0,equity-1);}
    private static double WalkForward(IReadOnlyList<CandleEvidence> c){var scores=new List<double>();for(var n=0;n<4;n++){var start=n*c.Count/8;var length=Math.Min(c.Count-start,c.Count/2);var m=Metrics(Simulate(c.Skip(start).Take(length).ToArray()).Select(x=>x.Return).ToArray());scores.Add(Math.Clamp(.5+m.Expectancy*100-m.MaxDrawdown,0,1));}return scores.Count==0?0:scores.Average();}
    private static double MonteCarlo(IReadOnlyList<double> r){if(r.Count==0)return 1;var random=new Random(43);var losses=0;for(var n=0;n<500;n++){var equity=1d;for(var i=0;i<Math.Min(r.Count,2000);i++)equity*=Math.Max(.01,1+r[random.Next(r.Count)]);if(equity<1)losses++;}return losses/500d;}
    private static int Coverage(IReadOnlyList<CandleEvidence> c)=>c.Count<2?0:(int)(c[^1].OpenTime-c[0].OpenTime).TotalDays;
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
