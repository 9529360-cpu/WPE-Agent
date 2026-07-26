using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpeAgent.Notifications;

public sealed class TelegramNotificationTransport : INotificationTransport
{
    private static readonly Uri ApiOrigin = new("https://api.telegram.org/");
    private static readonly Regex BotTokenPattern = new(
        "^[A-Za-z0-9_-]+:[A-Za-z0-9_-]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly HttpClient _http;

    public TelegramNotificationTransport() : this(new HttpClient()) { }

    public TelegramNotificationTransport(HttpMessageHandler handler)
        : this(new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), false)) { }

    public TelegramNotificationTransport(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public NotificationChannel Channel => NotificationChannel.Telegram;

    public async Task SendAsync(
        NotificationOutboundMessage message,
        NotificationChannelConfiguration configuration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateConfiguration(configuration);
        ct.ThrowIfCancellationRequested();

        var endpoint = new Uri(
            $"{ApiOrigin.AbsoluteUri}bot{configuration.AccessToken}/sendMessage",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                chat_id = configuration.Destination,
                text = message.Text
            })
        };

        await SendAsync(_http, request, configuration.TimeoutSeconds, "Telegram", ct)
            .ConfigureAwait(false);
    }

    private static void ValidateConfiguration(NotificationChannelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Channel != NotificationChannel.Telegram)
            throw new ArgumentException("The notification configuration is for a different channel.", nameof(configuration));
        if (!configuration.Enabled)
            throw new InvalidOperationException("The Telegram notification channel is disabled.");
        if (configuration.AccessToken.Length > 256 || !BotTokenPattern.IsMatch(configuration.AccessToken))
            throw new ArgumentException("The Telegram access token format is invalid.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.Destination) || configuration.Destination.Length > 64)
            throw new ArgumentException("The Telegram destination is invalid.", nameof(configuration));
        ValidateTimeout(configuration.TimeoutSeconds);
    }

    internal static async Task SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        int timeoutSeconds,
        string channel,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{channel} notification request timed out.");
        }
        catch (Exception)
        {
            throw new HttpRequestException($"{channel} notification request failed.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"{channel} notification request failed with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
        }
    }

    internal static void ValidateTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Notification timeout must be between 1 and 120 seconds.");
    }
}

public sealed class WhatsAppCloudNotificationTransport : INotificationTransport
{
    private static readonly Uri ApiOrigin = new("https://graph.facebook.com/");
    private static readonly Regex ApiVersionPattern = new(
        "^v[0-9]{1,2}\\.[0-9]{1,2}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex NumericIdPattern = new(
        "^[0-9]{6,32}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TemplateNamePattern = new(
        "^[a-z0-9_]{1,512}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex LanguagePattern = new(
        "^[a-z]{2,3}(?:_[A-Z]{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ParameterNamePattern = new(
        "^[a-z][a-z0-9_]{0,59}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly HttpClient _http;

    public WhatsAppCloudNotificationTransport() : this(new HttpClient()) { }

    public WhatsAppCloudNotificationTransport(HttpMessageHandler handler)
        : this(new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), false)) { }

    public WhatsAppCloudNotificationTransport(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public NotificationChannel Channel => NotificationChannel.WhatsApp;

    public async Task SendAsync(
        NotificationOutboundMessage message,
        NotificationChannelConfiguration configuration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateConfiguration(configuration);
        ValidateTemplateParameters(message.TemplateParameters);
        ct.ThrowIfCancellationRequested();

        var endpoint = new Uri(
            ApiOrigin,
            $"{configuration.ApiVersion}/{configuration.PhoneNumberId}/messages");
        var template = BuildTemplate(message, configuration);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = configuration.Destination,
                type = "template",
                template
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.AccessToken);

        await TelegramNotificationTransport.SendAsync(
                _http,
                request,
                configuration.TimeoutSeconds,
                "WhatsApp",
                ct)
            .ConfigureAwait(false);
    }

    private static object BuildTemplate(
        NotificationOutboundMessage message,
        NotificationChannelConfiguration configuration)
    {
        var parameters = message.TemplateParameters
            .Select(pair => new
            {
                type = "text",
                parameter_name = pair.Key,
                text = pair.Value
            })
            .ToArray();

        return parameters.Length == 0
            ? new
            {
                name = configuration.TemplateName!,
                language = new { code = configuration.LanguageCode! },
                components = Array.Empty<object>()
            }
            : new
            {
                name = configuration.TemplateName!,
                language = new { code = configuration.LanguageCode! },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters
                    }
                }
            };
    }

    private static void ValidateConfiguration(NotificationChannelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Channel != NotificationChannel.WhatsApp)
            throw new ArgumentException("The notification configuration is for a different channel.", nameof(configuration));
        if (!configuration.Enabled)
            throw new InvalidOperationException("The WhatsApp notification channel is disabled.");
        if (string.IsNullOrWhiteSpace(configuration.AccessToken) ||
            configuration.AccessToken.Any(char.IsWhiteSpace) ||
            configuration.AccessToken.Length > 4096)
            throw new ArgumentException("The WhatsApp access token format is invalid.", nameof(configuration));
        if (!NumericIdPattern.IsMatch(configuration.Destination))
            throw new ArgumentException("The WhatsApp destination is invalid.", nameof(configuration));
        if (configuration.PhoneNumberId is null || !NumericIdPattern.IsMatch(configuration.PhoneNumberId))
            throw new ArgumentException("The WhatsApp phone number ID is invalid.", nameof(configuration));
        if (configuration.TemplateName is null || !TemplateNamePattern.IsMatch(configuration.TemplateName))
            throw new ArgumentException("The WhatsApp template name is invalid.", nameof(configuration));
        if (configuration.LanguageCode is null || !LanguagePattern.IsMatch(configuration.LanguageCode))
            throw new ArgumentException("The WhatsApp language code is invalid.", nameof(configuration));
        if (!ApiVersionPattern.IsMatch(configuration.ApiVersion))
            throw new ArgumentException("The WhatsApp API version is invalid.", nameof(configuration));
        TelegramNotificationTransport.ValidateTimeout(configuration.TimeoutSeconds);
    }

    private static void ValidateTemplateParameters(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var (name, value) in parameters)
        {
            if (!ParameterNamePattern.IsMatch(name))
                throw new ArgumentException("A WhatsApp template parameter name is invalid.", nameof(parameters));
            if (value is null || value.Length > 1024)
                throw new ArgumentException("A WhatsApp template parameter value is invalid.", nameof(parameters));
        }
    }
}
