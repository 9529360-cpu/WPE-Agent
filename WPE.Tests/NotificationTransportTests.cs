using System.Net;
using System.Text.Json;
using WpeAgent.Notifications;

namespace WPE.Tests;

public sealed class NotificationTransportTests
{
    [Fact]
    public async Task Telegram_posts_send_message_json_to_fixed_origin()
    {
        var handler = new RecordingHandler();
        var transport = new TelegramNotificationTransport(handler);

        await transport.SendAsync(Message("Execution confirmed"), Telegram(), CancellationToken.None);

        Assert.Equal(NotificationChannel.Telegram, transport.Channel);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.telegram.org/bot123456:fixture_token/sendMessage", handler.Uri?.AbsoluteUri);
        Assert.Null(handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("-100123456", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("Execution confirmed", body.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task WhatsApp_posts_only_versioned_template_payload_with_named_parameters()
    {
        var handler = new RecordingHandler();
        var transport = new WhatsAppCloudNotificationTransport(new HttpClient(handler));
        var message = Message(
            "This arbitrary text must not be sent",
            new Dictionary<string, string>
            {
                ["symbol"] = "BTCUSDT",
                ["status"] = "filled"
            });

        await transport.SendAsync(message, WhatsApp(), CancellationToken.None);

        Assert.Equal(NotificationChannel.WhatsApp, transport.Channel);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://graph.facebook.com/v22.0/109876543210/messages", handler.Uri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("whatsapp_fixture_token", handler.Authorization?.Parameter);
        Assert.DoesNotContain(message.Text, handler.Body, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("individual", root.GetProperty("recipient_type").GetString());
        Assert.Equal("15551234567", root.GetProperty("to").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        var template = root.GetProperty("template");
        Assert.Equal("order_update", template.GetProperty("name").GetString());
        Assert.Equal("en_US", template.GetProperty("language").GetProperty("code").GetString());
        var parameters = template.GetProperty("components")[0].GetProperty("parameters");
        Assert.Collection(
            parameters.EnumerateArray(),
            parameter => AssertParameter(parameter, "symbol", "BTCUSDT"),
            parameter => AssertParameter(parameter, "status", "filled"));
    }

    [Fact]
    public async Task WhatsApp_empty_parameters_does_not_invent_template_fields()
    {
        var handler = new RecordingHandler();
        var transport = new WhatsAppCloudNotificationTransport(handler);

        await transport.SendAsync(Message("not sent"), WhatsApp(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        var components = body.RootElement.GetProperty("template").GetProperty("components");
        Assert.Empty(components.EnumerateArray());
    }

    [Theory]
    [InlineData("123456:token/sendMessage")]
    [InlineData("123456:token?chat_id=attacker")]
    [InlineData("123456:token@evil.example")]
    public async Task Telegram_rejects_token_path_query_or_userinfo_injection(string token)
    {
        var handler = new RecordingHandler();
        var transport = new TelegramNotificationTransport(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendAsync(Message("text"), Telegram() with { AccessToken = token }, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("v22.0/123/messages")]
    [InlineData("https://evil.example/v22.0")]
    [InlineData("v22.0?access_token=secret")]
    [InlineData("v22.0@evil.example")]
    public async Task WhatsApp_rejects_non_version_api_path(string apiVersion)
    {
        var handler = new RecordingHandler();
        var transport = new WhatsAppCloudNotificationTransport(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendAsync(Message("text"), WhatsApp() with { ApiVersion = apiVersion }, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("109876543210/messages")]
    [InlineData("109876543210?access_token=secret")]
    [InlineData("user@evil.example")]
    public async Task WhatsApp_rejects_phone_id_path_injection(string phoneNumberId)
    {
        var handler = new RecordingHandler();
        var transport = new WhatsAppCloudNotificationTransport(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendAsync(Message("text"), WhatsApp() with { PhoneNumberId = phoneNumberId }, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var handler = new RecordingHandler(waitForCancellation: true);
        var transport = new TelegramNotificationTransport(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(Message("text"), Telegram(), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Configuration_timeout_cancels_handler_and_reports_timeout()
    {
        var handler = new RecordingHandler(waitForCancellation: true);
        var transport = new TelegramNotificationTransport(handler);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            transport.SendAsync(
                Message("text"),
                Telegram() with { TimeoutSeconds = 1 },
                CancellationToken.None));

        Assert.Equal("Telegram notification request timed out.", error.Message);
        Assert.True(handler.SawCancellation);
    }

    [Fact]
    public async Task Http_error_exposes_status_without_credentials()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var transport = new TelegramNotificationTransport(handler);
        var configuration = Telegram();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.SendAsync(Message("text"), configuration, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain(configuration.AccessToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configuration.Destination, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_error_is_sanitized_for_WhatsApp()
    {
        var configuration = WhatsApp();
        var handler = new RecordingHandler(error: new InvalidOperationException(
            $"leak {configuration.AccessToken} {configuration.PhoneNumberId} {configuration.Destination}"));
        var transport = new WhatsAppCloudNotificationTransport(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.SendAsync(Message("text"), configuration, CancellationToken.None));

        Assert.Equal("WhatsApp notification request failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(configuration.AccessToken, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configuration.PhoneNumberId!, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(configuration.Destination, error.ToString(), StringComparison.Ordinal);
    }

    private static NotificationOutboundMessage Message(
        string text,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new("event-1", NotificationEventKind.OrderFilled, text, parameters ?? new Dictionary<string, string>());

    private static NotificationChannelConfiguration Telegram() =>
        new(
            NotificationChannel.Telegram,
            true,
            "123456:fixture_token",
            "-100123456",
            TimeoutSeconds: 10);

    private static NotificationChannelConfiguration WhatsApp() =>
        new(
            NotificationChannel.WhatsApp,
            true,
            "whatsapp_fixture_token",
            "15551234567",
            PhoneNumberId: "109876543210",
            TemplateName: "order_update",
            LanguageCode: "en_US",
            ApiVersion: "v22.0",
            TimeoutSeconds: 10);

    private static void AssertParameter(JsonElement parameter, string name, string value)
    {
        Assert.Equal("text", parameter.GetProperty("type").GetString());
        Assert.Equal(name, parameter.GetProperty("parameter_name").GetString());
        Assert.Equal(value, parameter.GetProperty("text").GetString());
        Assert.Equal(3, parameter.EnumerateObject().Count());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly bool _waitForCancellation;
        private readonly Exception? _error;

        public RecordingHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            bool waitForCancellation = false,
            Exception? error = null)
        {
            _statusCode = statusCode;
            _waitForCancellation = waitForCancellation;
            _error = error;
        }

        public int CallCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }
        public bool SawCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_error is not null)
                throw _error;
            if (_waitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
