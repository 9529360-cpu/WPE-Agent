using System.Text.Json;
using System.IO;
using 币安量化机器人.Core.Runtime;
using 币安量化机器人.Infrastructure.Runtime;

namespace 币安量化机器人.Services.Agent;

public sealed record ArchitectureTestResult(bool Success, string ReportPath, IReadOnlyList<ArchitectureTestCase> Cases);
public sealed record ArchitectureTestCase(string Name, bool Passed, string Detail);

public static class ArchitectureTestRunner
{
    public static async Task<ArchitectureTestResult> RunAsync()
    {
        var cases = new List<ArchitectureTestCase>();
        var root = Path.Combine(Path.GetTempPath(), "wpe-architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = new AgentSqliteStore(Path.Combine(root, "runtime.db"));
        try
        {
            Run(cases, "Assistant catalog is local-only", () =>
            {
                var adapters=AssistantAdapterCatalog.All;
                Require(adapters.Count==1&&adapters[0].Id=="local-deterministic"&&adapters[0].Local, "only the local deterministic assistant may be registered");
                var local=AssistantProviderFactory.Create(null,null,true);
                Require(local.IsLocal&&local is DeterministicBrainProvider, "assistant composition must remain local even when legacy remote settings are present");
                return "local-deterministic is the only registered assistant";
            });
            Run(cases, "状态图拒绝越级执行", () =>
            {
                Require(TradingWorkflowGraph.CanTransition(WorkflowNode.Planner, WorkflowNode.Critic), "合法边被拒绝");
                Require(!TradingWorkflowGraph.CanTransition(WorkflowNode.Planner, WorkflowNode.Execution), "Planner 可绕过审核与风控");
                try { TradingWorkflowGraph.EnsureTransition(WorkflowNode.Observation, WorkflowNode.Execution); }
                catch (InvalidOperationException) { return "非法 Observation→Execution 已拦截"; }
                throw new InvalidOperationException("非法状态跳转未被拦截");
            });

            Run(cases, "技能权限在运行时强制执行", () =>
            {
                var guard = new SkillExecutionGuard();
                guard.Authorize("ReliableOrderExecutor", Core.Models.TradingMode.Testnet);
                try { guard.Authorize("ReliableOrderExecutor", Core.Models.TradingMode.Live); }
                catch (UnauthorizedAccessException)
                {
                    try { guard.Authorize("UnregisteredShellTool", Core.Models.TradingMode.Testnet); }
                    catch (UnauthorizedAccessException) { return "Testnet 交易权限受环境约束，未注册工具被拒绝"; }
                }
                throw new InvalidOperationException("技能权限 Guardrail 未生效");
            });

            await RunAsync(cases, "事件总线保持顺序且隔离故障订阅者", async () =>
            {
                await using var bus = new InProcessAgentEventBus(16);
                var received = new List<long>();
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var bad = bus.Subscribe((_, _) => throw new InvalidOperationException("observer failure"));
                using var good = bus.Subscribe((value, _) =>
                {
                    lock (received) { received.Add(value.Sequence); if (received.Count == 3) done.TrySetResult(); }
                    return ValueTask.CompletedTask;
                });
                for (var i = 0; i < 3; i++) await bus.PublishAsync(AgentRuntimeEvent.Create("test", "test.event", "architecture-test", new { i }));
                await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Require(received.SequenceEqual(received.Order()), "事件顺序不稳定");
                return "3 个事件按序送达；故障观察者未中断内核";
            });

            await RunAsync(cases, "运行租约阻止重复交易核心", async () =>
            {
                Require(await database.TryAcquireRuntimeLeaseAsync("test-lease", "owner-a", TimeSpan.FromMinutes(1), default), "首个租约失败");
                Require(!await database.TryAcquireRuntimeLeaseAsync("test-lease", "owner-b", TimeSpan.FromMinutes(1), default), "重复核心错误取得租约");
                await database.ReleaseRuntimeLeaseAsync("test-lease", "owner-a", default);
                Require(await database.TryAcquireRuntimeLeaseAsync("test-lease", "owner-b", TimeSpan.FromMinutes(1), default), "释放后无法续接");
                await database.ReleaseRuntimeLeaseAsync("test-lease", "owner-b", default);
                return "重复实例被拒绝，释放后可安全接管";
            });

            await RunAsync(cases, "检查点可在重启后恢复", async () =>
            {
                var first = new AgentRuntimeSupervisor(database, new InProcessAgentEventBus());
                await first.StartAsync(default);
                await first.BeginCycleAsync("cycle-recovery", new { evidence = 88 }, default);
                await first.TransitionAsync("cycle-recovery", WorkflowNode.Research, new { candles = 400 }, default);
                await first.DisposeAsync();
                Require((await database.GetInterruptedWorkflowsAsync(default)).Any(x => x.CycleId == "cycle-recovery" && x.LastNode == WorkflowNode.Research), "未找到中断检查点");

                var second = new AgentRuntimeSupervisor(database, new InProcessAgentEventBus());
                await second.StartAsync(default);
                await second.RecoverInterruptedAsync(_ => Task.FromResult("orders reconciled"), default);
                Require(!(await database.GetInterruptedWorkflowsAsync(default)).Any(), "恢复后仍残留运行中工作流");
                await second.DisposeAsync();
                return "Research 检查点被识别并以新鲜行情安全续跑";
            });
        }
        catch (Exception ex) { cases.Add(new("测试运行器", false, ex.ToString())); }

        var reportPath = Path.Combine(AppDataPaths.DataDirectory, "architecture-test-report.json");
        var result = new ArchitectureTestResult(cases.All(x => x.Passed), reportPath, cases);
        await global::币安量化机器人.Services.SensitiveDataRedactor.WriteRedactedJsonAsync(reportPath,result);
        try { Directory.Delete(root, true); } catch { }
        return result;
    }

    private static void Run(List<ArchitectureTestCase> cases, string name, Func<string> test)
    {
        try { cases.Add(new(name, true, test())); } catch (Exception ex) { cases.Add(new(name, false, ex.Message)); }
    }

    private static async Task RunAsync(List<ArchitectureTestCase> cases, string name, Func<Task<string>> test)
    {
        try { cases.Add(new(name, true, await test())); } catch (Exception ex) { cases.Add(new(name, false, ex.Message)); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
