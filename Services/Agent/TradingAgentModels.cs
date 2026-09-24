namespace 币安量化机器人.Services.Agent;

public sealed record MarketSkillSnapshot(
    string Symbol,
    string Interval,
    decimal Price,
    decimal Support,
    decimal Resistance,
    double Rsi,
    double ShortTrend,
    double MediumTrend,
    DateTime Timestamp);
