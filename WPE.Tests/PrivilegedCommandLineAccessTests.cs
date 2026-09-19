using 币安量化机器人;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PrivilegedCommandLineAccessTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-privileged-cli-"+Guid.NewGuid().ToString("N"));
    private string SettingsPath=>Path.Combine(_directory,"agent-settings.json");
    private const string DeviceCode="WPE-DEV-UNIT-TEST";
    private static string LocalIdentity=>"LOCAL-"+DeviceCode;

    [Theory]
    [InlineData("--provider-readonly-access")]
    [InlineData("--access-live-test")]
    [InlineData("--four-pillars-live-test")]
    [InlineData("--smoke-test")]
    [InlineData("--risk-aggregate-acceptance")]
    [InlineData("--execution-aggregate-acceptance")]
    [InlineData("--recovery-aggregate-acceptance")]
    [InlineData("--SMOKE-TEST")]
    public void CredentialedOrMutatingCommands_RequireLocalSetupAccess(string flag)
    {
        Assert.True(PrivilegedCommandLineStartupGuard.RequiresLocalSetupAccess(["WPE.exe",flag]));
    }

    [Theory]
    [InlineData("--i18n-test")]
    [InlineData("--exchange-public-test")]
    [InlineData("--license-test")]
    [InlineData("--device-code")]
    [InlineData("--activate-license")]
    [InlineData("--four-pillars-test")]
    [InlineData("--configure-testnet-env")]
    public void OfflinePublicAndBootstrapCommands_RemainAvailableBeforeSetup(string flag)
    {
        Assert.False(PrivilegedCommandLineStartupGuard.RequiresLocalSetupAccess(["WPE.exe",flag]));
    }

    [Fact]
    public void MissingLocalIdentity_IsDenied()
    {
        Directory.CreateDirectory(_directory);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=true,ActiveUser=""});

        var result=PrivilegedLocalAccessPolicy.Evaluate(settingsStore,true,DeviceCode);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.identity-mismatch",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void MatchingPersistedLocalIdentity_IsAllowed()
    {
        Directory.CreateDirectory(_directory);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=true,ActiveUser=LocalIdentity});

        var result=PrivilegedLocalAccessPolicy.Evaluate(settingsStore,true,DeviceCode);

        Assert.True(result.Allowed);
        Assert.Equal("privileged-cli.allowed",result.Code);
        Assert.Equal(LocalIdentity,result.UserId);
        Assert.NotNull(result.Settings);
    }

    [Fact]
    public void PersistedIdentityFromAnotherDevice_IsDenied()
    {
        Directory.CreateDirectory(_directory);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=true,ActiveUser="LOCAL-WPE-DEV-OTHER"});

        var result=PrivilegedLocalAccessPolicy.Evaluate(settingsStore,true,DeviceCode);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.identity-mismatch",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void CorruptSettings_FailClosed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath,"{not-json");
        var settingsStore=new AgentSettingsStore(SettingsPath);

        var result=PrivilegedLocalAccessPolicy.Evaluate(settingsStore,true,DeviceCode);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.settings-invalid",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void IncompleteSetup_IsDenied()
    {
        Directory.CreateDirectory(_directory);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=false,ActiveUser=LocalIdentity});

        var result=PrivilegedLocalAccessPolicy.Evaluate(settingsStore,true,DeviceCode);

        Assert.False(result.Allowed);
        Assert.Equal("privileged-cli.setup-required",result.Code);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void BootstrapAccess_CanValidateIdentityBeforeSetup()
    {
        Directory.CreateDirectory(_directory);
        var settingsStore=new AgentSettingsStore(SettingsPath);
        settingsStore.Save(new AgentSettings{SetupCompleted=false,ActiveUser=LocalIdentity});

        var result=PrivilegedLocalAccessPolicy.Evaluate(settingsStore,false,DeviceCode);

        Assert.True(result.Allowed);
        Assert.Equal(LocalIdentity,result.UserId);
    }

    public void Dispose()
    {
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}
