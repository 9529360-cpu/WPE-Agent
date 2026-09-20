using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class LocalBrainDefaultTests
{
    [Fact]
    public void NewAgentSettingsDefaultToLocalDeterministicBrain()
    {
        var settings=new AgentSettings();

        Assert.Equal(AiRuntimeMode.LocalOnly,settings.AiMode);
        Assert.Equal("WPE Local Brain",settings.ActiveBrain);
    }

    [Fact]
    public void SetupProviderPickerPresentsLocalBrainAsTheDefault()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"SetupWindow.xaml"));

        Assert.Contains("Content=\"WPE Local Brain\" IsSelected=\"True\"",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"DeepSeek\" IsSelected=\"True\"",source,StringComparison.Ordinal);
    }

    private static string ProjectRoot()
        =>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
