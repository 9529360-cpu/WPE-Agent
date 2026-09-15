using 币安量化机器人.Services;

namespace WpeAgent.Notifications;

public enum TelegramRuntimeWorker
{
    SubscriberPoller,
    SubscriberDispatcher
}

public enum TelegramRuntimeWorkerState
{
    Starting,
    Healthy,
    BackingOff,
    Stopped
}

public sealed record TelegramRuntimeWorkerHealth(
    TelegramRuntimeWorker Worker,
    TelegramRuntimeWorkerState State,
    int ConsecutiveFailures,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    DateTime? NextAttemptAtUtc,
    string? DiagnosticCode);

public sealed record TelegramRuntimeHealthSnapshot(
    TelegramRuntimeWorkerHealth SubscriberPoller,
    TelegramRuntimeWorkerHealth SubscriberDispatcher)
{
    public bool Degraded =>
        SubscriberPoller.State == TelegramRuntimeWorkerState.BackingOff ||
        SubscriberDispatcher.State == TelegramRuntimeWorkerState.BackingOff;

    public string ToSafeStatusMessage()
    {
        return $"Telegram subscriber workers: {Describe(SubscriberPoller)}; {Describe(SubscriberDispatcher)}.";
    }

    private static string Describe(TelegramRuntimeWorkerHealth value)
    {
        var worker = value.Worker == TelegramRuntimeWorker.SubscriberPoller ? "poll" : "dispatch";
        var state = value.State switch
        {
            TelegramRuntimeWorkerState.Starting => "starting",
            TelegramRuntimeWorkerState.Healthy => "healthy",
            TelegramRuntimeWorkerState.BackingOff => "backing-off",
            TelegramRuntimeWorkerState.Stopped => "stopped",
            _ => "unknown"
        };
        var failure = value.DiagnosticCode is null ? string.Empty : $", code={value.DiagnosticCode}";
        var next = value.NextAttemptAtUtc is null ? string.Empty : $", next={value.NextAttemptAtUtc.Value:O}";
        return $"{worker}={state}, failures={value.ConsecutiveFailures}{failure}{next}";
    }
}

public static class TelegramRuntimeBackoff
{
    public static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    public static TimeSpan DelayForFailure(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));

        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var seconds = HealthyInterval.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(MaximumDelay.TotalSeconds, seconds));
    }
}

public sealed class TelegramRuntimeHealthStore
{
    public static TelegramRuntimeHealthStore Shared { get; } = new();

    private readonly object _gate = new();
    private TelegramRuntimeWorkerHealth _poller = Initial(TelegramRuntimeWorker.SubscriberPoller);
    private TelegramRuntimeWorkerHealth _dispatcher = Initial(TelegramRuntimeWorker.SubscriberDispatcher);

    public TelegramRuntimeHealthSnapshot Read()
    {
        lock (_gate)
            return new(_poller, _dispatcher);
    }

    public void RecordSuccess(TelegramRuntimeWorker worker, DateTime nowUtc)
    {
        nowUtc = nowUtc.ToUniversalTime();
        Update(worker, current => current with
        {
            State = TelegramRuntimeWorkerState.Healthy,
            ConsecutiveFailures = 0,
            LastSuccessAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc.Add(TelegramRuntimeBackoff.HealthyInterval),
            DiagnosticCode = null
        });
    }

    public TimeSpan RecordFailure(TelegramRuntimeWorker worker, Exception error, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(error);
        nowUtc = nowUtc.ToUniversalTime();
        TimeSpan delay = default;
        Update(worker, current =>
        {
            var failures = checked(current.ConsecutiveFailures + 1);
            delay = TelegramRuntimeBackoff.DelayForFailure(failures);
            var summary = worker == TelegramRuntimeWorker.SubscriberPoller
                ? "Telegram subscriber polling failed."
                : "Telegram subscriber dispatch failed.";
            return current with
            {
                State = TelegramRuntimeWorkerState.BackingOff,
                ConsecutiveFailures = failures,
                LastFailureAtUtc = nowUtc,
                NextAttemptAtUtc = nowUtc.Add(delay),
                DiagnosticCode = UiDiagnostic.FromText(error.ToString(), summary, nowUtc).Code
            };
        });
        return delay;
    }

    public void RecordStopped(TelegramRuntimeWorker worker, DateTime nowUtc)
    {
        nowUtc = nowUtc.ToUniversalTime();
        Update(worker, current => current with
        {
            State = TelegramRuntimeWorkerState.Stopped,
            NextAttemptAtUtc = null
        });
    }

    private void Update(
        TelegramRuntimeWorker worker,
        Func<TelegramRuntimeWorkerHealth, TelegramRuntimeWorkerHealth> update)
    {
        lock (_gate)
        {
            if (worker == TelegramRuntimeWorker.SubscriberPoller)
                _poller = update(_poller);
            else
                _dispatcher = update(_dispatcher);
        }
    }

    private static TelegramRuntimeWorkerHealth Initial(TelegramRuntimeWorker worker) =>
        new(worker, TelegramRuntimeWorkerState.Starting, 0, null, null, null, null);
}

public sealed class TelegramRuntimeLoop(
    TelegramSubscriptionPoller poller,
    TelegramSubscriberDispatcher dispatcher,
    TelegramRuntimeHealthStore? healthStore = null,
    INotificationClock? clock = null)
{
    private readonly TelegramRuntimeHealthStore _health = healthStore ?? TelegramRuntimeHealthStore.Shared;
    private readonly INotificationClock _clock = clock ?? SystemNotificationClock.Instance;

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(
        RunWorkerAsync(TelegramRuntimeWorker.SubscriberPoller, poller.PollOnceAsync, ct),
        RunWorkerAsync(TelegramRuntimeWorker.SubscriberDispatcher, dispatcher.DispatchOnceAsync, ct));

    private async Task RunWorkerAsync(
        TelegramRuntimeWorker worker,
        Func<CancellationToken, Task<int>> executeOnce,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    await executeOnce(ct).ConfigureAwait(false);
                    _health.RecordSuccess(worker, _clock.UtcNow);
                    delay = TelegramRuntimeBackoff.HealthyInterval;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    delay = _health.RecordFailure(worker, ex, _clock.UtcNow);
                }

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _health.RecordStopped(worker, _clock.UtcNow);
        }
    }
}
