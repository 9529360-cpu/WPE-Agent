using System.Globalization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingInvariantPropertyTests
{
    private const int Cases=25_000;

    [Fact]
    public void QuantityRoundingNeverIncreasesRequestedQuantityAndAlwaysAlignsToStep()
    {
        var gen=new DeterministicGenerator(0x4D595DF4D0F33173UL);
        for(var i=0;i<Cases;i++)
        {
            var value=PositiveDecimal(gen.NextUInt64(),1_000_000,10_000m);
            var step=PositiveDecimal(gen.NextUInt64(),10_000,10_000m);
            var rule=new TradingRule("TESTUSDT",step,.01m,step,1m,125);

            var rounded=rule.RoundQuantity(value);

            Assert.True(rounded>=0m,$"case={i}; value={value}; step={step}; rounded={rounded}");
            Assert.True(rounded<=value,$"case={i}; value={value}; step={step}; rounded={rounded}");
            Assert.True(IsAligned(rounded,step),$"case={i}; value={value}; step={step}; rounded={rounded}");
        }
    }

    [Fact]
    public void PositivePriceRoundingStaysOnTickAndMovesDownByLessThanOneTick()
    {
        var gen=new DeterministicGenerator(0xA0761D6478BD642FUL);
        for(var i=0;i<Cases;i++)
        {
            var value=PositiveDecimal(gen.NextUInt64(),10_000_000,10_000m);
            var tick=PositiveDecimal(gen.NextUInt64(),100_000,100_000m);
            var rule=new TradingRule("TESTUSDT",.001m,tick,.001m,1m,125);

            var rounded=rule.RoundPrice(value);

            Assert.True(rounded>=0m,$"case={i}; value={value}; tick={tick}; rounded={rounded}");
            Assert.True(rounded<=value,$"case={i}; value={value}; tick={tick}; rounded={rounded}");
            Assert.True(IsAligned(rounded,tick),$"case={i}; value={value}; tick={tick}; rounded={rounded}");
            Assert.True(value-rounded<tick+.0000000001m,
                $"case={i}; value={value}; tick={tick}; rounded={rounded}; delta={value-rounded}");
        }
    }

    [Fact]
    public void HighOpportunityProfileNeverLoosensConfiguredSafetyCaps()
    {
        var gen=new DeterministicGenerator(0xE7037ED1A0B428DBUL);
        for(var i=0;i<Cases;i++)
        {
            var configured=new RiskLimits
            {
                TestnetHighOpportunityMode=true,
                MaxRiskPerTrade=Ratio(gen.NextUInt64(),.0001m,.05m),
                MaxSymbolExposure=Ratio(gen.NextUInt64(),.01m,.90m),
                MaxAccountExposure=Ratio(gen.NextUInt64(),.01m,.95m),
                MaxDailyLoss=Ratio(gen.NextUInt64(),.001m,.50m),
                DailyDrawdownLimit=Ratio(gen.NextUInt64(),.001m,.50m),
                MaximumSpreadBps=(double)Ratio(gen.NextUInt64(),.1m,50m),
                MinimumLiquidityScore=(double)Ratio(gen.NextUInt64(),0m,1m),
                MinimumRiskReward=(double)Ratio(gen.NextUInt64(),.1m,5m),
                MaxAtrPercent=(double)Ratio(gen.NextUInt64(),.005m,.20m)
            };

            var resolved=TestnetHighOpportunityRiskProfileV1.Resolve(configured,ExchangeEnvironment.Testnet);

            Assert.True(resolved.MaxRiskPerTrade<=configured.MaxRiskPerTrade,Dump(i,configured,resolved));
            Assert.True(resolved.MaxSymbolExposure<=configured.MaxSymbolExposure,Dump(i,configured,resolved));
            Assert.True(resolved.MaxAccountExposure<=configured.MaxAccountExposure,Dump(i,configured,resolved));
            Assert.True(resolved.MaxDailyLoss<=configured.MaxDailyLoss,Dump(i,configured,resolved));
            Assert.True(resolved.DailyDrawdownLimit<=configured.DailyDrawdownLimit,Dump(i,configured,resolved));
            Assert.True(resolved.MaximumSpreadBps<=configured.MaximumSpreadBps+1e-12,Dump(i,configured,resolved));
            Assert.True(resolved.MinimumLiquidityScore+1e-12>=configured.MinimumLiquidityScore,Dump(i,configured,resolved));
            Assert.True(resolved.MinimumRiskReward+1e-12>=configured.MinimumRiskReward,Dump(i,configured,resolved));
            Assert.True(resolved.MaxAtrPercent<=configured.MaxAtrPercent+1e-12,Dump(i,configured,resolved));
            Assert.True(resolved.MaxRiskPerTrade<=TestnetHighOpportunityRiskProfileV1.MaximumLossRiskPerTrade,Dump(i,configured,resolved));
            Assert.True(resolved.MaximumSpreadBps<=TestnetHighOpportunityRiskProfileV1.MaximumSpreadBps+1e-12,Dump(i,configured,resolved));
            Assert.True(resolved.MinimumRiskReward+1e-12>=TestnetHighOpportunityRiskProfileV1.MinimumRiskReward,Dump(i,configured,resolved));
            Assert.True(resolved.Isolated,Dump(i,configured,resolved));
        }
    }

    [Fact]
    public void PlannedOpeningRiskNeverExceedsConfiguredLossCapAfterTickAndStepRounding()
    {
        var gen=new DeterministicGenerator(0xD1B54A32D192ED03UL);
        for(var i=0;i<15_000;i++)
        {
            var equity=Ratio(gen.NextUInt64(),500m,100_000m);
            var entry=Ratio(gen.NextUInt64(),.05m,50_000m);
            var stopFraction=Ratio(gen.NextUInt64(),.005m,.12m);
            var tick=Ratio(gen.NextUInt64(),.000001m,Math.Max(.000001m,entry*.002m));
            var step=Ratio(gen.NextUInt64(),.000001m,1m);
            var maxRisk=Ratio(gen.NextUInt64(),.001m,.02m);
            var longSide=(gen.NextUInt64()&1UL)==0;
            var rawStop=longSide?entry*(1m-stopFraction):entry*(1m+stopFraction);
            var distance=Math.Abs(entry-rawStop);
            var take=longSide?entry+distance*2.2m:entry-distance*2.2m;
            if(take<=0)continue;

            var decision=new DecisionPlan
            {
                Action=longSide?DecisionAction.OpenLong:DecisionAction.OpenShort,
                Instrument="GENUSDT",
                TargetTier=1,
                EntryPrice=entry,
                StopLossPrice=rawStop,
                TakeProfitPrice=take,
                RiskRewardRatio=2.2
            };
            var evidence=new EvidencePack
            {
                Completeness=100,
                Account=new AccountSnapshot(equity,equity,equity,DateTime.UtcNow),
                Markets=new Dictionary<string,MarketEvidence>
                {
                    ["GENUSDT"]=new("GENUSDT",entry,entry-distance*3m,entry+distance*3m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
                }
            };
            var limits=new RiskLimits
            {
                Leverage=150,
                MarginTiers=[.10m],
                MaxMargin=1m,
                MaxInitialMarginPerTrade=.10m,
                MaxRiskPerTrade=maxRisk,
                MaxSymbolExposure=100m,
                MaxAccountExposure=100m,
                MinimumRiskReward=1.8,
                Isolated=true
            };
            var rule=new TradingRule("GENUSDT",step,tick,0m,0m,150);

            var result=new RiskAndPositionPlanner().Plan(decision,evidence,rule,limits);
            if(result.Intents.Count==0)continue;

            var intent=Assert.Single(result.Intents);
            var actualRisk=intent.Quantity*Math.Abs(intent.ExpectedPrice-intent.StopLoss);
            var riskCap=equity*maxRisk;
            Assert.True(actualRisk<=riskCap+.00000001m,
                $"case={i}; side={intent.Side}; equity={equity}; entry={entry}; rawStop={rawStop}; roundedStop={intent.StopLoss}; tick={tick}; step={step}; qty={intent.Quantity}; actualRisk={actualRisk}; cap={riskCap}");
        }
    }

    [Fact]
    public void ValidExecutionReasonCodesAlwaysNormalizeWithoutMutation()
    {
        var gen=new DeterministicGenerator(0x8EBC6AF09C88C6E3UL);
        for(var i=0;i<Cases;i++)
        {
            var code=$"risk-{Token(gen.NextUInt64())}.execution-{Token(gen.NextUInt64())}.state-{Token(gen.NextUInt64())}";
            var accepted=ExecutionReasonCode.TryNormalize(code,out var normalized);

            Assert.True(accepted,$"case={i}; code={code}");
            Assert.Equal(code,normalized);
        }
    }

    private static decimal PositiveDecimal(ulong raw,ulong modulo,decimal divisor)=>
        (raw%modulo+1m)/divisor;

    private static decimal Ratio(ulong raw,decimal min,decimal max)
    {
        var unit=(raw%100_001UL)/100_000m;
        return min+(max-min)*unit;
    }

    private static string Token(ulong value)=>
        (value%1_000_000UL).ToString(CultureInfo.InvariantCulture);

    private static bool IsAligned(decimal value,decimal step)=>
        step<=0||value%step==0;

    private static string Dump(int index,RiskLimits configured,RiskLimits resolved)=>
        $"case={index}; configured={{risk={configured.MaxRiskPerTrade},symbol={configured.MaxSymbolExposure},account={configured.MaxAccountExposure},daily={configured.MaxDailyLoss},dd={configured.DailyDrawdownLimit},spread={configured.MaximumSpreadBps},liq={configured.MinimumLiquidityScore},rr={configured.MinimumRiskReward},atr={configured.MaxAtrPercent}}}; resolved={{risk={resolved.MaxRiskPerTrade},symbol={resolved.MaxSymbolExposure},account={resolved.MaxAccountExposure},daily={resolved.MaxDailyLoss},dd={resolved.DailyDrawdownLimit},spread={resolved.MaximumSpreadBps},liq={resolved.MinimumLiquidityScore},rr={resolved.MinimumRiskReward},atr={resolved.MaxAtrPercent}}}";

    private sealed class DeterministicGenerator
    {
        private ulong _state;
        public DeterministicGenerator(ulong seed)=>_state=seed;

        public ulong NextUInt64()
        {
            _state+=0x9E3779B97F4A7C15UL;
            var z=_state;
            z=(z^(z>>30))*0xBF58476D1CE4E5B9UL;
            z=(z^(z>>27))*0x94D049BB133111EBUL;
            return z^(z>>31);
        }
    }
}
