using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services;

namespace WpeAgent.Notifications;

public sealed class AgentNotificationConfigurationProvider(
    AgentSettingsStore? store=null):INotificationConfigurationProvider
{
    private readonly AgentSettingsStore _store=store??new AgentSettingsStore();

    public IReadOnlyList<NotificationChannelConfiguration> GetEnabled()
    {
        try
        {
            var slot=_store.Load().Notification;
            if(!slot.Enabled)return [];
            var result=new List<NotificationChannelConfiguration>();
            var allowed=slot.EventKinds
                .Select(x=>Enum.TryParse<NotificationEventKind>(x,true,out var kind)?kind:(NotificationEventKind?)null)
                .OfType<NotificationEventKind>()
                .ToHashSet();
            var quietStart=TimeOnly.TryParse(slot.QuietHoursStart,out var start)?start:(TimeOnly?)null;
            var quietEnd=TimeOnly.TryParse(slot.QuietHoursEnd,out var end)?end:(TimeOnly?)null;
            if(slot.Telegram.Enabled)
            {
                var token=SecretVaultService.Decrypt(slot.Telegram.EncryptedAccessToken);
                var chat=SecretVaultService.Decrypt(slot.Telegram.EncryptedDestination);
                if(!string.IsNullOrWhiteSpace(token)&&!string.IsNullOrWhiteSpace(chat))
                    result.Add(new(NotificationChannel.Telegram,true,token,chat,
                        TimeoutSeconds:slot.Telegram.TimeoutSeconds,
                        MaxRequestsPerMinute:slot.Telegram.MaxRequestsPerMinute,
                        AllowedEventKinds:allowed,QuietHoursEnabled:slot.QuietHoursEnabled,
                        QuietHoursStart:quietStart,QuietHoursEnd:quietEnd,
                        QuietHoursTimeZone:slot.QuietHoursTimeZone));
            }
            if(slot.WhatsApp.Enabled)
            {
                var token=SecretVaultService.Decrypt(slot.WhatsApp.EncryptedAccessToken);
                var phone=SecretVaultService.Decrypt(slot.WhatsApp.EncryptedPhoneNumberId);
                var recipient=SecretVaultService.Decrypt(slot.WhatsApp.EncryptedDestination);
                if(!string.IsNullOrWhiteSpace(token)&&!string.IsNullOrWhiteSpace(phone)&&
                   !string.IsNullOrWhiteSpace(recipient)&&!string.IsNullOrWhiteSpace(slot.WhatsApp.TemplateName))
                    result.Add(new(NotificationChannel.WhatsApp,true,token,recipient,phone,
                        slot.WhatsApp.TemplateName,slot.WhatsApp.LanguageCode,slot.WhatsApp.ApiVersion,
                        slot.WhatsApp.TimeoutSeconds,slot.WhatsApp.MaxRequestsPerMinute,
                        allowed,slot.QuietHoursEnabled,quietStart,quietEnd,slot.QuietHoursTimeZone));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }
}
