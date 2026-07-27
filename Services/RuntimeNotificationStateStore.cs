using WpeAgent.Notifications;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services;

namespace WpeAgent.RuntimeServices;

public sealed record RuntimeNotificationState(RuntimeCollectionState State,RuntimeNotificationStatusV1? Status,IReadOnlyList<RuntimeNotificationOutboxRowV1> Recent,IReadOnlyList<RuntimeTelegramSubscriberV1> Subscribers,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeNotificationState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,null,[],[],null,message);
    public static RuntimeNotificationState Error(string message)=>new(RuntimeCollectionState.Error,null,[],[],DateTimeOffset.UtcNow,message);
}

public sealed class RuntimeNotificationStateStore(AgentSettingsStore? settingsStore=null,NotificationOutboxStore? outboxStore=null,TelegramSubscriberStore? subscriberStore=null)
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);
    private readonly AgentSettingsStore _settings=settingsStore??new AgentSettingsStore();
    private readonly NotificationOutboxStore _outbox=outboxStore??new NotificationOutboxStore();
    private readonly TelegramSubscriberStore _subscribers=subscriberStore??new TelegramSubscriberStore();
    private readonly object _gate=new();
    private RuntimeNotificationState _current=RuntimeNotificationState.Unsupported("Notification state has not been read.");

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var configured=_settings.Load().Notification;
            var telegramStored=HasTelegram(configured);var whatsAppStored=HasWhatsApp(configured);
            var telegramReady=configured.Enabled&&configured.Telegram.Enabled&&telegramStored&&CanDecrypt(configured.Telegram.EncryptedAccessToken,configured.Telegram.EncryptedDestination);
            var whatsAppReady=configured.Enabled&&configured.WhatsApp.Enabled&&whatsAppStored&&CanDecrypt(configured.WhatsApp.EncryptedAccessToken,configured.WhatsApp.EncryptedDestination,configured.WhatsApp.EncryptedPhoneNumberId);
            if((configured.Telegram.Enabled&&telegramStored&&!telegramReady)||(configured.WhatsApp.Enabled&&whatsAppStored&&!whatsAppReady))throw new InvalidOperationException("Stored notification credentials are unavailable.");
            var projection=await _outbox.ReadProjectionAsync(20,5,ct);
            await _outbox.PurgeTerminalAsync(TimeSpan.FromDays(30),TimeSpan.FromDays(90),500,ct);
            var s=projection.Summary;
            var status=new RuntimeNotificationStatusV1(configured.Enabled,telegramStored,telegramReady,whatsAppStored,whatsAppReady,configured.LegacyMigrationPending,configured.LegacyMigrationDiagnosticCode,configured.EventKinds.ToArray(),configured.QuietHoursEnabled,configured.QuietHoursStart,configured.QuietHoursEnd,configured.QuietHoursTimeZone,s.PendingCount,s.RetryingCount,s.SentCount,s.DeadLetterCount);
            var rows=projection.Recent.Select(x=>new RuntimeNotificationOutboxRowV1(x.Id,x.Channel.ToString(),x.Kind.ToString(),x.UiState.ToString(),x.InFlight,x.Attempts,x.MaxAttempts,x.OccurredAtUtc,x.NextAttemptAtUtc,x.UpdatedAtUtc,x.DiagnosticCode)).ToArray();
            var subscribers=(await _subscribers.ListAsync(ct)).Select(x=>new RuntimeTelegramSubscriberV1("tg#"+x.SubscriberKey[..12].ToLowerInvariant(),x.ChatType,x.State.ToString(),x.AllowedEventKinds.Select(k=>k.ToString()).OrderBy(k=>k,StringComparer.Ordinal).ToArray(),x.FirstSeenAtUtc,x.UpdatedAtUtc)).ToArray();
            lock(_gate)_current=new(RuntimeCollectionState.Available,status,rows,subscribers,DateTimeOffset.UtcNow,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(_gate)_current=RuntimeNotificationState.Error(UiDiagnostic.Format(ex,"Notification state read failed."));}
    }
    public RuntimeNotificationState Read(){lock(_gate)return _current;}
    private static bool HasTelegram(NotificationSlot x)=>!string.IsNullOrWhiteSpace(x.Telegram.EncryptedAccessToken)&&!string.IsNullOrWhiteSpace(x.Telegram.EncryptedDestination);
    private static bool HasWhatsApp(NotificationSlot x)=>!string.IsNullOrWhiteSpace(x.WhatsApp.EncryptedAccessToken)&&!string.IsNullOrWhiteSpace(x.WhatsApp.EncryptedDestination)&&!string.IsNullOrWhiteSpace(x.WhatsApp.EncryptedPhoneNumberId)&&!string.IsNullOrWhiteSpace(x.WhatsApp.TemplateName);
    private static bool CanDecrypt(params string[] values){try{return values.All(x=>!string.IsNullOrWhiteSpace(SecretVaultService.Decrypt(x)));}catch{return false;}}
}
