using System;
using System.Collections.Generic;

namespace 币安量化机器人.Models;

public class FundingRateSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public string Pair { get; set; } = string.Empty;
    public string ContractType { get; set; } = string.Empty;
    public string BaseAsset { get; set; } = string.Empty;
    public string QuoteAsset { get; set; } = string.Empty;
    public double MarkPrice { get; set; }
    public double IndexPrice { get; set; }
    public double LastFundingRate { get; set; }
    public double PredictedFundingRate { get; set; }
    public double Avg7dFundingRate { get; set; }
    public DateTime NextFundingTime { get; set; }
    public double OpenInterest { get; set; }
    public List<FundingHistoryPoint> History { get; set; } = new();

    public double BasisSpread => MarkPrice - IndexPrice;
    public double BasisSpreadPct => IndexPrice == 0 ? 0 : BasisSpread / IndexPrice;

    public TimeSpan TimeUntilNextFunding => NextFundingTime - DateTime.UtcNow;
}

public class FundingHistoryPoint
{
    public DateTime Timestamp { get; set; }
    public double FundingRate { get; set; }
}
