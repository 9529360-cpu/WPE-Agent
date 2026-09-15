using System.Security.Cryptography;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PrivilegedCommandLineAccessTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-privileged-cli-"+Guid.NewGuid().ToString("N"));
    private string SettingsPath=>Path.Combine(_directory,"agent-settings.json");
    private const string DeviceCode="WPE-DEV-UNIT-TEST";
    private const string LicenseId="LICENSE-UNIT-1";

    [Theory]
    [InlineData("--provider-readonly-access")]
    [InlineData("--access-live-test")]
    [InlineData("--four-pillars-live-test")]
    [InlineData("--smoke-test")]
    [InlineData("--SMOKE-TEST")]
    public void CredentialedOrMutatingCommands_RequireLicensedAccess(string flag)
    {
        Assert.True(PrivilegedCommandLineStartupGuard.RequiresLicensedAccess(["WPE.exe",flag]));
    }

    [Theory]
    [InlineData("--i18n-test")]
    [InlineData("--exchange-public-test")]
    [InlineData("--license-test")]
    [InlineData("--device-code")]
    [InlineData("--activate-license")]
    [InlineData("--four-pillars-test")]
    public void OfflinePublicAndActivationCommands_RemainAvailableWithoutLicense(string flag)
    {
        Assert.False(PrivilegedCommandLineStartupGuard.RequiresLicensedAccess(["WPE.exe",flag]));
    }

    [Fact]
    public void MissingLicense_IsDenied()
    {
        Directory.CreateDirectory(_directory);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=true,ActiveUser="DEVICE-"+LicenseId});
        var licenseService=new DeviceLicenseService(_directory,CreatePublicKeyOnly(),DeviceCode);

        var result=PrivilegedLocalAccessPolicy.Evaluate(licenseService,settingsStore);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.license-required",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void ValidLicenseAndMatchingPersistedIdentity_AreAllowed()
    {
        Directory.CreateDirectory(_directory);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licenseService=CreateLicensedService(key);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=true,ActiveUser="DEVICE-"+LicenseId});

        var result=PrivilegedLocalAccessPolicy.Evaluate(licenseService,settingsStore);

        Assert.True(result.Allowed);
        Assert.Equal("privileged-cli.allowed",result.Code);
        Assert.Equal("DEVICE-"+LicenseId,result.UserId);
        Assert.NotNull(result.Settings);
    }

    [Fact]
    public void ValidLicenseCannotReuseAnotherPersistedIdentity()
    {
        Directory.CreateDirectory(_directory);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licenseService=CreateLicensedService(key);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=true,ActiveUser="DEVICE-OTHER-LICENSE"});

        var result=PrivilegedLocalAccessPolicy.Evaluate(licenseService,settingsStore);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.identity-mismatch",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void CorruptSettings_FailClosed()
    {
        Directory.CreateDirectory(_directory);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licenseService=CreateLicensedService(key);
        File.WriteAllText(SettingsPath,"{not-json");
        var settingsStore=new AgentSettingsStore(SettingsPath);

        var result=PrivilegedLocalAccessPolicy.Evaluate(licenseService,settingsStore);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.settings-invalid",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void IncompleteSetup_IsDeniedEvenWithValidLicense()
    {
        Directory.CreateDirectory(_directory);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licenseService=CreateLicensedService(key);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=false,ActiveUser="DEVICE-"+LicenseId});

        var result=PrivilegedLocalAccessPolicy.Evaluate(licenseService,settingsStore);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.setup-required",result.Code);
        Assert.Null(result.Settings);
    }

    public void Dispose()
    {
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }

    private DeviceLicenseService CreateLicensedService(ECDsa key)
    {
        var publicKey=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var service=new DeviceLicenseService(_directory,publicKey,DeviceCode);
        var now=DateTime.UtcNow;
        var code=DeviceLicenseCodec.Issue(new DeviceLicensePayload(
            "WPE-AGENT",LicenseId,DeviceCode,now.AddMinutes(-1).Ticks,now.AddHours(1).Ticks,"Test"),key);
        Assert.True(service.Activate(code).Success);
        return service;
    }

    private static string CreatePublicKeyOnly()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
