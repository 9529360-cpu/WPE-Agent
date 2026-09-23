namespace WpeAgent.Notifications;

public enum NotificationChannel{Telegram,WhatsApp}
public enum NotificationEventKind
{
    OrderFilled,
    PositionOpened,
    PositionClosed,
    ProtectionPlaced,
    ProtectionUpdated,
    ProtectionFailed,
    RiskBlocked,
    AgentDegraded,
    Test
}
public enum NotificationOutboxState{Queued,Sending,Sent,Failed,DeadLetter}
public enum NotificationUiState{Pending,Retrying,Sent,DeadLetter}
public sealed record NotificationOutboxSummary(int PendingCount,int RetryingCount,int SentCount,int DeadLetterCount);
public sealed record NotificationOutboxSafeRow(long Id,NotificationChannel Channel,NotificationEventKind Kind,NotificationUiState UiState,bool InFlight,int Attempts,int MaxAttempts,DateTime OccurredAtUtc,DateTime NextAttemptAtUtc,DateTime UpdatedAtUtc,string? DiagnosticCode);
public sealed record NotificationOutboxProjection(NotificationOutboxSummary Summary,IReadOnlyList<NotificationOutboxSafeRow> Recent);

public sealed record ConfirmedNotificationEvent(
    string EventKey,
    NotificationEventKind Kind,
    string Environment,
    string Provider,
    string? Symbol,
    string? Side,
    decimal? FilledPrice,
    decimal? Quantity,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTime OccurredAtUtc,
    string DiagnosticCode,
    string? Content=null);

public sealed record NotificationEnvelope(
    ConfirmedNotificationEvent Event,
    NotificationChannel Channel);

public sealed record NotificationOutboundMessage(
    string EventKey,
    NotificationEventKind Kind,
    string Text,
    IReadOnlyDictionary<string,string> TemplateParameters);

public sealed record NotificationChannelConfiguration(
    NotificationChannel Channel,
    bool Enabled,
    string AccessToken,
    string Destination,
    string? PhoneNumberId=null,
    string? TemplateName=null,
    string? LanguageCode=null,
    string ApiVersion="v22.0",
    int TimeoutSeconds=15,
    int MaxRequestsPerMinute=20,
    IReadOnlySet<NotificationEventKind>? AllowedEventKinds=null,
    bool QuietHoursEnabled=false,
    TimeOnly? QuietHoursStart=null,
    TimeOnly? QuietHoursEnd=null,
    string QuietHoursTimeZone="UTC");

public sealed record NotificationOutboxItem(
    long Id,
    string EventKey,
    NotificationChannel Channel,
    ConfirmedNotificationEvent Event,
    NotificationOutboxState State,
    int Attempts,
    DateTime NextAttemptAtUtc,
    string? LastDiagnosticCode,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public interface INotificationTransport
{
    NotificationChannel Channel{get;}
    Task SendAsync(
        NotificationOutboundMessage message,
        NotificationChannelConfiguration configuration,
        CancellationToken ct);
}

public interface INotificationEventPublisher
{
    Task PublishAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct);
}

public interface INotificationConfigurationProvider
{
    IReadOnlyList<NotificationChannelConfiguration> GetEnabled();
}

public interface INotificationClock
{
    DateTime UtcNow{get;}
}

public sealed class SystemNotificationClock:INotificationClock
{
    public static SystemNotificationClock Instance{get;}=new();
    public DateTime UtcNow=>DateTime.UtcNow;
}
