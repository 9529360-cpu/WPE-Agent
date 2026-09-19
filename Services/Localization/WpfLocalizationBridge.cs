using System.Windows;
using System.Windows.Markup;

namespace 币安量化机器人.Services.Localization;

/// <summary>
/// WPF-only projection of the platform-neutral localization state.
/// </summary>
public sealed class WpfLocalizationBridge : IDisposable
{
    private readonly LocalizationService _localization;
    private int _disposed;

    public WpfLocalizationBridge(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Apply();
        _localization.LanguageChanged += Apply;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _localization.LanguageChanged -= Apply;
    }

    private void Apply()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var application = global::System.Windows.Application.Current;
        if (application is null) return;

        foreach (var pair in _localization.GetResolvedResources())
            application.Resources[pair.Key] = pair.Value;

        var language = XmlLanguage.GetLanguage(_localization.Culture.IetfLanguageTag);
        foreach (Window window in application.Windows)
            window.Language = language;
    }
}
