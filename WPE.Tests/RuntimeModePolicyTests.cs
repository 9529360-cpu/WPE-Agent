using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using Xunit;

namespace WPE.Tests;

public sealed class RuntimeModePolicyTests
{
    [Theory]
    [InlineData(AiRuntimeMode.LocalOnly)]
    [InlineData(AiRuntimeMode.Hybrid)]
    [InlineData(AiRuntimeMode.AIResearch)]
    public void EveryLegacyModeResolvesToModelOffDeterministicRuntime(AiRuntimeMode requested)
    {
        var result = RuntimeModePolicy.Resolve(Configured(requested));

        Assert.Equal(requested, result.RequestedMode);
        Assert.Equal(AiRuntimeMode.LocalOnly, result.EffectiveMode);
        Assert.False(result.AllowRemoteBrain);
        Assert.Equal(RuntimeModePolicy.DeterministicRuntimeName, result.ProviderName);
        Assert.Equal(string.Empty, result.ModelName);
        Assert.Contains("Model-off product policy", result.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoModelConfigurationIsRequired()
    {
        var settings = new AgentSettings
        {
            AiMode = AiRuntimeMode.LocalOnly,
            ActiveBrain = string.Empty,
            Brains = new(StringComparer.OrdinalIgnoreCase)
        };

        var result = RuntimeModePolicy.Resolve(settings);

        Assert.True(result.IsLocalOnly);
        Assert.False(result.AllowRemoteBrain);
        Assert.Equal(RuntimeModePolicy.DeterministicRuntimeName, result.ProviderName);
        Assert.Null(RuntimeModePolicy.GetActiveBrain(settings));
    }

    [Theory]
    [InlineData(AiRuntimeMode.LocalOnly)]
    [InlineData(AiRuntimeMode.Hybrid)]
    [InlineData(AiRuntimeMode.AIResearch)]
    public void SmokeAlwaysUsesDeterministicRuntimeWithoutDecryptingOrConstructingModelProvider(AiRuntimeMode mode)
    {
        var settings=Configured(mode);
        var decryptCalls=0;var localCalls=0;var remoteCalls=0;

        var brain=SmokeTestRunner.CreateBrain(settings,
            _=>{decryptCalls++;return"should-not-be-used";},
            ()=>{localCalls++;return new DeterministicBrainProvider();},
            (_,_,_)=>{remoteCalls++;return new FakeAssistant(false);});

        Assert.IsType<DeterministicBrainProvider>(brain);
        Assert.True(brain.IsLocal);
        Assert.Equal(1,localCalls);
        Assert.Equal(0,decryptCalls);
        Assert.Equal(0,remoteCalls);
    }

    private static AgentSettings Configured(AiRuntimeMode mode)=>new()
    {
        AiMode=mode,ActiveBrain="DeepSeek",Brains=new(StringComparer.OrdinalIgnoreCase)
        {
            ["DeepSeek"]=new(){Provider="DeepSeek",Endpoint="https://api.deepseek.com/chat/completions",Model="deepseek-chat",EncryptedKey="fake-encrypted-key"}
        }
    };

    private sealed class FakeAssistant(bool isLocal):IAssistantProvider
    {
        public string Name=>isLocal?"fake-local":"fake-remote";
        public bool IsLocal=>isLocal;
        public Task<BrainHealth> HealthCheckAsync(CancellationToken cancellationToken)=>Task.FromResult(new BrainHealth(true,"ok"));
        public Task<BrainDecisionResult> DecideAsync(EvidencePack evidence,AgentContext context,CancellationToken cancellationToken)=>throw new NotSupportedException();
    }
}
