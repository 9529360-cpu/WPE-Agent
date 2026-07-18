using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Monitoring;

public interface ITradeMonitoringHub
{
    ValueTask BroadcastSignalAsync(TradeSignal signal, CancellationToken cancellationToken = default);

    ValueTask BroadcastPositionAsync(PositionSnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask BroadcastMetricsAsync(StrategyPerformanceSnapshot metrics, CancellationToken cancellationToken = default);

    ValueTask BroadcastRiskSnapshotAsync(PortfolioRiskSnapshot snapshot, CancellationToken cancellationToken = default);
}
