using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Access;

public sealed record PrivilegedLocalAccessResult(bool Allowed,string Code,string UserId="");

public static class PrivilegedLocalAccessPolicy
{
    public static PrivilegedLocalAccessResult Evaluate(
        DeviceLicenseService? licenseService=null,
        AgentSettingsStore? settingsStore=null,
        bool requireSetup=true)
    {
        licenseService??=new DeviceLicenseService();
        settingsStore??=new AgentSettingsStore();

        var license=licenseService.TryLoad();
        if(!license.Success||license.License is null)
            return new(false,"privileged-cli.license-required");

        var settings=settingsStore.Load();
        if(settingsStore.LastLoadDiagnostic is not null)
            return new(false,"privileged-cli.settings-invalid");

        var expectedUser="DEVICE-"+license.License.LicenseId;
        if(string.IsNullOrWhiteSpace(settings.ActiveUser)||
           !string.Equals(settings.ActiveUser,expectedUser,StringComparison.Ordinal))
            return new(false,"privileged-cli.identity-mismatch");

        if(requireSetup&&!settings.SetupCompleted)
            return new(false,"privileged-cli.setup-required");

        return new(true,"privileged-cli.allowed",expectedUser);
    }
}
