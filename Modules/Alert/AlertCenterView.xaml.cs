using System;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Alert;

public partial class AlertCenterView : UserControl
{
    private readonly NotificationService _notification = ServiceLocator.Notification;
    private readonly AppSettings _settings = ServiceLocator.Settings;

    public AlertCenterView()
    {
        InitializeComponent();
        WebhookMessageBox.Text = "Risk warning from Binance terminal";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TelegramTokenBox.Text = _settings.TelegramBotToken ?? string.Empty;
        TelegramChatBox.Text = _settings.TelegramChatId ?? string.Empty;
        WebhookBox.Text = _settings.DingTalkWebhook ?? string.Empty;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private async void SendTelegram_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = TelegramTokenBox.Text.Trim();
            var chat = TelegramChatBox.Text.Trim();
            await _notification.SendTelegramAsync(token, chat, "Binance 终端测试通知");
            _settings.TelegramBotToken = token;
            _settings.TelegramChatId = chat;
            SaveSettings();
            StatusText.Text = "状态：Telegram 消息已发送";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：Telegram 发送失败";
            MessageBox.Show(ex.Message, "Telegram", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SendWebhook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var webhook = WebhookBox.Text.Trim();
            var message = string.IsNullOrWhiteSpace(WebhookMessageBox.Text) ? "测试消息" : WebhookMessageBox.Text;
            await _notification.SendDingTalkAsync(webhook, message);
            _settings.DingTalkWebhook = webhook;
            SaveSettings();
            StatusText.Text = "状态：Webhook 消息已发送";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：Webhook 发送失败";
            MessageBox.Show(ex.Message, "Webhook", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSettings()
    {
        AppSettingsService.Save();
    }
}
