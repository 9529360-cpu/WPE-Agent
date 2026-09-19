using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Access;

public sealed record PrivilegedLocalAccessResult(
    bool Allowed,string Code,string UserId="",AgentSettings? Settings=null);

public static class PrivilegedLocalAccessPolicy
{
    public static PrivilegedLocalAccessResult Evaluate(
        AgentSettingsStore? settingsStore=null,
        bool requireSetup=true,
        string? deviceCode=null)
    {
        settingsStore??=new AgentSettingsStore();

        var settings=settingsStore.Load();
        if(settingsStore.LastLoadDiagnostic is not null)
            return new(false,"privileged-cli.settings-invalid");

        var expectedUser="LOCAL-"+(deviceCode??DeviceLicenseService.GetCurrentDeviceCode());
        if(string.IsNullOrWhiteSpace(settings.ActiveUser)||
           !string.Equals(settings.ActiveUser,expectedUser,StringComparison.Ordinal))
            return new(false,"privileged-cli.identity-mismatch");

        if(requireSetup&&!settings.SetupCompleted)
            return new(false,"privileged-cli.setup-required");

        return new(true,"privileged-cli.allowed",expectedUser,settings);
    }
}
