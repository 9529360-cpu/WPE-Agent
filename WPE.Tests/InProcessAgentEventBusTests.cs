using 币安量化机器人.Infrastructure.Runtime;

namespace WPE.Tests;

public sealed class InProcessAgentEventBusTests
{
    [Fact]
    public async Task DisposeAsyncIsIdempotent()
    {
        var bus=new InProcessAgentEventBus(16);

        await bus.DisposeAsync();
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDisposeAsyncDoesNotCancelAnAlreadyDisposedSource()
    {
        var bus=new InProcessAgentEventBus(16);

        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>bus.DisposeAsync().AsTask()));
    }
}
