using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Services.Execution;

public sealed class FileOrderStateStore : IOrderStateStore, IDisposable
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<long, OrderState> _orders = new();

    public FileOrderStateStore(string storeDirectory)
    {
        Directory.CreateDirectory(storeDirectory);
        _storePath = Path.Combine(storeDirectory, "order-state.json");
        Load();
    }

    public async Task SaveAsync(OrderState state, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _orders[state.ExchangeOrderId] = state;
            Persist();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OrderState?> GetAsync(long orderId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _orders.TryGetValue(orderId, out var state);
            return state;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyCollection<OrderState>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _orders.Values.Where(s => !s.IsTerminal).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_storePath))
            return;

        try
        {
            var json = File.ReadAllText(_storePath);
            var states = JsonSerializer.Deserialize<OrderState[]>(json);
            if (states is null)
                return;

            foreach (var state in states)
                _orders[state.ExchangeOrderId] = state;
        }
        catch
        {
            _orders.Clear();
        }
    }

    private void Persist()
    {
        var tempPath = _storePath + ".tmp";
        var json = JsonSerializer.Serialize(_orders.Values);
        File.WriteAllText(tempPath, json);
        File.Copy(tempPath, _storePath, overwrite: true);
        File.Delete(tempPath);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
