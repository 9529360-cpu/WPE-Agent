using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services.Access;

public sealed class LocalAccountRecord
{
    public string UserName { get; set; }=string.Empty; public string PasswordHash { get; set; }=string.Empty; public string Salt { get; set; }=string.Empty; public int Iterations { get; set; }=210000; public string RecoveryHash { get; set; }=string.Empty; public string RecoverySalt { get; set; }=string.Empty; public string RememberTokenHash { get; set; }=string.Empty; public DateTime CreatedAtUtc { get; set; }=DateTime.UtcNow; public DateTime? LastLoginAtUtc { get; set; }
}
public sealed record LoginResult(bool Success,string Message,string UserName="");
public sealed class LocalAccountService
{
    private readonly string _accountsPath;private readonly string _sessionPath;private const int Iterations=210000;
    public LocalAccountService(string? dataDirectory=null){var data=dataDirectory??Path.Combine(AppContext.BaseDirectory,"Data");Directory.CreateDirectory(data);_accountsPath=Path.Combine(data,"local-accounts.json");_sessionPath=Path.Combine(data,"local-session.dat");}
    public bool HasAccounts=>Load().Count>0;
    public (LoginResult Result,string RecoveryCode) Create(string userName,string password)
    {
        userName=Normalize(userName);ValidatePassword(password);var all=Load();if(all.Any(x=>x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase)))return(new(false,T("Access.AccountExists")),string.Empty);var recovery=Convert.ToHexString(RandomNumberGenerator.GetBytes(12));var salt=RandomNumberGenerator.GetBytes(16);var recoverySalt=RandomNumberGenerator.GetBytes(16);all.Add(new(){UserName=userName,Salt=Convert.ToBase64String(salt),PasswordHash=Hash(password,salt,Iterations),Iterations=Iterations,RecoverySalt=Convert.ToBase64String(recoverySalt),RecoveryHash=Hash(recovery,recoverySalt,Iterations)});Save(all);return(new(true,T("Access.AccountCreated"),userName),recovery);
    }
    public LoginResult Login(string userName,string password,bool remember)
    {
        userName=Normalize(userName);var all=Load();var account=all.FirstOrDefault(x=>x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase));if(account is null||!Fixed(account.PasswordHash,Hash(password,Convert.FromBase64String(account.Salt),account.Iterations)))return new(false,T("Access.InvalidLogin"));account.LastLoginAtUtc=DateTime.UtcNow;if(remember){var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));account.RememberTokenHash=Sha(token);File.WriteAllText(_sessionPath,SecretVaultService.Encrypt(JsonSerializer.Serialize(new{account.UserName,Token=token})));}else{account.RememberTokenHash=string.Empty;if(File.Exists(_sessionPath))File.Delete(_sessionPath);}Save(all);return new(true,T("Access.LoginSuccess"),account.UserName);
    }
    public LoginResult TryRememberedLogin()
    {
        try{if(!File.Exists(_sessionPath))return new(false,T("Access.NoSession"));var raw=SecretVaultService.Decrypt(File.ReadAllText(_sessionPath));using var doc=JsonDocument.Parse(raw);var user=doc.RootElement.GetProperty("UserName").GetString()??"";var token=doc.RootElement.GetProperty("Token").GetString()??"";var account=Load().FirstOrDefault(x=>x.UserName.Equals(user,StringComparison.OrdinalIgnoreCase));return account is not null&&Fixed(account.RememberTokenHash,Sha(token))?new(true,T("Access.SessionRestored"),account.UserName):new(false,T("Access.SessionExpired"));}catch{return new(false,T("Access.SessionFailed"));}
    }
    public LoginResult ResetPassword(string userName,string recoveryCode,string newPassword)
    {
        userName=Normalize(userName);ValidatePassword(newPassword);var all=Load();var account=all.FirstOrDefault(x=>x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase));if(account is null||!Fixed(account.RecoveryHash,Hash(recoveryCode.Trim().ToUpperInvariant(),Convert.FromBase64String(account.RecoverySalt),account.Iterations)))return new(false,T("Access.InvalidRecovery"));var salt=RandomNumberGenerator.GetBytes(16);account.Salt=Convert.ToBase64String(salt);account.PasswordHash=Hash(newPassword,salt,Iterations);account.Iterations=Iterations;account.RememberTokenHash=string.Empty;Save(all);if(File.Exists(_sessionPath))File.Delete(_sessionPath);return new(true,T("Access.PasswordReset"),account.UserName);
    }
    public void SignOut(){if(File.Exists(_sessionPath))File.Delete(_sessionPath);}
    private List<LocalAccountRecord> Load(){try{return File.Exists(_accountsPath)?JsonSerializer.Deserialize<List<LocalAccountRecord>>(File.ReadAllText(_accountsPath))??[]:[];}catch{return[];}}
    private void Save(List<LocalAccountRecord> accounts){var temp=_accountsPath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(accounts,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,_accountsPath,true);}
    private static string Normalize(string value){value=value.Trim();if(value.Length is <3 or >32||value.Any(c=>!char.IsLetterOrDigit(c)&&c is not '_' and not '-'))throw new InvalidOperationException(T("Access.UserRule"));return value;}
    private static void ValidatePassword(string value){if(value.Length<10||!value.Any(char.IsUpper)||!value.Any(char.IsLower)||!value.Any(char.IsDigit))throw new InvalidOperationException(T("Access.PasswordRule"));}
    private static string Hash(string value,byte[] salt,int iterations)=>Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(value),salt,iterations,HashAlgorithmName.SHA256,32));
    private static string Sha(string value)=>Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Fixed(string left,string right){try{return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(left),Convert.FromBase64String(right));}catch{return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left),Encoding.UTF8.GetBytes(right));}}
    private static string T(string key)=>LocalizationService.Current.T(key);
}
