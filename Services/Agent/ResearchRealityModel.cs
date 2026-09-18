using WpeAgent.CrossAssetResearch;

namespace 币安量化机器人.Services.Agent;

/// <summary>
/// Normalized research-time execution economics shared by strategy simulations.
/// Returns are unit-notional, so only variable commission/slippage are supported here.
/// Fixed currency costs and carry require notional/time-aware evidence and fail closed.
/// </summary>
internal sealed class ResearchRealityModel
{
    internal static readonly ResearchCostModel DefaultCosts = new(.0004m, .0003m);
    private readonly ResearchCostModel _costs;

    internal ResearchRealityModel(ResearchCostModel? costs = null)
    {
        _costs = costs ?? DefaultCosts;
        if (_costs.CommissionRate < 0 || _costs.SlippageRate < 0)
            throw new ArgumentOutOfRangeException(nameof(costs), "Research commission/slippage cannot be negative.");
        if (_costs.FixedCostPerTrade != 0 || _costs.BorrowRatePerDay != 0)
            throw new ArgumentException("Normalized strategy research cannot prove fixed-currency or carry costs without notional/time evidence.", nameof(costs));
    }

    internal ResearchCostModel Costs => _costs;
    internal double SideVariableRate => (double)(_costs.CommissionRate + _costs.SlippageRate);

    internal ResearchExposureStep Apply(int previousPosition, int desiredPosition, double underlyingReturn)
    {
        if (previousPosition is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(previousPosition));
        if (desiredPosition is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(desiredPosition));
        if (!double.IsFinite(underlyingReturn)) throw new ArgumentOutOfRangeException(nameof(underlyingReturn));

        var turnover = Math.Abs(desiredPosition - previousPosition);
        var netReturn = desiredPosition * underlyingReturn - turnover * SideVariableRate;
        var completedTrade = previousPosition != 0 && desiredPosition != previousPosition;
        return new(netReturn, completedTrade, desiredPosition, turnover);
    }

    internal double CloseCost(int position)
    {
        if (position is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(position));
        return Math.Abs(position) * SideVariableRate;
    }

    internal List<(double Return,bool Trade)> Simulate(IReadOnlyList<StrategyExposureDecisionV1> timeline)
    {
        if(!StrategyExposureTimelineV1.IsCanonical(timeline)||timeline.Count==0)return [];
        var result=new List<(double Return,bool Trade)>(timeline.Count);
        var position=0;
        foreach(var decision in timeline)
        {
            var underlyingReturn=(double)(decision.ExecutionClosePrice/decision.ExecutionOpenPrice-1);
            var step=Apply(position,decision.TargetExposure,underlyingReturn);
            result.Add((step.NetReturn,step.CompletedTrade));
            position=step.Position;
        }
        if(position!=0)
        {
            var last=result[^1];
            result[^1]=(last.Return-CloseCost(position),true);
        }
        return result;
    }
}

internal readonly record struct ResearchExposureStep(
    double NetReturn,
    bool CompletedTrade,
    int Position,
    int Turnover);
