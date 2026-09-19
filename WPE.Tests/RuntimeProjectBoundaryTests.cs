namespace WPE.Tests;

public sealed class RuntimeProjectBoundaryTests
{
    [Fact]
    public void ProductionProjectExposesPortableKernelAndWindowsPresentationTargets()
    {
        var project=File.ReadAllText(Path.Combine(Root(),"币安量化机器人.csproj"));

        Assert.Contains("<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>",project,StringComparison.Ordinal);
        Assert.Contains("<OutputType Condition=\"'$(TargetFramework)' == 'net8.0'\">Library</OutputType>",project,StringComparison.Ordinal);
        Assert.Contains("<UseWPF Condition=\"'$(TargetFramework)' == 'net8.0-windows'\">true</UseWPF>",project,StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2\" Version=\"1.0.2903.40\" Condition=\"'$(TargetFramework)' == 'net8.0-windows'\"",project,StringComparison.Ordinal);
        Assert.Contains("ScottPlot.WPF\" Version=\"5.0.56\" Condition=\"'$(TargetFramework)' == 'net8.0-windows'\"",project,StringComparison.Ordinal);
        Assert.Contains("System.Security.Cryptography.ProtectedData\" Version=\"8.0.0\" Condition=\"'$(TargetFramework)' == 'net8.0'\"",project,StringComparison.Ordinal);
        Assert.Contains("<ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\">",project,StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Modules\\**\\*.cs\" />",project,StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Services\\Localization\\WpfLocalizationBridge.cs\" />",project,StringComparison.Ordinal);
    }

    [Fact]
    public void ProductCiBuildsPortableTargetAndPublishesOnlyWindowsProduct()
    {
        var workflow=File.ReadAllText(Path.Combine(Root(),".github","workflows","dotnet.yml"));

        Assert.Contains("Verify portable runtime target",workflow,StringComparison.Ordinal);
        Assert.Contains("--framework net8.0 --no-restore",workflow,StringComparison.Ordinal);
        Assert.Contains("--framework net8.0-windows --no-restore --output artifacts/publish",workflow,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
