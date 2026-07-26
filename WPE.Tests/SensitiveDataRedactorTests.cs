using 币安量化机器人.Services;
using Serilog;
using System.Text.Json;

namespace WPE.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_RemovesHeadersJsonQueryTokensCiphertextAndAccountIdentifiers()
    {
        const string apiKey="api-key-value-123456";
        const string secret="api-secret-value-654321";
        const string cipher="AQAAANCMnd8BFdERjHoAwE_veryLongDpapiCiphertextPayload1234567890==";
        var input=$"Authorization: Bearer sk-live-12345678901234567890; X-MBX-APIKEY={apiKey}; " +
            $"{{\"secret\":\"{secret}\",\"EncryptedKey\":\"{cipher}\",\"accountId\":\"private-account-42\"}} " +
            "https://user:password@example.test/v1?signature=abcdef123456&key=gemini-secret-42 " +
            "token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.signaturepart123456";

        var result=SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain(apiKey,result,StringComparison.Ordinal);
        Assert.DoesNotContain(secret,result,StringComparison.Ordinal);
        Assert.DoesNotContain(cipher,result,StringComparison.Ordinal);
        Assert.DoesNotContain("private-account-42",result,StringComparison.Ordinal);
        Assert.DoesNotContain("password",result,StringComparison.Ordinal);
        Assert.DoesNotContain("gemini-secret-42",result,StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci",result,StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.Replacement,result,StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesKnownUnlabelledSecretAndNormalizesLogMessage()
    {
        const string secret="unlabelled-provider-secret";
        var result=SensitiveDataRedactor.ForLog("failure\r\nvalue="+secret,80,secret);

        Assert.Equal("failure  value="+SensitiveDataRedactor.Replacement,result);
    }

    [Fact]
    public void MaskIdentifier_IsStableAndDoesNotRevealOriginalValue()
    {
        var first=SensitiveDataRedactor.MaskIdentifier("private-account-42");
        var second=SensitiveDataRedactor.MaskIdentifier("private-account-42");

        Assert.Equal(first,second);
        Assert.StartsWith("account#",first,StringComparison.Ordinal);
        Assert.DoesNotContain("private-account-42",first,StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_PreservesAuditHashesWhileRemovingStandaloneDpapiCiphertext()
    {
        const string hash="0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        const string cipher="AQAAANCMnd8BFdERjHoAwECl+sBAAAABase64CipherPayload1234567890==";
        var result=SensitiveDataRedactor.Redact($"promptHash={hash}; failure blob {cipher}");

        Assert.Contains(hash,result,StringComparison.Ordinal);
        Assert.DoesNotContain(cipher,result,StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveFileLogSink_RedactsExceptionAndStructuredProperties()
    {
        var root=Path.Combine(Path.GetTempPath(),"wpe-redactor-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const string secret="sk-log-secret-123456789012345";
            var logger=new LoggerConfiguration().WriteTo.Sink(new SensitiveFileLogSink(Path.Combine(root,"test.log"))).CreateLogger();
            logger.Error(new InvalidOperationException("Authorization: Bearer "+secret),"request failed {ApiKey}",secret);
            logger.Dispose();
            var text=File.ReadAllText(Assert.Single(Directory.GetFiles(root)));
            Assert.DoesNotContain(secret,text,StringComparison.Ordinal);
            Assert.Contains(SensitiveDataRedactor.Replacement,text,StringComparison.Ordinal);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task WriteRedactedJsonAsync_RemovesSecretsFromReports()
    {
        var path=Path.Combine(Path.GetTempPath(),"wpe-redacted-report-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            const string cipher="AQAAANCMnd8BFdERjHoAwEAAABase64CipherPayload1234567890==";
            await SensitiveDataRedactor.WriteRedactedJsonAsync(path,new{Failure="Authorization: Bearer sk-report-secret-1234567890",EncryptedKey=cipher,AccountId="account-private-7"});
            var text=await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("sk-report-secret",text,StringComparison.Ordinal);
            Assert.DoesNotContain(cipher,text,StringComparison.Ordinal);
            Assert.DoesNotContain("account-private-7",text,StringComparison.Ordinal);
            using var _=JsonDocument.Parse(text);
        }
        finally{File.Delete(path);}
    }
}
