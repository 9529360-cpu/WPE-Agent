using System.Net;
using System.Net.Http;
using System.Text.Json;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class LlmGovernanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wpe-governance-{Guid.NewGuid():N}");

    [Fact]
    public async Task LocalOnly_HardGate_PerformsZeroRemoteCalls()
    {
        LlmRequestGovernor.ClearCurrentCallUsage();
        var governor = Governor(out var audit);
        var sends = 0;
        var error = await Assert.ThrowsAsync<LlmGovernanceException>(() => governor.SendAsync(
            "provider", "model", "research", "sensitive prompt", Context(AiRuntimeMode.LocalOnly),
            _ => { sends++; return Ok(); }, default));

        Assert.Equal("LOCAL_ONLY_BLOCKED", error.Code);
        Assert.Equal(0, sends);
        Assert.Contains("LOCAL_ONLY_BLOCKED", await File.ReadAllTextAsync(audit));
        var usage = LlmRequestGovernor.ConsumeCurrentCallUsage();
        Assert.NotNull(usage);
        Assert.False(usage!.Allowed);
        Assert.Equal("LOCAL_ONLY_BLOCKED", usage.Outcome);
        Assert.Equal(0, usage.LoggedTokens);
    }

    [Fact]
    public async Task RequestOverBudget_PerformsZeroRemoteCalls()
    {
        var policy = new LlmUsagePolicy(MaxInputTokens: 2, MaxOutputTokens: 10, MaxContextCharacters: 1000);
        var governor = Governor(out _, policy);
        var sends = 0;

        var error = await Assert.ThrowsAsync<LlmGovernanceException>(() => governor.SendAsync(
            "provider", "model", "research", new string('x', 80), Context(AiRuntimeMode.Hybrid),
            _ => { sends++; return Ok(); }, default));

        Assert.Equal("INPUT_TOKEN_BUDGET_BLOCKED", error.Code);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task RemoteFailure_IsAuditedAndDeterministicFallbackCompletes()
    {
        var governor = Governor(out _);
        await Assert.ThrowsAsync<LlmGovernanceException>(() => governor.SendAsync(
            "provider", "model", "research", "prompt", Context(AiRuntimeMode.Hybrid),
            _ => throw new HttpRequestException("network unavailable"), default));

        var local = new DeterministicBrainProvider();
        var result = await local.DecideAsync(new EvidencePack { Markets = new Dictionary<string, MarketEvidence>() },
            new AgentContext(local.Name),default);

        Assert.Equal(DecisionAction.Hold, result.Decision.Action);
        Assert.Equal(1, governor.GetTodaySnapshot().Fallbacks);
    }

    [Fact]
    public async Task Audit_RedactsPromptAndSecret_AndCapturesProviderUsage()
    {
        const string secret = "sk-super-secret-value";
        LlmRequestGovernor.ClearCurrentCallUsage();
        var governor = Governor(out var audit);
        using var response = await governor.SendAsync("openai", "model-x", "summary", $"private {secret}", Context(AiRuntimeMode.Hybrid),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"usage\":{\"input_tokens\":11,\"output_tokens\":7},\"output_text\":\"ok\"}")
            }), default);

        var text = await File.ReadAllTextAsync(audit);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("private", text, StringComparison.Ordinal);
        var row = JsonSerializer.Deserialize<LlmCallAudit>(Assert.Single(File.ReadAllLines(audit)))!;
        Assert.Equal("provider", row.TokenSource);
        Assert.Equal(11, row.EstimatedInputTokens);
        Assert.Equal(7, row.EstimatedOutputTokens);
        Assert.Equal("test-prompt-v1", row.PromptVersion);
        var usage = LlmRequestGovernor.ConsumeCurrentCallUsage();
        Assert.NotNull(usage);
        Assert.True(usage!.Allowed);
        Assert.Equal(18, usage.LoggedTokens);
        Assert.Equal(row.EstimatedCostUsd, usage.LoggedCostUsd);
        Assert.Equal("provider", usage.TokenSource);
        Assert.Equal("HTTP_200", usage.Outcome);
        Assert.Equal($"private {secret}".Length,usage.ContextCharacters);
    }

    private LlmRequestGovernor Governor(out string audit, LlmUsagePolicy? policy = null)
    {
        Directory.CreateDirectory(_root);
        audit = Path.Combine(_root, $"audit-{Guid.NewGuid():N}.jsonl");
        return new LlmRequestGovernor(policy ?? new LlmUsagePolicy(MaxOutputTokens: 100), audit);
    }

    private static LlmRequestContext Context(AiRuntimeMode mode) => new(mode, "test-prompt-v1", 10, 1000, TimeSpan.FromSeconds(2), "test-agent", "test-tool");
    private static Task<HttpResponseMessage> Ok() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
