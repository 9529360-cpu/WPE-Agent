using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using 币安量化机器人.Services;

namespace 币安量化机器人.Services.Access;

public sealed record DeviceLicensePayload(string Product,string LicenseId,string DeviceCode,long IssuedUtc,long? ExpiresUtc,string Edition);
public sealed record DeviceLicenseResult(bool Success,string Message,DeviceLicensePayload? License=null);

public static class DeviceLicenseCodec
{
    public static string Issue(DeviceLicensePayload payload,ECDsa privateKey)
    {
        var body=JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature=privateKey.SignData(body,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"WPE1.{Base64Url(body)}.{Base64Url(signature)}";
    }

    public static DeviceLicenseResult Verify(string activationCode,string publicKeyBase64,string expectedDevice,DateTime utcNow)
    {
        try
        {
            var normalized=new string(activationCode.Where(c=>!char.IsWhiteSpace(c)).ToArray());
            var parts=normalized.Split('.');
            if(parts.Length!=3||parts[0]!="WPE1")return new(false,"Activation.InvalidFormat");
            var body=FromBase64Url(parts[1]);var signature=FromBase64Url(parts[2]);
            using var verifier=ECDsa.Create();verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64),out _);
            if(!verifier.VerifyData(body,signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))return new(false,"Activation.InvalidSignature");
            var payload=JsonSerializer.Deserialize<DeviceLicensePayload>(body)??throw new JsonException();
            if(payload.Product!="WPE-AGENT")return new(false,"Activation.WrongProduct");
            if(!Canonical(payload.DeviceCode).Equals(Canonical(expectedDevice),StringComparison.Ordinal))return new(false,"Activation.WrongDevice");
            if(payload.ExpiresUtc is long expires&&utcNow>new DateTime(expires,DateTimeKind.Utc))return new(false,"Activation.Expired");
            return new(true,"Activation.Valid",payload);
        }
        catch{return new(false,"Activation.InvalidFormat");}
    }

    public static string Canonical(string value)=>value.Trim().ToUpperInvariant();
    private static string Base64Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] FromBase64Url(string value){value=value.Replace('-','+').Replace('_','/');return Convert.FromBase64String(value.PadRight(value.Length+(4-value.Length%4)%4,'='));}
}

public sealed class DeviceLicenseService
{
    public const string PublicKeyBase64="MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEUSMlb14eN6orfPyPQePphhYsI21wJyXar1OSXm3JrekZG/cgTCgTvLyjEaKtWBGJLq3j1ZONDEgZ8BVrr1GT+Q==";
    private readonly string _licensePath;private readonly string _publicKey;private readonly string _deviceCode;
    public DeviceLicenseService(string? dataDirectory=null,string? publicKey=null,string? deviceCode=null){var data=dataDirectory??AppDataPaths.DataDirectory;Directory.CreateDirectory(data);_licensePath=dataDirectory is null?AppDataPaths.File("device-license.dat"):Path.Combine(data,"device-license.dat");_publicKey=publicKey??PublicKeyBase64;_deviceCode=deviceCode??GetCurrentDeviceCode();}
    public string DeviceCode=>_deviceCode;
    public DeviceLicenseResult TryLoad(){try{if(!File.Exists(_licensePath))return new(false,"Activation.Required");var code=SecretVaultService.Decrypt(File.ReadAllText(_licensePath));return DeviceLicenseCodec.Verify(code,_publicKey,_deviceCode,DateTime.UtcNow);}catch{return new(false,"Activation.StorageInvalid");}}
    public DeviceLicenseResult Activate(string activationCode){var result=DeviceLicenseCodec.Verify(activationCode,_publicKey,_deviceCode,DateTime.UtcNow);if(!result.Success)return result;var temp=_licensePath+".tmp";File.WriteAllText(temp,SecretVaultService.Encrypt(new string(activationCode.Where(c=>!char.IsWhiteSpace(c)).ToArray())));File.Move(temp,_licensePath,true);return result;}
    public static string GetCurrentDeviceCode()
    {
        var machineGuid=ReadMachineGuid();var serial=GetSystemVolumeSerial();var material=$"WPE|{machineGuid}|{serial:X8}";var hash=SHA256.HashData(Encoding.UTF8.GetBytes(material));var encoded=Base32(hash);return "WPE-DEV-"+string.Join('-',Enumerable.Range(0,encoded.Length/4).Select(i=>encoded.Substring(i*4,4)));
    }
    private static string ReadMachineGuid(){try{return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid")?.ToString()??Environment.MachineName;}catch{return Environment.MachineName;}}
    private static uint GetSystemVolumeSerial(){try{var root=Path.GetPathRoot(Environment.SystemDirectory)??"C:\\";return GetVolumeInformation(root,null,0,out var serial,out _,out _,null,0)?serial:0;}catch{return 0;}}
    private static string Base32(byte[] data){const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var output=new StringBuilder(56);var buffer=0;var bits=0;foreach(var b in data){buffer=(buffer<<8)|b;bits+=8;while(bits>=5){bits-=5;output.Append(alphabet[(buffer>>bits)&31]);}}if(bits>0)output.Append(alphabet[(buffer<<(5-bits))&31]);return output.ToString();}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetVolumeInformation(string rootPathName,StringBuilder? volumeNameBuffer,int volumeNameSize,out uint volumeSerialNumber,out uint maximumComponentLength,out uint fileSystemFlags,StringBuilder? fileSystemNameBuffer,int nFileSystemNameSize);
}
