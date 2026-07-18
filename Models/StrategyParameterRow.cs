using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace 币安量化机器人.Models;

public class StrategyParameterRow : INotifyPropertyChanged
{
    private string _parameter = string.Empty;
    private string _value = string.Empty;
    private string _description = string.Empty;

    public string Parameter
    {
        get => _parameter;
        set => SetField(ref _parameter, value);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
