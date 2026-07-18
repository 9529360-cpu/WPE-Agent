using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;
using PositionModel = 币安量化机器人.Models.PositionSnapshot;

namespace 币安量化机器人.Services.Execution;

public class AccountStateProvider
{
    private readonly BinanceApiClient _apiClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private PortfolioSnapshot? _latest;

    public AccountStateProvider(BinanceApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<PortfolioSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var balances = await _apiClient.GetAccountBalancesAsync(cancellationToken).ConfigureAwait(false);
            var exchangePositions = (await _apiClient.GetPositionsAsync(cancellationToken).ConfigureAwait(false)).ToList();

            var wallet = balances.Sum(b => b.MarginBalance);
            var available = balances.Sum(b => b.AvailableBalance);
            var unrealized = balances.Sum(b => b.CrossUnrealizedPnl);
            var exposure = exchangePositions.Sum(p => Math.Abs(p.PositionAmt * p.MarkPrice));
            var maintenance = exchangePositions.Sum(p => p.MaintenanceMargin);

            var converted = exchangePositions.Select(ConvertPosition).ToList().AsReadOnly();
            var exchangeReadonly = exchangePositions.AsReadOnly();

            _latest = new PortfolioSnapshot(DateTime.UtcNow, converted, exchangeReadonly, wallet, available, unrealized, exposure, maintenance);
            return _latest;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<PortfolioSnapshot> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        if (_latest is not null)
            return _latest;

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PositionSnapshot?> GetPositionAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetLatestAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Positions.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }

    private static PositionSnapshot ConvertPosition(PositionModel source)
    {
        var quantity = (double)source.PositionAmt;
        var entryPrice = (double)source.EntryPrice;
        var currentPrice = (double)source.MarkPrice;
        var unrealized = (double)source.UnrealizedProfit;
        var exposure = Math.Abs((double)(source.PositionAmt * source.MarkPrice));

        return new PositionSnapshot(
            source.Symbol,
            quantity,
            entryPrice,
            currentPrice,
            unrealized,
            0d,
            exposure,
            0d,
            0d,
            0,
            null);
    }
}

public record PortfolioSnapshot(
    DateTime CapturedAt,
    IReadOnlyList<PositionSnapshot> Positions,
    IReadOnlyList<PositionModel> ExchangePositions,
    decimal TotalWalletBalance,
    decimal AvailableBalance,
    decimal TotalUnrealizedPnl,
    decimal NotionalExposure,
    decimal MaintenanceMargin)
{
    public decimal GrossLeverage => TotalWalletBalance == 0 ? 0 : NotionalExposure / TotalWalletBalance;

    public decimal FreeMargin => AvailableBalance + TotalUnrealizedPnl;

    public decimal MarginUsage => TotalWalletBalance == 0 ? 0 : MaintenanceMargin / TotalWalletBalance;
}
