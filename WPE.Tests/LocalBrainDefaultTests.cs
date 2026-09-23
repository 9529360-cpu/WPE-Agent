using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class LocalBrainDefaultTests
{
    [Fact]
    public void LocalDeterministicBrainIsTheOnlyAssistantFactory()
    {
        var brain=AssistantProviderFactory.CreateLocal();

        Assert.IsType<DeterministicBrainProvider>(brain);
        Assert.True(brain.IsLocal);
        Assert.Equal("WPE Local Brain",brain.Name);
        Assert.Single(AssistantAdapterCatalog.All);
        Assert.Equal("local-deterministic",AssistantAdapterCatalog.All[0].Id);
    }

    [Fact]
    public void SetupBrainSurfaceIsLocalOnly()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"SetupWindow.xaml"));

        Assert.Contains("Text=\"WPE Local Brain\"",source,StringComparison.Ordinal);
        Assert.Contains("Text=\"Technical Market Decision Agent\"",source,StringComparison.Ordinal);
        Assert.Contains("Local-only · no remote provider, endpoint, API key, or fallback.",source,StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderBox",source,StringComparison.Ordinal);
        Assert.DoesNotContain("BrainEndpointBox",source,StringComparison.Ordinal);
        Assert.DoesNotContain("BrainKeyBox",source,StringComparison.Ordinal);
        Assert.DoesNotContain("DeepSeek",source,StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Anthropic",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Local Ollama",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Custom API",source,StringComparison.Ordinal);
    }

    private static string ProjectRoot()
        =>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
