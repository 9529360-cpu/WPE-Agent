using System.Diagnostics;
using 币安量化机器人.Services.Access;

namespace WPE.Tests;

public sealed class ModelOffOptionalProviderLivenessTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(120);

    [Fact]
    public void CriticalReadinessSourceDoesNotConstructModelProviders()
    {
        var source = File.ReadAllText(ProductPath("Services", "Access", "AccessReadinessService.cs"));
        Assert.DoesNotContain("new HttpBrainProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DeterministicBrainProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddBrainCheckAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HungDiagnosticTimesOutWithinBoundAndRemainsNoncanonical()
    {
        var sw = Stopwatch.StartNew();
        var result = await OptionalModelDiagnosticRunnerV1.RunAsync(_ => new TaskCompletionSource<string?>().Task, TimeSpan.FromMilliseconds(20));
        Assert.Equal(OptionalModelDiagnosticStatusV1.TimedOut, result.Status);
        Assert.True(sw.Elapsed < Bound, $"elapsed={sw.Elapsed}");
        Assert.False(result.Authoritative);
        Assert.False(result.UsedForDecision);
    }

    [Fact]
    public async Task LateDiagnosticCannotDelayPastDeadline()
    {
        var sw = Stopwatch.StartNew();
        var result = await OptionalModelDiagnosticRunnerV1.RunAsync(async _ => { await Task.Delay(200); return "late"; }, TimeSpan.FromMilliseconds(20));
        Assert.Equal(OptionalModelDiagnosticStatusV1.TimedOut, result.Status);
        Assert.True(sw.Elapsed < Bound, $"elapsed={sw.Elapsed}");
    }

    [Fact]
    public async Task CancelledDiagnosticReturnsBoundedNoncanonicalStatus()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await OptionalModelDiagnosticRunnerV1.RunAsync(_ => Task.FromResult<string?>("unused"), TimeSpan.FromSeconds(1), cts.Token);
        Assert.Equal(OptionalModelDiagnosticStatusV1.Cancelled, result.Status);
        Assert.False(result.Authoritative);
        Assert.False(result.UsedForDecision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MalformedDiagnosticIsRejected(string? detail)
    {
        var result = await OptionalModelDiagnosticRunnerV1.RunAsync(_ => Task.FromResult(detail), TimeSpan.FromMilliseconds(20));
        Assert.Equal(OptionalModelDiagnosticStatusV1.Malformed, result.Status);
    }

    [Fact]
    public async Task DiagnosticFailureIsDataAndNeverAuthority()
    {
        var result = await OptionalModelDiagnosticRunnerV1.RunAsync(_ => throw new FormatException("malformed provider payload"), TimeSpan.FromMilliseconds(20));
        Assert.Equal(OptionalModelDiagnosticStatusV1.Failed, result.Status);
        Assert.False(result.Authoritative);
        Assert.False(result.UsedForDecision);
        Assert.DoesNotContain("payload", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProductPath(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine([root, .. segments]);
    }
}
