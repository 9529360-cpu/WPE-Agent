using System.Security.Cryptography;
using System.Text.Json;

var values=Args(args);if(!values.TryGetValue("device",out var device)||!values.TryGetValue("key",out var keyPath)||!values.TryGetValue("out",out var outputPath)){Console.Error.WriteLine("Required: --device <code> --key <DPAPI key file> --out <activation file> [--days 0] [--license id]");return 2;}
var protectedKey=Convert.FromBase64String(await File.ReadAllTextAsync(Path.GetFullPath(keyPath)));var privateKey=ProtectedData.Unprotect(protectedKey,"WPE-LICENSE-AUTHORITY-V1"u8.ToArray(),DataProtectionScope.CurrentUser);
try
{
    using var signer=ECDsa.Create();signer.ImportPkcs8PrivateKey(privateKey,out _);var now=DateTime.UtcNow;var days=values.TryGetValue("days",out var d)&&int.TryParse(d,out var parsed)?Math.Clamp(parsed,0,3650):0;var id=values.GetValueOrDefault("license")??$"LIC-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..21];var payload=new Payload("WPE-AGENT",id,device.Trim().ToUpperInvariant(),now.Ticks,days==0?null:now.AddDays(days).Ticks,"Testnet");var body=JsonSerializer.SerializeToUtf8Bytes(payload);var signature=signer.SignData(body,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);var code=$"WPE1.{Url(body)}.{Url(signature)}";var full=Path.GetFullPath(outputPath);Directory.CreateDirectory(Path.GetDirectoryName(full)!);await File.WriteAllTextAsync(full,code);Console.WriteLine($"Issued {id} for {payload.DeviceCode}; activation length={code.Length}; output={full}");return 0;
}
finally{CryptographicOperations.ZeroMemory(privateKey);}
static Dictionary<string,string> Args(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length-1;i++)if(args[i].StartsWith("--")){result[args[i][2..]]=args[++i];}return result;}
static string Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
sealed record Payload(string Product,string LicenseId,string DeviceCode,long IssuedUtc,long? ExpiresUtc,string Edition);
