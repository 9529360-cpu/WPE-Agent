using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using Xunit;

namespace WPE.Tests;

public sealed class RuntimeModePolicyTests
{
    [Fact]
    public void LocalOnly_Disables_Remote_Brain()
    {
        var settings = new AgentSettings
        {
            AiMode = AiRuntimeMode.LocalOnly,
            ActiveBrain = "DeepSeek",
            Brains = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DeepSeek"] = new BrainSlot
                {
                    Provider = "DeepSeek",
                    Endpoint = "https://api.deepseek.com/chat/completions",
                    Model = "deepseek-chat",
                    EncryptedKey = "encrypted"
                }
            }
        };

        var result = RuntimeModePolicy.Resolve(settings);

        Assert.Equal(AiRuntimeMode.LocalOnly, result.RequestedMode);
        Assert.Equal(AiRuntimeMode.LocalOnly, result.EffectiveMode);
        Assert.False(result.AllowRemoteBrain);
    }

    [Fact]
    public void Hybrid_Falls_Back_To_Local_When_Remote_Config_Is_Incomplete()
    {
        var settings = new AgentSettings
        {
            AiMode = AiRuntimeMode.Hybrid,
            ActiveBrain = "DeepSeek",
            Brains = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DeepSeek"] = new BrainSlot
                {
                    Provider = "DeepSeek",
                    Endpoint = "https://api.deepseek.com/chat/completions",
                    Model = "",
                    EncryptedKey = ""
                }
            }
        };

        var result = RuntimeModePolicy.Resolve(settings);

        Assert.Equal(AiRuntimeMode.Hybrid, result.RequestedMode);
        Assert.Equal(AiRuntimeMode.LocalOnly, result.EffectiveMode);
        Assert.False(result.AllowRemoteBrain);
        Assert.Contains("falling back", result.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AIResearch_Allows_Remote_Brain_When_Config_Is_Complete()
    {
        var settings = new AgentSettings
        {
            AiMode = AiRuntimeMode.AIResearch,
            ActiveBrain = "DeepSeek",
            Brains = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DeepSeek"] = new BrainSlot
                {
                    Provider = "DeepSeek",
                    Endpoint = "https://api.deepseek.com/chat/completions",
                    Model = "deepseek-chat",
                    EncryptedKey = "encrypted"
                }
            }
        };

        var result = RuntimeModePolicy.Resolve(settings);

        Assert.Equal(AiRuntimeMode.AIResearch, result.RequestedMode);
        Assert.Equal(AiRuntimeMode.AIResearch, result.EffectiveMode);
        Assert.True(result.AllowRemoteBrain);
        Assert.Equal("DeepSeek", result.ProviderName);
    }

    [Fact]
    public void SmokeLocalOnly_UsesDeterministicBrainWithoutDecryptingOrConstructingRemoteProvider()
    {
        var settings=Configured(AiRuntimeMode.LocalOnly);
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

    [Theory]
    [InlineData(AiRuntimeMode.Hybrid)]
    [InlineData(AiRuntimeMode.AIResearch)]
    public void SmokeRemoteModes_PreserveConfiguredRemoteProviderBehavior(AiRuntimeMode mode)
    {
        var settings=Configured(mode);
        var decryptCalls=0;var localCalls=0;var remoteCalls=0;

        var brain=SmokeTestRunner.CreateBrain(settings,
            _=>{decryptCalls++;return"fake-remote-secret";},
            ()=>{localCalls++;return new DeterministicBrainProvider();},
            (slot,secret,effectiveMode)=>
            {
                remoteCalls++;
                Assert.Equal("DeepSeek",slot.Provider);
                Assert.Equal("fake-remote-secret",secret);
                Assert.Equal(mode,effectiveMode);
                return new FakeAssistant(false);
            });

        Assert.False(brain.IsLocal);
        Assert.Equal(0,localCalls);
        Assert.Equal(1,decryptCalls);
        Assert.Equal(1,remoteCalls);
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
