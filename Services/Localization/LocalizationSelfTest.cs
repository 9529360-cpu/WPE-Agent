using System.Text;
using System.IO;

namespace 币安量化机器人.Services.Localization;

public sealed record LocalizationTestResult(bool Success, string ReportPath);

public static class LocalizationSelfTest
{
    public static LocalizationTestResult Run()
    {
        var service = LocalizationService.Current;
        var original = service.CurrentCode;
        var lines = new List<string> { "WPE i18n self-test", $"UTC: {DateTime.UtcNow:O}", $"Languages: {service.AvailableLanguages.Count}" };
        var success = service.AvailableLanguages.Count >= 6;
        foreach (var language in service.AvailableLanguages)
        {
            var switched = service.SetLanguage(language.Code, save: false, notify: false);
            var missing = service.ValidateResources()[language.Code];
            var label = service.T("Nav.CommandCenter");
            var number = service.Number(3114.93m);
            var date = service.DateTime(new DateTime(2026, 7, 19, 13, 25, 0));
            var valid = switched && missing.Count == 0 && !label.StartsWith('[') && !string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(date);
            success &= valid;
            lines.Add($"{language.Code}: {(valid ? "PASS" : "FAIL")} | {label} | {number} USDT | {date} | missing={missing.Count}");
        }
        service.SetLanguage(original, save: false, notify: false);
        var directory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "i18n-self-test.txt");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        return new(success, path);
    }
}
