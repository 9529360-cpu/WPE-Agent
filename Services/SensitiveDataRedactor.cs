using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace 币安量化机器人.Services;

/// <summary>Single boundary for removing credentials and sensitive identifiers from persisted or displayed diagnostics.</summary>
public static class SensitiveDataRedactor
{
    public const string Replacement = "[REDACTED]";

    private const string SensitiveName =
        "api[-_]?key|apikey|api[-_]?secret|secret(?:key)?|authorization|proxy[-_]?authorization|" +
        "x[-_]?mbx[-_]?apikey|x[-_]?bapi[-_]?(?:api[-_]?key|sign)|access[-_](?:key|sign|passphrase)|" +
        "passphrase|signature|sign|token|access[-_]?token|refresh[-_]?token|brain[-_]?key|" +
        "encrypted[a-z0-9_-]*|cipher(?:text)?|listen[-_]?key|account[-_]?id|uid|sub[-_]?account|key";

    private static readonly Regex AuthorizationValue = new(@"(?i)\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveQuery = new($@"(?i)(?<prefix>[?&](?:{SensitiveName})=)[^&#\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveAssignment = new("(?ix)(?<prefix>[\\\"']?(?:"+SensitiveName+")[\\\"']?\\s*(?:=|:)\\s*[\\\"']?)(?<value>[^\\\"'\\s,;}&]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UriUserInfo = new(@"(?i)(?<scheme>https?://)[^/@\s:]+(?::[^/@\s]*)?@", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Jwt = new(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KnownApiToken = new(@"(?i)(?<![A-Za-z0-9_-])(?:sk|rk|pk|AIza|ghp|github_pat|xox[baprs])[-_A-Za-z0-9]{12,}(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DpapiCipher = new(@"(?<![A-Za-z0-9+/_=-])AQAAANCMnd8BFdERjHoAwE[A-Za-z0-9+/_=-]{16,}(?![A-Za-z0-9+/_=-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Base64Cipher = new(@"(?<![A-Za-z0-9+/=])(?=[A-Za-z0-9+/]{40,}={0,2}(?![A-Za-z0-9+/=]))(?=[A-Za-z0-9+/]*[+/=])[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? value, params string?[] knownSecrets)
    {
        var result = value ?? string.Empty;
        foreach(var secret in knownSecrets.Where(x=>!string.IsNullOrWhiteSpace(x)&&x!.Length>=4).Distinct(StringComparer.Ordinal).OrderByDescending(x=>x!.Length))
            result=result.Replace(secret!,Replacement,StringComparison.Ordinal);
        result=UriUserInfo.Replace(result,m=>m.Groups["scheme"].Value+Replacement+"@");
        result=AuthorizationValue.Replace(result,Replacement);
        result=SensitiveQuery.Replace(result,m=>m.Groups["prefix"].Value+Replacement);
        result=SensitiveAssignment.Replace(result,m=>m.Groups["prefix"].Value+Replacement);
        result=Jwt.Replace(result,Replacement);
        result=KnownApiToken.Replace(result,Replacement);
        result=DpapiCipher.Replace(result,Replacement);
        return Base64Cipher.Replace(result,Replacement);
    }

    public static string ForLog(string? value,int maxLength=512,params string?[] knownSecrets)
    {
        var safe=Redact(value,knownSecrets).Replace('\r',' ').Replace('\n',' ').Trim();
        return safe[..Math.Min(Math.Max(0,maxLength),safe.Length)];
    }

    public static string MaskIdentifier(string? value,string prefix="account")
    {
        if(string.IsNullOrWhiteSpace(value))return prefix+"#unknown";
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return prefix+"#"+hash[..12];
    }

    public static async Task WriteRedactedJsonAsync(string path,object value,CancellationToken ct=default)
    {
        var json=JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync(path,Redact(json),Encoding.UTF8,ct);
    }
}

/// <summary>Final Serilog file boundary; sanitizes rendered properties and exception chains before persistence.</summary>
public sealed class SensitiveFileLogSink(string path) : ILogEventSink
{
    private readonly object _gate=new();
    private readonly MessageTemplateTextFormatter _formatter=new("{Timestamp:O} [{Level:u3}] {Message:lj} {Exception}{NewLine}");

    public void Emit(LogEvent logEvent)
    {
        try
        {
            using var writer=new StringWriter();_formatter.Format(logEvent,writer);
            var safe=SensitiveDataRedactor.Redact(writer.ToString());
            var directory=Path.GetDirectoryName(path);if(!string.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);
            var extension=Path.GetExtension(path);var daily=Path.Combine(directory??string.Empty,Path.GetFileNameWithoutExtension(path)+"-"+DateTime.UtcNow.ToString("yyyyMMdd")+extension);
            lock(_gate)File.AppendAllText(daily,safe,Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash the trading runtime.
        }
    }
}
