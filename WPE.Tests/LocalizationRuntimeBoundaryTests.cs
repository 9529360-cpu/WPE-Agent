namespace WPE.Tests;

public sealed class LocalizationRuntimeBoundaryTests
{
    [Fact]
    public void LocalizationCoreHasNoWpfDependency()
    {
        var root=Root();
        var core=File.ReadAllText(Path.Combine(root,"Services","Localization","LocalizationService.cs"));
        var bridge=File.ReadAllText(Path.Combine(root,"Services","Localization","WpfLocalizationBridge.cs"));

        Assert.DoesNotContain("System.Windows",core,StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current",core,StringComparison.Ordinal);
        Assert.DoesNotContain("XmlLanguage",core,StringComparison.Ordinal);
        Assert.Contains("GetResolvedResources()",core,StringComparison.Ordinal);

        Assert.Contains("using System.Windows;",bridge,StringComparison.Ordinal);
        Assert.Contains("Application.Current",bridge,StringComparison.Ordinal);
        Assert.Contains("XmlLanguage.GetLanguage",bridge,StringComparison.Ordinal);
        Assert.Contains("_localization.LanguageChanged += Apply;",bridge,StringComparison.Ordinal);
        Assert.Contains("_localization.LanguageChanged -= Apply;",bridge,StringComparison.Ordinal);
    }

    [Fact]
    public void WpfAppOwnsOnlyTheWpfLocalizationProjection()
    {
        var root=Root();
        var app=File.ReadAllText(Path.Combine(root,"App.xaml.cs"));
        var bootstrap=File.ReadAllText(Path.Combine(root,"Services","RuntimeProcessBootstrap.cs"));

        Assert.Contains("_localizationBridge = new WpfLocalizationBridge(LocalizationService.Current);",app,StringComparison.Ordinal);
        Assert.Contains("_localizationBridge?.Dispose();",app,StringComparison.Ordinal);
        Assert.Contains("private WpfLocalizationBridge? _localizationBridge;",app,StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Current.Initialize();",app,StringComparison.Ordinal);
        Assert.Contains("LocalizationService.Current.Initialize();",bootstrap,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
