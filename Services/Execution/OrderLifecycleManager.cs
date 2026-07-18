using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace 币安量化机器人.Services.Execution;

public class OrderLifecycleManager
{
    private readonly BinanceApiClient _apiClient;
    private readonly IOrderStateStore _stateStore;
    private readonly ILogger _logger;

    public OrderLifecycleManager(BinanceApiClient apiClient, IOrderStateStore stateStore, ILogger logger)
    {
        _apiClient = apiClient;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await SyncPersistedOrdersAsync(cancellationToken).ConfigureAwait(false);
        await SyncMissingOrdersAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncPersistedOrdersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<OrderState> persisted;
        try
        {
            persisted = await _stateStore.GetOpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load persisted order states");
            return;
        }

        foreach (var state in persisted)
        {
            try
            {
                var updated = await _apiClient.GetOrderAsync(state.Symbol, state.ExchangeOrderId, cancellationToken).ConfigureAwait(false);
                await _stateStore.SaveAsync(OrderState.FromResponse(updated), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Unable to reconcile order {OrderId} for {Symbol}", state.ExchangeOrderId, state.Symbol);
            }
        }
    }

    private async Task SyncMissingOrdersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<OrderState> persisted;
        try
        {
            persisted = await _stateStore.GetOpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load persisted order states when syncing missing orders");
            return;
        }

        IReadOnlyList<Models.OrderResponse> remote;
        try
        {
            remote = await _apiClient.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unable to load open orders from exchange for reconciliation");
            return;
        }

        var knownIds = new HashSet<long>(persisted.Select(p => p.ExchangeOrderId));
        foreach (var order in remote)
        {
            if (knownIds.Contains(order.OrderId))
                continue;

            try
            {
                await _stateStore.SaveAsync(OrderState.FromResponse(order), cancellationToken).ConfigureAwait(false);
                _logger.Information("Captured external order {OrderId} for {Symbol} into state store", order.OrderId, order.Symbol);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to persist external order {OrderId} for {Symbol}", order.OrderId, order.Symbol);
            }
        }
    }
}
