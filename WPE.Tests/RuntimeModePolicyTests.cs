using 币安量化机器人.Services.Agent;
using 币安量化机器人.Core.Models;

namespace WPE.Tests;

public sealed class RuntimeModePolicyTests
{
    [Theory]
    [InlineData(AiRuntimeMode.LocalOnly)]
    [InlineData(AiRuntimeMode.Hybrid)]
    [InlineData(AiRuntimeMode.AIResearch)]
    public void TradingRuntimeIsAlwaysLocalOnly(AiRuntimeMode requested)
    {
        var settings=Configured(requested);

        var result=RuntimeModePolicy.Resolve(settings);

        Assert.Equal(AiRuntimeMode.LocalOnly,result.RequestedMode);
        Assert.Equal(AiRuntimeMode.LocalOnly,result.EffectiveMode);
        Assert.False(result.AllowRemoteBrain);
        Assert.Equal("WPE Local Brain",result.ProviderName);
        Assert.Equal("local-deterministic",result.ModelName);
    }

    [Fact]
    public void LegacyRemoteSettingsCannotCreateRemoteTradingBrain()
    {
        var settings=Configured(AiRuntimeMode.AIResearch);
        var localCalls=0;

        var brain=SmokeTestRunner.CreateBrain(settings,()=>{localCalls++;return new DeterministicBrainProvider();});

        Assert.IsType<DeterministicBrainProvider>(brain);
        Assert.True(brain.IsLocal);
        Assert.Equal(1,localCalls);
    }

    [Fact]
    public void AssistantFactoryIgnoresLegacyRemoteSlot()
    {
        var settings=Configured(AiRuntimeMode.Hybrid);
        var slot=RuntimeModePolicy.GetActiveBrain(settings);

        var brain=AssistantProviderFactory.Create(slot,"legacy-secret",true);

        Assert.IsType<DeterministicBrainProvider>(brain);
        Assert.True(brain.IsLocal);
        Assert.Single(AssistantAdapterCatalog.All);
        Assert.Equal("local-deterministic",AssistantAdapterCatalog.All[0].Id);
    }

    private static AgentSettings Configured(AiRuntimeMode mode)=>new()
    {
        AiMode=mode,
        ActiveBrain="DeepSeek",
        Brains=new(StringComparer.OrdinalIgnoreCase)
        {
            ["DeepSeek"]=new()
            {
                Provider="DeepSeek",
                Endpoint="https://api.deepseek.com/chat/completions",
                Model="deepseek-chat",
                EncryptedKey="legacy-encrypted-key"
            }
        }
    };
}
