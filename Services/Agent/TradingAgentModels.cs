using System.Text.Json.Serialization;

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

public sealed class AgentDecision
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "HOLD";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("regime")]
    public string Regime { get; set; } = "未分类";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("support")]
    public decimal Support { get; set; }

    [JsonPropertyName("resistance")]
    public decimal Resistance { get; set; }

    [JsonPropertyName("invalidation")]
    public string Invalidation { get; set; } = string.Empty;

}

