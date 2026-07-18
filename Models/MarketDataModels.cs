namespace 币安量化机器人.Models;

public record struct MiniTickerUpdate(
    string Symbol,
    double LastPrice,
    double IndexPrice,
    double ChangePercent,
    double Volume,
    double HighPrice,
    double LowPrice);
