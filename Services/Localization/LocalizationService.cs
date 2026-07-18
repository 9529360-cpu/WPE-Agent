using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace 币安量化机器人.Services.Localization;

public sealed record LanguageInfo(string Code, string Culture, string DisplayName, string AiLanguage);

public sealed class LocalizationService
{
    private const string FallbackCode = "zh_CN";
    private readonly Dictionary<string, LanguageInfo> _languages = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _resourceDirectory = Path.Combine(AppContext.BaseDirectory, "Resources", "i18n");
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "Data", "ui-settings.json");

    public static LocalizationService Current { get; } = new();
    public event Action? LanguageChanged;
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _languages.Values.OrderBy(x => x.DisplayName).ToArray();
    public string CurrentCode { get; private set; } = FallbackCode;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("zh-CN");
    public LanguageInfo CurrentLanguage => _languages.TryGetValue(CurrentCode, out var value)
        ? value : new(FallbackCode, "zh-CN", "简体中文", "简体中文");

    public void Initialize()
    {
        DiscoverLanguages();
        _fallback = LoadStrings(FallbackCode);
        SetLanguage(ResolveInitialLanguage(), save: false, notify: false);
    }

    public bool SetLanguage(string code, bool save = true, bool notify = true)
    {
        if (!_languages.TryGetValue(code, out var language)) return false;
        var values = LoadStrings(language.Code);
        if (values.Count == 0) return false;
        CurrentCode = language.Code;
        Culture = CultureInfo.GetCultureInfo(language.Culture);
        _strings = values;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        if (global::System.Windows.Application.Current is not null)
        {
            foreach (var pair in _fallback) global::System.Windows.Application.Current.Resources[pair.Key] = pair.Value;
            foreach (var pair in _strings) global::System.Windows.Application.Current.Resources[pair.Key] = pair.Value;
            foreach (Window window in global::System.Windows.Application.Current.Windows)
                window.Language = XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
        }
        if (save) SaveLanguage();
        if (notify) LanguageChanged?.Invoke();
        return true;
    }

    public string T(string key, params object?[] args)
    {
        var template = _strings.TryGetValue(key, out var value) ? value
            : _fallback.TryGetValue(key, out var fallback) ? fallback : $"[{key}]";
        return args.Length == 0 ? template : string.Format(Culture, template, args);
    }

    public string Number(decimal value, int decimals = 2) => value.ToString($"N{decimals}", Culture);
    public string Number(double value, int decimals = 2) => value.ToString($"N{decimals}", Culture);
    public string DateTime(DateTime value) => value.ToString(T("Format.DateTime"), Culture);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ValidateResources()
    {
        var required = _fallback.Keys.Where(x => !x.StartsWith("__", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _languages.Values.ToDictionary(
            x => x.Code,
            x => (IReadOnlyList<string>)required.Except(LoadStrings(x.Code).Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private void DiscoverLanguages()
    {
        _languages.Clear();
        if (!Directory.Exists(_resourceDirectory)) return;
        foreach (var file in Directory.GetFiles(_resourceDirectory, "*.json"))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (map is null || !map.TryGetValue("__code", out var code)) continue;
                _languages[code] = new(code, map.GetValueOrDefault("__culture", code.Replace('_', '-')),
                    map.GetValueOrDefault("__name", code), map.GetValueOrDefault("__aiLanguage", code));
            }
            catch { /* Invalid language packs are ignored and reported by the self-test. */ }
        }
    }

    private Dictionary<string, string> LoadStrings(string code)
    {
        var path = Path.Combine(_resourceDirectory, code + ".json");
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? new(StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("__fallback", out var parent) && !string.Equals(parent, code, StringComparison.OrdinalIgnoreCase))
        {
            var merged = LoadStrings(parent);
            foreach (var pair in values) merged[pair.Key] = pair.Value;
            return merged;
        }
        return values;
    }

    private string ResolveInitialLanguage()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                if (doc.RootElement.TryGetProperty("language", out var item))
                {
                    var saved = item.GetString();
                    if (saved is not null && _languages.ContainsKey(saved)) return saved;
                }
            }
        }
        catch { }
        var system = CultureInfo.InstalledUICulture.Name;
        var exact = _languages.Values.FirstOrDefault(x => string.Equals(x.Culture, system, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Code;
        var prefix = system.Split('-')[0];
        return _languages.Values.FirstOrDefault(x => x.Culture.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))?.Code
            ?? FallbackCode;
    }

    private void SaveLanguage()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(new { language = CurrentCode }, new JsonSerializerOptions { WriteIndented = true });
        var temp = _settingsPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _settingsPath, true);
    }
}
