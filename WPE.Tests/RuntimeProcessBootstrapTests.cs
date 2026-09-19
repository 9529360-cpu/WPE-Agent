namespace WPE.Tests;

public sealed class RuntimeProcessBootstrapTests
{
    [Fact]
    public void BootstrapIsUiNeutralAndOwnsProcessInitialization()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"Services","RuntimeProcessBootstrap.cs"));

        foreach(var forbidden in new[]{"System.Windows","ReferenceUiWindow","SetupWindow","ActivationWindow","WpfLocalizationBridge"})
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);

        var lease=source.IndexOf("DataRootMaintenanceLease.AcquireProcessLease",StringComparison.Ordinal);
        var migration=source.IndexOf("AppDataPaths.MigrateLegacyPortableData()",StringComparison.Ordinal);
        Assert.True(lease>=0&&migration>lease);
        Assert.Contains("new SensitiveFileLogSink(logPath)",source,StringComparison.Ordinal);
        Assert.Contains("LocalizationService.Current.Initialize()",source,StringComparison.Ordinal);
        Assert.Contains("if (_current is not null) return _current;",source,StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopAppDelegatesSharedBootstrap()
    {
        var app=File.ReadAllText(Path.Combine(Root(),"App.xaml.cs"));

        Assert.Contains("RuntimeProcessBootstrap.Initialize(\"app.log\");",app,StringComparison.Ordinal);
        Assert.DoesNotContain("AppDataPaths.MigrateLegacyPortableData()",app,StringComparison.Ordinal);
        Assert.DoesNotContain("new LoggerConfiguration()",app,StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Current.Initialize()",app,StringComparison.Ordinal);
        Assert.Contains("new WpfLocalizationBridge(LocalizationService.Current)",app,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
