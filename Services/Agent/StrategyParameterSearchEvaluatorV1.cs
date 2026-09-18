using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

/// <summary>
/// Produces deterministic, audit-friendly multiple-testing evidence from the actual persisted
/// parameter candidates for one symbol/family. The statistical test only sees the training region;
/// purged/embargoed historical OOS is hashed as robustness evidence, while forward Shadow/live
/// observations remain the independent qualification boundary.
/// </summary>
internal static class StrategyParameterSearchEvaluatorV1
{
    internal const int MaximumTrials=20;
    internal const int SignificanceBlockBars=24;
    internal const double NominalAlpha=.05;
    internal const string TestMethod="24bar-block-sign-normal-v1";
    internal const string CorrectionMethod="Bonferroni";
    internal const string SelectionRule="candidate must pass corrected training significance and untouched purged holdout";

    internal static StrategyParameterSearchEvidence Evaluate(
        StrategyProfile selected,
        IReadOnlyList<StrategyProfile> attemptedTrials,
        IReadOnlyList<StrategyExposureDecisionV1> timeline,
        IReadOnlyList<(double Return,bool Trade)> returns,
        ResearchOosWindowV1 oosWindow)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(attemptedTrials);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(returns);

        var trials=attemptedTrials
            .Where(x=>string.Equals(x.Symbol,selected.Symbol,StringComparison.Ordinal)
                      &&x.Family==selected.Family)
            .GroupBy(ParameterHash,StringComparer.Ordinal)
            .Select(x=>x.OrderBy(y=>y.CreatedAtUtc).ThenBy(y=>y.Id,StringComparer.Ordinal).First())
            .OrderBy(x=>x.CreatedAtUtc)
            .ThenBy(x=>x.Id,StringComparer.Ordinal)
            .ToArray();

        var selectedHash=ParameterHash(selected);
        var selectedIndex=Array.FindIndex(trials,x=>
            string.Equals(x.Id,selected.Id,StringComparison.Ordinal)
            &&string.Equals(x.Version,selected.Version,StringComparison.Ordinal)
            &&string.Equals(ParameterHash(x),selectedHash,StringComparison.Ordinal));

        var trialCount=trials.Length;
        var training=returns.Take(Math.Clamp(oosWindow.TrainingEndExclusive,0,returns.Count)).ToArray();
        var pValue=TrainingPValue(training);
        var threshold=ResearchMultipleTestingV1.BonferroniThreshold(NominalAlpha,trialCount);
        var selectionTimeline=timeline.Take(Math.Clamp(oosWindow.TrainingEndExclusive,0,timeline.Count)).ToArray();
        var holdoutTimeline=timeline.Skip(Math.Clamp(oosWindow.Start,0,timeline.Count))
            .Take(Math.Clamp(oosWindow.Count,0,Math.Max(0,timeline.Count-Math.Clamp(oosWindow.Start,0,timeline.Count))))
            .ToArray();

        return new(
            trialCount,
            selectedIndex,
            SearchSpaceHash(selected.Symbol,selected.Family,trials),
            DatasetHash(selectionTimeline),
            DatasetHash(holdoutTimeline),
            TestMethod,
            CorrectionMethod,
            NominalAlpha,
            pValue,
            threshold,
            SelectionRule);
    }

    internal static bool IsQualified(StrategyParameterSearchEvidence? evidence)
    {
        if(evidence is null
           ||evidence.TrialCount is <1 or >MaximumTrials
           ||evidence.SelectedTrialIndex<0
           ||evidence.SelectedTrialIndex>=evidence.TrialCount
           ||!Sha(evidence.SearchSpaceHash)
           ||!Sha(evidence.SelectionDatasetHash)
           ||!Sha(evidence.HistoricalOosDatasetHash)
           ||string.Equals(evidence.SelectionDatasetHash,evidence.HistoricalOosDatasetHash,StringComparison.Ordinal)
           ||!string.Equals(evidence.TestMethod,TestMethod,StringComparison.Ordinal)
           ||!string.Equals(evidence.CorrectionMethod,CorrectionMethod,StringComparison.Ordinal)
           ||Math.Abs(evidence.NominalAlpha-NominalAlpha)>1e-12
           ||!double.IsFinite(evidence.SelectedTrialPValue)
           ||evidence.SelectedTrialPValue is <0 or >1
           ||!double.IsFinite(evidence.CorrectedSignificanceThreshold))
            return false;

        var expected=ResearchMultipleTestingV1.BonferroniThreshold(evidence.NominalAlpha,evidence.TrialCount);
        return expected>0
               &&Math.Abs(evidence.CorrectedSignificanceThreshold-expected)<=1e-12
               &&evidence.SelectedTrialPValue<=expected;
    }

    internal static double TrainingPValue(IReadOnlyList<(double Return,bool Trade)> trainingReturns)
    {
        ArgumentNullException.ThrowIfNull(trainingReturns);
        var blocks=new List<double>();
        for(var start=0;start<trainingReturns.Count;start+=SignificanceBlockBars)
        {
            var equity=1d;
            var end=Math.Min(trainingReturns.Count,start+SignificanceBlockBars);
            for(var i=start;i<end;i++)equity*=Math.Max(.0001,1+trainingReturns[i].Return);
            var value=equity-1;
            if(Math.Abs(value)>1e-12)blocks.Add(value);
        }
        if(blocks.Count<8)return 1;
        var wins=blocks.Count(x=>x>0);
        var n=blocks.Count;
        var z=(wins-.5*n-.5)/Math.Sqrt(.25*n);
        return Math.Clamp(1-NormalCdf(z),0,1);
    }

    private static string SearchSpaceHash(string symbol,StrategyFamily family,IReadOnlyList<StrategyProfile> trials)
        =>Hash(string.Join("\n",
            "wpe.strategy-search-space/1",
            symbol,
            family.ToString(),
            string.Join("\n",trials.Select((x,i)=>$"{i}|{x.Version}|{ParameterHash(x)}"))));

    private static string DatasetHash(IReadOnlyList<StrategyExposureDecisionV1> values)
        =>Hash(string.Join("\n",
            "wpe.strategy-market-window/1",
            string.Join("\n",values.Select(x=>string.Join('|',
                x.SourceCandleOpenTimeUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
                x.TradableAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
                x.ExecutionOpenPrice.ToString(CultureInfo.InvariantCulture),
                x.ExecutionClosePrice.ToString(CultureInfo.InvariantCulture),
                x.ExecutionVolume.ToString(CultureInfo.InvariantCulture))))));

    private static string ParameterHash(StrategyProfile value)
        =>string.IsNullOrWhiteSpace(value.ParametersHash)?LocalStrategyParameters.Hash(value.Parameters):value.ParametersHash;

    private static string Hash(string value)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool Sha(string value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);

    // Abramowitz-Stegun style normal CDF approximation; deterministic and sufficient for the
    // declared block-sign normal approximation. This is not presented as an exact binomial test.
    private static double NormalCdf(double x)
    {
        var absolute=Math.Abs(x);
        var t=1/(1+.2316419*absolute);
        var polynomial=t*(.319381530+t*(-.356563782+t*(1.781477937+t*(-1.821255978+t*1.330274429))));
        var density=.3989422804014327*Math.Exp(-.5*absolute*absolute);
        var upper=density*polynomial;
        return x>=0?1-upper:upper;
    }
}
