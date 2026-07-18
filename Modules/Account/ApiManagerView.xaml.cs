using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Models;
using 币安量化机器人.Services;

namespace 币安量化机器人.Modules.Account;

public partial class ApiManagerView : UserControl
{
    private readonly ObservableCollection<AccountProfile> _profiles = new();
    private readonly DataCacheService _cache = ServiceLocator.Cache;
    private readonly BinanceApiClient _api = ServiceLocator.Api;
    private AccountProfile? _selected;

    public ApiManagerView()
    {
        InitializeComponent();
        AccountsGrid.ItemsSource = _profiles;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            StatusText.Text = "状态：加载 API 凭证...";
            var accounts = await _cache.LoadAccountsAsync();
            _profiles.Clear();
            foreach (var profile in accounts.OrderByDescending(p => p.CreatedAt))
                _profiles.Add(profile);
            StatusText.Text = $"状态：共 {_profiles.Count} 份凭证";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：加载失败";
            MessageBox.Show(ex.Message, "API 管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = _selected ?? new AccountProfile();
            profile.Label = LabelBox.Text.Trim();
            profile.ApiKey = KeyBox.Text.Trim();
            profile.Notes = NotesBox.Text.Trim();
            profile.IsPrimary = PrimaryBox.IsChecked == true;
            profile.IsPaperTrading = PaperBox.IsChecked == true;
            if (string.IsNullOrWhiteSpace(profile.Label) || string.IsNullOrWhiteSpace(profile.ApiKey))
                throw new InvalidOperationException("请填写标签与 API Key");

            if (!string.IsNullOrWhiteSpace(SecretBox.Password))
                profile.EncryptedSecret = SecretVaultService.Encrypt(SecretBox.Password);

            await _cache.SaveAccountProfileAsync(profile);
            await LoadAsync();
            SecretBox.Password = string.Empty;
            StatusText.Text = "状态：凭证已保存";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：保存失败";
            MessageBox.Show(ex.Message, "保存凭证", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            MessageBox.Show("请选择需要激活的凭证", "API 管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_selected.EncryptedSecret))
                throw new InvalidOperationException("该凭证未保存 Secret，无法激活");

            var secret = SecretVaultService.Decrypt(_selected.EncryptedSecret);
            _api.SetApiCredentials(_selected.ApiKey, secret);
            StatusText.Text = $"状态：已激活 {_selected.Label}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态：激活失败";
            MessageBox.Show(ex.Message, "激活凭证", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AccountsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = AccountsGrid.SelectedItem as AccountProfile;
        if (_selected is null)
            return;

        LabelBox.Text = _selected.Label;
        KeyBox.Text = _selected.ApiKey;
        NotesBox.Text = _selected.Notes;
        PrimaryBox.IsChecked = _selected.IsPrimary;
        PaperBox.IsChecked = _selected.IsPaperTrading;
        SecretBox.Password = string.Empty;
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAsync();
}
