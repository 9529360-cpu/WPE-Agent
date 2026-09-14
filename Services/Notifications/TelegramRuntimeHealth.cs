using System.Security.Cryptography;
using System.Text;
using 币安量化机器人.Services;

namespace WpeAgent.Notifications;

public sealed record TelegramWorkerHealthSnapshot(
    string State,
    int ConsecutiveFailures,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    string? DiagnosticCode,
    bool OwnsSingleInstanceLease,
    DateTimeOffset UpdatedAtUtc);

public sealed record TelegramRuntimeHealthSnapshot(
    TelegramWorkerHealthSnapshot Poller,
    TelegramWorkerHealthSnapshot Dispatcher);

public sealed class TelegramRuntimeHealthStore
{
    private static readonly TelegramWorkerHealthSnapshot Stopped =
        new("Stopped", 0, null, null, null, null, false, DateTimeOffset.MinValue);
    private readonly object _gate = new();
    private TelegramWorkerHealthSnapshot _poller = Stopped;
    private TelegramWorkerHealthSnapshot _dispatcher = Stopped;

    public static TelegramRuntimeHealthStore Shared { get; } = new();

    public TelegramRuntimeHealthSnapshot Read()
    {
        lock (_gate) return new(_poller, _dispatcher);
    }

    public void PublishPoller(TelegramWorkerHealthSnapshot value)
    {
        lock (_gate) _poller = value;
    }

    public void PublishDispatcher(TelegramWorkerHealthSnapshot value)
    {
        lock (_gate) _dispatcher = value;
    }
}

public sealed class TelegramSubscriberRuntime
{
    private static readonly TimeSpan HealthyDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseRetryDelay = TimeSpan.FromSeconds(15);
    private readonly TelegramSubscriptionPoller _poller;
    private readonly TelegramSubscriberDispatcher _dispatcher;
    private readonly TelegramRuntimeHealthStore _health;
    private readonly Func<FileStream?> _leaseFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TelegramSubscriberRuntime(
        TelegramSubscriptionPoller poller,
        TelegramSubscriberDispatcher dispatcher,
        TelegramRuntimeHealthStore? health = null,
        Func<FileStream?>? leaseFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _poller = poller;
        _dispatcher = dispatcher;
        _health = health ?? TelegramRuntimeHealthStore.Shared;
        _leaseFactory = leaseFactory ?? TryAcquirePollingLease;
        _delay = delay ?? Task.Delay;
    }

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(RunPollerAsync(ct), RunDispatcherAsync(ct));

    public static TimeSpan RetryDelay(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 6);
        return TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, exponent)));
    }

    private async Task RunPollerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            FileStream? lease;
            try
            {
                lease = _leaseFactory();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                _health.PublishPoller(Failed("Error", 1, null, now, now + RetryDelay(1), ex, false, now));
                await _delay(RetryDelay(1), ct);
                continue;
            }

            if (lease is null)
            {
                var now = DateTimeOffset.UtcNow;
                _health.PublishPoller(new(
                    "Standby", 0, null, null, now + LeaseRetryDelay, "WPE-TG-POLL-SINGLE-OWNER", false, now));
                await _delay(LeaseRetryDelay, ct);
                continue;
            }

            await using (lease)
            {
                var failures = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await _poller.PollOnceAsync(ct);
                        failures = 0;
                        var now = DateTimeOffset.UtcNow;
                        _health.PublishPoller(new("Healthy", 0, now, null, now + HealthyDelay, null, true, now));
                        await _delay(HealthyDelay, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        var retry = RetryDelay(failures);
                        var now = DateTimeOffset.UtcNow;
                        _health.PublishPoller(Failed("BackingOff", failures, null, now, now + retry, ex, true, now));
                        await _delay(retry, ct);
                    }
                }
            }
        }
    }

    private async Task RunDispatcherAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _dispatcher.DispatchOnceAsync(ct);
                failures = 0;
                var now = DateTimeOffset.UtcNow;
                _health.PublishDispatcher(new("Healthy", 0, now, null, now + HealthyDelay, null, false, now));
                await _delay(HealthyDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                var retry = RetryDelay(failures);
                var now = DateTimeOffset.UtcNow;
                _health.PublishDispatcher(Failed("BackingOff", failures, null, now, now + retry, ex, false, now));
                await _delay(retry, ct);
            }
        }
    }

    private static TelegramWorkerHealthSnapshot Failed(
        string state,
        int failures,
        DateTimeOffset? lastSuccess,
        DateTimeOffset failureAt,
        DateTimeOffset nextAttempt,
        Exception exception,
        bool ownsLease,
        DateTimeOffset updatedAt) =>
        new(state, failures, lastSuccess, failureAt, nextAttempt, DiagnosticCode(exception), ownsLease, updatedAt);

    private static string DiagnosticCode(Exception exception)
    {
        var identity = exception.GetType().FullName ?? exception.GetType().Name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "WPE-TG-WORKER-" + hash[..12];
    }

    private static FileStream? TryAcquirePollingLease()
    {
        var path = AppDataPaths.RuntimeFile("telegram-getupdates.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }
}
