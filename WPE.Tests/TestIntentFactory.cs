using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

internal static class TestIntentFactory
{
    internal static ExecutionIntent Opening(string clientOrderId) => new(
        "BTCUSDT", PositionSide.Long, 0.01m, false, 49_000m, 51_000m,
        clientOrderId, "test intent", DecisionAction.OpenLong, ExpectedPrice: 50_000m);
}
