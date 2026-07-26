using System.Text.Json;
using WpeAgent.Notifications;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class NotificationUiSecurityTests:IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-notification-ui-"+Guid.NewGuid().ToString("N"));
    private readonly Clock _clock=new(){UtcNow=new DateTime(2026,7,21,0,0,0,DateTimeKind.Utc)};
    public NotificationUiSecurityTests()=>Directory.CreateDirectory(_dir);

    [Fact]
    public async Task RuntimeProjection_ContainsNoSecretOrRoutingIdentity_AndCorruptDpapiFailsClosed()
    {
        const string token="123456:super-secret-token",destination="-100998877";var settingsPath=Path.Combine(_dir,"settings.json");var settingsStore=new AgentSettingsStore(settingsPath);var settings=settingsStore.Load();settings.Notification=new(){Enabled=true,Telegram=new(){Enabled=true,EncryptedAccessToken=SecretVaultService.Encrypt(token),EncryptedDestination=SecretVaultService.Encrypt(destination)}};settingsStore.Save(settings);
        var runtime=new RuntimeNotificationStateStore(settingsStore,new NotificationOutboxStore(Path.Combine(_dir,"outbox.db"),_clock));await runtime.RefreshAsync(default);var json=JsonSerializer.Serialize(runtime.Read());
        Assert.DoesNotContain(token,json,StringComparison.Ordinal);Assert.DoesNotContain(destination,json,StringComparison.Ordinal);Assert.DoesNotContain("EventKey",json,StringComparison.OrdinalIgnoreCase);Assert.True(runtime.Read().Status!.TelegramStored);Assert.True(runtime.Read().Status!.TelegramReady);
        settings=settingsStore.Load();settings.Notification.Telegram.EncryptedAccessToken="corrupt-dpapi";settingsStore.Save(settings);await runtime.RefreshAsync(default);Assert.Equal(WpeAgent.RuntimeContracts.RuntimeCollectionState.Error,runtime.Read().State);Assert.Null(runtime.Read().Status);
    }

    [Fact]
    public async Task TestSend_BypassesBusinessFilters_IsSelectedChannelOnly_AndRequestIdIsIdempotent()
    {
        var store=new NotificationOutboxStore(Path.Combine(_dir,"test.db"),_clock);var config=new StaticConfig([
            new(NotificationChannel.Telegram,true,"token","chat",AllowedEventKinds:new HashSet<NotificationEventKind>([NotificationEventKind.PositionOpened]),QuietHoursEnabled:true,QuietHoursStart:new(0,0),QuietHoursEnd:new(23,59)),
            new(NotificationChannel.WhatsApp,true,"token","1234567890","1234567","template","en_US")]);
        var service=new NotificationTestEventService(store,config);var request=new NotificationTestRequest(true,"SEND TEST NOTIFICATION","wpe-ui","Testnet",NotificationChannel.Telegram,"same-request");
        Assert.True(await service.QueueAsync(request,default));Assert.False(await service.QueueAsync(request,default));var projection=await store.ReadProjectionAsync(ct:default);var row=Assert.Single(projection.Recent);Assert.Equal(NotificationChannel.Telegram,row.Channel);Assert.Equal(NotificationEventKind.Test,row.Kind);
    }

    [Fact]
    public async Task ProjectionIsBounded_AndRetentionDeletesOnlyExpiredTerminalRows()
    {
        var store=new NotificationOutboxStore(Path.Combine(_dir,"retention.db"),_clock);
        for(var i=0;i<25;i++)await store.EnqueueAsync(new(Event("event-"+i),NotificationChannel.Telegram),default);
        var claimed=await store.ClaimDueAsync(1,TimeSpan.FromMinutes(1),default);await store.MarkSentAsync(Assert.Single(claimed).Id,default);
        var first=await store.ReadProjectionAsync(7,5,default);Assert.Equal(7,first.Recent.Count);Assert.Equal(24,first.Summary.PendingCount);Assert.Equal(1,first.Summary.SentCount);
        _clock.UtcNow=_clock.UtcNow.AddDays(31);Assert.Equal(1,await store.PurgeTerminalAsync(TimeSpan.FromDays(30),TimeSpan.FromDays(90),default));var after=await store.ReadProjectionAsync(ct:default);Assert.Equal(24,after.Summary.PendingCount);Assert.Equal(0,after.Summary.SentCount);
    }

    [Fact]
    public void LegacyCleanupFailure_PersistsPendingDiagnosticWhileNewEncryptedConfigurationRemainsUsable()
    {
        var path=Path.Combine(_dir,"settings-failure.json");var store=new AgentSettingsStore(path);var service=new NotificationConfigurationService(store,new FailingCleaner());var input=new NotificationConfigurationInput(true,new HashSet<NotificationEventKind>([NotificationEventKind.Test]),false,"22:00","07:00","UTC",new(true,"123456:secret-value","private-chat"),new(false,"","","",""));
        Assert.Throws<IOException>(()=>service.SaveConfirmed(input,true,"SAVE NOTIFICATION CONFIG"));var text=File.ReadAllText(path);Assert.DoesNotContain("secret-value",text,StringComparison.Ordinal);Assert.DoesNotContain("private-chat",text,StringComparison.Ordinal);var saved=store.Load().Notification;Assert.False(string.IsNullOrWhiteSpace(saved.Telegram.EncryptedAccessToken));Assert.True(saved.LegacyMigrationPending);Assert.StartsWith("WPE-",saved.LegacyMigrationDiagnosticCode);
    }

    [Fact]
    public void EncryptionFailure_LeavesExistingConfigurationAndLegacyFixtureUnchanged()
    {
        var settingsPath=Path.Combine(_dir,"encrypt-failure.json");var legacyPath=Path.Combine(_dir,"legacy-encrypt-failure.json");File.WriteAllText(legacyPath,"{\"TelegramBotToken\":\"legacy-token\"}");var store=new AgentSettingsStore(settingsPath);var before=File.Exists(settingsPath)?File.ReadAllText(settingsPath):null;var cleaner=new CountingCleaner();var service=new NotificationConfigurationService(store,cleaner,new FailingProtector());var input=new NotificationConfigurationInput(true,new HashSet<NotificationEventKind>([NotificationEventKind.Test]),false,"22:00","07:00","UTC",new(true,"new-token","new-chat"),new(false,"","","",""));
        Assert.Throws<InvalidOperationException>(()=>service.SaveConfirmed(input,true,"SAVE NOTIFICATION CONFIG"));Assert.Equal(before,File.Exists(settingsPath)?File.ReadAllText(settingsPath):null);Assert.Contains("legacy-token",File.ReadAllText(legacyPath));Assert.Equal(0,cleaner.Calls);
    }

    [Fact]
    public void SuccessfulLegacyCleanup_RemovesOnlyKnownSecretPropertiesFromFixture()
    {
        var legacyPath=Path.Combine(_dir,"legacy-success.json");File.WriteAllText(legacyPath,"{\"TelegramBotToken\":\"legacy-token\",\"TelegramChatId\":\"legacy-chat\",\"Keep\":\"value\"}");new LegacyNotificationSecretCleaner(legacyPath).ClearAfterConfirmedMigration();var text=File.ReadAllText(legacyPath);Assert.DoesNotContain("TelegramBotToken",text);Assert.DoesNotContain("legacy-token",text);Assert.DoesNotContain("TelegramChatId",text);Assert.Contains("\"Keep\"",text);Assert.Contains("value",text);
    }

    private ConfirmedNotificationEvent Event(string key)=>ConfirmedNotificationTruth.System(key,NotificationEventKind.Test,"wpe-ui","Testnet",_clock.UtcNow,"WPE-TEST");
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch{}}
    private sealed class Clock:INotificationClock{public DateTime UtcNow{get;set;}}
    private sealed class StaticConfig(IReadOnlyList<NotificationChannelConfiguration> values):INotificationConfigurationProvider{public IReadOnlyList<NotificationChannelConfiguration> GetEnabled()=>values;}
    private sealed class FailingCleaner:ILegacyNotificationSecretCleaner{public void ClearAfterConfirmedMigration()=>throw new IOException("legacy file locked");}
    private sealed class CountingCleaner:ILegacyNotificationSecretCleaner{public int Calls{get;private set;}public void ClearAfterConfirmedMigration()=>Calls++;}
    private sealed class FailingProtector:INotificationSecretProtector{public string Protect(string value)=>throw new InvalidOperationException("DPAPI unavailable");}
}
