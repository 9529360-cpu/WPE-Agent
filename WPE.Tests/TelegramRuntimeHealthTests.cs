using System.Text.Json;
using WpeAgent.Notifications;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TelegramRuntimeHealthTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tg-health-" + Guid.NewGuid().ToString("N"));

    public TelegramRuntimeHealthTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(7, 300)]
    [InlineData(20, 300)]
    public void Backoff_is_exponential_and_bounded(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TelegramRuntimeBackoff.DelayForFailure(failures));
    }

    [Fact]
    public void Health_store_resets_after_success_and_never_projects_raw_failure_text()
    {
        var store = new TelegramRuntimeHealthStore();
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        const string secret = "123456:super-secret-token";

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            store.RecordFailure(
                TelegramRuntimeWorker.SubscriberPoller,
                new HttpRequestException($"GET https://api.telegram.org/bot{secret}/getUpdates failed"),
                now));

        var failed = store.Read();
        Assert.True(failed.Degraded);
        Assert.Equal(TelegramRuntimeWorkerState.BackingOff, failed.SubscriberPoller.State);
        Assert.Equal(1, failed.SubscriberPoller.ConsecutiveFailures);
        Assert.StartsWith("WPE-", failed.SubscriberPoller.DiagnosticCode);
        Assert.DoesNotContain(secret, failed.ToSafeStatusMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("api.telegram.org", failed.ToSafeStatusMessage(), StringComparison.OrdinalIgnoreCase);

        store.RecordSuccess(TelegramRuntimeWorker.SubscriberPoller, now.AddSeconds(5));
        var recovered = store.Read();
        Assert.False(recovered.Degraded);
        Assert.Equal(TelegramRuntimeWorkerState.Healthy, recovered.SubscriberPoller.State);
        Assert.Equal(0, recovered.SubscriberPoller.ConsecutiveFailures);
        Assert.Null(recovered.SubscriberPoller.DiagnosticCode);
    }

    [Fact]
    public async Task Runtime_notification_projection_exposes_only_safe_degraded_worker_health()
    {
        var health = new TelegramRuntimeHealthStore();
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        const string secret = "987654:another-secret-token";
        health.RecordFailure(
            TelegramRuntimeWorker.SubscriberDispatcher,
            new InvalidOperationException($"dispatch failed with {secret} at https://api.telegram.org/private"),
            now);

        var settings = new AgentSettingsStore(Path.Combine(_directory, "settings.json"));
        var runtime = new RuntimeNotificationStateStore(
            settings,
            new NotificationOutboxStore(Path.Combine(_directory, "outbox.db"), new FixedClock(now)),
            new TelegramSubscriberStore(Path.Combine(_directory, "subscribers.db"), new FixedClock(now), new ReversibleProtector()),
            health);

        await runtime.RefreshAsync(default);

        var state = runtime.Read();
        Assert.Equal(RuntimeCollectionState.Available, state.State);
        Assert.NotNull(state.Status);
        Assert.NotNull(state.Message);
        Assert.Contains("dispatch=backing-off", state.Message, StringComparison.Ordinal);
        Assert.Contains("failures=1", state.Message, StringComparison.Ordinal);
        Assert.Contains("WPE-", state.Message, StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(state);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("api.telegram.org", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Notification_runtime_routes_subscriber_workers_through_the_health_loop()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "Notifications", "ConfirmedNotificationObserver.cs"));

        Assert.Contains("var telegramRuntime=new TelegramRuntimeLoop(subscriberPoller,subscriberDispatcher);", source, StringComparison.Ordinal);
        Assert.Contains("telegramRuntime.RunAsync(ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("subscriberPoller.RunAsync(ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("subscriberDispatcher.RunAsync(ct)", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch { }
    }

    private sealed class FixedClock(DateTime now) : INotificationClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class ReversibleProtector : ITelegramSubscriberSecretProtector
    {
        public string Protect(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(value.Reverse().ToArray())));
        public string Unprotect(string value) => new string(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Reverse().ToArray());
    }
}
