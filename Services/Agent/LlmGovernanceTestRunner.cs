using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record LlmGovernanceTestResult(bool Success, string ReportPath, IReadOnlyList<string> Cases);

public static class LlmGovernanceTestRunner
{
    public static async Task<LlmGovernanceTestResult> RunAsync()
    {
        var cases = new List<string>(); var success = true;
        void Check(string name, bool passed) { success &= passed; cases.Add($"{(passed ? "PASS" : "FAIL")} {name}"); }
        var root = Path.Combine(Path.GetTempPath(), $"wpe-llm-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var audit = Path.Combine(root, "audit.jsonl");
        try
        {
            var governor = new LlmRequestGovernor(new LlmUsagePolicy(1, 1000, 1m, TimeSpan.FromMinutes(5)), audit);
            var sends = 0;
            Task<HttpResponseMessage> Send(CancellationToken _) { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") }); }
            using var first = await governor.SendAsync("fake", "test-model", "summary", "same prompt", Send, default);
            using var cached = await governor.SendAsync("fake", "test-model", "summary", "same prompt", Send, default);
            Check("相同 Prompt 命中缓存", sends == 1);
            var blocked = false; try { using var denied = await governor.SendAsync("fake", "test-model", "summary", "different prompt", Send, default); } catch (InvalidOperationException) { blocked = true; }
            Check("达到每日调用预算后自动拒绝", blocked && sends == 1);
            var rows = File.ReadAllLines(audit).Select(x => JsonSerializer.Deserialize<LlmCallAudit>(x)).Where(x => x is not null).ToArray();
            Check("请求、缓存和拒绝均有审计", rows.Length == 3 && rows.Any(x => x!.CacheHit) && rows.Any(x => !x!.Allowed));
            Check("审计不保存完整 Prompt", File.ReadAllText(audit).IndexOf("same prompt", StringComparison.Ordinal) < 0);
        }
        catch (Exception ex) { success = false; cases.Add("FAIL " + ex); }
        var report = AppDataPaths.File("llm-governance-test-report.json");
        var result = new LlmGovernanceTestResult(success, report, cases);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        try { Directory.Delete(root, true); } catch { }
        return result;
    }
}
