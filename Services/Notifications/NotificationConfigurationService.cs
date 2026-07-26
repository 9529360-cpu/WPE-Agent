using WpeAgent.Notifications;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using System.Text.Json.Nodes;
using System.Text;
using System.IO;

namespace WpeAgent.Notifications;

public sealed record TelegramNotificationInput(
    bool Enabled,string AccessToken,string Destination,int TimeoutSeconds=15,int MaxRequestsPerMinute=20);
public sealed record WhatsAppNotificationInput(
    bool Enabled,string AccessToken,string Destination,string PhoneNumberId,
    string TemplateName,string LanguageCode="en_US",string ApiVersion="v22.0",
    int TimeoutSeconds=15,int MaxRequestsPerMinute=20);
public sealed record NotificationConfigurationInput(
    bool Enabled,IReadOnlySet<NotificationEventKind> EventKinds,
    bool QuietHoursEnabled,string QuietHoursStart,string QuietHoursEnd,string QuietHoursTimeZone,
    TelegramNotificationInput Telegram,WhatsAppNotificationInput WhatsApp);

public interface ILegacyNotificationSecretCleaner{void ClearAfterConfirmedMigration();}
public interface INotificationSecretProtector{string Protect(string value);}
public sealed class DpapiNotificationSecretProtector:INotificationSecretProtector{public string Protect(string value)=>SecretVaultService.Encrypt(value);}
public sealed class LegacyNotificationSecretCleaner(string? path=null):ILegacyNotificationSecretCleaner
{
    private readonly string _path=path??AppDataPaths.File("appsettings.json");
    public void ClearAfterConfirmedMigration()
    {
        var path=_path;if(!File.Exists(path))return;
        var root=JsonNode.Parse(File.ReadAllText(path)) as JsonObject??throw new InvalidOperationException("Legacy settings document is invalid.");
        var changed=Remove(root,"TelegramBotToken","TelegramChatId","WhatsAppAccessToken","WhatsAppDestination","WhatsAppPhoneNumberId");
        if(root["Notification"] is JsonObject notification)changed|=Remove(notification,"TelegramBotToken","TelegramChatId","WhatsAppAccessToken","WhatsAppDestination","WhatsAppPhoneNumberId");
        if(!changed)return;
        var temp=Path.Combine(Path.GetDirectoryName(path)!,$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try{File.WriteAllText(temp,root.ToJsonString(new(){WriteIndented=true}),new UTF8Encoding(false));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static bool Remove(JsonObject value,params string[] names){var changed=false;foreach(var name in names)changed|=value.Remove(name);return changed;}
}

public sealed class NotificationConfigurationService(
    AgentSettingsStore? store=null,
    ILegacyNotificationSecretCleaner? legacyCleaner=null,
    INotificationSecretProtector? protector=null)
{
    private readonly AgentSettingsStore _store=store??new AgentSettingsStore();
    private readonly ILegacyNotificationSecretCleaner _legacyCleaner=legacyCleaner??new LegacyNotificationSecretCleaner();
    private readonly INotificationSecretProtector _protector=protector??new DpapiNotificationSecretProtector();

    public void SaveConfirmed(
        NotificationConfigurationInput input,bool userConfirmed,string confirmation)
    {
        if(!userConfirmed||!string.Equals(confirmation,"SAVE NOTIFICATION CONFIG",StringComparison.Ordinal))
            throw new InvalidOperationException("Notification configuration requires explicit user confirmation.");
        var settings=_store.Load();
        Validate(input,settings.Notification);
        var previous=settings.Notification;
        settings.Notification=new()
        {
            Enabled=input.Enabled,
            EventKinds=input.EventKinds.Select(x=>x.ToString()).Distinct(StringComparer.Ordinal).ToList(),
            QuietHoursEnabled=input.QuietHoursEnabled,
            QuietHoursStart=input.QuietHoursStart,
            QuietHoursEnd=input.QuietHoursEnd,
            QuietHoursTimeZone=input.QuietHoursTimeZone,
            Telegram=new()
            {
                Enabled=input.Telegram.Enabled,
                EncryptedAccessToken=PreserveOrEncrypt(previous.Telegram.EncryptedAccessToken,input.Telegram.AccessToken),
                EncryptedDestination=PreserveOrEncrypt(previous.Telegram.EncryptedDestination,input.Telegram.Destination),
                TimeoutSeconds=input.Telegram.TimeoutSeconds,
                MaxRequestsPerMinute=input.Telegram.MaxRequestsPerMinute
            },
            WhatsApp=new()
            {
                Enabled=input.WhatsApp.Enabled,
                EncryptedAccessToken=PreserveOrEncrypt(previous.WhatsApp.EncryptedAccessToken,input.WhatsApp.AccessToken),
                EncryptedDestination=PreserveOrEncrypt(previous.WhatsApp.EncryptedDestination,input.WhatsApp.Destination),
                EncryptedPhoneNumberId=PreserveOrEncrypt(previous.WhatsApp.EncryptedPhoneNumberId,input.WhatsApp.PhoneNumberId),
                TemplateName=input.WhatsApp.TemplateName,
                LanguageCode=input.WhatsApp.LanguageCode,
                ApiVersion=input.WhatsApp.ApiVersion,
                TimeoutSeconds=input.WhatsApp.TimeoutSeconds,
                MaxRequestsPerMinute=input.WhatsApp.MaxRequestsPerMinute
            }
        };
        _store.Save(settings);
        try
        {
            _legacyCleaner.ClearAfterConfirmedMigration();settings.Notification.LegacyMigrationPending=false;settings.Notification.LegacyMigrationDiagnosticCode=null;_store.Save(settings);
        }
        catch(Exception ex)
        {
            settings.Notification.LegacyMigrationPending=true;settings.Notification.LegacyMigrationDiagnosticCode=UiDiagnostic.FromText(ex.ToString(),"Legacy notification cleanup pending.").Code;_store.Save(settings);throw;
        }
    }

    public void ClearChannelConfirmed(NotificationChannel channel,bool userConfirmed,string confirmation)
    {
        if(!userConfirmed||!string.Equals(confirmation,$"CLEAR {channel.ToString().ToUpperInvariant()} NOTIFICATION",StringComparison.Ordinal))
            throw new InvalidOperationException("Clearing notification credentials requires explicit user confirmation.");
        var settings=_store.Load();
        if(channel==NotificationChannel.Telegram)settings.Notification.Telegram=new();
        else settings.Notification.WhatsApp=new();
        settings.Notification.Enabled=settings.Notification.Telegram.Enabled||settings.Notification.WhatsApp.Enabled;
        _store.Save(settings);
    }

    private static void Validate(NotificationConfigurationInput input,NotificationSlot previous)
    {
        if(input.EventKinds.Any(x=>!Enum.IsDefined(x)))throw new InvalidOperationException("Notification event allowlist is invalid.");
        if(input.QuietHoursEnabled)
        {
            if(!TimeOnly.TryParse(input.QuietHoursStart,out _)||!TimeOnly.TryParse(input.QuietHoursEnd,out _))
                throw new InvalidOperationException("Notification quiet hours are invalid.");
            try{_=TimeZoneInfo.FindSystemTimeZoneById(input.QuietHoursTimeZone);}
            catch{throw new InvalidOperationException("Notification quiet-hours timezone is invalid.");}
        }
        ValidateLimits(input.Telegram.TimeoutSeconds,input.Telegram.MaxRequestsPerMinute);
        ValidateLimits(input.WhatsApp.TimeoutSeconds,input.WhatsApp.MaxRequestsPerMinute);
        if(input.Telegram.Enabled&&(!HasSecret(input.Telegram.AccessToken,previous.Telegram.EncryptedAccessToken)||!HasSecret(input.Telegram.Destination,previous.Telegram.EncryptedDestination)))
            throw new InvalidOperationException("Telegram credentials are incomplete.");
        if(input.WhatsApp.Enabled&&(!HasSecret(input.WhatsApp.AccessToken,previous.WhatsApp.EncryptedAccessToken)||
            !HasSecret(input.WhatsApp.Destination,previous.WhatsApp.EncryptedDestination)||!HasSecret(input.WhatsApp.PhoneNumberId,previous.WhatsApp.EncryptedPhoneNumberId)||
            string.IsNullOrWhiteSpace(input.WhatsApp.TemplateName)))
            throw new InvalidOperationException("WhatsApp Cloud API template configuration is incomplete.");
        if(input.WhatsApp.Enabled&&!System.Text.RegularExpressions.Regex.IsMatch(input.WhatsApp.ApiVersion,"^v[0-9]{1,2}\\.[0-9]{1,2}$"))
            throw new InvalidOperationException("WhatsApp Cloud API version is invalid.");
    }
    private static void ValidateLimits(int timeout,int rate)
    {
        if(timeout is<1 or>60||rate is<1 or>600)throw new InvalidOperationException("Notification timeout or rate limit is invalid.");
    }
    private static bool HasSecret(string value,string encrypted)=>!string.IsNullOrWhiteSpace(value)||!string.IsNullOrWhiteSpace(encrypted);
    private string PreserveOrEncrypt(string encrypted,string value)=>string.IsNullOrWhiteSpace(value)?encrypted:_protector.Protect(value.Trim());
}
