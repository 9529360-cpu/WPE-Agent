namespace WpeAgent.CrossAssetResearch;

/// <summary>
/// Shared multiple-testing correction authority. Strategy promotion and cross-asset research must
/// not carry independent significance-threshold implementations.
/// </summary>
internal static class ResearchMultipleTestingV1
{
    internal static double BonferroniThreshold(double nominalAlpha,int trialCount)
        =>trialCount>=1&&nominalAlpha is >0 and <1?nominalAlpha/trialCount:0;

    internal static double CorrectedThreshold(
        MultipleTestingCorrectionMethod method,
        double nominalAlpha,
        IReadOnlyList<double> pValues)
    {
        if(pValues is null||pValues.Count<1||nominalAlpha is <=0 or >=1||pValues.Any(x=>!double.IsFinite(x)||x is <0 or >1))
            return 0;
        return method switch
        {
            MultipleTestingCorrectionMethod.Bonferroni=>BonferroniThreshold(nominalAlpha,pValues.Count),
            MultipleTestingCorrectionMethod.Conservative=>nominalAlpha/(2*pValues.Count),
            MultipleTestingCorrectionMethod.BenjaminiHochberg=>BenjaminiHochbergCutoff(pValues,nominalAlpha),
            _=>0
        };
    }

    private static double BenjaminiHochbergCutoff(IReadOnlyList<double> pValues,double alpha)
    {
        var ordered=pValues.Order().ToArray();
        var cutoff=0d;
        for(var i=0;i<ordered.Length;i++)
            if(ordered[i]<=alpha*(i+1)/ordered.Length)cutoff=ordered[i];
        return cutoff;
    }
}
