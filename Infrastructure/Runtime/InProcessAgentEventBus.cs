using System.Collections.Concurrent;
using System.Threading.Channels;
using 币安量化机器人.Core.Runtime;

namespace 币安量化机器人.Infrastructure.Runtime;

public sealed class InProcessAgentEventBus : IAgentEventBus
{
    private readonly Channel<AgentRuntimeEvent> _channel;
    private readonly ConcurrentDictionary<long, Func<AgentRuntimeEvent, CancellationToken, ValueTask>> _handlers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private long _subscriberId;
    private long _sequence;
    private int _disposeState;

    public InProcessAgentEventBus(int capacity = 2048)
    {
        _channel = Channel.CreateBounded<AgentRuntimeEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pump = Task.Run(PumpAsync);
    }

    public IDisposable Subscribe(Func<AgentRuntimeEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Interlocked.Increment(ref _subscriberId);
        _handlers[id] = handler;
        return new Subscription(_handlers, id);
    }

    public ValueTask PublishAsync(AgentRuntimeEvent value, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(value with { Sequence = Interlocked.Increment(ref _sequence) }, cancellationToken);

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var value in _channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                foreach (var handler in _handlers.OrderBy(x => x.Key).Select(x => x.Value))
                {
                    try { await handler(value, _shutdown.Token); }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                    catch { /* A faulty observer must not stop the trading kernel. */ }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposeState,1)!=0)return;
        _channel.Writer.TryComplete();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { _shutdown.Cancel(); }
        finally { _shutdown.Cancel(); _shutdown.Dispose(); }
    }

    private sealed class Subscription(ConcurrentDictionary<long, Func<AgentRuntimeEvent, CancellationToken, ValueTask>> handlers, long id) : IDisposable
    {
        public void Dispose() => handlers.TryRemove(id, out _);
    }
}
