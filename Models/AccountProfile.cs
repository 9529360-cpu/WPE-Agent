using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace 币安量化机器人.Models;

public class AccountProfile : INotifyPropertyChanged
{
    private string _label = string.Empty;
    private string _apiKey = string.Empty;
    private bool _isPrimary;
    private bool _isPaperTrading;
    private string? _notes;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetField(ref _apiKey, value);
    }

    public string MaskedKey => ApiKey.Length switch
    {
        0 => string.Empty,
        <= 6 => ApiKey,
        _ => $"{ApiKey[..4]}***{ApiKey[^4..]}"
    };

    public string? EncryptedSecret { get; set; }

    public string? PassphraseHint { get; set; }

    public bool IsPrimary
    {
        get => _isPrimary;
        set => SetField(ref _isPrimary, value);
    }

    public bool IsPaperTrading
    {
        get => _isPaperTrading;
        set => SetField(ref _isPaperTrading, value);
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? Notes
    {
        get => _notes;
        set => SetField(ref _notes, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;

        field = value!;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
