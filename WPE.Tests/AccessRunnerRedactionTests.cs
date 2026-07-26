using System.Reflection;
using 币安量化机器人.Services.Access;

namespace WPE.Tests;

public sealed class AccessRunnerRedactionTests
{
    [Fact]
    public void AccessTestRunner_SafeDetail_RedactsSecrets()
    {
        const string secret = "sk-access-secret-123456789012345";
        var result = InvokeSafeDetail(typeof(AccessTestRunner), $"Authorization: Bearer {secret}", 400);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceLicenseTestRunner_SafeDetail_RedactsSecrets()
    {
        const string secret = "license-secret-123456789012345";
        var result = InvokeSafeDetail(typeof(DeviceLicenseTestRunner), $"api_key={secret}", 400);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessTestRunner_WritesCaseDetailsThroughSafeDetail()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Services", "Access", "AccessTestRunner.cs"));

        Assert.Contains("SafeDetail(ex.ToString(),400)", source, StringComparison.Ordinal);
        Assert.Contains("SafeDetail(detail)", source, StringComparison.Ordinal);
        Assert.Contains("SafeDetail(ex.Message)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceLicenseTestRunner_WritesCaseDetailsThroughSafeDetail()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Services", "Access", "DeviceLicenseTestRunner.cs"));

        Assert.Contains("SafeDetail(ex.ToString(),400)", source, StringComparison.Ordinal);
        Assert.Contains("SafeDetail(detail)", source, StringComparison.Ordinal);
        Assert.Contains("SafeDetail(ex.Message)", source, StringComparison.Ordinal);
    }

    private static string InvokeSafeDetail(Type type, string value, int maxLength)
    {
        var method = type.GetMethod("SafeDetail", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [value, maxLength]));
    }

    private static string SourceRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
