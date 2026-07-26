using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class BrainEndpointValidationTests
{
    [Theory]
    [InlineData("DeepSeek", "http://api.deepseek.com/chat/completions", "deepseek-chat")]
    [InlineData("DeepSeek", "https://example.com/chat/completions", "deepseek-chat")]
    [InlineData("OpenAI", "https://user:pass@api.openai.com/v1/responses", "gpt-test")]
    [InlineData("Anthropic", "https://api.openai.com/v1/messages", "claude-test")]
    [InlineData("Custom API", "https://127.0.0.1/v1/chat", "custom-model")]
    [InlineData("Custom API", "https://localhost/v1/chat", "custom-model")]
    [InlineData("OpenAI", "https://api.openai.com/v1/responses", "")]
    public void RemoteBrain_RejectsUnsafeOrIncompleteEndpoint(string provider, string endpoint, string model)
    {
        var slot = new BrainSlot { Provider = provider, Endpoint = endpoint, Model = model };
        Assert.Throws<InvalidOperationException>(() => AgentSettingsStore.ValidateBrainEndpoint(slot));
    }

    [Theory]
    [InlineData("DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-chat")]
    [InlineData("OpenAI", "https://api.openai.com/v1/responses", "gpt-test")]
    [InlineData("Anthropic", "https://api.anthropic.com/v1/messages", "claude-test")]
    [InlineData("Custom API", "https://brain.example.com/v1/chat", "custom-model")]
    public void RemoteBrain_AcceptsExpectedHttpsHost(string provider, string endpoint, string model)
    {
        AgentSettingsStore.ValidateBrainEndpoint(new BrainSlot { Provider = provider, Endpoint = endpoint, Model = model });
    }

    [Fact]
    public void LocalOllama_AllowsExplicitLoopback()
    {
        AgentSettingsStore.ValidateBrainEndpoint(new BrainSlot { Provider = "Local Ollama", Endpoint = "http://127.0.0.1:11434/v1/chat/completions", Model = "qwen3", IsLocal = true });
    }

    [Theory]
    [InlineData("http://192.168.1.8:11434/v1/chat/completions")]
    [InlineData("https://ollama.example.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11435/v1/chat/completions")]
    public void LocalOllama_RejectsNonLoopback(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => AgentSettingsStore.ValidateBrainEndpoint(new BrainSlot { Provider = "Local Ollama", Endpoint = endpoint, Model = "qwen3", IsLocal = true }));
    }

    [Fact]
    public void WpeLocalBrain_RequiresNoEndpoint()
    {
        AgentSettingsStore.ValidateBrainEndpoint(new BrainSlot { Provider = "WPE Local Brain", Endpoint = string.Empty, Model = "deterministic-local-v1", IsLocal = true });
    }
}
