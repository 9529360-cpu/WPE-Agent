namespace WPE.Tests;

public sealed class DesktopShellRetirementTests
{
    [Fact]
    public void StartupUsesOnlyReferenceUiAndProductExecutableName()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        var project = File.ReadAllText(Path.Combine(root, "币安量化机器人.csproj"));

        Assert.Contains("new DesktopRuntimeHost(localIdentity)", app, StringComparison.Ordinal);
        Assert.Contains("new WpeAgent.ReferenceUiWindow(runtimeHost.BuildRuntimeJson", app, StringComparison.Ordinal);
        Assert.Contains("runtimeHost.StartAgentAsync", app, StringComparison.Ordinal);
        Assert.Contains("var accessReady = await runtimeHost.RefreshAccessAsync();", app, StringComparison.Ordinal);
        Assert.Contains("if (accessReady) AutoTradingAgent.StartDefault();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new MainWindow", app, StringComparison.Ordinal);
        Assert.DoesNotContain("--legacy-ui", app, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<AssemblyName>WPE-Agent</AssemblyName>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredDesktopShellSourcesAreAbsent()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Assert.False(File.Exists(Path.Combine(root, "MainWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(root, "MainWindow.xaml.cs")));
    }
}
