using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace 币安量化机器人.Services;

public sealed record UiDiagnosticNotice(string Code,DateTime TimeUtc,string Summary);
public static class UiDiagnostic
{
    private static readonly Regex Endpoint=new(@"(?i)\bhttps?://[^\s]+",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex ModelPayload=new(@"(?i)\b(?:prompt|response)\s*[:=]",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    public static UiDiagnosticNotice FromText(string? raw,string summary,DateTime? timeUtc=null)
    {
        var safe=Endpoint.Replace(SensitiveDataRedactor.Redact(raw),"[REMOTE]");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safe)));
        return new("WPE-"+hash[..8],(timeUtc??DateTime.UtcNow).ToUniversalTime(),summary);
    }
    public static string Format(Exception ex,string summary)=>Format(FromText(ex.ToString(),summary));
    public static string FormatText(string? raw,string summary,DateTime? timeUtc=null)=>Format(FromText(raw,summary,timeUtc));
    public static string Format(UiDiagnosticNotice value)=>$"{value.Summary} [{value.Code} · {value.TimeUtc:yyyy-MM-dd HH:mm:ss} UTC]";
    public static string SafeText(string? value,int maxLength=240)
    {
        if(ModelPayload.IsMatch(value??string.Empty))return "[REDACTED DIAGNOSTIC TEXT]";
        return Endpoint.Replace(SensitiveDataRedactor.ForLog(value,maxLength),"[REMOTE]");
    }
}
