using System.Security.Cryptography;
using System.Text.Json;

namespace WpeLicenseGenerator;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if(args.Length>0)return await RunCommandLineAsync(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new LicenseGeneratorForm());
        return 0;
    }

    private static async Task<int> RunCommandLineAsync(string[] args)
    {
        try
        {
            var values=ParseArguments(args);
            if(!values.TryGetValue("device",out var device)||!values.TryGetValue("key",out var keyPath)||!values.TryGetValue("out",out var outputPath))return 2;
            var days=values.TryGetValue("days",out var value)&&int.TryParse(value,out var parsed)?Math.Clamp(parsed,0,3650):0;
            var id=values.GetValueOrDefault("license")??LicenseIssuer.CreateLicenseId();
            var code=await LicenseIssuer.IssueAsync(device,id,days,Path.GetFullPath(keyPath));
            var full=Path.GetFullPath(outputPath);Directory.CreateDirectory(Path.GetDirectoryName(full)!);await File.WriteAllTextAsync(full,code);
            return 0;
        }
        catch{return 3;}
    }

    private static Dictionary<string,string> ParseArguments(string[] args)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<args.Length-1;i++)if(args[i].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];
        return result;
    }
}

internal static class LicenseIssuer
{
    private static readonly byte[] Entropy="WPE-LICENSE-AUTHORITY-V1"u8.ToArray();
    public static string DefaultKeyPath=>Path.Combine(AppContext.BaseDirectory,"wpe-license-authority.key");
    public static string CreateLicenseId()=> $"LIC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpperInvariant();

    public static async Task<string> IssueAsync(string deviceCode,string licenseId,int days,string? keyPath=null)
    {
        var device=NormalizeDeviceCode(deviceCode);
        var id=licenseId.Trim().ToUpperInvariant();
        if(id.Length is <3 or >64||id.Any(c=>!(char.IsLetterOrDigit(c)||c is '-' or '_')))throw new InvalidOperationException("授权编号只能包含字母、数字、- 或 _，长度为 3-64 位。");
        var authority=keyPath??DefaultKeyPath;
        if(!File.Exists(authority))throw new FileNotFoundException("找不到所有者签名密钥。请确认密钥文件与生成器位于同一目录。",authority);
        var protectedKey=Convert.FromBase64String((await File.ReadAllTextAsync(authority)).Trim());
        var privateKey=ProtectedData.Unprotect(protectedKey,Entropy,DataProtectionScope.CurrentUser);
        try
        {
            using var signer=ECDsa.Create();signer.ImportPkcs8PrivateKey(privateKey,out _);var now=DateTime.UtcNow;
            var payload=new Payload("WPE-AGENT",id,device,now.Ticks,days<=0?null:now.AddDays(Math.Clamp(days,1,3650)).Ticks,"Testnet");
            var body=JsonSerializer.SerializeToUtf8Bytes(payload);var signature=signer.SignData(body,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"WPE1.{Base64Url(body)}.{Base64Url(signature)}";
        }
        finally{CryptographicOperations.ZeroMemory(privateKey);}
    }

    private static string NormalizeDeviceCode(string value)
    {
        var result=new string(value.Where(c=>!char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if(!result.StartsWith("WPE-DEV-",StringComparison.Ordinal)||result.Length<32||result.Any(c=>!(char.IsLetterOrDigit(c)||c=='-')))throw new InvalidOperationException("设备码格式无效，请复制 WPE Agent 激活页面显示的完整设备码。");
        return result;
    }
    private static string Base64Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    private sealed record Payload(string Product,string LicenseId,string DeviceCode,long IssuedUtc,long? ExpiresUtc,string Edition);
}
