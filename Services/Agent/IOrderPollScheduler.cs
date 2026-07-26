namespace 币安量化机器人.Services.Agent;

public interface IOrderPollScheduler
{
    TimeSpan OrderTimeout { get; }
    TimeSpan PollInterval { get; }
    TimeSpan PostCancelWindow { get; }
    DateTimeOffset UtcNow { get; }
    ValueTask DelayAsync(CancellationToken cancellationToken);
}

public sealed class SystemOrderPollScheduler : IOrderPollScheduler
{
    public static SystemOrderPollScheduler Instance { get; } = new();
    public TimeSpan OrderTimeout => TimeSpan.FromSeconds(12);
    public TimeSpan PollInterval => TimeSpan.FromSeconds(1);
    public TimeSpan PostCancelWindow => TimeSpan.FromSeconds(3);
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    private SystemOrderPollScheduler() { }
    public async ValueTask DelayAsync(CancellationToken cancellationToken) =>
        await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
}
