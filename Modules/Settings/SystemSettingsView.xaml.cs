using System;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Settings;

public partial class SystemSettingsView : UserControl
{
    public SystemSettingsView()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = ServiceLocator.Settings;
        ReconnectBox.IsChecked = settings.AutoReconnect;
        NotificationBox.IsChecked = settings.EnableNotifications;
        RefreshBox.Text = settings.RefreshIntervalSeconds.ToString();
        EnvironmentBox.SelectedIndex = settings.Environment switch
        {
            "Staging" => 1,
            "Development" => 2,
            _ => 0
        };
        LogLevelBox.SelectedIndex = settings.LogLevel switch
        {
            "Trace" => 0,
            "Debug" => 1,
            "Warning" => 3,
            "Error" => 4,
            _ => 2
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ServiceLocator.Settings;
            settings.AutoReconnect = ReconnectBox.IsChecked == true;
            settings.EnableNotifications = NotificationBox.IsChecked == true;
            settings.Environment = (EnvironmentBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Production";
            settings.LogLevel = (LogLevelBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Info";
            settings.RefreshIntervalSeconds = int.TryParse(RefreshBox.Text, out var refresh) ? Math.Max(1, refresh) : 5;
            AppSettingsService.Save();
            StatusText.Text = "状态：设置已保存";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：保存失败";
            MessageBox.Show(ex.Message, "系统设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
