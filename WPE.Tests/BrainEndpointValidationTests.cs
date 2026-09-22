using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class BrainEndpointValidationTests
{
    [Theory]
    [InlineData("DeepSeek","https://api.deepseek.com/chat/completions","deepseek-chat")]
    [InlineData("OpenAI","https://api.openai.com/v1/responses","gpt-test")]
    [InlineData("Anthropic","https://api.anthropic.com/v1/messages","claude-test")]
    [InlineData("Local Ollama","http://127.0.0.1:11434/v1/chat/completions","qwen3")]
    [InlineData("Custom API","https://brain.example.com/v1/chat","custom-model")]
    public void RetiredBrainProviders_AreRejected(string provider,string endpoint,string model)
    {
        var slot=new BrainSlot
        {
            Provider=provider,
            Endpoint=endpoint,
            Model=model,
            IsLocal=provider.Contains("Ollama",StringComparison.OrdinalIgnoreCase)
        };

        var error=Assert.Throws<InvalidOperationException>(()=>AgentSettingsStore.ValidateBrainEndpoint(slot));

        Assert.Contains("retired",error.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WpeLocalBrain_IsTheOnlyAcceptedProvider()
    {
        AgentSettingsStore.ValidateBrainEndpoint(new BrainSlot());
    }

    [Fact]
    public void WpeLocalBrain_RejectsEndpointOrCredential()
    {
        var withEndpoint=new BrainSlot{Endpoint="http://127.0.0.1:11434"};
        var withKey=new BrainSlot{EncryptedKey="legacy-secret"};

        Assert.Throws<InvalidOperationException>(()=>AgentSettingsStore.ValidateBrainEndpoint(withEndpoint));
        Assert.Throws<InvalidOperationException>(()=>AgentSettingsStore.ValidateBrainEndpoint(withKey));
    }

    [Theory]
    [InlineData("other-model",true)]
    [InlineData("deterministic-local-v1",false)]
    public void WpeLocalBrain_RequiresBuiltInModelAndLocalFlag(string model,bool isLocal)
    {
        var slot=new BrainSlot{Model=model,IsLocal=isLocal};

        Assert.Throws<InvalidOperationException>(()=>AgentSettingsStore.ValidateBrainEndpoint(slot));
    }
}
