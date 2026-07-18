using System;

namespace 币安量化机器人.Models;

public class AccountBalance
{
    public string Asset { get; init; } = string.Empty;
    public decimal WalletBalance { get; init; }
    public decimal AvailableBalance { get; init; }
    public decimal CrossUnrealizedPnl { get; init; }
    public decimal MarginBalance { get; init; }
}
