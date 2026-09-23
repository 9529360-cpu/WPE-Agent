namespace 币安量化机器人.Services.Agent;

public interface IMarketStructureAnalysisTool
{
    string Name { get; }
    MarketStructureRead Analyze(MarketEvidence market);
}

public sealed class MarketStructureAnalysisTool : IMarketStructureAnalysisTool
{
    public const string ToolName = "market.structure.analyze";
    public static MarketStructureAnalysisTool Shared { get; } = new();

    public string Name => ToolName;

    public MarketStructureRead Analyze(MarketEvidence market)
    {
        ArgumentNullException.ThrowIfNull(market);
        return MarketStructureIntelligence.Analyze(market);
    }
}
