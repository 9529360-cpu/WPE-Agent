using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Models;
using 币安量化机器人.Monitoring;
using CorePositionSnapshot = 币安量化机器人.Core.Models.PositionSnapshot;

namespace 币安量化机器人.Services.Execution;

public class StrategySignalExecutor
{
    private readonly ExecutionService _executionService;
    private readonly AccountStateProvider _accountStateProvider;
    private readonly IRiskManager _riskManager;
    private readonly ITradeMonitoringHub _monitoringHub;
    private readonly Serilog.ILogger _logger;

    public StrategySignalExecutor(
        ExecutionService executionService,
        AccountStateProvider accountStateProvider,
        IRiskManager riskManager,
        ITradeMonitoringHub monitoringHub,
        Serilog.ILogger logger)
    {
        _executionService = executionService;
        _accountStateProvider = accountStateProvider;
        _riskManager = riskManager;
        _monitoringHub = monitoringHub;
        _logger = logger;
    }

    public async ValueTask HandleAsync(TradeSignal signal, CancellationToken cancellationToken = default)
    {
        await _monitoringHub.BroadcastSignalAsync(signal, cancellationToken);

        var action = signal.Action;
        if (action.ActionType is TradeActionType.Hold)
            return;

        var portfolio = await _accountStateProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var position = portfolio.Positions.FirstOrDefault(p => string.Equals(p.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));

        if (position is not null)
            await _riskManager.UpdateAsync(position, cancellationToken);

        if (!_riskManager.Approve(action))
        {
            _logger.Warning("Risk rejected signal for {Symbol} ({Reason})", signal.Symbol, action.Reason);
            return;
        }

        // 基于系统状态的额外守护：降级/停止时阻止新开仓
        var sys = Services.ServiceLocator.SystemState;
        if (sys.Status is Core.Models.AgentStatus.Degraded or Core.Models.AgentStatus.Stopped
            && action.ActionType is TradeActionType.EnterLong or TradeActionType.EnterShort)
        {
            _logger.Warning("System state {Status} blocked new position for {Symbol}", sys.Status, signal.Symbol);
            return;
        }

        var intent = BuildIntent(signal.Symbol, action, position);
        if (intent is null)
        {
            _logger.Warning("Unable to convert signal action {Action} for {Symbol} into trade intent", action.ActionType, signal.Symbol);
            return;
        }

        try
        {
            var result = await _executionService.SubmitAsync(intent, cancellationToken).ConfigureAwait(false);
            await _riskManager.RecordFillAsync(new TradeFill(signal.Symbol, (double)result.FilledQuantity, (double)result.AveragePrice, result.Timestamp, action.ActionType), cancellationToken);

            var updatedPortfolio = await _accountStateProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var updatedPosition = updatedPortfolio.Positions.FirstOrDefault(p => string.Equals(p.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));
            if (updatedPosition is not null)
            {
                await _monitoringHub.BroadcastPositionAsync(updatedPosition, cancellationToken);
            }

            var riskSnapshot = BuildRiskSnapshot(updatedPortfolio);
            Services.RiskGuardian.Evaluate(riskSnapshot);
            await _monitoringHub.BroadcastRiskSnapshotAsync(riskSnapshot, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to execute signal for {Symbol}", signal.Symbol);
            throw;
        }
    }

    private static TradeIntent? BuildIntent(string symbol, TradeAction action, CorePositionSnapshot? currentPosition)
    {
        decimal quantity = action.ActionType switch
        {
            TradeActionType.EnterLong or TradeActionType.EnterShort => action.Quantity <= 0 ? 0m : (decimal)Math.Abs(action.Quantity),
            TradeActionType.Exit when currentPosition is not null => Math.Abs((decimal)currentPosition.Quantity),
            _ => 0m
        };

        if (quantity <= 0)
            return null;

        var side = action.ActionType switch
        {
            TradeActionType.EnterLong => OrderSide.Buy,
            TradeActionType.EnterShort => OrderSide.Sell,
            TradeActionType.Exit when currentPosition is not null => currentPosition.Quantity >= 0 ? OrderSide.Sell : OrderSide.Buy,
            _ => OrderSide.Buy
        };

        return new TradeIntent(symbol, side, OrderType.Market, quantity);
    }

    private static PortfolioRiskSnapshot BuildRiskSnapshot(PortfolioSnapshot snapshot)
    {
        var top = snapshot.ExchangePositions
            .Select(p => new PositionRiskBreakdown(
                p.Symbol,
                Math.Abs(p.PositionAmt * p.MarkPrice),
                p.UnrealizedProfit,
                (double)p.PositionAmt))
            .OrderByDescending(p => p.Notional)
            .Take(5)
            .ToArray();

        var largestShare = snapshot.NotionalExposure == 0 ? 0 : (top.FirstOrDefault()?.Notional ?? 0) / snapshot.NotionalExposure;
        var freeMargin = snapshot.FreeMargin;
        var marginUsage = snapshot.MarginUsage;
        var grossLeverage = snapshot.GrossLeverage;

        return new PortfolioRiskSnapshot(
            snapshot.CapturedAt,
            snapshot.TotalWalletBalance,
            snapshot.AvailableBalance,
            snapshot.TotalUnrealizedPnl,
            snapshot.MaintenanceMargin,
            snapshot.NotionalExposure,
            grossLeverage,
            marginUsage,
            freeMargin,
            largestShare,
            top);
    }
}
