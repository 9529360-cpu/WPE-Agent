using System.Diagnostics;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Access;

public sealed record DeviceLicenseTestCase(string Name,string Status,long DurationMs,string Detail);
public sealed class DeviceLicenseTestReport { public DateTime StartedAtUtc{get;init;}=DateTime.UtcNow;public DateTime CompletedAtUtc{get;set;}public bool Success{get;set;}public List<DeviceLicenseTestCase> Cases{get;}=[]; }

public static class DeviceLicenseTestRunner
{
    public static async Task<SmokeRunResult> RunAsync(CancellationToken ct=default)
    {
        var root=Path.Combine(AppContext.BaseDirectory,"Data","license-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var reportPath=Path.Combine(root,"device-license-report.json");var report=new DeviceLicenseTestReport();
        try
        {
            using var signer=ECDsa.Create(ECCurve.NamedCurves.nistP256);var publicKey=Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());const string device="WPE-DEV-AAAA-BBBB-CCCC-DDDD";
            var payload=new DeviceLicensePayload("WPE-AGENT","TEST-001",device,DateTime.UtcNow.Ticks,null,"Testnet");var code=DeviceLicenseCodec.Issue(payload,signer);
            await Case(report,"签名激活码仅在绑定设备有效",()=>{var service=new DeviceLicenseService(root,publicKey,device);var activated=service.Activate(code);Require(activated.Success,"正确设备激活失败");Require(service.TryLoad().Success,"加密许可证无法恢复");return $"length={code.Length}; license={activated.License?.LicenseId}";});
            await Case(report,"许可证使用 DPAPI 保存且不含明文",()=>{var disk=File.ReadAllText(Path.Combine(root,"device-license.dat"));Require(!disk.Contains(code,StringComparison.Ordinal)&&!disk.Contains(device,StringComparison.Ordinal),"许可证文件泄露明文");return "activation code and device fingerprint absent on disk";});
            await Case(report,"更换设备后原激活码失效",()=>{var wrong=new DeviceLicenseService(Path.Combine(root,"wrong"),publicKey,"WPE-DEV-ZZZZ-YYYY-XXXX-WWWW").Activate(code);Require(!wrong.Success&&wrong.Message=="Activation.WrongDevice","换机未被拒绝");return wrong.Message;});
            await Case(report,"篡改激活码无法通过签名验证",()=>{var tail=code[^1]=='A'?'B':'A';var tampered=DeviceLicenseCodec.Verify(code[..^1]+tail,publicKey,device,DateTime.UtcNow);Require(!tampered.Success,"篡改码被接受");return tampered.Message;});
            await Case(report,"过期许可证被拒绝",()=>{var expired=new DeviceLicensePayload("WPE-AGENT","OLD-001",device,DateTime.UtcNow.AddDays(-2).Ticks,DateTime.UtcNow.AddDays(-1).Ticks,"Testnet");var result=DeviceLicenseCodec.Verify(DeviceLicenseCodec.Issue(expired,signer),publicKey,device,DateTime.UtcNow);Require(!result.Success&&result.Message=="Activation.Expired","过期码被接受");return result.Message;});
            report.Success=report.Cases.All(x=>x.Status=="PASSED");
        }
        catch(Exception ex){report.Cases.Add(new("许可证测试中止","FAILED",0,ex.ToString()));report.Success=false;}
        report.CompletedAtUtc=DateTime.UtcNow;await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),ct);return new(report.Success,reportPath);
    }
    private static async Task Case(DeviceLicenseTestReport report,string name,Func<string> test){var sw=Stopwatch.StartNew();try{var detail=test();report.Cases.Add(new(name,"PASSED",sw.ElapsedMilliseconds,detail));}catch(Exception ex){report.Cases.Add(new(name,"FAILED",sw.ElapsedMilliseconds,ex.Message));throw;}await Task.CompletedTask;}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
