using System.Diagnostics;
using System.IO;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Access;

public sealed record AccessTestCase(string Name,string Status,long DurationMs,string Detail);
public sealed class AccessTestReport
{
    public DateTime StartedAtUtc { get; init; }=DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public List<AccessTestCase> Cases { get; }=[];
}

public static class AccessTestRunner
{
    private static string SafeDetail(string? value,int maxLength=240)=>SensitiveDataRedactor.ForLog(value,maxLength);

    public static async Task<SmokeRunResult> RunLiveAsync(CancellationToken ct=default)
    {
        var root=Path.Combine(AppContext.BaseDirectory,"Data","access-tests");Directory.CreateDirectory(root);
        var reportPath=Path.Combine(root,$"access-live-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var store=new AgentSettingsStore();var settings=store.Load();
        var readiness=await new AccessReadinessService().CheckAsync(settings,ct);
        settings.LastAccessCheckAtUtc=readiness.CheckedAtUtc;store.Save(settings);
        var payload=new{readiness.CheckedAtUtc,readiness.Ready,Environment=settings.Environment.ToString(),ActiveBrain=settings.ActiveBrain,Checks=readiness.Checks};
        await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}),ct);
        return new(readiness.Ready,reportPath);
    }

    public static async Task<SmokeRunResult> RunAsync(CancellationToken ct=default)
    {
        var root=Path.Combine(AppContext.BaseDirectory,"Data","access-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reportPath=Path.Combine(root,"access-security-report.json");
        var report=new AccessTestReport();
        try
        {
            var accounts=new LocalAccountService(root);
            const string password="WpeSecure12345";
            string recovery=string.Empty;
            await Case(report,"本地账户使用 PBKDF2 且不落盘明文密码",()=>
            {
                var created=accounts.Create("wpe_test",password);recovery=created.RecoveryCode;
                Require(created.Result.Success,"账户创建失败");
                var disk=File.ReadAllText(Path.Combine(root,"local-accounts.json"));
                Require(!disk.Contains(password,StringComparison.Ordinal)&&!disk.Contains(recovery,StringComparison.Ordinal),"账户文件包含明文秘密");
                using var doc=JsonDocument.Parse(disk);var node=doc.RootElement[0];
                Require(node.GetProperty("Iterations").GetInt32()>=210000,"PBKDF2 迭代次数不足");
                return "PBKDF2-SHA256 / 210000 iterations / plaintext absent";
            });
            await Case(report,"错误密码被拒绝且正确密码可登录",()=>
            {
                Require(!accounts.Login("wpe_test","WrongPassword1",false).Success,"错误密码被接受");
                Require(accounts.Login("wpe_test",password,true).Success,"正确密码无法登录");
                return "wrong password rejected; correct password accepted";
            });
            await Case(report,"记住登录状态由 DPAPI 加密并可恢复",()=>
            {
                var session=File.ReadAllText(Path.Combine(root,"local-session.dat"));
                Require(!session.Contains("wpe_test",StringComparison.Ordinal)&&!session.Contains(password,StringComparison.Ordinal),"会话文件泄露身份或密码");
                Require(accounts.TryRememberedLogin().Success,"DPAPI 会话无法恢复");
                return "DPAPI session encrypted and restored";
            });
            await Case(report,"恢复码可重置密码并使旧密码失效",()=>
            {
                const string next="WpeReset98765";
                Require(!accounts.ResetPassword("wpe_test","BAD-RECOVERY",next).Success,"错误恢复码被接受");
                Require(accounts.ResetPassword("wpe_test",recovery,next).Success,"正确恢复码无法重置");
                Require(!accounts.Login("wpe_test",password,false).Success,"旧密码重置后仍可用");
                Require(accounts.Login("wpe_test",next,false).Success,"新密码无法登录");
                return "invalid recovery rejected; password rotated";
            });
            await Case(report,"关键连接缺失时启动门禁保持关闭",async()=>
            {
                var settings=new AgentSettings{Environment=ExchangeEnvironment.Testnet,ActiveBrain="missing"};
                var readiness=await new AccessReadinessService().CheckAsync(settings,ct);
                Require(!readiness.Ready,"缺少交易所和 Brain 凭据时门禁错误放行");
                Require(readiness.Checks.Any(x=>x.Key=="credentials"&&!x.Passed&&x.Critical),"未报告交易所凭据失败");
                Require(readiness.Checks.Any(x=>x.Key=="brain"&&!x.Passed&&x.Critical),"未报告 Brain 失败");
                return string.Join("; ",readiness.Checks.Where(x=>!x.Passed).Select(x=>x.Key));
            });
            report.Success=report.Cases.All(x=>x.Status=="PASSED");
        }
        catch(Exception ex){report.Cases.Add(new("接入测试中止","FAILED",0,SafeDetail(ex.ToString(),400)));report.Success=false;}
        report.CompletedAtUtc=DateTime.UtcNow;
        await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),ct);
        return new(report.Success,reportPath);
    }

    private static async Task Case(AccessTestReport report,string name,Func<string> test){var sw=Stopwatch.StartNew();try{var detail=test();report.Cases.Add(new(name,"PASSED",sw.ElapsedMilliseconds,SafeDetail(detail)));}catch(Exception ex){report.Cases.Add(new(name,"FAILED",sw.ElapsedMilliseconds,SafeDetail(ex.Message)));throw;}await Task.CompletedTask;}
    private static async Task Case(AccessTestReport report,string name,Func<Task<string>> test){var sw=Stopwatch.StartNew();try{report.Cases.Add(new(name,"PASSED",sw.ElapsedMilliseconds,SafeDetail(await test())));}catch(Exception ex){report.Cases.Add(new(name,"FAILED",sw.ElapsedMilliseconds,SafeDetail(ex.Message)));throw;}}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
