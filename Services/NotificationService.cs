using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Services;

public class NotificationService
{
    private const string DingTalkWebhookHost = "oapi.dingtalk.com";
    private const string DingTalkWebhookPath = "/robot/send";
    private readonly HttpClient _httpClient = new();

    public async Task SendTelegramAsync(string botToken, string chatId, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            throw new ArgumentException("未配置 Telegram Bot Token 或 ChatId");

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = message, parse_mode = "Markdown" });
        try{await PostJsonAsync(url,payload,cancellationToken).ConfigureAwait(false);}
        catch(Exception ex)when(ex is not OperationCanceledException){throw new InvalidOperationException("Telegram delivery failed: "+SensitiveDataRedactor.ForLog(ex.Message,180,botToken,chatId));}
    }

    public async Task SendDingTalkAsync(string webhook, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhook))
            throw new ArgumentException("未配置钉钉 Webhook 地址");
        if (!IsAllowedDingTalkWebhook(webhook))
            throw new ArgumentException("钉钉 Webhook 地址无效或不是官方自定义机器人地址", nameof(webhook));

        var payload = JsonSerializer.Serialize(new { msgtype = "text", text = new { content = message } });
        try{await PostJsonAsync(webhook,payload,cancellationToken).ConfigureAwait(false);}
        catch(Exception ex)when(ex is not OperationCanceledException){throw new InvalidOperationException("DingTalk delivery failed: "+SensitiveDataRedactor.ForLog(ex.Message,180,webhook));}
    }

    internal static bool IsAllowedDingTalkWebhook(string webhook)
    {
        if (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.Host, DingTalkWebhookHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, DingTalkWebhookPath, StringComparison.Ordinal)) return false;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 &&
                string.Equals(pair[0], "access_token", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair[1])) return true;
        }
        return false;
    }

    private async Task PostJsonAsync(string url, string payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
