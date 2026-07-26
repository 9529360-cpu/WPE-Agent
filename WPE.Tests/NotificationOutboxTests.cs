using Microsoft.Data.Sqlite;
using WpeAgent.Notifications;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class NotificationOutboxTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-notify-"+Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock=new(){UtcNow=new DateTime(2026,7,21,1,0,0,DateTimeKind.Utc)};

    public NotificationOutboxTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Enqueue_DeduplicatesEventPerChannel()
    {
        var store=Store();
        var envelope=new NotificationEnvelope(Event("event-1"),NotificationChannel.Telegram);

        Assert.True(await store.EnqueueAsync(envelope,default));
        Assert.False(await store.EnqueueAsync(envelope,default));

        var row=Assert.Single(await store.ListAsync(default));
        Assert.Equal(NotificationOutboxState.Queued,row.State);
        Assert.Equal(0,row.Attempts);
    }

    [Fact]
    public async Task ExpiredSendingLease_IsRecoveredAfterRestart()
    {
        var path=Path.Combine(_directory,"restart.db");
        var first=new NotificationOutboxStore(path,_clock);
        await first.EnqueueAsync(new(Event("event-restart"),NotificationChannel.Telegram),default);
        var claimed=Assert.Single(await first.ClaimDueAsync(1,TimeSpan.FromSeconds(30),default));
        Assert.Equal(NotificationOutboxState.Sending,claimed.State);
        Assert.Equal(1,claimed.Attempts);

        _clock.UtcNow=_clock.UtcNow.AddMinutes(1);
        var restarted=new NotificationOutboxStore(path,_clock);
        var reclaimed=Assert.Single(await restarted.ClaimDueAsync(1,TimeSpan.FromSeconds(30),default));

        Assert.Equal(NotificationOutboxState.Sending,reclaimed.State);
        Assert.Equal(2,reclaimed.Attempts);
    }

    [Fact]
    public async Task Dispatcher_RetriesThenDeadLettersWithoutThrowing()
    {
        var store=Store();
        await store.EnqueueAsync(new(Event("event-fail"),NotificationChannel.Telegram),default);
        var transport=new RecordingTransport(NotificationChannel.Telegram){Failure=new HttpRequestException("secret response")};
        var dispatcher=new NotificationDispatcher(store,Config(),[transport],clock:_clock,maxAttempts:2);

        Assert.Equal(0,await dispatcher.DispatchDueAsync());
        var failed=Assert.Single(await store.ListAsync(default));
        Assert.Equal(NotificationOutboxState.Failed,failed.State);
        Assert.Equal(1,failed.Attempts);
        Assert.Matches("^WPE-[A-F0-9]{8}$",failed.LastDiagnosticCode);

        _clock.UtcNow=failed.NextAttemptAtUtc;
        Assert.Equal(0,await dispatcher.DispatchDueAsync());
        var dead=Assert.Single(await store.ListAsync(default));
        Assert.Equal(NotificationOutboxState.DeadLetter,dead.State);
        Assert.Equal(2,dead.Attempts);
        Assert.DoesNotContain("secret",dead.LastDiagnosticCode,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispatcher_SendsExactlyOnceForIdempotentEvent()
    {
        var store=Store();
        var publisher=new NotificationOutboxPublisher(store,Config());
        await publisher.PublishAsync(Event("event-sent"),default);
        await publisher.PublishAsync(Event("event-sent"),default);
        var transport=new RecordingTransport(NotificationChannel.Telegram);
        var dispatcher=new NotificationDispatcher(store,Config(),[transport],clock:_clock);

        Assert.Equal(1,await dispatcher.DispatchDueAsync());
        Assert.Equal(0,await dispatcher.DispatchDueAsync());

        Assert.Equal(1,transport.SendCount);
        Assert.Equal(NotificationOutboxState.Sent,Assert.Single(await store.ListAsync(default)).State);
    }

    [Fact]
    public async Task Publisher_FiltersDisallowedAndQuietHourEventsBeforeOutbox()
    {
        var store=Store();
        var disallowed=new StaticConfiguration([
            new(NotificationChannel.Telegram,true,"token","destination",
                AllowedEventKinds:new HashSet<NotificationEventKind>([NotificationEventKind.RiskBlocked]))
        ]);
        await new NotificationOutboxPublisher(store,disallowed,_clock)
            .PublishAsync(Event("event-disallowed"),default);
        Assert.Empty(await store.ListAsync(default));

        var quiet=new StaticConfiguration([
            new(NotificationChannel.Telegram,true,"token","destination",
                AllowedEventKinds:new HashSet<NotificationEventKind>([NotificationEventKind.PositionOpened]),
                QuietHoursEnabled:true,QuietHoursStart:new TimeOnly(0,0),
                QuietHoursEnd:new TimeOnly(2,0),QuietHoursTimeZone:"UTC")
        ]);
        await new NotificationOutboxPublisher(store,quiet,_clock)
            .PublishAsync(Event("event-quiet"),default);
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public void Formatter_IncludesOnlyAvailableConfirmedFields()
    {
        var formatted=new NotificationFormatter().Format(Event("event-format") with
        {
            StopLoss=null,
            TakeProfit=120m
        });

        Assert.Contains("environment: Testnet",formatted.Text,StringComparison.Ordinal);
        Assert.Contains("provider: binance-futures",formatted.Text,StringComparison.Ordinal);
        Assert.Contains("symbol: SOLUSDT",formatted.Text,StringComparison.Ordinal);
        Assert.Contains("filled price: 100",formatted.Text,StringComparison.Ordinal);
        Assert.Contains("take profit: 120",formatted.Text,StringComparison.Ordinal);
        Assert.DoesNotContain("stop loss",formatted.Text,StringComparison.Ordinal);
        Assert.DoesNotContain("order",formatted.Text,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("balance",formatted.Text,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TruthFactory_DoesNotPublishPlanOrUnfilledOrder()
    {
        var intent=new ExecutionIntent("SOLUSDT",PositionSide.Long,1m,false,90m,120m,"client","reason",DecisionAction.OpenLong);
        var unfilled=new ExchangeOrder("SOLUSDT","order","client","NEW",0,0,"LIMIT",PositionSide.Long,false,_clock.UtcNow);
        var filled=unfilled with{Status="FILLED",ExecutedQuantity=1m,AvgPrice=100m};

        Assert.Null(ConfirmedNotificationTruth.FromExecution(
            "event-unfilled","binance-futures","Testnet",intent,unfilled,_clock.UtcNow,"WPE-TEST"));
        var value=ConfirmedNotificationTruth.FromExecution(
            "event-filled","binance-futures","Testnet",intent,filled,_clock.UtcNow,"WPE-TEST");

        Assert.NotNull(value);
        Assert.Equal(NotificationEventKind.PositionOpened,value.Kind);
        Assert.Equal(100m,value.FilledPrice);
    }

    [Fact]
    public async Task ObserverFailure_DoesNotAffectConfirmedTrade()
    {
        var observer=new ConfirmedNotificationObserver(new ThrowingPublisher());

        await observer.ObserveAsync(Event("event-observer"),default);
    }

    [Fact]
    public void AgentConfiguration_DecryptsOnlyEnabledDpapiChannels()
    {
        var path=Path.Combine(_directory,"agent-settings.json");
        var store=new AgentSettingsStore(path);
        var settings=store.Load();
        settings.Notification=new NotificationSlot
        {
            Enabled=true,
            EventKinds=[NotificationEventKind.PositionOpened.ToString()],
            Telegram=new()
            {
                Enabled=true,
                EncryptedAccessToken=SecretVaultService.Encrypt("telegram-secret-token"),
                EncryptedDestination=SecretVaultService.Encrypt("chat-private")
            }
        };
        store.Save(settings);

        var configuration=Assert.Single(new AgentNotificationConfigurationProvider(store).GetEnabled());
        var persisted=File.ReadAllText(path);

        Assert.Equal(NotificationChannel.Telegram,configuration.Channel);
        Assert.Equal("telegram-secret-token",configuration.AccessToken);
        Assert.DoesNotContain("telegram-secret-token",persisted,StringComparison.Ordinal);
        Assert.DoesNotContain("chat-private",persisted,StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationSave_RequiresConfirmationEncryptsAndThenCleansLegacy()
    {
        var path=Path.Combine(_directory,"notification-settings.json");
        var store=new AgentSettingsStore(path);
        var cleaner=new RecordingLegacyCleaner();
        var service=new NotificationConfigurationService(store,cleaner);
        var input=new NotificationConfigurationInput(
            true,new HashSet<NotificationEventKind>([NotificationEventKind.PositionOpened]),
            false,"22:00","07:00","UTC",
            new(true,"123456:telegram-secret","-100123",10,10),
            new(false,"","","","","en_US","v22.0",10,10));

        Assert.Throws<InvalidOperationException>(()=>
            service.SaveConfirmed(input,false,string.Empty));
        Assert.False(cleaner.Called);

        service.SaveConfirmed(input,true,"SAVE NOTIFICATION CONFIG");
        var persisted=File.ReadAllText(path);

        Assert.True(cleaner.Called);
        Assert.DoesNotContain("telegram-secret",persisted,StringComparison.Ordinal);
        Assert.DoesNotContain("-100123",persisted,StringComparison.Ordinal);
        Assert.Contains("PositionOpened",persisted,StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestEventService_RequiresSecondConfirmationAndUsesTestKind()
    {
        var store=Store();var service=new NotificationTestEventService(store,Config());
        var request=new NotificationTestRequest(true,"WRONG","binance-futures","Testnet",NotificationChannel.Telegram,"request-1");

        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.QueueAsync(request,default));
        await service.QueueAsync(request with{Confirmation="SEND TEST NOTIFICATION"},default);

        Assert.Equal(NotificationEventKind.Test,Assert.Single((await store.ReadProjectionAsync(ct:default)).Recent).Kind);
    }

    private NotificationOutboxStore Store()=>new(Path.Combine(_directory,Guid.NewGuid().ToString("N")+".db"),_clock);
    private StaticConfiguration Config()=>new([
        new(NotificationChannel.Telegram,true,"telegram-secret","chat-private",
            TimeoutSeconds:5,MaxRequestsPerMinute:20,
            AllowedEventKinds:new HashSet<NotificationEventKind>([NotificationEventKind.PositionOpened]))
    ]);
    private ConfirmedNotificationEvent Event(string key)=>new(
        key,NotificationEventKind.PositionOpened,"Testnet","binance-futures","SOLUSDT","Long",
        100m,1m,90m,120m,_clock.UtcNow,"WPE-TEST0001");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch{}
    }

    private sealed class MutableClock:INotificationClock{public DateTime UtcNow{get;set;}}
    private sealed class StaticConfiguration(IReadOnlyList<NotificationChannelConfiguration> values):INotificationConfigurationProvider
    {
        public IReadOnlyList<NotificationChannelConfiguration> GetEnabled()=>values;
    }
    private sealed class RecordingTransport(NotificationChannel channel):INotificationTransport
    {
        public NotificationChannel Channel=>channel;
        public Exception? Failure{get;init;}
        public int SendCount{get;private set;}
        public Task SendAsync(NotificationOutboundMessage message,NotificationChannelConfiguration configuration,CancellationToken ct)
        {
            SendCount++;
            return Failure is null?Task.CompletedTask:Task.FromException(Failure);
        }
    }
    private sealed class ThrowingPublisher:INotificationEventPublisher
    {
        public Task PublishAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)=>
            throw new IOException("outbox unavailable");
    }
    private sealed class RecordingPublisher:INotificationEventPublisher
    {
        public List<ConfirmedNotificationEvent> Events{get;}=[];
        public Task PublishAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)
        {
            Events.Add(notificationEvent);
            return Task.CompletedTask;
        }
    }
    private sealed class RecordingLegacyCleaner:ILegacyNotificationSecretCleaner
    {
        public bool Called{get;private set;}
        public void ClearAfterConfirmedMigration()=>Called=true;
    }
}
