namespace 币安量化机器人.Services.Agent;

public enum MediumHorizonBias { Unknown, Bullish, Bearish, Neutral }

public sealed record MediumHorizonFrame(
    string Interval,
    bool Available,
    MediumHorizonBias Bias,
    decimal Close,
    decimal Atr,
    decimal Ema20,
    decimal Ema50,
    decimal SwingHigh,
    decimal SwingLow,
    decimal RecentHigh,
    decimal RecentLow,
    decimal PreviousHigh,
    decimal PreviousLow,
    DateTime ClosedAtUtc,
    int Strength);

public sealed record MediumHorizonRead(
    bool Available,
    MediumHorizonFrame Daily,
    MediumHorizonFrame FourHour,
    string Narrative);

public static class MediumHorizonDecisionSkill
{
    public const string DecisionContextKind="medium-horizon-d1-h4";
    public const string Version="medium-horizon-v1";
    public const double MinimumOpportunityScore=75d;
    public const double MinimumRiskReward=2.2d;

    public static IReadOnlyList<DecisionPlan> DecideCandidates(EvidencePack evidence,int limit=5)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if(limit<1||limit>5)throw new ArgumentOutOfRangeException(nameof(limit));
        var open=(evidence.Positions??Array.Empty<ManagedPosition>())
            .Where(x=>x.Quantity>0)
            .Select(x=>x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates=new List<(DecisionPlan Plan,double Score,double Cost,double Quality)>();
        foreach(var market in (evidence.Markets??new Dictionary<string,MarketEvidence>()).Values)
        {
            if(market is null||open.Contains(market.Symbol))continue;
            var plan=DecideMarket(market);
            if(!DeterministicPlanSkill.IsRiskIncreasing(plan.Action))continue;
            var score=EvidenceDouble(plan,"opportunity_score");
            var cost=Math.Max(0,market.Quality.SpreadBps);
            candidates.Add((plan,score,cost,market.Quality.QualityScore));
        }
        return candidates
            .OrderByDescending(x=>x.Score)
            .ThenByDescending(x=>x.Quality)
            .ThenBy(x=>x.Cost)
            .ThenBy(x=>x.Plan.Instrument,StringComparer.Ordinal)
            .Take(limit)
            .Select(x=>x.Plan)
            .ToArray();
    }

    public static DecisionPlan Decide(EvidencePack evidence)
    {
        var candidates=DecideCandidates(evidence,5);
        return candidates.Count>0?candidates[0]:Hold(evidence.Markets.Values.FirstOrDefault(),"No daily/4h thesis is actionable.");
    }

    public static DecisionPlan DecideMarket(MarketEvidence market)
    {
        ArgumentNullException.ThrowIfNull(market);
        var read=Analyze(market);
        if(!read.Available)return Hold(market,"Daily/4h confirmed candle history is incomplete.");

        var daily=read.Daily;
        var h4=read.FourHour;
        if(daily.Bias is MediumHorizonBias.Unknown or MediumHorizonBias.Neutral)
            return Hold(market,$"{market.Symbol}: daily regime is neutral; no directional thesis.");
        if(h4.Bias!=daily.Bias)
            return Hold(market,$"{market.Symbol}: 4h structure is not aligned with the daily regime.");

        var longSide=daily.Bias==MediumHorizonBias.Bullish;
        var candles=ConfirmedMarketCandlesV1.Select(market.Candles4h,"4h",market.CollectedAt).TakeLast(12).ToArray();
        if(candles.Length<6)return Hold(market,$"{market.Symbol}: 4h setup history is incomplete.");

        var last=candles[^1];
        var previous=candles[^2];
        var range=Math.Max(.00000001m,last.High-last.Low);
        var body=Math.Abs(last.Close-last.Open);
        var bodyRatio=body/range;
        var pullbackWindow=candles.Skip(Math.Max(0,candles.Length-6)).Take(5).ToArray();
        var pullbackSeen=longSide
            ?pullbackWindow.Any(x=>x.Low<=h4.Ema20+h4.Atr*.35m||x.Close<=h4.Ema20+h4.Atr*.20m)
            :pullbackWindow.Any(x=>x.High>=h4.Ema20-h4.Atr*.35m||x.Close>=h4.Ema20-h4.Atr*.20m);
        var confirmation=longSide
            ?last.Close>last.Open&&last.Close>previous.High&&bodyRatio>=.45m
            :last.Close<last.Open&&last.Close<previous.Low&&bodyRatio>=.45m;
        var chaseDistance=h4.Atr>0?Math.Abs(last.Close-h4.Ema20)/h4.Atr:99m;
        if(!pullbackSeen)return Hold(market,$"{market.Symbol}: daily/4h trend is aligned but no 4h pullback has reset entry risk.");
        if(!confirmation)return Hold(market,$"{market.Symbol}: 4h pullback exists but the closed 4h confirmation candle is still missing.");
        if(chaseDistance>1.35m)return Hold(market,$"{market.Symbol}: 4h confirmation is too extended from the 4h mean; do not chase.");

        var entry=market.Price>0?market.Price:last.Close;
        var structuralStop=longSide
            ?Math.Min(h4.SwingLow,candles.TakeLast(8).Min(x=>x.Low))-h4.Atr*.25m
            :Math.Max(h4.SwingHigh,candles.TakeLast(8).Max(x=>x.High))+h4.Atr*.25m;
        if(structuralStop<=0||(longSide&&structuralStop>=entry)||(!longSide&&structuralStop<=entry))
            return Hold(market,$"{market.Symbol}: 4h structural stop is invalid.");

        var risk=Math.Abs(entry-structuralStop);
        var stopFraction=risk/entry;
        if(stopFraction<.004m)return Hold(market,$"{market.Symbol}: structural stop is under 0.4%; setup is too noisy.");
        if(stopFraction>.09m)return Hold(market,$"{market.Symbol}: structural stop exceeds 9%; setup is too loose.");

        var dailyObstacle=longSide?daily.RecentHigh:daily.RecentLow;
        var obstacleReward=longSide?dailyObstacle-entry:entry-dailyObstacle;
        if(obstacleReward>0&&obstacleReward/risk<(decimal)MinimumRiskReward)
            return Hold(market,$"{market.Symbol}: daily opposing structure leaves less than {MinimumRiskReward:F1}R.");

        var defaultTarget=longSide?entry+risk*3m:entry-risk*3m;
        var take=obstacleReward>0&&obstacleReward/risk>=2.2m&&obstacleReward/risk<=4m
            ?dailyObstacle
            :defaultTarget;
        var rr=(double)(Math.Abs(take-entry)/risk);

        var score=Score(market,daily,h4,pullbackSeen,confirmation,chaseDistance);
        if(score<MinimumOpportunityScore)
            return Hold(market,$"{market.Symbol}: daily/4h thesis exists but opportunity score {score:F0} is below {MinimumOpportunityScore:F0}.");

        var riskFraction=score>=90?.0085m:score>=82?.0060m:.0035m;
        var action=longSide?DecisionAction.OpenLong:DecisionAction.OpenShort;
        var sideText=longSide?"LONG":"SHORT";
        var context=$"MH|{market.Symbol}|{daily.ClosedAtUtc:yyyyMMdd}|{h4.ClosedAtUtc:yyyyMMddHH}|{sideText}";
        return new DecisionPlan
        {
            Action=action,
            Instrument=market.Symbol,
            TargetTier=score>=90?3:score>=82?2:1,
            EntryPrice=entry,
            StopLossPrice=structuralStop,
            TakeProfitPrice=take,
            RiskRewardRatio=rr,
            RiskBudgetFraction=riskFraction,
            Regime=$"Daily-{daily.Bias}/4H-{h4.Bias}",
            Reason=$"[{Version}] Daily {daily.Bias} regime and 4h aligned pullback continuation confirmed on a closed 4h candle.",
            Invalidation="Hard stop, daily regime reversal, or confirmed 4h thesis failure. Intraday/minute noise is not an exit authority.",
            EvidenceReferences=
            [
                "decision_path=medium-horizon-d1-h4",
                $"daily_bias={daily.Bias}",
                $"daily_strength={daily.Strength}",
                $"4h_bias={h4.Bias}",
                $"4h_strength={h4.Strength}",
                $"4h_closed_utc={h4.ClosedAtUtc:O}",
                $"4h_atr={h4.Atr}",
                $"4h_ema20={h4.Ema20}",
                $"4h_ema50={h4.Ema50}",
                $"4h_chase_atr={chaseDistance:F4}",
                $"opportunity_score={score:F2}",
                $"risk_budget_fraction={riskFraction:F4}",
                "direction_authority=daily-plus-4h-only",
                "minute_data_role=execution-only"
            ],
            MissingConditions=[],
            ConflictSummary=$"Daily and 4h aligned; pullback={pullbackSeen}; 4h_confirmation={confirmation}; minute candles have no directional authority.",
            StrategyVersion=Version,
            DecisionContextKind=DecisionContextKind,
            DecisionContextId=context
        };
    }

    public static MediumHorizonRead Analyze(MarketEvidence market)
    {
        var daily=AnalyzeFrame("1d",market.Candles1d,market.CollectedAt);
        var h4=AnalyzeFrame("4h",market.Candles4h,market.CollectedAt);
        return new(daily.Available&&h4.Available,daily,h4,
            $"1D={daily.Bias}({daily.Strength}), 4H={h4.Bias}({h4.Strength}), 4H close={h4.Close}");
    }

    public static bool IsMediumHorizon(DecisionPlan? decision)=>
        decision is not null&&string.Equals(decision.DecisionContextKind,DecisionContextKind,StringComparison.Ordinal);

    public static bool IsMediumHorizonOpening(ExecutionIntent? opening)=>
        opening is not null&&!string.IsNullOrWhiteSpace(opening.Reason)&&opening.Reason.Contains($"[{Version}]",StringComparison.Ordinal);

    public static bool ContextMatches(DecisionPlan decision,MarketEvidence market)
    {
        if(!IsMediumHorizon(decision)||!string.Equals(decision.Instrument,market.Symbol,StringComparison.OrdinalIgnoreCase))return false;
        var current=DecideMarket(market);
        return current.Action==decision.Action
            &&string.Equals(current.DecisionContextId,decision.DecisionContextId,StringComparison.Ordinal)
            &&string.Equals(current.StrategyVersion,Version,StringComparison.Ordinal);
    }

    public static bool HasFullPostEntryFourHourBar(DateTimeOffset openingAt,MarketEvidence market)
    {
        ArgumentNullException.ThrowIfNull(market);
        openingAt=openingAt.ToUniversalTime();
        return ConfirmedMarketCandlesV1.Select(market.Candles4h,"4h",market.CollectedAt)
            .Any(x=>new DateTimeOffset(x.OpenTime,TimeSpan.Zero)>=openingAt);
    }

    public static bool PositionInvalidated(ManagedPosition position,MarketEvidence market)
    {
        var read=Analyze(market);
        if(!read.Available)return false;
        var daily=read.Daily;
        var h4=read.FourHour;
        var candles=ConfirmedMarketCandlesV1.Select(market.Candles4h,"4h",market.CollectedAt).TakeLast(4).ToArray();
        if(candles.Length<3)return false;

        if(position.Side==PositionSide.Long)
        {
            if(daily.Bias==MediumHorizonBias.Bearish)return true;
            var twoClosesBroken=candles[^1].Close<h4.Ema20&&candles[^2].Close<h4.Ema20&&candles[^1].Close<h4.SwingLow;
            return h4.Bias==MediumHorizonBias.Bearish&&twoClosesBroken;
        }
        else
        {
            if(daily.Bias==MediumHorizonBias.Bullish)return true;
            var twoClosesBroken=candles[^1].Close>h4.Ema20&&candles[^2].Close>h4.Ema20&&candles[^1].Close>h4.SwingHigh;
            return h4.Bias==MediumHorizonBias.Bullish&&twoClosesBroken;
        }
    }

    private static MediumHorizonFrame AnalyzeFrame(string interval,IReadOnlyList<CandleEvidence> source,DateTime observedAtUtc)
    {
        var candles=ConfirmedMarketCandlesV1.Select(source,interval,observedAtUtc).TakeLast(160).ToArray();
        if(candles.Length<60)return new(interval,false,MediumHorizonBias.Unknown,0,0,0,0,0,0,0,0,0,0,default,0);
        var closes=candles.Select(x=>x.Close).ToArray();
        var close=closes[^1];
        var ema20=Ema(closes,20);
        var ema50=Ema(closes,50);
        var ema20Ago=Ema(closes.Take(closes.Length-5).ToArray(),20);
        var atr=Atr(candles);
        var recent=candles.TakeLast(20).ToArray();
        var previous=candles.Skip(candles.Length-40).Take(20).ToArray();
        var recentHigh=recent.Max(x=>x.High);
        var recentLow=recent.Min(x=>x.Low);
        var previousHigh=previous.Max(x=>x.High);
        var previousLow=previous.Min(x=>x.Low);
        var swingHigh=LatestPivot(candles,true);
        var swingLow=LatestPivot(candles,false);
        var higherStructure=recentHigh>previousHigh&&recentLow>previousLow;
        var lowerStructure=recentHigh<previousHigh&&recentLow<previousLow;

        var bullishVotes=0;
        if(close>ema20)bullishVotes++;
        if(ema20>ema50)bullishVotes++;
        if(ema20>ema20Ago)bullishVotes++;
        if(higherStructure)bullishVotes++;
        if(close>(recentHigh+recentLow)/2m)bullishVotes++;

        var bearishVotes=0;
        if(close<ema20)bearishVotes++;
        if(ema20<ema50)bearishVotes++;
        if(ema20<ema20Ago)bearishVotes++;
        if(lowerStructure)bearishVotes++;
        if(close<(recentHigh+recentLow)/2m)bearishVotes++;

        var bias=bullishVotes>=4?MediumHorizonBias.Bullish:bearishVotes>=4?MediumHorizonBias.Bearish:MediumHorizonBias.Neutral;
        var strength=Math.Max(bullishVotes,bearishVotes)*20;
        var closedAt=candles[^1].OpenTime.ToUniversalTime()+ConfirmedMarketCandlesV1.Duration(interval);
        return new(interval,true,bias,close,atr,ema20,ema50,swingHigh,swingLow,recentHigh,recentLow,previousHigh,previousLow,closedAt,strength);
    }

    private static decimal Ema(IReadOnlyList<decimal> values,int period)
    {
        if(values.Count==0)return 0;
        var alpha=2m/(period+1m);
        var ema=values.Take(Math.Min(period,values.Count)).Average();
        foreach(var value in values.Skip(Math.Min(period,values.Count)))ema=value*alpha+ema*(1m-alpha);
        return ema;
    }

    private static decimal Atr(IReadOnlyList<CandleEvidence> candles)
    {
        if(candles.Count<2)return 0;
        var ranges=new List<decimal>();
        for(var i=Math.Max(1,candles.Count-20);i<candles.Count;i++)
        {
            var c=candles[i];var prev=candles[i-1].Close;
            ranges.Add(Math.Max(c.High-c.Low,Math.Max(Math.Abs(c.High-prev),Math.Abs(c.Low-prev))));
        }
        return ranges.Count==0?0:ranges.Average();
    }

    private static decimal LatestPivot(IReadOnlyList<CandleEvidence> candles,bool high)
    {
        for(var i=candles.Count-4;i>=3;i--)
        {
            var value=high?candles[i].High:candles[i].Low;
            var pivot=true;
            for(var j=i-2;j<=i+2;j++)
            {
                if(j==i||j<0||j>=candles.Count)continue;
                if(high&&candles[j].High>=value){pivot=false;break;}
                if(!high&&candles[j].Low<=value){pivot=false;break;}
            }
            if(pivot)return value;
        }
        return high?candles.TakeLast(20).Max(x=>x.High):candles.TakeLast(20).Min(x=>x.Low);
    }

    private static double Score(MarketEvidence market,MediumHorizonFrame daily,MediumHorizonFrame h4,bool pullback,bool confirmation,decimal chaseAtr)
    {
        var score=0d;
        score+=Math.Min(35,daily.Strength*.35);
        score+=Math.Min(30,h4.Strength*.30);
        if(pullback)score+=10;
        if(confirmation)score+=10;
        if(chaseAtr<=.75m)score+=5;else if(chaseAtr<=1.1m)score+=3;
        score+=Math.Clamp((market.Quality.QualityScore-65)/35d*7d,0,7);
        if(market.Quality.LiquidityScore>=.8)score+=2;
        if(market.Quality.SpreadBps<=8)score+=1;
        return Math.Clamp(score,0,100);
    }

    private static double EvidenceDouble(DecisionPlan plan,string key)
    {
        var prefix=key+"=";
        var value=plan.EvidenceReferences.FirstOrDefault(x=>x.StartsWith(prefix,StringComparison.Ordinal));
        return value is not null&&double.TryParse(value[prefix.Length..],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var parsed)?parsed:0;
    }

    private static DecisionPlan Hold(MarketEvidence? market,string reason)=>new()
    {
        Action=DecisionAction.Hold,
        Instrument=market?.Symbol??string.Empty,
        TargetTier=0,
        Regime="medium-horizon-observation",
        Reason=reason,
        Invalidation="No risk-increasing medium-horizon thesis exists.",
        StrategyVersion=Version,
        DecisionContextKind="medium-horizon-observation"
    };
}
