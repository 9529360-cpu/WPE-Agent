using System;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public class KellyAllocator
{
    public double Calculate(PositionSnapshot snapshot)
    {
        var winRate = snapshot.RealizedPnl > 0 ? 0.6 : 0.4;
        var profitFactor = snapshot.RealizedPnl > 0 ? 1.8 : 0.9;
        var fraction = winRate - (1 - winRate) / profitFactor;
        return Math.Clamp(fraction, 0, 1);
    }
}
