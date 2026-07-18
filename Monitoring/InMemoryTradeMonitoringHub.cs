using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Monitoring;

public class InMemoryTradeMonitoringHub : ITradeMonitoringHub
{
    private readonly Channel<TradeMonitorEvent> _channel = Channel.CreateUnbounded<TradeMonitorEvent>();

    public ChannelReader<TradeMonitorEvent> Reader => _channel.Reader;

    public ValueTask BroadcastSignalAsync(TradeSignal signal, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(new TradeMonitorEvent(signal, null, null, null), cancellationToken);

    public ValueTask BroadcastPositionAsync(PositionSnapshot snapshot, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(new TradeMonitorEvent(null, snapshot, null, null), cancellationToken);

    public ValueTask BroadcastMetricsAsync(StrategyPerformanceSnapshot metrics, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(new TradeMonitorEvent(null, null, metrics, null), cancellationToken);

    public ValueTask BroadcastRiskSnapshotAsync(PortfolioRiskSnapshot snapshot, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(new TradeMonitorEvent(null, null, null, snapshot), cancellationToken);
}

public record TradeMonitorEvent(TradeSignal? Signal, PositionSnapshot? Position, StrategyPerformanceSnapshot? Metrics, PortfolioRiskSnapshot? Risk);
